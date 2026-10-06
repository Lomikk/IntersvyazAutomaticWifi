using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal sealed record StatusMachineState(
    bool Installed,
    string? InstalledVersion,
    string InstalledExecutablePath,
    bool AutomaticAuthorizationEnabled,
    bool AgentRunning,
    string? WifiSsid);

internal sealed class StatusService
{
    private readonly string currentVersion;
    private readonly AppPaths paths;
    private readonly JsonFileStore json;
    private readonly Func<StatusMachineState> readMachineState;

    public StatusService(
        string currentVersion,
        AppPaths paths,
        JsonFileStore json,
        Func<StatusMachineState>? readMachineState = null)
    {
        this.currentVersion = currentVersion;
        this.paths = paths;
        this.json = json;
        this.readMachineState = readMachineState ?? ReadDefaultMachineState;
    }

    public InteractiveStatusSnapshot ReadLocalSnapshot(bool? internetOverride = null)
    {
        var local = ReadLocalState();
        return BuildSnapshot(local, internetOverride);
    }

    public async Task<InteractiveStatusSnapshot> RefreshNetworkSnapshotAsync(
        Func<CancellationToken, Task<bool?>> probeInternet,
        Func<CancellationToken, Task> checkUpdates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probeInternet);
        ArgumentNullException.ThrowIfNull(checkUpdates);

        var updateTask = checkUpdates(cancellationToken);
        bool? online;
        try
        {
            online = await probeInternet(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            online = null;
        }

        try
        {
            await updateTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Update discovery is best-effort and must never block the main UI.
        }

        return ReadLocalSnapshot(online);
    }

    public async Task<IReadOnlyList<string>> BuildDetailedReportAsync(
        Func<CancellationToken, Task<bool?>> probeInternet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probeInternet);

        var internet = await probeInternet(cancellationToken).ConfigureAwait(false);
        var local = ReadLocalState();
        var state = local.Runtime;
        var settings = local.Settings;
        var machine = local.Machine;

