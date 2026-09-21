using System.Diagnostics;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal static class Program
{
    private const string ProductVersion = "v0.1.0-alpha.17";
    private const string AnonymousStatisticsConsentMessage =
        "Разрешить отправку анонимной статистики о работе приложения? Это помогает развивать приложение, улучшать стабильность и скорость авторизации, а также позволяет участвовать в анонимном рейтинге скорости интернета.";
    private const string AnonymousStatisticsPublishMessage =
        "Для публикации результата требуется отправка анонимной статистики. Разрешить её?";
    private const string AutomaticUpdateGateName = @"Local\IS74Wifi.CSharp.AutoUpdate";
    private static bool forwardMenuWithoutReveal;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "menu";

        if (command == "update-apply")
        {
            // The updater deliberately attaches to the same console before the
            // parent exits. That keeps the terminal window alive while the
            // installed EXE is replaced instead of leaving a silent/busy gap.
            ConsoleSession.EnsureInteractiveConsole();
            return ApplyPreparedUpdate(args.Skip(1).ToArray());
        }

        if (command == "uninstall-apply")
        {
            return ApplyDeferredUninstall(args.Skip(1).ToArray());
        }

        if (command == "agent")
        {
            if (TryForwardToInstalledCopy(args, command, waitForExit: false, out var forwardedAgentExit))
            {
                return forwardedAgentExit;
            }
            return await RunAgentAsync().ConfigureAwait(false);
        }

        if (command == "update-auto")
        {
            return await RunAutomaticUpdateAsync().ConfigureAwait(false);
        }

        CleanupStaleUpdateDirectories();
        ConsoleSession.EnsureInteractiveConsole();

        try
        {
            if (command == "menu" && BootstrapInteractiveLaunch(out var bootstrapExit))
            {
                return bootstrapExit;
            }

            if (TryForwardToInstalledCopy(args, command, waitForExit: true, out var forwardedExit))
            {
                return forwardedExit;
            }

            return command switch
            {
                "version" or "--version" or "-v" => PrintVersion(),
                "help" or "--help" or "-h" => PrintHelp(),
                "contract" => PrintContract(),
                "register" => await RegisterAsync().ConfigureAwait(false),
                "connect" => await ConnectAsync().ConfigureAwait(false),
                "status" => await PrintStatusAsync().ConfigureAwait(false),
                "install" => InstallApplication(),
                "enable-autostart" => EnableAutomaticAuthorization(),
                "disable-autostart" => DisableAutostart(),
                "uninstall" => Uninstall(),
                "reset" => ResetRegistration(),
                "purge" => Purge(),
                "logs" => OpenLogs(),
                "update-check" => await CheckForUpdatesAsync().ConfigureAwait(false),
                "update" => await UpdateCommandAsync().ConfigureAwait(false),
                "backend-diagnose" => await RunBackendDiagnosticsAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "menu" => await RunMenuAsync(args.Skip(1).Any(arg => string.Equals(arg, "updates", StringComparison.OrdinalIgnoreCase))).ConfigureAwait(false),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"IS74Wifi {ProductVersion}");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("IS74Wifi — автоматическая авторизация Campus Wi-Fi");
        Console.WriteLine("register           Зарегистрировать устройство");
        Console.WriteLine("connect            Авторизовать Wi-Fi один раз сейчас");
        Console.WriteLine("install            Установить программу для текущего пользователя");
        Console.WriteLine("enable-autostart   Включить автоматическую авторизацию");
        Console.WriteLine("disable-autostart  Отключить автоматическую авторизацию");
        Console.WriteLine("status             Показать состояние");
        Console.WriteLine("update-check       Проверить обновления");
        Console.WriteLine("update             Установить доступное обновление");
        Console.WriteLine("backend-diagnose  Проверить DNS, redirects и ответ telemetry backend");
        Console.WriteLine("uninstall          Удалить программу и локальные данные");
        return 0;
    }

    private static async Task<int> RunBackendDiagnosticsAsync(string[] args)
    {
        var includePost = args.Any(arg =>
            string.Equals(arg, "--post", StringComparison.OrdinalIgnoreCase));
        var paths = new AppPaths();
        var settings = new SettingsStore(paths, new JsonFileStore()).Load();
        var endpoint = ApplicationRuntime.ResolveTelemetryEndpoint(settings);

        Console.WriteLine("IS74Wifi backend diagnostics");
        Console.WriteLine($"Endpoint: {endpoint}");
        Console.WriteLine($"System proxy: enabled for telemetry HttpClient");
        Console.WriteLine();

        var dnsWatch = Stopwatch.StartNew();
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(endpoint.Host).ConfigureAwait(false);
            Console.WriteLine(
                $"DNS {endpoint.Host}: {string.Join(", ", addresses.Select(address => address.ToString()))} " +
                $"({dnsWatch.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DNS {endpoint.Host}: ERROR {ex.GetType().Name}: {ex.Message} ({dnsWatch.ElapsedMilliseconds} ms)");
        }

        using var http = HttpClientProfiles.CreateTelemetryDiagnosticClient();
        Console.WriteLine();
        Console.WriteLine("GET leaderboard");
        var getReport = await TelemetryDiagnostics.ProbeGetAsync(
            http,
            endpoint,
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        PrintBackendDiagnosticReport(getReport);

        Console.WriteLine();
        Console.WriteLine("Production TelemetryClient GET");
        var productionSuccess = false;
        using (var productionHttp = HttpClientProfiles.CreateTelemetryClient())
        {
            var productionClient = new TelemetryClient(productionHttp, endpoint);
            var productionWatch = Stopwatch.StartNew();
            var productionResult = await productionClient.GetLeaderboardAsync(
                3,
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Console.WriteLine(
                $"Result: success={productionResult.Success} error={productionResult.Error ?? "none"} " +
                $"entries={productionResult.Entries.Count} elapsed={productionWatch.ElapsedMilliseconds} ms");
            productionSuccess = productionResult.Success;
        }

        TelemetryDiagnosticReport? postReport = null;
        if (includePost)
        {
            Console.WriteLine();
            Console.WriteLine("POST speedtest probe (invalid empty payload; no row is created)");
            postReport = await TelemetryDiagnostics.ProbePostAsync(
                http,
                endpoint,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            PrintBackendDiagnosticReport(postReport);
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("Add --post to trace doPost with a deliberately invalid, non-writing payload.");
        }

        return getReport.Completed && productionSuccess && (postReport?.Completed ?? true) ? 0 : 1;
    }

    private static void PrintBackendDiagnosticReport(TelemetryDiagnosticReport report)
    {
        foreach (var hop in report.Hops)
        {
            Console.WriteLine($"[{hop.Index}] START {hop.Method} {FormatDiagnosticUri(hop.Uri)}");
            if (hop.StatusCode is { } status)
            {
                Console.WriteLine(
                    $"[{hop.Index}] RESPONSE status={status} http={hop.HttpVersion ?? "?"} " +
                    $"elapsed={hop.Elapsed.TotalMilliseconds:0} ms content-type={hop.ContentType ?? "unknown"}");
                if (hop.Location is not null)
                {
                    Console.WriteLine($"[{hop.Index}] REDIRECT -> {FormatDiagnosticUri(hop.Location)}");
                }
                if (!string.IsNullOrWhiteSpace(hop.Body))
                {
                    Console.WriteLine($"[{hop.Index}] BODY {hop.Body}");
                }
            }
            if (hop.Error is not null)
            {
                Console.WriteLine($"[{hop.Index}] ERROR {hop.Error}: {hop.ErrorDetail}");
            }
        }
        Console.WriteLine(report.Completed
            ? "Completed: final HTTP response received."
            : $"Failed: {report.Error ?? "unknown"}.");
    }

    private static string FormatDiagnosticUri(Uri uri)
    {
        var value = uri.ToString();
        return value.Length <= 320 ? value : value[..320] + "…";
    }

    private static int PrintContract()
    {
        Console.WriteLine($"SSID prefix: {ProtocolContract.CampusSsidPrefix}");
        Console.WriteLine($"Automatic stepOne limit: {ProtocolContract.MaxAutomaticStepOneAttempts}");
        Console.WriteLine($"Push poll offsets (ms): {string.Join(",", ProtocolContract.PushPollOffsetsMilliseconds.ToArray())}");
        return 0;
    }

    private static async Task<int> RegisterAsync()
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        app.Logger.Write(DiagnosticLevel.Info, "cli.start command=register runtime=csharp");
        if (app.Secrets.Load() is not null)
        {
            Console.WriteLine("Устройство уже зарегистрировано. Для новой регистрации сначала выполните reset.");
            return 0;
        }

        Console.Write("Введите номер телефона: ");
        var phone = NormalizePhone(Console.ReadLine());
        var deviceId = app.DeviceIdentity.GetOrCreate();

        Console.WriteLine("Запрашиваю код подтверждения...");
        var requested = await app.Api.RequestConfirmationAsync(phone, deviceId).ConfigureAwait(false);
        if (!requested.IsSuccess)
        {
            app.Logger.Write(DiagnosticLevel.Warn, $"registration.failed stage=get-confirm failure={requested.Failure!.Kind}");
            throw new InvalidOperationException(DescribeApiFailure(requested.Failure));
        }

        Console.Write("Введите SMS-код: ");
        var smsCode = (Console.ReadLine() ?? string.Empty).Trim();
        if (smsCode.Length != 4 || smsCode.Any(c => c is < '0' or > '9'))
        {
            throw new InvalidOperationException("SMS-код должен состоять ровно из 4 цифр.");
        }

        var checkedCode = await app.Api.CheckConfirmationAsync(phone, smsCode, deviceId).ConfigureAwait(false);
        if (!checkedCode.IsSuccess)
        {
            app.Logger.Write(DiagnosticLevel.Warn, $"registration.failed stage=check-confirm failure={checkedCode.Failure!.Kind}");
            throw new InvalidOperationException(DescribeApiFailure(checkedCode.Failure));
        }

        var confirmation = checkedCode.Value!;
        app.Logger.Write(DiagnosticLevel.Info, "registration.confirmed mode=phone-only");

        var sessionResult = await app.Api.GetTokenAsync(
            confirmation.AuthId,
            deviceId).ConfigureAwait(false);
        if (!sessionResult.IsSuccess)
        {
            app.Logger.Write(DiagnosticLevel.Warn, $"registration.failed stage=get-token failure={sessionResult.Failure!.Kind}");
            throw new InvalidOperationException(DescribeApiFailure(sessionResult.Failure));
        }

        var session = sessionResult.Value!;
        app.Secrets.Save(new StoredSecrets(session.Token, phone));
        app.Session.Save(new SessionMetadata
        {
            DeviceId = deviceId,
            UserId = session.UserId,
            ProfileId = session.ProfileId,
            AccessBegin = session.AccessBegin,
            AccessEnd = session.AccessEnd,
            RegisteredAtUtc = DateTimeOffset.UtcNow
        });

        var osVersion = Environment.OSVersion.VersionString;
        var deviceModel = Environment.MachineName;
        var metadata = await app.Api.RegisterDeviceMetadataAsync(
            session.Token,
            new DeviceMetadataRegistration(deviceId, phone, osVersion, deviceModel)).ConfigureAwait(false);
        if (metadata.IsSuccess)
        {
            app.Json.Write(
                app.Paths.DeviceMetadataFile,
                new DeviceMetadataSnapshot(deviceId, deviceModel, osVersion, DateTimeOffset.UtcNow),
                AppJsonContext.Default.DeviceMetadataSnapshot);
        }
        else
        {
            app.Logger.Write(DiagnosticLevel.Warn, $"device-metadata failure={metadata.Failure?.Kind}");
        }

        app.Logger.Write(DiagnosticLevel.Info,
            $"registration.complete accessBegin={session.AccessBegin ?? ""} accessEnd={session.AccessEnd ?? ""}");
        Console.WriteLine("Регистрация завершена. Данные авторизации сохранены для текущего пользователя Windows.");
        if (!string.IsNullOrWhiteSpace(session.AccessEnd))
        {
            Console.WriteLine($"Сессия API действует до: {session.AccessEnd}");
        }
        Console.WriteLine("Теперь можно авторизовать Wi-Fi один раз сейчас или включить автоматическую авторизацию.");
        PromptAnonymousStatisticsConsentConsole();
        return 0;
    }

    private static async Task<int> ConnectAsync()
    {
        Console.WriteLine("Проверяю подключение и выполняю разовую авторизацию Wi-Fi...");
        var outcome = await RunManualAuthorizationAsync().ConfigureAwait(false);
        return PrintAuthorizationOutcome(outcome);
    }

    private static async Task<AuthorizationOutcome> RunManualAuthorizationAsync(
        Action<AuthorizationProgressStage>? progress = null)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        app.Logger.Write(DiagnosticLevel.Info, "cli.start command=connect runtime=csharp");
        var secrets = app.Secrets.Load() ?? throw new InvalidOperationException("Устройство не зарегистрировано. Сначала выполните register.");
        var deviceId = app.DeviceIdentity.GetOrCreate();

        return await app.Authorization.RunAsync(new AuthorizationRequest(
            secrets.Token,
            secrets.Phone,
            deviceId,
            AuthorizationAttemptReason.Manual,
            Force: true,
            Progress: progress)).ConfigureAwait(false);
    }

    private static int PrintAuthorizationOutcome(AuthorizationOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case AuthorizationOutcomeKind.Success:
                Console.WriteLine(outcome.InternetConfirmed == true
                    ? "Wi-Fi авторизация завершена, Интернет подтверждён."
                    : "Wi-Fi авторизация принята; Интернет пока не подтверждён проверкой.");
                return 0;
            case AuthorizationOutcomeKind.AlreadyAuthorized:
                Console.WriteLine("Wi-Fi уже авторизован сервером.");
                return 0;
            case AuthorizationOutcomeKind.AlreadyOnline:
                Console.WriteLine("Интернет уже доступен; авторизация не требуется.");
                return 0;
            case AuthorizationOutcomeKind.WrongWifi:
                Console.Error.WriteLine($"Авторизация работает только в сети {ProtocolContract.CampusSsidPrefix}*. Подключитесь к кампусной сети.");
                return 2;
            case AuthorizationOutcomeKind.Busy:
                Console.Error.WriteLine("Другая Wi-Fi авторизация уже выполняется.");
                return 2;
            case AuthorizationOutcomeKind.BearerInvalid:
                Console.Error.WriteLine("Bearer отклонён сервером. Выполните reset и register заново.");
                return 1;
            case AuthorizationOutcomeKind.RetryableBeforeStepOne:
                Console.Error.WriteLine("Сервер Интерсвязи временно недоступен до отправки captive-запроса. Попробуйте ещё раз.");
                return 1;
            case AuthorizationOutcomeKind.RetryableStepOne:
                Console.Error.WriteLine("Captive portal временно не завершил stepOne. Попробуйте ещё раз.");
                return 1;
            case AuthorizationOutcomeKind.StepTwoAmbiguous:
                Console.Error.WriteLine("Ответ stepTwo потерян, а Интернет не подтвердился. Код повторно не отправлялся; требуется действие пользователя.");
                return 1;
            case AuthorizationOutcomeKind.UserActionRequired:
                Console.Error.WriteLine("Автоматическое продолжение остановлено: требуется действие пользователя. Подробности есть в diagnostic.log.");
                return 1;
            case AuthorizationOutcomeKind.Cancelled:
                Console.Error.WriteLine("Операция отменена.");
                return 1;
            default:
                Console.Error.WriteLine($"Неожиданный результат: {outcome.Kind}");
                return 1;
        }
    }

    private static async Task<int> PrintStatusAsync()
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        app.Logger.Write(DiagnosticLevel.Info, "cli.start command=status runtime=csharp");
        var secrets = app.Secrets.Load();
        var session = app.Session.Load();
        var state = app.RuntimeState.Load();
        var internet = await app.Internet.ProbeAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("=== IS74 Automatic Wi-Fi ===");
        var installation = new ProgramInstallation();
        var installedVersion = installation.ReadInstalledVersion();
        var installedAppRegistration = new WindowsInstalledAppRegistration();
        var staleAutostartRemoved = app.Autostart.RemoveIfStale(installation.ExecutablePath);
        var installedAppRegistrationChanged = installedAppRegistration.Reconcile(
            installation,
            installedVersion ?? ProductVersion);

        Console.WriteLine($"Версия               : {ProductVersion}");
        Console.WriteLine($"Установка            : {(installation.IsInstalled ? "есть" : "нет")}");
        if (installation.IsInstalled && !string.IsNullOrWhiteSpace(installedVersion) &&
            !string.Equals(installedVersion, ProductVersion, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Установленная версия : {installedVersion}");
        }
        Console.WriteLine($"Данные регистрации   : {(secrets is null ? "нет" : "сохранены")}");
        Console.WriteLine($"Телефон              : {MaskPhone(secrets?.Phone)}");
        var automaticAuthorizationEnabled = app.Autostart.IsEnabledFor(installation.ExecutablePath);
        var agentRunning = AgentProcessControl.IsAgentRunning();
        Console.WriteLine($"Автоматическая авторизация : {(automaticAuthorizationEnabled ? "включена" : "выключена")}");
        Console.WriteLine($"Фоновый режим              : {(agentRunning ? "работает" : "остановлен")}");
        Console.WriteLine($"Уведомления                : {FormatNotificationMode(app.Settings.NotificationMode)}");
        var updateMaintenance = new UpdateMaintenanceService(ProductVersion, app.Paths, app.Json, app.Logger);
        var updateState = updateMaintenance.LoadState();
        Console.WriteLine($"Режим обновлений           : {(app.Settings.AutomaticUpdates ? "автоматически" : "уведомлять")}");
        Console.WriteLine($"Канал обновлений            : {(updateMaintenance.IncludePrereleases(app.Settings) ? "обычные + Pre-release" : "обычные версии")}");
        if (!string.IsNullOrWhiteSpace(updateState.AvailableVersion))
        {
            Console.WriteLine($"Доступное обновление        : {updateState.AvailableVersion}");
        }
        if (updateState.LastCheckedUtc is { } lastUpdateCheck)
        {
            Console.WriteLine($"Последняя проверка обновлений: {lastUpdateCheck.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        }
        Console.WriteLine($"Запуск вместе с Windows    : {(automaticAuthorizationEnabled ? "включён" : "выключен")}");
        Console.WriteLine($"Интернет                     : {(internet.Online ? "доступен" : "не подтверждён")}");
        var displayedSsid = GetDisplayedWifiSsid();
        Console.WriteLine($"Сеть                         : {(app.Settings.IgnoreNetworkCheck ? "проверка отключена ○" : FormatWifiNetwork(displayedSsid))}");
        Console.WriteLine($"Авторизация                  : {FormatWifiAuthorization(state)}");
        if (!string.IsNullOrWhiteSpace(session?.AccessEnd))
        {
            Console.WriteLine($"API-сессия  : до {session.AccessEnd}");
        }
        if (state.LastAuthUtc is { } lastAuth)
        {
            Console.WriteLine($"Последняя Wi-Fi авторизация : {lastAuth.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        }
        if (state.ExpectedExpiryUtc is { } expiry)
        {
            Console.WriteLine($"Ожидаемое окончание окна    : {expiry.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        }
        if (!string.IsNullOrWhiteSpace(state.LastResult))
        {
            Console.WriteLine($"Последний результат          : {FormatRuntimeResultForUi(state.LastResult)}");
        }
        if (state.AutomaticStepOneAttempts > 0)
        {
            Console.WriteLine($"Автоматические попытки       : {state.AutomaticStepOneAttempts}/{app.Settings.MaxAutomaticStepOneAttempts}");
        }
        if (state.UserActionRequired)
        {
            Console.WriteLine("Требуется действие           : да — автоматические попытки остановлены");
        }
        var telemetryStatus = app.TelemetryQueue.GetStatus();
        Console.WriteLine($"Анонимная статистика         : {FormatAnonymousStatisticsConsent(app.Settings.AnonymousStatisticsConsent)}");
        Console.WriteLine($"Очередь телеметрии           : {telemetryStatus.PendingFiles} файлов / {FormatByteCount(telemetryStatus.PendingBytes)}");
        if (telemetryStatus.RejectedFiles > 0)
        {
            Console.WriteLine($"Отклонённые trace-файлы      : {telemetryStatus.RejectedFiles} / {FormatByteCount(telemetryStatus.RejectedBytes)}");
        }
        if (staleAutostartRemoved)
        {
            Console.WriteLine("Обслуживание                 : удалена устаревшая запись автозапуска");
            app.Logger.Write(DiagnosticLevel.Info, "autostart.stale-entry removed=true");
        }
        if (installedAppRegistrationChanged)
        {
            Console.WriteLine(installation.IsInstalled
                ? "Обслуживание                 : запись в установленных приложениях восстановлена"
                : "Обслуживание                 : устаревшая запись установленного приложения удалена");
            app.Logger.Write(DiagnosticLevel.Info, $"installed-app.registration reconciled=true installed={installation.IsInstalled}");
        }
        if (installation.IsInstalled)
        {
            Console.WriteLine($"Установленная программа      : {installation.ExecutablePath}");
        }
        Console.WriteLine($"Данные приложения            : {app.Paths.Root}");
        Console.WriteLine($"Диагностический журнал       : {app.Paths.DiagnosticLogFile}");
        Console.WriteLine();
        return 0;
    }

    private static int InstallApplication()
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
        {
            throw new InvalidOperationException("Не удалось определить путь к IS74Wifi.exe.");
        }

        var installation = new ProgramInstallation();
        if (installation.IsInstalledExecutable(current))
        {
            new WindowsInstalledAppRegistration().Register(installation, ProductVersion);
            Console.WriteLine($"IS74Wifi уже установлена: {installation.ExecutablePath}");
            return 0;
        }

        InstallOrUpgradeCanonicalCopy(current, installation);
        Console.WriteLine("IS74Wifi установлена для текущего пользователя Windows.");
        Console.WriteLine($"Рабочая копия программы: {installation.ExecutablePath}");
        Console.WriteLine("Автоматическая авторизация пока не включена.");
        Console.WriteLine("Скачанный EXE теперь можно переместить или удалить.");
        return 0;
    }

    private static int EnableAutomaticAuthorization(bool quiet = false, Action<string>? progress = null)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        if (app.Secrets.Load() is null)
        {
            throw new InvalidOperationException("Сначала зарегистрируйте устройство.");
        }
        ReportMenuBatchProgress(progress, "Регистрация устройства проверена");

        var installation = new ProgramInstallation();
        if (!installation.IsInstalled)
        {
            throw new InvalidOperationException("IS74Wifi не установлена. Сначала запустите программу обычным способом и выполните установку.");
        }
        ReportMenuBatchProgress(progress, "Установленная копия найдена");

        new WindowsInstalledAppRegistration().Register(installation, installation.ReadInstalledVersion() ?? ProductVersion);
        ReportMenuBatchProgress(progress, "Запись программы в Windows обновлена");
        app.Autostart.Enable(installation.ExecutablePath, startNow: true);
        ReportMenuBatchProgress(progress, "Автозапуск включён, фоновый режим запущен");
        app.Logger.Write(DiagnosticLevel.Info, "autostart.enabled mode=hkcu-run installed-copy=true");
        if (!quiet)
        {
            Console.WriteLine("Автоматическая авторизация включена.");
            Console.WriteLine("Фоновый режим запущен и сразу проверит текущее подключение.");
            Console.WriteLine("Если вы подключены к Campus Wi-Fi и требуется авторизация, программа попробует выполнить её автоматически.");
            Console.WriteLine("Фоновый режим будет автоматически запускаться при входе в Windows.");
        }
        return 0;
    }

    private static void ReportMenuBatchProgress(Action<string>? progress, string message)
    {
        try
        {
            progress?.Invoke(message);
        }
        catch
        {
            // Interactive progress rendering is best-effort only.
        }
    }

    private static int DisableAutostart(bool quiet = false, Action<string>? progress = null)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        app.Autostart.Disable();
        ReportMenuBatchProgress(progress, "Автозапуск отключён, фоновый режим остановлен");
        app.Logger.Write(DiagnosticLevel.Info, "autostart.disabled mode=hkcu-run");
        if (!quiet)
        {
            Console.WriteLine("Автоматическая авторизация отключена.");
            Console.WriteLine("Фоновый режим остановлен.");
            Console.WriteLine("Программа больше не будет запускаться автоматически вместе с Windows.");
            Console.WriteLine("Разовая авторизация Wi-Fi по-прежнему доступна через пункт 2.");
        }
        return 0;
    }

    private static int ResetRegistration(bool quiet = false, Action<string>? progress = null)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        app.Autostart.Disable();
        ReportMenuBatchProgress(progress, "Автозапуск отключён, фоновый режим остановлен");
        app.Maintenance.ResetRegistration();
        ReportMenuBatchProgress(progress, "Регистрация и локальная Wi-Fi сессия удалены");
        app.Logger.Write(DiagnosticLevel.Info, "registration.reset");
        if (!quiet)
        {
            Console.WriteLine("Регистрация и локальная Wi-Fi сессия удалены. Настройки и диагностические логи сохранены.");
        }
        return 0;
    }

    private static int Purge()
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        app.Autostart.Disable();
        app.Maintenance.PurgeAllData();
        Console.WriteLine("Автозапуск отключён, все локальные данные приложения удалены.");
        return 0;
    }

    private static int Uninstall(bool quiet = false, Action<string>? progress = null)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        app.Autostart.Disable();
        ReportMenuBatchProgress(progress, "Автозапуск отключён, фоновый режим остановлен");
        app.Maintenance.PurgeAllData();
        ReportMenuBatchProgress(progress, "Локальные данные удалены");

        var installation = new ProgramInstallation();
        new WindowsInstalledAppRegistration().Unregister();
        ReportMenuBatchProgress(progress, "Запись программы в Windows удалена");
        var current = Environment.ProcessPath;
        if (!installation.IsInstalled)
        {
            if (!quiet) Console.WriteLine("Автоматическая авторизация отключена, локальные данные и запись программы в Windows удалены.");
            return 0;
        }

        if (!installation.IsInstalledExecutable(current))
        {
            installation.DeleteInstalledFilesIfNotRunning(current);
            ReportMenuBatchProgress(progress, "Установленная копия удалена");
            if (!quiet) Console.WriteLine("Программа полностью удалена: автозапуск, локальные данные, установленная копия и запись в Windows очищены.");
            return 0;
        }

        ScheduleDeferredUninstall(installation);
        ReportMenuBatchProgress(progress, "Удаление запущенного EXE запланировано после выхода");
        if (!quiet)
        {
            Console.WriteLine("Автозапуск, локальные данные и запись программы в Windows удалены.");
            Console.WriteLine("Установленная копия программы будет удалена после закрытия текущего процесса.");
        }
        return 0;
    }

    private static int OpenLogs(bool quiet = false)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        app.Paths.EnsureDirectories();
        if (!quiet)
        {
            Console.WriteLine($"Журнал: {app.Paths.DiagnosticLogFile}");
            Console.WriteLine("Для диагностики можно прислать diagnostic.log и diagnostic.N.log из этой папки.");
        }
        try
        {
            _ = Process.Start(new ProcessStartInfo(app.Paths.LogDirectory) { UseShellExecute = true });
        }
        catch
        {
        }
        return 0;
    }

    private static bool BootstrapInteractiveLaunch(out int exitCode)
    {
        exitCode = 0;
        if (IsLocalRunRequested())
        {
            return false;
        }

        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
        {
            throw new InvalidOperationException("Не удалось определить путь к IS74Wifi.exe.");
        }

        var installation = new ProgramInstallation();
        if (installation.IsInstalledExecutable(current))
        {
            return false;
        }

        var installedVersionText = installation.ReadInstalledVersion();
        if (installation.IsInstalled &&
            SemanticVersion.TryParse(ProductVersion, out var currentVersion) &&
            SemanticVersion.TryParse(installedVersionText, out var installedVersion) &&
            installedVersion.CompareTo(currentVersion) >= 0)
        {
            return false;
        }

        var wasInstalled = installation.IsInstalled;
        var ui = new InteractiveTerminalUi(ProductVersion);
        var proceed = ui.ConfirmInstallOrUpgradeAsync(
                upgrade: wasInstalled,
                installedVersion: installedVersionText,
                installDirectory: installation.InstallDirectory)
            .GetAwaiter()
            .GetResult();

        if (!proceed)
        {
            Console.Clear();
            Console.WriteLine(wasInstalled ? "Обновление отменено." : "Установка отменена.");
            exitCode = 0;
            return true;
        }

        InstallOrUpgradeCanonicalCopy(current, installation);
        ui.ShowBusyMessage(
            "ГОТОВО",
            wasInstalled
                ? $"Установленная копия обновлена до {ProductVersion}."
                : "IS74W установлена для текущего пользователя Windows.",
            GetInteractiveStatusSnapshot());

        // The downloaded bootstrap process now forwards to the canonical copy.
        // Keep the header in its settled state instead of replaying the full reveal twice.
        forwardMenuWithoutReveal = true;
        return false;
    }

    private static void InstallOrUpgradeCanonicalCopy(string sourceExecutable, ProgramInstallation installation)
    {
        var autostart = new WindowsAutostartService();
        var hadInstalledCopy = installation.IsInstalled;
        var automaticAuthorizationWasEnabled = hadInstalledCopy && autostart.IsEnabledFor(installation.ExecutablePath);

        if (hadInstalledCopy)
        {
            AgentProcessControl.StopAgentOrThrow();
        }
        else
        {
            _ = autostart.RemoveIfStale(installation.ExecutablePath);
        }

        var installedExecutable = installation.InstallFrom(sourceExecutable, ProductVersion);
        new WindowsInstalledAppRegistration().Register(installation, ProductVersion);

        if (automaticAuthorizationWasEnabled)
        {
            autostart.Enable(installedExecutable, startNow: true);
        }
    }

    private static bool IsLocalRunRequested()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("IS74W_RUN_LOCAL"),
            "1",
            StringComparison.Ordinal);
    }

    private static bool ReadYesAnswer(string? value)
    {
        var answer = (value ?? string.Empty).Trim();
        return answer.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
               answer.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
               answer.Equals("Д", StringComparison.OrdinalIgnoreCase) ||
               answer.Equals("ДА", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryForwardToInstalledCopy(
        string[] args,
        string command,
        bool waitForExit,
        out int exitCode)
    {
        exitCode = 0;
        if (IsLocalRunRequested())
        {
            return false;
        }

        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current)) return false;

        var installation = new ProgramInstallation();
        if (!installation.IsInstalled || installation.IsInstalledExecutable(current)) return false;
        if (!SemanticVersion.TryParse(ProductVersion, out var currentVersion) ||
            !SemanticVersion.TryParse(installation.ReadInstalledVersion(), out var installedVersion) ||
            installedVersion.CompareTo(currentVersion) < 0)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(installation.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = command == "agent"
        };
        if (command == "menu" && forwardMenuWithoutReveal)
        {
            startInfo.Environment["IS74W_SKIP_REVEAL"] = "1";
        }
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        if (args.Length == 0)
        {
            startInfo.ArgumentList.Add("menu");
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить установленную копию IS74Wifi.");
        if (waitForExit)
        {
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        return true;
    }

    private static async Task<int> CheckForUpdatesAsync()
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        var maintenance = new UpdateMaintenanceService(ProductVersion, app.Paths, app.Json, app.Logger);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var updater = new GitHubUpdateClient(http);
        var result = await maintenance.CheckAsync(updater, force: true).ConfigureAwait(false);
        var update = result.Descriptor;

        if (update is null)
        {
            Console.WriteLine($"Обновлений нет. Текущая версия: {ProductVersion}");
            return 0;
        }

        Console.WriteLine($"Доступна новая версия: {update.TagName}");
        Console.WriteLine($"Текущая версия       : {ProductVersion}");
        Console.WriteLine($"Страница релиза      : {update.ReleasePageUrl}");
        return 0;
    }

    private static async Task<int> UpdateCommandAsync()
    {
        await UpdateAsync(restartMenu: false, askConfirmation: false).ConfigureAwait(false);
        return 0;
    }

    private static async Task<bool> UpdateAsync(
        bool restartMenu,
        bool askConfirmation = true,
        bool quiet = false,
        Action<UpdateProgressStage>? downloadProgress = null,
        Action<UpdateTransferProgress>? transferProgress = null,
        Action<UpdateApplyProgressStage>? applyProgress = null)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        var maintenance = new UpdateMaintenanceService(ProductVersion, app.Paths, app.Json, app.Logger);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var updater = new GitHubUpdateClient(http);
        var update = (await maintenance.CheckAsync(
            updater,
            force: true,
            progress: downloadProgress).ConfigureAwait(false)).Descriptor;

        if (update is null)
        {
            if (!quiet) Console.WriteLine($"Обновлений нет. Текущая версия: {ProductVersion}");
            return false;
        }

        if (!quiet)
        {
            Console.WriteLine($"Доступна новая версия: {update.TagName}");
            Console.WriteLine($"Текущая версия       : {ProductVersion}");
        }
        if (askConfirmation)
        {
            Console.Write("Скачать и установить обновление? [Y/N]: ");
            if (!ReadYesAnswer(Console.ReadLine()))
            {
                Console.WriteLine("Обновление отменено.");
                return false;
            }
        }

        return await PrepareAndScheduleUpdateAsync(
            updater,
            update,
            restartMenu,
            quiet,
            downloadProgress,
            transferProgress,
            applyProgress).ConfigureAwait(false);
    }

    private static async Task<bool> PrepareAndScheduleUpdateAsync(
        GitHubUpdateClient updater,
        UpdateDescriptor update,
        bool restartMenu,
        bool quiet,
        Action<UpdateProgressStage>? downloadProgress = null,
        Action<UpdateTransferProgress>? transferProgress = null,
        Action<UpdateApplyProgressStage>? applyProgress = null,
        Func<Task<bool>>? confirmApply = null,
        Func<bool>? canApply = null)
    {
        if (!quiet) Console.WriteLine("Скачиваю обновление с GitHub Releases и проверяю SHA-256...");
        var prepared = await updater.DownloadAndVerifyAsync(update, downloadProgress, transferProgress).ConfigureAwait(false);
        var shouldRestartAgentOnFailure = false;
        string? installedExecutableForRestart = null;

        try
        {
            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.ValidatingExecutable);
            ValidatePreparedExecutable(prepared.ExecutablePath);
            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.ExecutableValidated);

            if (canApply is not null && !canApply())
            {
                GitHubUpdateClient.TryDeleteDirectory(prepared.WorkingDirectory);
                return false;
            }

            using var app = ApplicationRuntime.Create(ProductVersion);
            var currentExecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExecutable) || !File.Exists(currentExecutable))
                throw new InvalidOperationException("Не удалось определить текущий IS74Wifi.exe.");

            var installation = new ProgramInstallation();
            _ = app.Autostart.RemoveIfStale(installation.ExecutablePath);
            var autostartWasEnabled = app.Autostart.IsEnabledFor(installation.ExecutablePath);

            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.StoppingAgent);
            AgentProcessControl.StopAgentOrThrow();
            shouldRestartAgentOnFailure = autostartWasEnabled;
            installedExecutableForRestart = installation.ExecutablePath;
            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.AgentStopped);

            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.PreparingInstalledCopy);
            var installedExecutable = installation.InstallFrom(currentExecutable, ProductVersion);
            new WindowsInstalledAppRegistration().Register(installation, ProductVersion);
            if (autostartWasEnabled)
            {
                app.Autostart.Enable(installedExecutable, startNow: false);
            }
            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.InstalledCopyPrepared);

            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.SchedulingReplacement);
            var helperPath = Path.Combine(prepared.WorkingDirectory, "IS74Wifi-updater.exe");
            // Run the apply phase with the already verified NEW executable.
            // This lets updater fixes ship with the update itself instead of
            // being delayed until the following release.
            File.Copy(prepared.ExecutablePath, helperPath, overwrite: true);
            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.ReplacementScheduled);

            if (confirmApply is not null && !await confirmApply().ConfigureAwait(false))
            {
                if (autostartWasEnabled)
                {
                    try
                    {
                        StartInstalledAgent(installedExecutable);
                        shouldRestartAgentOnFailure = false;
                    }
                    catch (Exception restartEx)
                    {
                        app.Logger.Write(DiagnosticLevel.Warn,
                            $"update.cancel agent-restart warning type={restartEx.GetType().Name} message={restartEx.Message}");
                    }
                }

                GitHubUpdateClient.TryDeleteDirectory(prepared.WorkingDirectory);
                return false;
            }

            var startInfo = new ProcessStartInfo(helperPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("update-apply");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(prepared.ExecutablePath);
            startInfo.ArgumentList.Add(installedExecutable);
            startInfo.ArgumentList.Add(update.TagName);
            startInfo.ArgumentList.Add(prepared.WorkingDirectory);
            startInfo.ArgumentList.Add(autostartWasEnabled ? "1" : "0");
            startInfo.ArgumentList.Add(restartMenu ? "1" : "0");

            using var helperProcess = Process.Start(startInfo) ??
                throw new InvalidOperationException("Не удалось запустить процесс применения обновления.");
            app.Logger.Write(DiagnosticLevel.Info,
                $"update.helper started pid={helperProcess.Id} targetVersion={update.TagName}");
            if (helperProcess.WaitForExit(750))
            {
                app.Logger.Write(DiagnosticLevel.Error,
                    $"update.helper exited-before-handoff pid={helperProcess.Id} exitCode={helperProcess.ExitCode}");
                throw new InvalidOperationException(
                    $"Модуль установки обновления завершился до передачи управления (код {helperProcess.ExitCode}).");
            }
            app.Logger.Write(DiagnosticLevel.Info,
                $"update.scheduled from={ProductVersion} to={update.TagName} autostart={autostartWasEnabled}");
            shouldRestartAgentOnFailure = false;

            if (!quiet)
            {
                Console.WriteLine($"Обновление {update.TagName} проверено и подготовлено.");
                Console.WriteLine($"Установленная программа: {installedExecutable}");
                Console.WriteLine("Текущий процесс завершится, после чего EXE будет заменён.");
            }
            return true;
        }
        catch
        {
            if (shouldRestartAgentOnFailure &&
                !string.IsNullOrWhiteSpace(installedExecutableForRestart) &&
                File.Exists(installedExecutableForRestart))
            {
                try
                {
                    StartInstalledAgent(installedExecutableForRestart);
                }
                catch
                {
                    // Preserve the original update failure. The next normal
                    // application launch can still restore the background mode.
                }
            }
            GitHubUpdateClient.TryDeleteDirectory(prepared.WorkingDirectory);
            throw;
        }
    }

    private static void ReportUpdateApplyProgress(
        Action<UpdateApplyProgressStage>? progress,
        UpdateApplyProgressStage stage)
    {
        try
        {
            progress?.Invoke(stage);
        }
        catch
        {
            // Rendering progress is best-effort and must not interfere with applying an update.
        }
    }

    private static void ValidatePreparedExecutable(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("version");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить скачанную версию IS74Wifi.");
        if (!process.WaitForExit(15000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("Скачанная версия IS74Wifi не завершила проверочный запуск.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Скачанная версия IS74Wifi не прошла проверочный запуск (код {process.ExitCode}).");
    }

    private static int ApplyPreparedUpdate(string[] args)
    {
        var logger = new DiagnosticLogger(new AppPaths());
        logger.Write(DiagnosticLevel.Info, $"update.apply entry argCount={args.Length}");
        if (args.Length != 7 ||
            !int.TryParse(args[0], out var parentPid))
        {
            logger.Write(DiagnosticLevel.Error, "update.apply rejected invalid arguments");
            return 2;
        }

        var source = Path.GetFullPath(args[1]);
        var target = Path.GetFullPath(args[2]);
        var version = args[3];
        var workingDirectory = Path.GetFullPath(args[4]);
        var restartAgent = args[5] == "1";
        var restartMenu = args[6] == "1";
        var installation = new ProgramInstallation();

        if (!ProgramInstallation.PathsEqual(target, installation.ExecutablePath) ||
            !IsPathInside(source, workingDirectory))
        {
            logger.Write(DiagnosticLevel.Error, "update.apply rejected invalid source/target path");
            return 2;
        }

        logger.Write(DiagnosticLevel.Info,
            $"update.apply start targetVersion={version} parentPid={parentPid} restartAgent={restartAgent} restartMenu={restartMenu}");

        if (!WaitForProcessExit(parentPid))
        {
            logger.Write(DiagnosticLevel.Error, "update.apply failed waiting for parent process exit");
            return 3;
        }

        var ui = new InteractiveTerminalUi(version);
        var history = new InteractiveActionHistory();
        history.AddSuccess("Основная программа закрыта; терминал передан модулю обновления");

        void RenderProgress()
        {
            try
            {
                ui.ShowActionProgress("УСТАНОВКА ОБНОВЛЕНИЯ", history, GetInteractiveStatusSnapshot());
            }
            catch
            {
                // Applying the update must not depend on progress rendering.
            }
        }

        var backupPath = Path.Combine(workingDirectory, "IS74Wifi-previous.exe");
        var previousVersion = installation.ReadInstalledVersion();
        var updateApplied = false;
        var previousCopyAvailable = false;
        Exception? applyFailure = null;

        try
        {
            history.Start("Проверяю, что фоновый режим остановлен...");
            RenderProgress();
            AgentProcessControl.StopAgentOrThrow(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            history.CompleteActive("Фоновый режим остановлен");
            RenderProgress();

            Directory.CreateDirectory(installation.InstallDirectory);
            if (File.Exists(installation.ExecutablePath))
            {
                history.Start("Сохраняю предыдущую версию для отката...");
                RenderProgress();
                CopyFileWithRetry(installation.ExecutablePath, backupPath, overwrite: true, attempts: 12, delayMilliseconds: 250);
                previousCopyAvailable = true;
                history.CompleteActive("Предыдущая версия сохранена");
                RenderProgress();
            }

            history.Start("Заменяю установленный IS74Wifi.exe...");
            RenderProgress();
            ReplaceInstalledExecutableWithRetry(source, installation.ExecutablePath, attempts: 24, delayMilliseconds: 250);
            history.CompleteActive("IS74Wifi.exe заменён");
            RenderProgress();

            history.Start("Обновляю сведения об установленной версии...");
            RenderProgress();
            installation.WriteVersionMarker(version);
            new UpdateMaintenanceService(version, new AppPaths(), new JsonFileStore(), logger)
                .MarkInstalled(version, notify: !restartMenu);
            history.CompleteActive($"Установлена версия {version}");
            RenderProgress();

            // The executable and its version marker are the atomic update
            // boundary. Shell registration and agent restart are recoverable
            // follow-up tasks and must not roll back a valid new binary.
            updateApplied = true;

            try
            {
                new WindowsInstalledAppRegistration().Register(installation, version);
                history.AddSuccess("Запись программы в Windows обновлена");
            }
            catch (Exception registrationEx)
            {
                history.AddWarning($"Не удалось обновить запись программы в Windows: {registrationEx.Message}");
                logger.Write(DiagnosticLevel.Warn,
                    $"update.apply registration warning type={registrationEx.GetType().Name} message={registrationEx.Message}");
            }
            RenderProgress();

            if (restartAgent)
            {
                history.Start("Запускаю фоновый режим...");
                RenderProgress();
                try
                {
                    StartInstalledAgent(installation.ExecutablePath);
                    history.CompleteActive("Фоновый режим запущен");
                }
                catch (Exception agentEx)
                {
                    history.WarnActive($"Обновление установлено, но фоновый процесс не запущен: {agentEx.Message}");
                    logger.Write(DiagnosticLevel.Warn,
                        $"update.apply agent-restart warning type={agentEx.GetType().Name} message={agentEx.Message}");
                }
                RenderProgress();
            }

            logger.Write(DiagnosticLevel.Info, $"update.apply complete targetVersion={version}");
        }
        catch (Exception ex)
        {
            applyFailure = ex;
            history.FailActive("Не удалось завершить замену программы");
            history.AddError(ex.Message);
            logger.Write(DiagnosticLevel.Error,
                $"update.apply failed targetVersion={version} type={ex.GetType().Name} message={ex.Message}");

            if (previousCopyAvailable)
            {
                try
                {
                    history.Start("Восстанавливаю предыдущую версию...");
                    RenderProgress();
                    ReplaceInstalledExecutableWithRetry(backupPath, installation.ExecutablePath, attempts: 24, delayMilliseconds: 250);
                    if (!string.IsNullOrWhiteSpace(previousVersion))
                    {
                        installation.WriteVersionMarker(previousVersion);
                        try
                        {
                            new WindowsInstalledAppRegistration().Register(installation, previousVersion);
                        }
                        catch
                        {
                        }
                    }
                    else
                    {
                        try
                        {
                            if (File.Exists(installation.VersionMarkerPath)) File.Delete(installation.VersionMarkerPath);
                        }
                        catch
                        {
                        }
                    }
                    history.CompleteActive("Предыдущая версия восстановлена");
                    logger.Write(DiagnosticLevel.Warn, "update.apply rollback complete");
                }
                catch (Exception rollbackEx)
                {
                    history.FailActive("Не удалось автоматически восстановить предыдущую версию");
                    history.AddError(rollbackEx.Message);
                    logger.Write(DiagnosticLevel.Error,
                        $"update.apply rollback failed type={rollbackEx.GetType().Name} message={rollbackEx.Message}");
                }
            }

            if (restartAgent && File.Exists(installation.ExecutablePath))
            {
                try
                {
                    StartInstalledAgent(installation.ExecutablePath);
                    history.AddInfo("Фоновый режим предыдущей версии снова запущен");
                }
                catch (Exception restartEx)
                {
                    history.AddWarning($"Не удалось перезапустить фоновый процесс: {restartEx.Message}");
                }
            }
            RenderProgress();
        }

        if (restartMenu && File.Exists(installation.ExecutablePath))
        {
            if (!updateApplied)
            {
                try
                {
                    ui.ShowActionHistoryAsync(
                            "ОБНОВЛЕНИЕ НЕ УСТАНОВЛЕНО",
                            history,
                            GetInteractiveStatusSnapshot(),
                            dismissHint: "↑ ↓ история   Enter / Esc — вернуться в программу")
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                }
            }
            else
            {
                history.AddSuccess("Запускаю обновлённую программу...");
                RenderProgress();
                Thread.Sleep(450);
            }

            try
            {
                StartInstalledMenu(
                    installation.ExecutablePath,
                    updateApplied ? "success" : "failure",
                    version,
                    applyFailure?.Message);
            }
            catch (Exception restartEx)
            {
                logger.Write(DiagnosticLevel.Error,
                    $"update.apply menu-restart failed type={restartEx.GetType().Name} message={restartEx.Message}");
                if (updateApplied)
                {
                    history.AddWarning("Обновление установлено, но программу не удалось запустить автоматически");
                    history.AddError(restartEx.Message);
                    try
                    {
                        ui.ShowActionHistoryAsync(
                                "ОБНОВЛЕНИЕ УСТАНОВЛЕНО",
                                history,
                                GetInteractiveStatusSnapshot(),
                                dismissHint: "Enter / Esc — закрыть модуль обновления")
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch
                    {
                    }
                }
            }
        }

        ScheduleDirectoryCleanup(workingDirectory);
        return updateApplied ? 0 : 4;
    }

    private static void ReplaceInstalledExecutableWithRetry(
        string source,
        string target,
        int attempts,
        int delayMilliseconds)
    {
        var staged = target + ".new";
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                try
                {
                    if (File.Exists(staged)) File.Delete(staged);
                }
                catch
                {
                }

                File.Copy(source, staged, overwrite: true);
                File.Move(staged, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt < attempts)
                {
                    Thread.Sleep(delayMilliseconds);
                }
            }
        }

        throw new IOException(
            $"Не удалось заменить {Path.GetFileName(target)} после {attempts} попыток.",
            lastError);
    }

    private static void CopyFileWithRetry(
        string source,
        string target,
        bool overwrite,
        int attempts,
        int delayMilliseconds)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt < attempts)
                {
                    Thread.Sleep(delayMilliseconds);
                }
            }
        }

        throw new IOException(
            $"Не удалось скопировать {Path.GetFileName(source)} после {attempts} попыток.",
            lastError);
    }

    private static void StartInstalledAgent(string executablePath)
    {
        var agent = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        agent.ArgumentList.Add("agent");
        _ = Process.Start(agent) ?? throw new InvalidOperationException("Не удалось запустить фоновый процесс после обновления.");
    }

    private static void StartInstalledMenu(
        string executablePath,
        string result,
        string version,
        string? message)
    {
        var menu = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = false
        };
        menu.ArgumentList.Add("menu");
        menu.Environment["IS74W_SKIP_REVEAL"] = "1";
        menu.Environment["IS74W_UPDATE_RESULT"] = result;
        menu.Environment["IS74W_UPDATE_VERSION"] = version;
        if (!string.IsNullOrWhiteSpace(message))
        {
            menu.Environment["IS74W_UPDATE_MESSAGE"] = message;
        }
        _ = Process.Start(menu) ?? throw new InvalidOperationException("Не удалось перезапустить IS74Wifi после обновления.");
    }

    private static void ScheduleDeferredUninstall(ProgramInstallation installation)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к IS74Wifi.exe.");
        var work = Path.Combine(Path.GetTempPath(), "IS74Wifi-uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var helper = Path.Combine(work, "IS74Wifi-uninstaller.exe");
        File.Copy(current, helper, overwrite: true);

        var startInfo = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("uninstall-apply");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(installation.InstallDirectory);
        startInfo.ArgumentList.Add(work);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить завершение удаления программы.");
    }

    private static int ApplyDeferredUninstall(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[0], out var parentPid))
            return 2;

        var installDirectory = Path.GetFullPath(args[1]);
        var work = Path.GetFullPath(args[2]);
        var installation = new ProgramInstallation();
        if (!ProgramInstallation.PathsEqual(installDirectory, installation.InstallDirectory))
            return 2;

        if (!WaitForProcessExit(parentPid, TimeSpan.FromSeconds(30)))
            return 3;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(installDirectory)) Directory.Delete(installDirectory, recursive: true);
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }

        ScheduleDirectoryCleanup(work);
        return Directory.Exists(installDirectory) ? 3 : 0;
    }

    private static bool WaitForProcessExit(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit();
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        if (processId <= 0 || processId == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool IsPathInside(string candidate, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(candidate);
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanupStaleUpdateDirectories()
    {
        try
        {
            var temp = Path.GetTempPath();
            foreach (var directory in Directory.EnumerateDirectories(temp, "IS74Wifi-update-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddHours(-1))
                        Directory.Delete(directory, recursive: true);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static void ScheduleDirectoryCleanup(string directory)
    {
        try
        {
            var command = $"ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{directory}\"";
            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
            _ = Process.Start(startInfo);
        }
        catch
        {
        }
    }

    private static async Task<int> RunMenuAsync(bool startInUpdates = false)
    {
        using var interactiveSession = WindowsNotificationService.TryMarkInteractiveSession();
        var ui = new InteractiveTerminalUi(ProductVersion);
        if (startInUpdates)
        {
            ui.OpenUpdatesPage();
        }
        var showReveal = !string.Equals(
            Environment.GetEnvironmentVariable("IS74W_SKIP_REVEAL"),
            "1",
            StringComparison.Ordinal);

        var updateResult = Environment.GetEnvironmentVariable("IS74W_UPDATE_RESULT");
        var updateVersion = Environment.GetEnvironmentVariable("IS74W_UPDATE_VERSION");
        var updateMessage = Environment.GetEnvironmentVariable("IS74W_UPDATE_MESSAGE");
        Environment.SetEnvironmentVariable("IS74W_UPDATE_RESULT", null);
        Environment.SetEnvironmentVariable("IS74W_UPDATE_VERSION", null);
        Environment.SetEnvironmentVariable("IS74W_UPDATE_MESSAGE", null);

        if (!string.IsNullOrWhiteSpace(updateResult))
        {
            var history = new InteractiveActionHistory();
            if (string.Equals(updateResult, "success", StringComparison.OrdinalIgnoreCase))
            {
                history.AddSuccess($"Обновление {updateVersion ?? ProductVersion} установлено");
                history.AddInfo("Программа успешно перезапущена из новой установленной копии");
            }
            else
            {
                history.AddError($"Обновление {updateVersion ?? string.Empty} не было установлено".Trim());
                if (!string.IsNullOrWhiteSpace(updateMessage))
                {
                    history.AddError(updateMessage);
                }
                history.AddInfo("Запущена предыдущая рабочая версия; обновление можно повторить");
            }

            await ui.ShowActionHistoryAsync(
                "РЕЗУЛЬТАТ ОБНОВЛЕНИЯ",
                history,
                GetInteractiveStatusSnapshot(),
                dismissHint: "Enter / Esc — продолжить").ConfigureAwait(false);
            showReveal = false;
        }

        while (true)
        {
            var initialStatus = GetInteractiveStatusSnapshot();
            var refreshedStatus = RefreshInteractiveStatusAsync();
            var action = await ui.RunMenuAsync(initialStatus, refreshedStatus, showReveal).ConfigureAwait(false);
            showReveal = false;

            try
            {
                switch (action)
                {
                    case InteractiveMenuAction.Register:
                        await RegisterFromMenuAsync(ui, initialStatus).ConfigureAwait(false);
                        break;

                    case InteractiveMenuAction.Connect:
                    {
                        var history = new InteractiveActionHistory();
                        history.Start(initialStatus.NetworkCheckIgnored
                            ? "Проверка сети отключена — начинаю авторизацию..."
                            : "Проверяю подключение к сети Интерсвязи...");
                        ui.ShowActionProgress("АВТОРИЗАЦИЯ WI-FI", history, initialStatus);

                        var stepTwoAccepted = false;
                        var outcome = await RunManualAuthorizationAsync(stage =>
                        {
                            switch (stage)
                            {
                                case AuthorizationProgressStage.NetworkGatePassed:
                                    history.CompleteActive(initialStatus.WifiNetwork == WifiNetworkState.Campus
                                        ? "Подключение к сети Интерсвязи подтверждено"
                                        : "Проверка сети пропущена по настройке");
                                    history.Start("Получаю состояние push-очереди...");
                                    break;
                                case AuthorizationProgressStage.BaselineLoaded:
                                    history.CompleteActive("Состояние push-очереди получено");
                                    history.Start("Отправляю captive-запрос и жду 4-значный код...");
                                    break;
                                case AuthorizationProgressStage.CaptiveRequestStarted:
                                case AuthorizationProgressStage.StepTwoStarted:
                                    break;
                                case AuthorizationProgressStage.FreshCodeReceived:
                                    history.CompleteActive("Получен свежий 4-значный код");
                                    history.Start("Отправляю код captive portal...");
                                    break;
                                case AuthorizationProgressStage.StepTwoAccepted:
                                    stepTwoAccepted = true;
                                    history.CompleteActive("Код принят captive portal");
                                    history.Start("Проверяю доступ в Интернет...");
                                    break;
                                case AuthorizationProgressStage.InternetCheckStarted:
                                    if (!stepTwoAccepted)
                                    {
                                        history.WarnActive("Ответ captive portal не подтверждён");
                                        history.Start("Проверяю Интернет после неоднозначного ответа...");
                                    }
                                    break;
                                case AuthorizationProgressStage.InternetConfirmed:
                                    history.CompleteActive("Доступ в Интернет подтверждён");
                                    break;
                            }

                            ui.ShowActionProgress(
                                "АВТОРИЗАЦИЯ WI-FI",
                                history,
                                GetInteractiveStatusSnapshot());
                        }).ConfigureAwait(false);

                        history.FinishActiveAsInfo();
                        var outcomeText = DescribeAuthorizationOutcomeForUi(outcome);
                        if (IsSuccessfulMenuAuthorization(outcome))
                        {
                            history.AddSuccess(outcomeText);
                        }
                        else
                        {
                            history.AddError(outcomeText);
                        }

                        await ui.ShowActionHistoryAsync(
                            "АВТОРИЗАЦИЯ WI-FI",
                            history,
                            GetInteractiveStatusSnapshot()).ConfigureAwait(false);
                        break;
                    }

                    case InteractiveMenuAction.EnableAutomaticAuthorization:
                        await RunMenuBatchActionAsync(
                            ui,
                            "АВТОМАТИЧЕСКАЯ АВТОРИЗАЦИЯ",
                            initialStatus,
                            "Включаю автоматическую авторизацию...",
                            progress => EnableAutomaticAuthorization(quiet: true, progress: progress),
                            "Автоматическая авторизация включена").ConfigureAwait(false);
                        break;

                    case InteractiveMenuAction.DisableAutomaticAuthorization:
                        await RunMenuBatchActionAsync(
                            ui,
                            "АВТОМАТИЧЕСКАЯ АВТОРИЗАЦИЯ",
                            initialStatus,
                            "Отключаю автоматическую авторизацию...",
                            progress => DisableAutostart(quiet: true, progress: progress),
                            "Автоматическая авторизация отключена").ConfigureAwait(false);
                        break;

                    case InteractiveMenuAction.ToggleNetworkCheck:
                    {
                        var ignore = !initialStatus.NetworkCheckIgnored;
                        if (ignore)
                        {
                            var confirmed = await ui.ConfirmAsync(
                                "ПРОВЕРКА СЕТИ",
                                "Отключить проверку сети? Авторизация будет запускаться независимо от обнаруженной сети. Полезно при USB-раздаче, VPN и прокси.",
                                "Отключить проверку",
                                initialStatus).ConfigureAwait(false);
                            if (!confirmed)
                            {
                                break;
                            }
                        }

                        await RunMenuBatchActionAsync(
                            ui,
                            "ПРОВЕРКА СЕТИ",
                            initialStatus,
                            ignore ? "Отключаю проверку сети..." : "Включаю проверку сети...",
                            progress => SetIgnoreNetworkCheck(ignore, progress),
                            ignore
                                ? "Проверка сети отключена"
                                : "Проверка сети включена").ConfigureAwait(false);
                        break;
                    }

                    case InteractiveMenuAction.ToggleAnonymousStatistics:
                    {
                        if (initialStatus.AnonymousStatisticsConsent == AnonymousStatisticsConsent.Allowed)
                        {
                            await RunMenuBatchActionAsync(
                                ui,
                                "АНОНИМНАЯ СТАТИСТИКА",
                                initialStatus,
                                "Отключаю отправку анонимной статистики...",
                                progress => SetAnonymousStatisticsConsent(AnonymousStatisticsConsent.Declined, progress),
                                "Анонимная статистика отключена").ConfigureAwait(false);
                        }
                        else
                        {
                            _ = await PromptAnonymousStatisticsConsentAsync(
                                ui,
                                initialStatus,
                                forPublication: false).ConfigureAwait(false);
                        }
                        break;
                    }

                    case InteractiveMenuAction.ToggleAutomaticUpdates:
                    {
                        var enabled = !initialStatus.AutomaticUpdates;
                        SetAutomaticUpdates(enabled);
                        break;
                    }

                    case InteractiveMenuAction.TogglePrereleaseUpdates:
                    {
                        var enabled = !initialStatus.IncludePrereleaseUpdates;
                        SetPrereleaseUpdates(enabled);
                        break;
                    }

                    case InteractiveMenuAction.CheckUpdates:
                        _ = await CheckForUpdatesFromMenuAsync(
                            ui,
                            initialStatus,
                            offerInstall: false).ConfigureAwait(false);
                        break;

                    case InteractiveMenuAction.CycleNotifications:
                        CycleNotificationMode();
                        break;

                    case InteractiveMenuAction.ShowDetailedStatus:
                    {
                        await OpenDetailedStatusReportAsync().ConfigureAwait(false);
                        break;
                    }

                    case InteractiveMenuAction.ResetRegistration:
                    {
                        var confirmed = await ui.ConfirmAsync(
                            "СБРОС РЕГИСТРАЦИИ",
                            "Удалить сохранённую регистрацию и локальную Wi-Fi сессию? Настройки и диагностические логи останутся.",
                            "Сбросить регистрацию",
                            initialStatus).ConfigureAwait(false);
                        if (confirmed)
                        {
                            await RunMenuBatchActionAsync(
                                ui,
                                "СБРОС РЕГИСТРАЦИИ",
                                initialStatus,
                                "Сбрасываю регистрацию...",
                                progress => ResetRegistration(quiet: true, progress: progress),
                                "Регистрация сброшена").ConfigureAwait(false);
                        }
                        break;
                    }

                    case InteractiveMenuAction.Uninstall:
                    {
                        var confirmed = await ui.ConfirmAsync(
                            "УДАЛЕНИЕ IS74W",
                            "Удалить программу, автозапуск и все локальные данные IS74W?",
                            "Удалить IS74W",
                            initialStatus).ConfigureAwait(false);
                        if (!confirmed)
                        {
                            break;
                        }
                        var uninstalled = await RunMenuBatchActionAsync(
                            ui,
                            "УДАЛЕНИЕ IS74W",
                            initialStatus,
                            "Удаляю IS74W и локальные данные...",
                            progress => Uninstall(quiet: true, progress: progress),
                            "Удаление подготовлено",
                            refreshStatus: false).ConfigureAwait(false);
                        if (uninstalled)
                        {
                            return 0;
                        }
                        break;
                    }

                    case InteractiveMenuAction.OpenLogs:
                        OpenLogs(quiet: true);
                        break;

                    case InteractiveMenuAction.Update:
                        if (await CheckForUpdatesFromMenuAsync(ui, initialStatus).ConfigureAwait(false))
                        {
                            return 0;
                        }
                        break;

                    case InteractiveMenuAction.OpenUpdateReleasePage:
                        OpenAvailableUpdateReleasePage();
                        break;

                    case InteractiveMenuAction.SpeedTools:
                    {
                        using var speedRuntime = ApplicationRuntime.Create(ProductVersion);
                        await ui.RunSpeedToolsAsync(
                            GetInteractiveStatusSnapshot(),
                            speedRuntime.CampusSpeedTools,
                            cancellationToken => EnsureAnonymousStatisticsConsentForPublicationAsync(ui, cancellationToken)).ConfigureAwait(false);
                        break;
                    }

                    case InteractiveMenuAction.Exit:
                        Console.Clear();
                        _ = TryLaunchPendingAutomaticUpdate();
                        return 0;

                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                await ui.ShowMessageAsync(
                    "ОШИБКА",
                    ex.Message,
                    GetInteractiveStatusSnapshot(),
                    isError: true).ConfigureAwait(false);
            }
        }
    }

    private static async Task RegisterFromMenuAsync(
        InteractiveTerminalUi ui,
        InteractiveStatusSnapshot currentStatus)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        app.Logger.Write(DiagnosticLevel.Info, "cli.start command=register runtime=csharp-ui");
        if (app.Secrets.Load() is not null)
        {
            await ui.ShowMessageAsync(
                "РЕГИСТРАЦИЯ",
                "Устройство уже зарегистрировано. Сначала сбросьте текущую регистрацию.",
                currentStatus,
                isError: true).ConfigureAwait(false);
            return;
        }

        var history = new InteractiveActionHistory();
        try
        {
            var phoneInput = await ui.PromptDigitsAsync(
                "РЕГИСТРАЦИЯ",
                "Введите номер телефона без +7. Esc отменяет регистрацию.",
                "+7 ",
                minimumDigits: 10,
                maximumDigits: 10,
                currentStatus: currentStatus,
                history: history).ConfigureAwait(false);
            if (phoneInput is null)
            {
                return;
            }

            history.AddSuccess("Номер телефона введён");
            var phone = NormalizePhone(phoneInput);
            var deviceId = app.DeviceIdentity.GetOrCreate();

            history.Start("Запрашиваю 4-значный SMS-код...");
            ui.ShowActionProgress("РЕГИСТРАЦИЯ", history, currentStatus);
            var requested = await app.Api.RequestConfirmationAsync(phone, deviceId).ConfigureAwait(false);
            if (!requested.IsSuccess)
            {
                app.Logger.Write(DiagnosticLevel.Warn, $"registration.failed stage=get-confirm failure={requested.Failure!.Kind}");
                history.FailActive("Не удалось запросить SMS-код");
                history.AddError(DescribeApiFailure(requested.Failure));
                await ui.ShowActionHistoryAsync(
                    "РЕГИСТРАЦИЯ",
                    history,
                    GetInteractiveStatusSnapshot()).ConfigureAwait(false);
                return;
            }
            history.CompleteActive("SMS-код запрошен");

            var smsCode = await ui.PromptDigitsAsync(
                "РЕГИСТРАЦИЯ",
                "Введите 4-значный SMS-код. Esc отменяет продолжение регистрации.",
                string.Empty,
                minimumDigits: 4,
                maximumDigits: 4,
                currentStatus: currentStatus,
                history: history).ConfigureAwait(false);
            if (smsCode is null)
            {
                return;
            }
            history.AddSuccess("4-значный SMS-код введён");

            history.Start("Проверяю код подтверждения...");
            ui.ShowActionProgress("РЕГИСТРАЦИЯ", history, currentStatus);
            var checkedCode = await app.Api.CheckConfirmationAsync(phone, smsCode, deviceId).ConfigureAwait(false);
            if (!checkedCode.IsSuccess)
            {
                app.Logger.Write(DiagnosticLevel.Warn, $"registration.failed stage=check-confirm failure={checkedCode.Failure!.Kind}");
                history.FailActive("Код подтверждения отклонён");
                history.AddError(DescribeApiFailure(checkedCode.Failure));
                await ui.ShowActionHistoryAsync(
                    "РЕГИСТРАЦИЯ",
                    history,
                    GetInteractiveStatusSnapshot()).ConfigureAwait(false);
                return;
            }

            history.CompleteActive("Код подтверждения принят");
            var confirmation = checkedCode.Value!;
            app.Logger.Write(DiagnosticLevel.Info, "registration.confirmed mode=phone-only");

            history.Start("Получаю API-сессию...");
            ui.ShowActionProgress("РЕГИСТРАЦИЯ", history, currentStatus);
            var sessionResult = await app.Api.GetTokenAsync(confirmation.AuthId, deviceId).ConfigureAwait(false);
            if (!sessionResult.IsSuccess)
            {
                app.Logger.Write(DiagnosticLevel.Warn, $"registration.failed stage=get-token failure={sessionResult.Failure!.Kind}");
                history.FailActive("Не удалось получить API-сессию");
                history.AddError(DescribeApiFailure(sessionResult.Failure));
                await ui.ShowActionHistoryAsync(
                    "РЕГИСТРАЦИЯ",
                    history,
                    GetInteractiveStatusSnapshot()).ConfigureAwait(false);
                return;
            }

            history.CompleteActive("API-сессия получена");
            var session = sessionResult.Value!;

            history.Start("Сохраняю регистрацию локально...");
            ui.ShowActionProgress("РЕГИСТРАЦИЯ", history, currentStatus);
            app.Secrets.Save(new StoredSecrets(session.Token, phone));
            app.Session.Save(new SessionMetadata
            {
                DeviceId = deviceId,
                UserId = session.UserId,
                ProfileId = session.ProfileId,
                AccessBegin = session.AccessBegin,
                AccessEnd = session.AccessEnd,
                RegisteredAtUtc = DateTimeOffset.UtcNow
            });
            history.CompleteActive("Регистрация сохранена локально");

            history.Start("Регистрирую сведения об устройстве...");
            ui.ShowActionProgress("РЕГИСТРАЦИЯ", history, GetInteractiveStatusSnapshot());
            var osVersion = Environment.OSVersion.VersionString;
            var deviceModel = Environment.MachineName;
            var metadata = await app.Api.RegisterDeviceMetadataAsync(
                session.Token,
                new DeviceMetadataRegistration(deviceId, phone, osVersion, deviceModel)).ConfigureAwait(false);
            if (metadata.IsSuccess)
            {
                app.Json.Write(
                    app.Paths.DeviceMetadataFile,
                    new DeviceMetadataSnapshot(deviceId, deviceModel, osVersion, DateTimeOffset.UtcNow),
                    AppJsonContext.Default.DeviceMetadataSnapshot);
                history.CompleteActive("Сведения об устройстве зарегистрированы");
            }
            else
            {
                app.Logger.Write(DiagnosticLevel.Warn, $"device-metadata failure={metadata.Failure?.Kind}");
                history.WarnActive("Регистрация сохранена; сведения об устройстве не отправлены");
            }

            app.Logger.Write(DiagnosticLevel.Info,
                $"registration.complete accessBegin={session.AccessBegin ?? ""} accessEnd={session.AccessEnd ?? ""}");

            history.AddSuccess("Регистрация завершена");

            await ui.ShowActionHistoryAsync(
                "РЕГИСТРАЦИЯ",
                history,
                GetInteractiveStatusSnapshot()).ConfigureAwait(false);

            using (var consentRuntime = ApplicationRuntime.Create(ProductVersion))
            {
                if (consentRuntime.Settings.AnonymousStatisticsConsent == AnonymousStatisticsConsent.Unknown)
                {
                    _ = await PromptAnonymousStatisticsConsentAsync(
                        ui,
                        GetInteractiveStatusSnapshot(),
                        forPublication: false).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            history.FailActive("Операция прервана");
            history.AddError(ex.Message);
            await ui.ShowActionHistoryAsync(
                "РЕГИСТРАЦИЯ",
                history,
                GetInteractiveStatusSnapshot()).ConfigureAwait(false);
        }
    }

    private static async Task<bool> RunMenuBatchActionAsync(
        InteractiveTerminalUi ui,
        string title,
        InteractiveStatusSnapshot currentStatus,
        string startingMessage,
        Action<Action<string>> action,
        string finalMessage,
        bool refreshStatus = true)
    {
        var history = new InteractiveActionHistory();
        history.Start(startingMessage);
        ui.ShowActionProgress(title, history, currentStatus);
        var firstProgress = true;

        try
        {
            action(message =>
            {
                if (firstProgress)
                {
                    history.CompleteActive(message);
                    firstProgress = false;
                }
                else
                {
                    history.AddSuccess(message);
                }
                ui.ShowActionProgress(
                    title,
                    history,
                    refreshStatus ? GetInteractiveStatusSnapshot() : currentStatus);
            });

            history.FinishActiveAsInfo();
            history.AddSuccess(finalMessage);
            await ui.ShowActionHistoryAsync(
                title,
                history,
                refreshStatus ? GetInteractiveStatusSnapshot() : currentStatus).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            history.FailActive("Операция прервана");
            history.AddError(ex.Message);
            await ui.ShowActionHistoryAsync(
                title,
                history,
                refreshStatus ? GetInteractiveStatusSnapshot() : currentStatus).ConfigureAwait(false);
            return false;
        }
    }

    private static bool IsSuccessfulMenuAuthorization(AuthorizationOutcome outcome) =>
        outcome.Kind is AuthorizationOutcomeKind.Success or
            AuthorizationOutcomeKind.AlreadyAuthorized or
            AuthorizationOutcomeKind.AlreadyOnline;

    private static string DescribeAuthorizationOutcomeForUi(AuthorizationOutcome outcome) => outcome.Kind switch
    {
        AuthorizationOutcomeKind.Success => outcome.InternetConfirmed == true
            ? "Wi-Fi авторизация завершена, Интернет подтверждён."
            : "Wi-Fi авторизация принята; Интернет пока не подтверждён проверкой.",
        AuthorizationOutcomeKind.AlreadyAuthorized => "Wi-Fi уже авторизован сервером.",
        AuthorizationOutcomeKind.AlreadyOnline => "Интернет уже доступен; авторизация не требуется.",
        AuthorizationOutcomeKind.WrongWifi =>
            $"Авторизация работает только в сети {ProtocolContract.CampusSsidPrefix}*. Подключитесь к кампусной сети.",
        AuthorizationOutcomeKind.Busy => "Другая Wi-Fi авторизация уже выполняется.",
        AuthorizationOutcomeKind.BearerInvalid => "API-сессия отклонена сервером. Сбросьте регистрацию и зарегистрируйте устройство заново.",
        AuthorizationOutcomeKind.RetryableBeforeStepOne => "Сервер Интерсвязи временно недоступен до отправки captive-запроса. Попробуйте ещё раз.",
        AuthorizationOutcomeKind.RetryableStepOne => "Captive portal временно не завершил stepOne. Попробуйте ещё раз.",
        AuthorizationOutcomeKind.StepTwoAmbiguous => "Ответ stepTwo потерян, а Интернет не подтвердился. Код повторно не отправлялся.",
        AuthorizationOutcomeKind.UserActionRequired => "Автоматическое продолжение остановлено. Подробности есть в diagnostic.log.",
        AuthorizationOutcomeKind.Cancelled => "Операция отменена.",
        _ => $"Неожиданный результат: {outcome.Kind}"
    };

    private static async Task OpenDetailedStatusReportAsync()
    {
        var lines = await BuildDetailedStatusLinesAsync().ConfigureAwait(false);
        var reportPath = Path.Combine(Path.GetTempPath(), "IS74Wifi-status.txt");
        await File.WriteAllLinesAsync(reportPath, lines, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = "notepad.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(reportPath);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось открыть подробный отчёт.");
    }

    private static async Task<IReadOnlyList<string>> BuildDetailedStatusLinesAsync()
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        var installation = new ProgramInstallation();
        var secrets = app.Secrets.Load();
        var session = app.Session.Load();
        var state = app.RuntimeState.Load();
        var internet = await app.Internet.ProbeAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        var automatic = installation.IsInstalled && app.Autostart.IsEnabledFor(installation.ExecutablePath);
        var installedVersion = installation.ReadInstalledVersion();
        var displayedSsid = GetDisplayedWifiSsid();
        var updateMaintenance = new UpdateMaintenanceService(ProductVersion, app.Paths, app.Json, app.Logger);
        var updateState = updateMaintenance.LoadState();

        var lines = new List<string>
        {
            "IS74W — подробный отчёт",
            $"Создан: {DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss zzz}",
            string.Empty,
            "=== Программа ===",
            $"Запущенная версия: {ProductVersion}",
            $"Запущенный EXE: {Environment.ProcessPath ?? "неизвестно"}",
            $"Установка: {(installation.IsInstalled ? "есть" : "нет")}",
            $"Установленная версия: {installedVersion ?? "неизвестно"}",
            $"Установленный EXE: {(installation.IsInstalled ? installation.ExecutablePath : "—")}",
            string.Empty,
            "=== Состояние ===",
            $"Интернет: {(internet.Online ? "доступен" : "не подтверждён")}",
            $"Сеть: {(app.Settings.IgnoreNetworkCheck ? "проверка отключена ○" : FormatWifiNetwork(displayedSsid))}",
            $"Авторизация: {FormatWifiAuthorization(state)}",
            $"Регистрация: {(secrets is null ? "нет" : "сохранена")}",
            $"Телефон: {MaskPhone(secrets?.Phone)}",
            $"Автовход: {(automatic ? "включён" : "выключен")}",
            $"Фоновый режим: {(AgentProcessControl.IsAgentRunning() ? "работает" : "остановлен")}",
            $"Уведомления: {FormatNotificationMode(app.Settings.NotificationMode)}",
            $"API-сессия: {FormatSessionEnd(session?.AccessEnd)}"
        };

        lines.Add(string.Empty);
        lines.Add("=== Обновления ===");
        lines.Add($"Режим: {(app.Settings.AutomaticUpdates ? "автоматически" : "уведомлять")}");
        lines.Add($"Канал: {(updateMaintenance.IncludePrereleases(app.Settings) ? "обычные + Pre-release" : "обычные версии")}");
        lines.Add($"Доступно: {updateState.AvailableVersion ?? "нет"}");
        lines.Add($"Последняя проверка: {(updateState.LastCheckedUtc is { } checkedAt ? checkedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") : "ещё не выполнялась")}");
        if (!string.IsNullOrWhiteSpace(updateState.LastError))
        {
            lines.Add($"Последняя ошибка проверки: {updateState.LastError}");
        }

        var telemetryStatus = app.TelemetryQueue.GetStatus();
        lines.Add(string.Empty);
        lines.Add("=== Телеметрия ===");
        lines.Add($"Режим: {FormatAnonymousStatisticsConsent(app.Settings.AnonymousStatisticsConsent)}");
        lines.Add($"Ожидает выгрузки: {telemetryStatus.PendingFiles} файлов / {FormatByteCount(telemetryStatus.PendingBytes)}");
        lines.Add($"Карантин: {telemetryStatus.RejectedFiles} файлов / {FormatByteCount(telemetryStatus.RejectedBytes)}");

        if (state.LastAuthUtc is { } lastAuth)
            lines.Add($"Последняя Wi-Fi авторизация: {lastAuth.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        if (state.ExpectedExpiryUtc is { } expiry)
            lines.Add($"Ожидаемое окончание окна: {expiry.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(state.LastResult))
            lines.Add($"Последний результат: {FormatRuntimeResultForUi(state.LastResult)}");
        if (state.AutomaticStepOneAttempts > 0)
            lines.Add($"Автоматические попытки: {state.AutomaticStepOneAttempts}/{app.Settings.MaxAutomaticStepOneAttempts}");
        lines.Add($"Требуется действие пользователя: {(state.UserActionRequired ? "да" : "нет")}");

        lines.Add(string.Empty);
        lines.Add("=== Пути ===");
        lines.Add($"Данные приложения: {app.Paths.Root}");
        lines.Add($"Диагностический журнал: {app.Paths.DiagnosticLogFile}");
        return lines;
    }

    private static async Task<bool> CheckForUpdatesFromMenuAsync(
        InteractiveTerminalUi ui,
        InteractiveStatusSnapshot currentStatus,
        bool offerInstall = true)
    {
        var history = new InteractiveActionHistory();
        history.Start("Запрашиваю список GitHub Releases...");
        ui.ShowActionProgress("ОБНОВЛЕНИЯ", history, currentStatus);
        var progressTitle = "ОБНОВЛЕНИЯ";

        using var updateRuntime = ApplicationRuntime.Create(ProductVersion);
        var maintenance = new UpdateMaintenanceService(
            ProductVersion,
            updateRuntime.Paths,
            updateRuntime.Json,
            updateRuntime.Logger);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var updater = new GitHubUpdateClient(http);

        void RenderProgress() => ui.ShowActionProgress(
            progressTitle,
            history,
            GetInteractiveStatusSnapshot());

        void ReportDownloadProgress(UpdateProgressStage stage)
        {
            switch (stage)
            {
                case UpdateProgressStage.RequestingReleases:
                    break;
                case UpdateProgressStage.ReleasesLoaded:
                    history.CompleteActive("Список GitHub Releases получен");
                    history.Start("Сравниваю доступные версии...");
                    break;
                case UpdateProgressStage.DownloadingPackage:
                    history.Start("Скачиваю пакет обновления...");
                    break;
                case UpdateProgressStage.PackageDownloaded:
                    history.CompleteActive("Пакет обновления скачан");
                    break;
                case UpdateProgressStage.DownloadingChecksum:
                    history.Start("Скачиваю файл SHA-256...");
                    break;
                case UpdateProgressStage.ChecksumDownloaded:
                    history.CompleteActive("Файл SHA-256 скачан");
                    break;
                case UpdateProgressStage.VerifyingChecksum:
                    history.Start("Проверяю SHA-256 пакета...");
                    break;
                case UpdateProgressStage.ChecksumVerified:
                    history.CompleteActive("SHA-256 пакета совпадает");
                    break;
                case UpdateProgressStage.ExtractingPackage:
                    history.Start("Подготавливаю IS74Wifi.exe...");
                    break;
                case UpdateProgressStage.PackageExtracted:
                    history.CompleteActive("IS74Wifi.exe подготовлен");
                    break;
            }
            RenderProgress();
        }

        void ReportTransferProgress(UpdateTransferProgress transfer)
        {
            switch (transfer.Stage)
            {
                case UpdateProgressStage.DownloadingPackage:
                    history.UpdateActive(FormatTransferProgress("Скачиваю пакет обновления", transfer));
                    break;
                case UpdateProgressStage.DownloadingChecksum:
                    history.UpdateActive(FormatTransferProgress("Скачиваю файл SHA-256", transfer));
                    break;
                default:
                    return;
            }
            RenderProgress();
        }

        void ReportApplyProgress(UpdateApplyProgressStage stage)
        {
            switch (stage)
            {
                case UpdateApplyProgressStage.ValidatingExecutable:
                    history.Start("Проверяю запуск скачанной версии...");
                    break;
                case UpdateApplyProgressStage.ExecutableValidated:
                    history.CompleteActive("Скачанная версия запускается корректно");
                    break;
                case UpdateApplyProgressStage.StoppingAgent:
                    history.Start("Останавливаю фоновый режим...");
                    break;
                case UpdateApplyProgressStage.AgentStopped:
                    history.CompleteActive("Фоновый режим остановлен");
                    break;
                case UpdateApplyProgressStage.PreparingInstalledCopy:
                    history.Start("Подготавливаю установленную копию...");
                    break;
                case UpdateApplyProgressStage.InstalledCopyPrepared:
                    history.CompleteActive("Установленная копия подготовлена");
                    break;
                case UpdateApplyProgressStage.SchedulingReplacement:
                    history.Start("Подготавливаю модуль установки обновления...");
                    break;
                case UpdateApplyProgressStage.ReplacementScheduled:
                    history.CompleteActive("Модуль установки обновления подготовлен");
                    break;
            }
            RenderProgress();
        }

        try
        {
            var update = (await maintenance.CheckAsync(
                updater,
                force: true,
                progress: ReportDownloadProgress).ConfigureAwait(false)).Descriptor;

            if (update is null)
            {
                history.CompleteActive("Новых версий не найдено");
                history.AddSuccess($"Установлена актуальная версия {ProductVersion}");
                await ui.ShowActionHistoryAsync(
                    "ОБНОВЛЕНИЯ",
                    history,
                    GetInteractiveStatusSnapshot()).ConfigureAwait(false);
                return false;
            }

            history.CompleteActive($"Найдена версия {update.TagName}");
            history.AddInfo($"Текущая версия: {ProductVersion}");

            if (!offerInstall)
            {
                history.AddSuccess($"Обновление {update.TagName} доступно для установки");
                await ui.ShowActionHistoryAsync(
                    "ОБНОВЛЕНИЯ",
                    history,
                    GetInteractiveStatusSnapshot()).ConfigureAwait(false);
                return false;
            }

            var install = await ui.ConfirmAsync(
                "ОБНОВЛЕНИЕ",
                $"Доступна {update.TagName}. Скачать, проверить SHA-256 и установить её сейчас?",
                "Установить обновление",
                GetInteractiveStatusSnapshot(),
                history).ConfigureAwait(false);
            if (!install)
            {
                history.AddInfo("Обновление отменено пользователем");
                await ui.ShowActionHistoryAsync(
                    "ОБНОВЛЕНИЯ",
                    history,
                    GetInteractiveStatusSnapshot()).ConfigureAwait(false);
                return false;
            }

            history.AddSuccess("Установка обновления подтверждена");
            progressTitle = "ОБНОВЛЕНИЕ";
            var scheduled = await PrepareAndScheduleUpdateAsync(
                updater,
                update,
                restartMenu: true,
                quiet: true,
                downloadProgress: ReportDownloadProgress,
                transferProgress: ReportTransferProgress,
                applyProgress: ReportApplyProgress,
                confirmApply: async () =>
                {
                    history.AddSuccess($"Обновление {update.TagName} готово к установке");
                    var confirmed = await ui.ConfirmAsync(
                        "ОБНОВЛЕНИЕ ГОТОВО",
                        $"Закрыть текущую версию IS74Wifi и установить {update.TagName} сейчас?",
                        "Установить и перезапустить",
                        GetInteractiveStatusSnapshot(),
                        history).ConfigureAwait(false);
                    if (!confirmed)
                    {
                        history.AddInfo("Установка обновления отменена");
                    }
                    return confirmed;
                }).ConfigureAwait(false);

            return scheduled;
        }
        catch (Exception ex)
        {
            history.FailActive("Операция обновления прервана");
            history.AddError(ex.Message);
            await ui.ShowActionHistoryAsync(
                "ОБНОВЛЕНИЯ",
                history,
                GetInteractiveStatusSnapshot()).ConfigureAwait(false);
            return false;
        }
    }

    private static string FormatTransferProgress(string label, UpdateTransferProgress progress)
    {
        var received = FormatByteCount(progress.BytesReceived);
        if (progress.TotalBytes is > 0)
        {
            var total = progress.TotalBytes.Value;
            var percent = (int)Math.Clamp(progress.BytesReceived * 100L / total, 0L, 100L);
            return $"{label}... {percent}% ({received} / {FormatByteCount(total)})";
        }
        return $"{label}... {received}";
    }

    private static string FormatByteCount(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024L * 1024L) return $"{bytes / 1024d:0.0} КБ";
        return $"{bytes / (1024d * 1024d):0.0} МБ";
    }

    private static InteractiveStatusSnapshot GetInteractiveStatusSnapshot(bool? internetOverride = null)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        return BuildInteractiveStatusSnapshot(app, internetOverride);
    }

    private static async Task<InteractiveStatusSnapshot> RefreshInteractiveStatusAsync()
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        var updateTask = TryCheckForUpdatesIfDueAsync(app);
        bool? online;
        try
        {
            var probe = await app.Internet.ProbeAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            online = probe.Online;
        }
        catch
        {
            online = null;
        }

        try
        {
            await updateTask.ConfigureAwait(false);
        }
        catch
        {
            // Update discovery is best-effort and must never block the main UI.
        }
        return BuildInteractiveStatusSnapshot(app, online);
    }

    private static InteractiveStatusSnapshot BuildInteractiveStatusSnapshot(
        ApplicationRuntime app,
        bool? internetOverride)
    {
        var installation = new ProgramInstallation();
        var secrets = app.Secrets.Load();
        var session = app.Session.Load();
        var runtime = app.RuntimeState.Load();
        var automatic = installation.IsInstalled && app.Autostart.IsEnabledFor(installation.ExecutablePath);
        var agentRunning = AgentProcessControl.IsAgentRunning();
        var displayedSsid = GetDisplayedWifiSsid();
        var wifiNetwork = SsidPolicy.IsTarget(displayedSsid)
            ? WifiNetworkState.Campus
            : displayedSsid is not null
                ? WifiNetworkState.Other
                : WifiNetworkState.Unknown;
        var internet = internetOverride ?? runtime.InternetConfirmed;
        var updateMaintenance = new UpdateMaintenanceService(ProductVersion, app.Paths, app.Json, app.Logger);
        var updateState = updateMaintenance.LoadState();
        return new InteractiveStatusSnapshot(
            Installed: installation.IsInstalled,
            Registered: secrets is not null,
            InternetAvailable: internet,
            WifiNetwork: wifiNetwork,
            WifiSsid: displayedSsid,
            AuthorizationExpectedExpiryUtc: runtime.ExpectedExpiryUtc,
            AuthorizationAlreadyActive: string.Equals(runtime.LastResult, "already-authorized", StringComparison.Ordinal),
            NetworkCheckIgnored: app.Settings.IgnoreNetworkCheck,
            AnonymousStatisticsConsent: app.Settings.AnonymousStatisticsConsent,
            AutomaticUpdates: app.Settings.AutomaticUpdates,
            IncludePrereleaseUpdates: updateMaintenance.IncludePrereleases(app.Settings),
            AvailableUpdateVersion: updateState.AvailableVersion,
            LastUpdateCheckUtc: updateState.LastCheckedUtc,
            AutomaticAuthorizationEnabled: automatic,
            AgentRunning: agentRunning,
            NotificationMode: FormatNotificationMode(app.Settings.NotificationMode),
            MaskedPhone: MaskPhone(secrets?.Phone),
            ApiSessionEnd: FormatSessionEnd(session?.AccessEnd),
            LastResult: FormatRuntimeResultForUi(runtime.LastResult),
            Version: ProductVersion);
    }

    private static string? GetDisplayedWifiSsid()
    {
        var connectedSsids = WindowsWifiService.GetConnectedSsids();
        return connectedSsids.FirstOrDefault(SsidPolicy.IsTarget) ?? connectedSsids.FirstOrDefault();
    }

    private static WifiAuthorizationState GetWifiAuthorizationState(RuntimeState runtime) =>
        runtime.ExpectedExpiryUtc switch
        {
            { } expiry when expiry > DateTimeOffset.UtcNow => WifiAuthorizationState.Active,
            { } => WifiAuthorizationState.Expired,
            _ => WifiAuthorizationState.Unknown
        };

    private static string FormatWifiNetwork(string? ssid) => ssid switch
    {
        null => "не определена ○",
        _ when SsidPolicy.IsTarget(ssid) => $"{ssid} ●",
        _ => $"{ssid} ○"
    };

    private static string FormatWifiAuthorization(RuntimeState state)
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

        return GetWifiAuthorizationState(state) switch
        {
            WifiAuthorizationState.Expired => "срок истёк ○",
            _ => "ещё не выполнялась ○"
        };
    }

    private static string FormatRuntimeResultForUi(string? result) => result switch
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

    private static async Task<bool> PromptAnonymousStatisticsConsentAsync(
        InteractiveTerminalUi ui,
        InteractiveStatusSnapshot currentStatus,
        bool forPublication,
        CancellationToken cancellationToken = default)
    {
        using (var app = ApplicationRuntime.Create(ProductVersion))
        {
            var current = app.Settings.AnonymousStatisticsConsent;
            if (current == AnonymousStatisticsConsent.Allowed)
            {
                return true;
            }
        }

        var allowed = await ui.ConfirmYesNoAsync(
            "АНОНИМНАЯ СТАТИСТИКА",
            forPublication ? AnonymousStatisticsPublishMessage : AnonymousStatisticsConsentMessage,
            forPublication ? "Разрешить и опубликовать" : "Разрешить",
            forPublication ? "Не публиковать" : "Не отправлять",
            currentStatus,
            cancellationToken).ConfigureAwait(false);

        SetAnonymousStatisticsConsent(allowed
            ? AnonymousStatisticsConsent.Allowed
            : AnonymousStatisticsConsent.Declined);
        return allowed;
    }

    private static Task<bool> EnsureAnonymousStatisticsConsentForPublicationAsync(
        InteractiveTerminalUi ui,
        CancellationToken cancellationToken) =>
        PromptAnonymousStatisticsConsentAsync(
            ui,
            GetInteractiveStatusSnapshot(),
            forPublication: true,
            cancellationToken: cancellationToken);

    private static void PromptAnonymousStatisticsConsentConsole()
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        if (app.Settings.AnonymousStatisticsConsent != AnonymousStatisticsConsent.Unknown)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("АНОНИМНАЯ СТАТИСТИКА");
        Console.WriteLine(AnonymousStatisticsConsentMessage);
        Console.Write("[Y] Разрешить   [N] Не отправлять: ");
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Y)
            {
                Console.WriteLine("Y");
                SetAnonymousStatisticsConsent(AnonymousStatisticsConsent.Allowed);
                return;
            }
            if (key.Key is ConsoleKey.N or ConsoleKey.Escape)
            {
                Console.WriteLine("N");
                SetAnonymousStatisticsConsent(AnonymousStatisticsConsent.Declined);
                return;
            }
        }
    }

    private static void SetAnonymousStatisticsConsent(
        AnonymousStatisticsConsent consent,
        Action<string>? progress = null)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        if (app.Settings.AnonymousStatisticsConsent == consent)
        {
            ReportMenuBatchProgress(progress, "Настройка уже сохранена");
            return;
        }

        var installation = new ProgramInstallation();
        var restartAgent = AgentProcessControl.IsAgentRunning() && installation.IsInstalled;

        if (restartAgent)
        {
            AgentProcessControl.StopAgentOrThrow();
            ReportMenuBatchProgress(progress, "Фоновый режим остановлен для применения настройки");
        }

        try
        {
            // Never let telemetry collected under an earlier policy cross a consent
            // boundary. New events are recorded only while consent is enabled.
            app.TelemetryQueue.ClearPending();
            new SettingsStore(app.Paths, app.Json).Save(app.Settings with
            {
                AnonymousStatisticsConsent = consent
            });
            app.Logger.Write(DiagnosticLevel.Info, $"telemetry.consent value={consent}");
            ReportMenuBatchProgress(progress, "Настройка сохранена");
        }
        catch
        {
            if (restartAgent)
            {
                StartInstalledAgent(installation.ExecutablePath);
            }
            throw;
        }

        if (restartAgent)
        {
            StartInstalledAgent(installation.ExecutablePath);
            ReportMenuBatchProgress(progress, "Фоновый режим запущен с новой настройкой");
        }
    }

    private static void CycleNotificationMode()
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        var next = app.Settings.NotificationMode switch
        {
            NotificationMode.Important => NotificationMode.All,
            NotificationMode.All => NotificationMode.Off,
            _ => NotificationMode.Important
        };

        new SettingsStore(app.Paths, app.Json).Save(app.Settings with { NotificationMode = next });
        app.Logger.Write(DiagnosticLevel.Info, $"notifications.mode value={next}");
    }

    private static void SetIgnoreNetworkCheck(bool enabled, Action<string>? progress = null)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        var installation = new ProgramInstallation();
        var restartAgent = AgentProcessControl.IsAgentRunning() && installation.IsInstalled;

        new SettingsStore(app.Paths, app.Json).Save(app.Settings with
        {
            IgnoreNetworkCheck = enabled
        });
        app.Logger.Write(
            DiagnosticLevel.Info,
            $"authorization.network-policy ignoreNetworkCheck={enabled.ToString().ToLowerInvariant()}");
        ReportMenuBatchProgress(progress, "Настройка сохранена");

        if (!restartAgent)
        {
            return;
        }

        AgentProcessControl.StopAgentOrThrow();
        ReportMenuBatchProgress(progress, "Фоновый режим остановлен для применения настройки");
        StartInstalledAgent(installation.ExecutablePath);
        ReportMenuBatchProgress(progress, "Фоновый режим запущен с новой настройкой");
    }

    private static string FormatAnonymousStatisticsConsent(AnonymousStatisticsConsent consent) => consent switch
    {
        AnonymousStatisticsConsent.Allowed => "включена",
        AnonymousStatisticsConsent.Declined => "выключена",
        _ => "не выбрано"
    };

    private static string FormatNotificationMode(NotificationMode mode) => mode switch
    {
        NotificationMode.All => "все",
        NotificationMode.Off => "выкл",
        _ => "важные"
    };

    private static string FormatSessionEnd(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "—";
        }

        if (DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            return "до " + parsed.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        return "до " + value.Trim();
    }

    private static void OpenAvailableUpdateReleasePage()
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        var state = new UpdateMaintenanceService(ProductVersion, app.Paths, app.Json, app.Logger).LoadState();
        if (!Uri.TryCreate(state.AvailableReleasePageUrl, UriKind.Absolute, out var releaseUri) ||
            (!string.Equals(releaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(releaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Страница доступного обновления пока неизвестна. Сначала проверьте обновления.");
        }

        _ = Process.Start(new ProcessStartInfo(releaseUri.AbsoluteUri) { UseShellExecute = true });
    }

    private static void SetAutomaticUpdates(bool enabled)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        new SettingsStore(app.Paths, app.Json).Save(app.Settings with { AutomaticUpdates = enabled });
        app.Logger.Write(DiagnosticLevel.Info, $"update.auto value={enabled.ToString().ToLowerInvariant()}");
    }

    private static void SetPrereleaseUpdates(bool enabled)
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        new SettingsStore(app.Paths, app.Json).Save(app.Settings with { IncludePrereleaseUpdates = enabled });
        new UpdateMaintenanceService(ProductVersion, app.Paths, app.Json, app.Logger).ForceNextCheck();
        app.Logger.Write(DiagnosticLevel.Info, $"update.prerelease value={enabled.ToString().ToLowerInvariant()}");
    }

    private static async Task TryCheckForUpdatesIfDueAsync(ApplicationRuntime app)
    {
        var maintenance = new UpdateMaintenanceService(ProductVersion, app.Paths, app.Json, app.Logger);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var updater = new GitHubUpdateClient(http);
        try
        {
            _ = await maintenance.CheckAsync(updater, force: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            app.Logger.Write(DiagnosticLevel.Info,
                $"update.background-check deferred error={ex.GetType().Name}");
        }
    }

    private static async Task TryRunBackgroundUpdateMaintenanceAsync(
        ApplicationRuntime app,
        CancellationToken cancellationToken)
    {
        var maintenance = new UpdateMaintenanceService(ProductVersion, app.Paths, app.Json, app.Logger);
        var settingsStore = new SettingsStore(app.Paths, app.Json);
        var notifications = new WindowsNotificationService(settingsStore, app.Logger);
        var state = maintenance.LoadState();

        if (!string.IsNullOrWhiteSpace(state.PendingInstalledNotificationVersion) &&
            !WindowsNotificationService.IsInteractiveSessionRunning())
        {
            notifications.Publish(new AgentNotification(
                "IS74Wifi обновлён",
                $"Установлена версия {state.PendingInstalledNotificationVersion}.",
                AgentNotificationImportance.Important,
                AgentNotificationSeverity.Success));
            maintenance.ClearPendingInstalledNotification();
            state = maintenance.LoadState();
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var updater = new GitHubUpdateClient(http);
        try
        {
            var result = await maintenance.CheckAsync(
                updater,
                force: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            state = result.State;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return;
        }

        var settings = maintenance.LoadSettings();
        if (string.IsNullOrWhiteSpace(state.AvailableVersion))
        {
            return;
        }

        if (settings.AutomaticUpdates)
        {
            // A failed automatic download/check leaves the known target cached,
            // but CheckAsync also records a retry time. Do not immediately spawn
            // another worker on every agent tick while that backoff is active.
            if (string.IsNullOrWhiteSpace(state.LastError) &&
                !WindowsNotificationService.IsInteractiveSessionRunning() &&
                TryLaunchAutomaticUpdateWorker())
            {
                app.Logger.Write(DiagnosticLevel.Info,
                    $"update.auto-worker launched target={state.AvailableVersion}");
            }
            return;
        }

        if (!WindowsNotificationService.IsInteractiveSessionRunning() &&
            !string.Equals(state.LastNotifiedVersion, state.AvailableVersion, StringComparison.Ordinal))
        {
            notifications.Publish(new AgentNotification(
                "Доступно обновление IS74Wifi",
                $"Доступна версия {state.AvailableVersion}. Нажмите, чтобы посмотреть и установить.",
                AgentNotificationImportance.Important,
                AgentNotificationSeverity.Info,
                AgentNotificationAction.OpenUpdates));
            maintenance.MarkNotified(state.AvailableVersion);
        }
    }

    private static bool TryLaunchPendingAutomaticUpdate()
    {
        try
        {
            using var app = ApplicationRuntime.Create(ProductVersion);
            var maintenance = new UpdateMaintenanceService(ProductVersion, app.Paths, app.Json, app.Logger);
            var settings = maintenance.LoadSettings();
            var state = maintenance.LoadState();
            return settings.AutomaticUpdates &&
                   !string.IsNullOrWhiteSpace(state.AvailableVersion) &&
                   TryLaunchAutomaticUpdateWorker();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLaunchAutomaticUpdateWorker()
    {
        var installation = new ProgramInstallation();
        if (!installation.IsInstalled)
        {
            return false;
        }

        // Avoid spawning a short-lived process on every agent tick while a
        // previous automatic updater is still downloading or validating.
        var workerProbe = NamedSemaphoreLease.TryAcquire(AutomaticUpdateGateName);
        if (workerProbe is null)
        {
            return false;
        }
        workerProbe.Dispose();

        try
        {
            var startInfo = new ProcessStartInfo(installation.ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("update-auto");
            _ = Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> RunAutomaticUpdateAsync()
    {
        using var lease = NamedSemaphoreLease.TryAcquire(AutomaticUpdateGateName);
        if (lease is null)
        {
            return 0;
        }

        using var app = ApplicationRuntime.Create(ProductVersion);
        var maintenance = new UpdateMaintenanceService(ProductVersion, app.Paths, app.Json, app.Logger);
        if (!maintenance.LoadSettings().AutomaticUpdates)
        {
            return 0;
        }

        // When this worker was launched while the interactive menu was closing,
        // give the menu a short grace period to release its session gate.
        var waitUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (WindowsNotificationService.IsInteractiveSessionRunning() && DateTimeOffset.UtcNow < waitUntil)
        {
            await Task.Delay(200).ConfigureAwait(false);
        }
        if (WindowsNotificationService.IsInteractiveSessionRunning())
        {
            app.Logger.Write(DiagnosticLevel.Info, "update.auto deferred reason=interactive-session");
            return 0;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var updater = new GitHubUpdateClient(http);
            var update = (await maintenance.CheckAsync(updater, force: true).ConfigureAwait(false)).Descriptor;
            if (update is null)
            {
                return 0;
            }

            app.Logger.Write(DiagnosticLevel.Info, $"update.auto preparing target={update.TagName}");
            var scheduled = await PrepareAndScheduleUpdateAsync(
                updater,
                update,
                restartMenu: false,
                quiet: true,
                canApply: () =>
                    maintenance.LoadSettings().AutomaticUpdates &&
                    !WindowsNotificationService.IsInteractiveSessionRunning()).ConfigureAwait(false);
            return scheduled ? 0 : 1;
        }
        catch (Exception ex)
        {
            app.Logger.Write(DiagnosticLevel.Warn,
                $"update.auto failed error={ex.GetType().Name}:{ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAgentAsync()
    {
        using var app = ApplicationRuntime.Create(ProductVersion);
        if (app.Secrets.Load() is null)
        {
            return 0;
        }

        using var lease = NamedSemaphoreLease.TryAcquire(AgentProcessControl.AgentGateName);
        if (lease is null)
        {
            app.Logger.Write(DiagnosticLevel.Info, "agent.start skipped reason=already-running runtime=csharp");
            return 0;
        }

        using var stopEvent = AgentProcessControl.CreateStopEvent();
        AgentProcessControl.RegisterCurrentAgentProcess();
        using var stopCts = new CancellationTokenSource();
        var stopRegistration = ThreadPool.RegisterWaitForSingleObject(
            stopEvent,
            static (state, _) => ((CancellationTokenSource)state!).Cancel(),
            stopCts,
            Timeout.Infinite,
            executeOnlyOnce: true);
        app.Logger.Write(DiagnosticLevel.Info, "agent.start runtime=csharp");
        try
        {
            while (!stopCts.IsCancellationRequested)
            {
                try
                {
                    await app.Agent.TickAsync(stopCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    app.Logger.Write(DiagnosticLevel.Error, $"agent.tick error={ex.GetType().Name}:{ex.Message}");
                }

                if (stopCts.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    // The update worker downloads and validates in parallel with
                    // the agent. Only once it is ready to replace the installed
                    // EXE does the existing apply pipeline stop this agent. This
                    // keeps autoauthorization alive if an update check/download
                    // fails because the network disappeared.
                    await TryRunBackgroundUpdateMaintenanceAsync(app, stopCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    app.Logger.Write(DiagnosticLevel.Warn,
                        $"update.maintenance error={ex.GetType().Name}");
                }

                var delay = app.Agent.GetSleepDelay();
                if (delay >= TimeSpan.FromMinutes(1))
                {
                    try
                    {
                        await app.TelemetryUploader.TryFlushIfDueAsync(stopCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stopCts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        app.Logger.Write(DiagnosticLevel.Warn,
                            $"telemetry.upload error={ex.GetType().Name}");
                    }
                    delay = app.Agent.GetSleepDelay();
                }

                if (stopEvent.WaitOne(delay))
                {
                    break;
                }
            }
        }
        finally
        {
            stopRegistration.Unregister(null);
            AgentProcessControl.ClearCurrentAgentProcess();
            app.Logger.Write(DiagnosticLevel.Info, "agent.stop runtime=csharp");
        }

        return 0;
    }

    private static string NormalizePhone(string? input)
    {
        var digits = new string((input ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && (digits[0] == '7' || digits[0] == '8'))
        {
            digits = digits[1..];
        }
        if (digits.Length != 10)
        {
            throw new InvalidOperationException("Номер телефона должен содержать 10 цифр после кода страны.");
        }
        return digits;
    }

    private static string MaskPhone(string? phone) =>
        phone is { Length: 10 }
            ? $"+7 *** ***-{phone.Substring(6, 2)}-{phone.Substring(8, 2)}"
            : "-";

    private static string DescribeApiFailure(Is74ApiFailure failure)
    {
        return failure.Kind switch
        {
            Is74ApiFailureKind.Transport when failure.TransportFailure == TransportFailureKind.DnsUnavailable =>
                "Не удалось разрешить адрес API Интерсвязи. Проверьте сеть и повторите попытку.",
            Is74ApiFailureKind.Transport when failure.TransportFailure == TransportFailureKind.Timeout =>
                "API Интерсвязи не ответил вовремя. Повторите попытку.",
            Is74ApiFailureKind.Unauthorized => "API отклонил авторизацию.",
            Is74ApiFailureKind.HttpStatus => $"API Интерсвязи вернул HTTP {failure.StatusCode}.",
            Is74ApiFailureKind.InvalidJson or Is74ApiFailureKind.InvalidPayload =>
                "API Интерсвязи вернул неожиданный формат ответа.",
            _ => $"Ошибка API Интерсвязи: {failure.Kind}."
        };
    }

    private enum UpdateApplyProgressStage
    {
        ValidatingExecutable,
        ExecutableValidated,
        StoppingAgent,
        AgentStopped,
        PreparingInstalledCopy,
        InstalledCopyPrepared,
        SchedulingReplacement,
        ReplacementScheduled
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        return 2;
    }
}
