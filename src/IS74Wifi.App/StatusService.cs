using System.Globalization;
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
            $"Сеть: {(settings.IgnoreNetworkCheck ? "проверка отключена ○" : FormatWifiNetwork(machine.WifiSsid))}",
            $"Авторизация: {FormatWifiAuthorization(state)}",
            $"Регистрация: {(local.Secrets is null ? "нет" : "сохранена")}",
            $"Телефон: {MaskPhone(local.Secrets?.Phone)}",
            $"Автовход: {(machine.AutomaticAuthorizationEnabled ? "включён" : "выключен")}",
            $"Фоновый режим: {(machine.AgentRunning ? "работает" : "остановлен")}",
            $"Уведомления: {FormatNotificationMode(settings.NotificationMode)}",
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

        if (state.LastAuthUtc is { } lastAuth)
            lines.Add($"Последняя Wi-Fi авторизация: {lastAuth.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        if (state.ExpectedExpiryUtc is { } expiry)
            lines.Add($"Ожидаемое окончание окна: {expiry.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(state.LastResult))
            lines.Add($"Последний результат: {FormatRuntimeResultForUi(state.LastResult)}");
        if (state.AutomaticStepOneAttempts > 0)
            lines.Add($"Автоматические попытки: {state.AutomaticStepOneAttempts}/{settings.MaxAutomaticStepOneAttempts}");
        lines.Add($"Требуется действие пользователя: {(state.UserActionRequired ? "да" : "нет")}");

        lines.Add(string.Empty);
        lines.Add("=== Пути ===");
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
        return new LocalStatusState(
            settings,
            new DpapiSecretStore(paths).Load(),
            new SessionMetadataStore(paths, json).Load(),
            new RuntimeStateStore(paths, json).Load(),
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
        var internet = internetOverride ?? local.Runtime.InternetConfirmed;

        return new InteractiveStatusSnapshot(
            Installed: local.Machine.Installed,
            Registered: local.Secrets is not null,
            InternetAvailable: internet,
            WifiNetwork: wifiNetwork,
            WifiSsid: local.Machine.WifiSsid,
            AuthorizationExpectedExpiryUtc: local.Runtime.ExpectedExpiryUtc,
            AuthorizationAlreadyActive: string.Equals(local.Runtime.LastResult, "already-authorized", StringComparison.Ordinal),
            NetworkCheckIgnored: local.Settings.IgnoreNetworkCheck,
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
            MaskedPhone: MaskPhone(local.Secrets?.Phone),
            ApiSessionEnd: FormatSessionEnd(local.Session?.AccessEnd),
            LastResult: FormatRuntimeResultForUi(local.Runtime.LastResult),
            Version: currentVersion);
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
        UpdateState UpdateState,
        bool IncludePrereleases,
        StatusMachineState Machine);
}