        var lines = new List<string>
        {
            "IS74W — подробный отчёт",
            $"Создан: {DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss zzz}",
            string.Empty,
            "=== Программа ===",
            $"Запущенная версия: {currentVersion}",
            $"Запущенный EXE: {Environment.ProcessPath ?? "неизвестно"}",
            $"Установка: {(machine.Installed ? "есть" : "нет")}",
            $"Установленная версия: {machine.InstalledVersion ?? "неизвестно"}",
            $"Установленный EXE: {(machine.Installed ? machine.InstalledExecutablePath : "—")}",
            string.Empty,
            "=== Состояние ===",
            $"Интернет: {(internet == true ? "доступен" : "не подтверждён")}",
            $"Физические пути: {local.CurrentPaths.Count}",
            $"Авторизация: {FormatPathAuthorizationSummary(local.PathState, local.CurrentPaths)}",
            $"Регистрация: {(local.Secrets is null ? "нет" : "сохранена")}",
            $"Телефон: {MaskPhone(local.Secrets?.Phone)}",
            $"Автовход: {(machine.AutomaticAuthorizationEnabled ? "включён" : "выключен")}",
            $"Фоновый режим: {(machine.AgentRunning ? "работает" : "остановлен")}",
            $"Уведомления: {FormatNotificationMode(settings.NotificationMode)}",
            $"Значок в трее: {(settings.ShowTrayIcon ? "показывать" : "скрывать")}",
            $"API-сессия: {FormatSessionEnd(local.Session?.AccessEnd)}",
            string.Empty,
            "=== Обновления ===",
            $"Режим: {(settings.AutomaticUpdates ? "автоматически" : "уведомлять")}",
            $"Канал: {(local.IncludePrereleases ? "обычные + Pre-release" : "обычные версии")}",
            $"Доступно: {local.UpdateState.AvailableVersion ?? "нет"}",
            $"Последняя проверка: {(local.UpdateState.LastCheckedUtc is { } checkedAt ? checkedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") : "ещё не выполнялась")}"
        };

        if (!string.IsNullOrWhiteSpace(local.UpdateState.LastError))
        {
            lines.Add($"Последняя ошибка проверки: {local.UpdateState.LastError}");
        }

        var telemetryStatus = new TelemetryQueue(paths).GetStatus();
        var telemetryUpload = new TelemetryUploadStateStore(paths, json).Load();
        lines.Add(string.Empty);
        lines.Add("=== Телеметрия ===");
        lines.Add($"Режим: {FormatAnonymousStatisticsConsent(settings.AnonymousStatisticsConsent)}");
        lines.Add($"Ожидает выгрузки: {telemetryStatus.PendingFiles} файлов / {FormatByteCount(telemetryStatus.PendingBytes)}");
        lines.Add($"Последняя выгрузка: {(telemetryUpload.LastSuccessfulUploadUtc is { } lastUpload ? lastUpload.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") : "ещё не было")}");
        if (telemetryUpload.NextAttemptUtc is { } nextUpload)
            lines.Add($"Следующая попытка: {nextUpload.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        if (telemetryUpload.ConsecutiveFailures > 0)
            lines.Add($"Неудачных попыток подряд: {telemetryUpload.ConsecutiveFailures}");
        lines.Add($"Карантин: {telemetryStatus.RejectedFiles} файлов / {FormatByteCount(telemetryStatus.RejectedBytes)}");

        lines.Add(string.Empty);
        lines.Add("=== Сетевые пути ===");
        if (local.CurrentPaths.Count == 0)
        {
            lines.Add("Активные физические пути не обнаружены.");
        }
        else
        {
            foreach (var path in local.CurrentPaths)
            {
                var pathState = local.PathState.Paths.FirstOrDefault(item => SamePathIdentity(item.Identity, path.Identity));
                var label = path.Ssid is null ? path.Name : $"{path.Name} · {path.Ssid}";
                lines.Add($"{label}: {FormatPathStatus(pathState?.Status ?? PathAuthorizationStatus.Unknown)}");
                lines.Add($"  IPv4: {path.SourceIPv4?.ToString() ?? "—"}; gateway: {path.GatewayIPv4?.ToString() ?? "—"}; ifIndex: {path.InterfaceIndex}");
                if (pathState?.ExpectedExpiryUtc is { } pathExpiry)
                    lines.Add($"  Ожидаемая граница: {pathExpiry.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
                if (pathState?.LastSuccessfulAuthUtc is { } pathAuth)
                    lines.Add($"  Последняя авторизация: {pathAuth.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
                if (pathState?.UserActionRequired == true)
                    lines.Add("  Требуется действие пользователя: да");
            }
        }

        lines.Add(string.Empty);
        lines.Add("=== Пути файлов ===");
        lines.Add($"Данные приложения: {paths.Root}");
        lines.Add($"Диагностический журнал: {paths.DiagnosticLogFile}");
        return lines;
    }

    private LocalStatusState ReadLocalState()
    {
        // These reads intentionally preserve the existing local normalization behavior:
        // SettingsStore.Load() creates the default settings file when it is absent, and
        // UpdateMaintenanceService.LoadState() may persist a normalized stale update state.
        // No HTTP client is created or called on this path.
        var settings = new SettingsStore(paths, json).Load();
        var logger = new DiagnosticLogger(paths);
        var updateMaintenance = new UpdateMaintenanceService(currentVersion, paths, json, logger);
        var updateState = updateMaintenance.LoadState();
        var pathState = new PathAuthorizationStateStore(paths, json).Load();
        var enumerator = new NetworkPathEnumerator();
        var allPaths = enumerator.EnumerateAll();
        var currentPaths = allPaths
            .Where(path => path.CanAutomaticallyAuthorize)
            .ToArray();
        var vpnActive = DetectActiveVpnRoute(allPaths);
        return new LocalStatusState(
            settings,
            new DpapiSecretStore(paths).Load(),
            new SessionMetadataStore(paths, json).Load(),
            new RuntimeStateStore(paths, json).Load(),
            pathState,
            currentPaths,
            vpnActive,
            updateState,
            updateMaintenance.IncludePrereleases(settings),
            readMachineState());
    }

    private InteractiveStatusSnapshot BuildSnapshot(LocalStatusState local, bool? internetOverride)
    {
        var wifiNetwork = SsidPolicy.IsTarget(local.Machine.WifiSsid)
            ? WifiNetworkState.Campus
            : local.Machine.WifiSsid is not null
                ? WifiNetworkState.Other
                : WifiNetworkState.Unknown;
        var activeStates = local.CurrentPaths
            .Select(path => local.PathState.Paths.FirstOrDefault(state => SamePathIdentity(state.Identity, path.Identity)))
            .Where(state => state is not null)
            .Cast<PathAuthorizationState>()
            .ToArray();
        var internetPathCount = activeStates.Count(state => state.Status == PathAuthorizationStatus.Internet);
        var captivePathCount = activeStates.Count(state => state.Status == PathAuthorizationStatus.Captive);
        var problemPathCount = Math.Max(0, local.CurrentPaths.Count - internetPathCount - captivePathCount);
        var internet = internetOverride ?? (internetPathCount > 0 ? true : local.Runtime.InternetConfirmed);
        var nextExpiry = activeStates
            .Where(state => state.ExpectedExpiryUtc is not null)
            .Select(state => state.ExpectedExpiryUtc!.Value)
            .OrderBy(value => value)
            .Cast<DateTimeOffset?>()
            .FirstOrDefault();
        var latestPathResult = activeStates
            .Where(state => !string.IsNullOrWhiteSpace(state.LastResult))
            .OrderByDescending(state => state.LastAttemptUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        var preferredPath = new PreferredNetworkPathResolver().Resolve(local.CurrentPaths);
        var networkPaths = local.CurrentPaths
            .OrderByDescending(path => preferredPath is not null && SamePathIdentity(path.Identity, preferredPath))
            .ThenBy(path => path.Ssid is null ? path.Name : path.Ssid, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var pathState = local.PathState.Paths.FirstOrDefault(state => SamePathIdentity(state.Identity, path.Identity));
                var displayName = path.IsWifi
                    ? $"Wi-Fi · {path.Ssid ?? path.Name}"
                    : path.Name;
                return new InteractiveNetworkPathStatus(
                    displayName,
                    pathState?.Status ?? PathAuthorizationStatus.Unknown,
                    preferredPath is not null && SamePathIdentity(path.Identity, preferredPath),
                    pathState?.ExpectedExpiryUtc,
                    pathState?.LastResult,
                    AuthorizationWindowApproximate: IsAuthorizationWindowApproximate(path));
            })
            .ToArray();

        return new InteractiveStatusSnapshot(
            Installed: local.Machine.Installed,
            Registered: local.Secrets is not null,
            InternetAvailable: internet,
            WifiNetwork: wifiNetwork,
            WifiSsid: local.Machine.WifiSsid,
            AuthorizationExpectedExpiryUtc: nextExpiry,
            AuthorizationAlreadyActive: activeStates.Any(state => string.Equals(state.LastResult, "already-authorized", StringComparison.Ordinal)),
            NetworkCheckIgnored: false,
            DirectNetworkMode: local.Settings.DirectNetworkAdapterId switch
            {
                null => "автоматически",
                PhysicalAdapterSelection.SystemRoute => "системный",
                _ => "вручную"
            },
            AnonymousStatisticsConsent: local.Settings.AnonymousStatisticsConsent,
            AutomaticUpdates: local.Settings.AutomaticUpdates,
            IncludePrereleaseUpdates: local.IncludePrereleases,
            AvailableUpdateVersion: local.UpdateState.AvailableVersion,
            LastUpdateCheckUtc: local.UpdateState.LastCheckedUtc,
            AutomaticAuthorizationEnabled: local.Machine.AutomaticAuthorizationEnabled,
            AgentRunning: local.Machine.AgentRunning,
            NotificationMode: FormatNotificationMode(local.Settings.NotificationMode),
            ShowTrayIcon: local.Settings.ShowTrayIcon,
            MaskedPhone: MaskPhone(local.Secrets?.Phone),
            ApiSessionEnd: FormatSessionEnd(local.Session?.AccessEnd),
            LastResult: FormatRuntimeResultForUi(latestPathResult?.LastResult ?? local.Runtime.LastResult),
            Version: currentVersion,
            ActivePhysicalPathCount: local.CurrentPaths.Count,
            InternetPathCount: internetPathCount,
            CaptivePathCount: captivePathCount,
            ProblemPathCount: problemPathCount,
            NetworkPaths: networkPaths,
            VpnActive: local.VpnActive);
    }


    private static bool DetectActiveVpnRoute(IReadOnlyList<NetworkPathSnapshot> allPaths)
    {
        try
        {
            var route = new SystemNetworkRouteResolver().Resolve(IPAddress.Parse("1.1.1.1"), allPaths);
            if (route.LocalPath is { LooksVirtual: true })
            {
                return true;
            }
            if (route.SourceAddress is null)
            {
                return false;
            }

            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (network.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }
                    var properties = network.GetIPProperties();
                    var ownsRouteSource = properties.UnicastAddresses.Any(address =>
                        address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        address.Address.Equals(route.SourceAddress));
                    if (!ownsRouteSource)
                    {
                        continue;
                    }

                    var index = properties.GetIPv4Properties()?.Index ?? 0;
                    return PhysicalAdapterSelection.LooksVirtual(
                        network.NetworkInterfaceType,
                        network.Name,
                        network.Description,
                        index);
                }
                catch (NetworkInformationException)
                {
                    // The interface may disappear while the status pane is refreshing.
                }
            }
            return false;
        }
        catch
        {
            // Route inspection is best-effort UI diagnostics. Failure must not
            // affect authorization or make the status screen unavailable.
            return false;
        }
    }

    private static StatusMachineState ReadDefaultMachineState()
    {
        var installation = new ProgramInstallation();
        var displayedSsid = ReadDisplayedWifiSsid();
        return new StatusMachineState(
            Installed: installation.IsInstalled,
            InstalledVersion: installation.ReadInstalledVersion(),
            InstalledExecutablePath: installation.ExecutablePath,
            AutomaticAuthorizationEnabled: installation.IsInstalled && new WindowsAutostartService().IsEnabledFor(installation.ExecutablePath),
            AgentRunning: AgentProcessControl.IsAgentRunning(),
            WifiSsid: displayedSsid);
    }

    private static string? ReadDisplayedWifiSsid()
    {
        var connectedSsids = WindowsWifiService.GetConnectedSsids();
        return connectedSsids.FirstOrDefault(SsidPolicy.IsTarget) ?? connectedSsids.FirstOrDefault();
    }

    internal static string FormatWifiNetwork(string? ssid) => ssid switch
    {
        null => "не определена ○",
        _ when SsidPolicy.IsTarget(ssid) => $"{ssid} ●",
        _ => $"{ssid} ○"
    };

    internal static string FormatWifiAuthorization(RuntimeState state)
    {
        if (state.ExpectedExpiryUtc is { } expiry && expiry > DateTimeOffset.UtcNow)
        {
            var remaining = expiry - DateTimeOffset.UtcNow;
            var totalMinutes = Math.Max(0, (int)Math.Floor(remaining.TotalMinutes));
            return $"до следующей ~{totalMinutes / 60:00}:{totalMinutes % 60:00} ●";
        }

        if (string.Equals(state.LastResult, "already-authorized", StringComparison.Ordinal))
        {
            return "уже активна ●";
        }

        return state.ExpectedExpiryUtc is not null
            ? "срок истёк ○"
            : "ещё не выполнялась ○";
    }

    internal static string FormatRuntimeResultForUi(string? result) => result switch
    {
        null or "" => "—",
        "success" => "успешно",
        "already-authorized" => "уже авторизован",
        "step-one-sent" => "авторизация начата",
        "step-one-retryable-error" or "pre-step-retryable-error" => "временная ошибка",
        "automatic-step-one-limit" => "нужно действие",
        "bearer-invalid" => "API-сессия отклонена",
        "step-two-ambiguous" => "результат неясен",
        "step-two-rejected" => "код отклонён",
        "step-one-rate-limited" => "слишком много попыток",
        "step-one-rejected" => "запрос отклонён",
        "unexpected-step-one-redirect" => "неожиданный ответ портала",
        "cancelled" => "отменено",
        _ => "ошибка авторизации"
    };

    internal static string FormatPathStatus(PathAuthorizationStatus status) => status switch
    {
        PathAuthorizationStatus.Internet => "Интернет доступен",
        PathAuthorizationStatus.Captive => "требуется авторизация",
        PathAuthorizationStatus.Unreachable => "недоступен",
        PathAuthorizationStatus.Ambiguous => "состояние неясно",
        PathAuthorizationStatus.Disconnected => "отключён",
        _ => "ещё не проверен"
    };

    private static string FormatPathAuthorizationSummary(
        PathAuthorizationStateDocument document,
        IReadOnlyList<NetworkPathSnapshot> currentPaths)
    {
        if (currentPaths.Count == 0) return "нет активных физических путей";
        var states = currentPaths
            .Select(path => document.Paths.FirstOrDefault(state => SamePathIdentity(state.Identity, path.Identity)))
            .Where(state => state is not null)
            .Cast<PathAuthorizationState>()
            .ToArray();
        var captive = states.Count(state => state.Status == PathAuthorizationStatus.Captive);
        if (captive > 0) return $"требуют авторизацию: {captive}";
        var internet = states.Count(state => state.Status == PathAuthorizationStatus.Internet);
        if (internet > 0) return $"Internet: {internet}/{currentPaths.Count}";
        return "ожидает проверки";
    }

    internal static string FormatAnonymousStatisticsConsent(AnonymousStatisticsConsent consent) => consent switch
    {
        AnonymousStatisticsConsent.Allowed => "включена",
        AnonymousStatisticsConsent.Declined => "выключена",
        _ => "не выбрано"
    };

    internal static string FormatNotificationMode(NotificationMode mode) => mode switch
    {
        NotificationMode.All => "все",
        NotificationMode.Off => "выкл",
        _ => "важные"
    };

    internal static string FormatSessionEnd(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "—";
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            return "до " + parsed.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        }

        return "до " + value.Trim();
    }

    internal static string MaskPhone(string? phone) =>
        phone is { Length: 10 }
            ? $"+7 *** ***-{phone.Substring(6, 2)}-{phone.Substring(8, 2)}"
            : "-";

    private static string FormatByteCount(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024L * 1024L) return $"{bytes / 1024d:0.0} КБ";
        return $"{bytes / (1024d * 1024d):0.0} МБ";
    }

    internal static bool IsAuthorizationWindowApproximate(NetworkPathSnapshot path)
    {
        // Only the known direct Campus Wi-Fi SSID family gets an exact-looking
        // countdown. Any other Wi-Fi SSID may be a phone/laptop hotspot, and
        // Ethernet/USB may hide an upstream that can switch to LTE or another
        // network without changing the locally visible path.
        return !(path.IsWifi && SsidPolicy.IsTarget(path.Ssid));
    }

    private static bool SamePathIdentity(NetworkPathIdentity left, NetworkPathIdentity right) =>
        left.InterfaceType == right.InterfaceType &&
        string.Equals(left.AdapterId, right.AdapterId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.NetworkDiscriminator, right.NetworkDiscriminator, StringComparison.OrdinalIgnoreCase);

    private sealed record LocalStatusState(
        AppSettings Settings,
        StoredSecrets? Secrets,
        SessionMetadata? Session,
        RuntimeState Runtime,
        PathAuthorizationStateDocument PathState,
        IReadOnlyList<NetworkPathSnapshot> CurrentPaths,
        bool VpnActive,
        UpdateState UpdateState,
        bool IncludePrereleases,
        StatusMachineState Machine);
}
