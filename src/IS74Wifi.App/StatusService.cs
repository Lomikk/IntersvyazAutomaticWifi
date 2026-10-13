using System.Globalization;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal sealed record StatusMachineState(
    bool Installed,
    string? InstalledVersion,
    string InstalledExecutablePath,
    bool AutomaticAuthorizationEnabled,
    bool AgentRunning,
    bool NetworkOperationRunning = false);

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

    public InteractiveStatusSnapshot ReadLocalSnapshot()
    {
        var local = ReadLocalState();
        return BuildSnapshot(local, networkOverride: null);
    }

    internal InteractiveStatusSnapshot ReadSnapshot(AgentNetworkSnapshot networkSnapshot)
    {
        ArgumentNullException.ThrowIfNull(networkSnapshot);
        return BuildSnapshot(ReadLocalState(), networkSnapshot);
    }

    public async Task<InteractiveStatusSnapshot> RefreshNetworkSnapshotAsync(
        Func<CancellationToken, Task<AgentNetworkSnapshot>> refreshNetwork,
        Func<CancellationToken, Task> checkUpdates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshNetwork);
        ArgumentNullException.ThrowIfNull(checkUpdates);

        var updateTask = checkUpdates(cancellationToken);
        AgentNetworkSnapshot? network = null;
        try
        {
            network = await refreshNetwork(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Agent refresh is best-effort for the UI; retain the last persisted snapshot.
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

        var local = ReadLocalState();
        return BuildSnapshot(local, network);
    }

    public async Task<IReadOnlyList<string>> BuildDetailedReportAsync(
        Func<CancellationToken, Task<AgentNetworkSnapshot>> refreshNetwork,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshNetwork);

        var network = await refreshNetwork(cancellationToken).ConfigureAwait(false);
        var local = ReadLocalState();
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
            $"Интернет: {(network.InternetAvailable == true ? "доступен" : "не подтверждён")}",
            $"Физические пути: {network.Paths.Length}",
            $"Авторизация: {FormatPathAuthorizationSummary(network)}",
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
        if (network.Paths.Length == 0)
        {
            lines.Add("Активные физические пути не обнаружены.");
        }
        else
        {
            foreach (var path in network.Paths)
            {
                lines.Add($"{path.Name}: {FormatPathStatus(path.Status)}");
                lines.Add($"  IPv4: {path.SourceIPv4 ?? "—"}; gateway: {path.GatewayIPv4 ?? "—"}; ifIndex: {path.InterfaceIndex}");
                if (path.ExpectedExpiryUtc is { } pathExpiry)
                    lines.Add($"  Ожидаемая граница: {pathExpiry.ToLocalTime():dd.MM.yyyy HH:mm:ss}" +
                              (path.AuthorizationWindowApproximate ? " (приблизительно)" : string.Empty));
                if (path.EdgeWatchActive)
                    lines.Add("  Режим: наблюдение фактической границы (сервер всё ещё считает путь авторизованным)");
                if (path.NextCheckUtc is { } scheduledCheck)
                    lines.Add($"  Следующая проверка: {scheduledCheck.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
                else if (path.CheckOnConnectivityEvent)
                    lines.Add("  Следующая проверка: по событию Windows/NCSI");
                if (path.LastSuccessfulAuthUtc is { } pathAuth)
                    lines.Add($"  Последняя авторизация: {pathAuth.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
                if (path.UserActionRequired)
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
        var network = new AgentNetworkSnapshotStore(paths, json).Load() ?? AgentNetworkSnapshot.Empty;
        return new LocalStatusState(
            settings,
            new DpapiSecretStore(paths).Load(),
            new SessionMetadataStore(paths, json).Load(),
            new RuntimeStateStore(paths, json).Load(),
            network,
            updateState,
            updateMaintenance.IncludePrereleases(settings),
            readMachineState());
    }

    private InteractiveStatusSnapshot BuildSnapshot(
        LocalStatusState local,
        AgentNetworkSnapshot? networkOverride)
    {
        var network = networkOverride ?? local.Network;
        var wifiNetwork = SsidPolicy.IsTarget(network.WifiSsid)
            ? WifiNetworkState.Campus
            : network.WifiSsid is not null
                ? WifiNetworkState.Other
                : WifiNetworkState.Unknown;
        var internetPathCount = network.Paths.Count(path => path.Status == PathAuthorizationStatus.Internet);
        var captivePathCount = network.Paths.Count(path => path.Status == PathAuthorizationStatus.Captive);
        var problemPathCount = Math.Max(0, network.Paths.Length - internetPathCount - captivePathCount);
        var internet = network.InternetAvailable ??
            (internetPathCount > 0 ? true : local.Runtime.InternetConfirmed);
        var nextExpiry = network.Paths
            .Where(path => path.ExpectedExpiryUtc is not null)
            .Select(path => path.ExpectedExpiryUtc!.Value)
            .OrderBy(value => value)
            .Cast<DateTimeOffset?>()
            .FirstOrDefault();
        var latestPathResult = network.Paths
            .Where(path => !string.IsNullOrWhiteSpace(path.LastResult))
            .OrderByDescending(path => path.LastAttemptUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        var networkPaths = network.Paths
            .Select(path => new InteractiveNetworkPathStatus(
                path.Name,
                path.Status,
                path.Preferred,
                path.ExpectedExpiryUtc,
                path.LastResult,
                path.AuthorizationWindowApproximate,
                path.NextCheckUtc,
                path.CheckOnConnectivityEvent,
                path.EdgeWatchActive,
                local.Machine.NetworkOperationRunning))
            .ToArray();

        return new InteractiveStatusSnapshot(
            Installed: local.Machine.Installed,
            Registered: local.Secrets is not null,
            InternetAvailable: internet,
            WifiNetwork: wifiNetwork,
            WifiSsid: network.WifiSsid,
            AuthorizationExpectedExpiryUtc: nextExpiry,
            AuthorizationAlreadyActive: network.Paths.Any(path =>
                string.Equals(path.LastResult, "already-authorized", StringComparison.Ordinal)),
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
            ActivePhysicalPathCount: network.Paths.Length,
            InternetPathCount: internetPathCount,
            CaptivePathCount: captivePathCount,
            ProblemPathCount: problemPathCount,
            NetworkPaths: networkPaths,
            VpnActive: network.VpnActive);
    }

    private static StatusMachineState ReadDefaultMachineState()
    {
        var installation = new ProgramInstallation();
        return new StatusMachineState(
            Installed: installation.IsInstalled,
            InstalledVersion: installation.ReadInstalledVersion(),
            InstalledExecutablePath: installation.ExecutablePath,
            AutomaticAuthorizationEnabled: installation.IsInstalled && new WindowsAutostartService().IsEnabledFor(installation.ExecutablePath),
            AgentRunning: AgentProcessControl.IsAgentRunning(),
            NetworkOperationRunning: AgentProcessControl.IsNetworkOperationRunning());
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

    private static string FormatPathAuthorizationSummary(AgentNetworkSnapshot network)
    {
        if (network.Paths.Length == 0) return "нет активных физических путей";
        var captive = network.Paths.Count(path => path.Status == PathAuthorizationStatus.Captive);
        if (captive > 0) return $"требуют авторизацию: {captive}";
        var internet = network.Paths.Count(path => path.Status == PathAuthorizationStatus.Internet);
        if (internet > 0) return $"Internet: {internet}/{network.Paths.Length}";
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

    private sealed record LocalStatusState(
        AppSettings Settings,
        StoredSecrets? Secrets,
        SessionMetadata? Session,
        RuntimeState Runtime,
        AgentNetworkSnapshot Network,
        UpdateState UpdateState,
        bool IncludePrereleases,
        StatusMachineState Machine);
}
