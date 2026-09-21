using System.Diagnostics;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal static class Program
{
    private const string ProductVersion = "v0.1.0-alpha.13";
    private static bool forwardMenuWithoutReveal;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "menu";

        if (command == "update-apply")
        {
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
                "menu" => await RunMenuAsync().ConfigureAwait(false),
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
        Console.WriteLine("uninstall          Удалить программу и локальные данные");
        return 0;
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
        using var app = ApplicationRuntime.Create();
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
        await app.Dns.WarmKnownHostsAsync(TimeSpan.Zero).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(session.AccessEnd))
        {
            Console.WriteLine($"Сессия API действует до: {session.AccessEnd}");
        }
        Console.WriteLine("Теперь можно авторизовать Wi-Fi один раз сейчас или включить автоматическую авторизацию.");
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
        using var app = ApplicationRuntime.Create();
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
        using var app = ApplicationRuntime.Create();
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
        Console.WriteLine($"Фоновый агент              : {(agentRunning ? "работает" : "остановлен")}");
        Console.WriteLine($"Запуск вместе с Windows    : {(automaticAuthorizationEnabled ? "включён" : "выключен")}");
        Console.WriteLine($"Интернет                     : {(internet.Online ? "доступен" : "не подтверждён")}");
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
            Console.WriteLine($"Последний результат          : {state.LastResult}");
        }
        if (state.AutomaticStepOneAttempts > 0)
        {
            Console.WriteLine($"Автоматические попытки       : {state.AutomaticStepOneAttempts}/{app.Settings.MaxAutomaticStepOneAttempts}");
        }
        if (state.UserActionRequired)
        {
            Console.WriteLine("Требуется действие           : да — автоматические попытки остановлены");
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
        using var app = ApplicationRuntime.Create();
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
        ReportMenuBatchProgress(progress, "Автозапуск включён, фоновый агент запущен");
        app.Logger.Write(DiagnosticLevel.Info, "autostart.enabled mode=hkcu-run installed-copy=true");
        if (!quiet)
        {
            Console.WriteLine("Автоматическая авторизация включена.");
            Console.WriteLine("Фоновый агент запущен и сразу проверит текущее подключение.");
            Console.WriteLine("Если вы подключены к Campus Wi-Fi и требуется авторизация, программа попробует выполнить её автоматически.");
            Console.WriteLine("Фоновый агент будет автоматически запускаться при входе в Windows.");
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
        using var app = ApplicationRuntime.Create();
        app.Autostart.Disable();
        ReportMenuBatchProgress(progress, "Автозапуск отключён, фоновый агент остановлен");
        app.Logger.Write(DiagnosticLevel.Info, "autostart.disabled mode=hkcu-run");
        if (!quiet)
        {
            Console.WriteLine("Автоматическая авторизация отключена.");
            Console.WriteLine("Фоновый агент остановлен.");
            Console.WriteLine("Программа больше не будет запускаться автоматически вместе с Windows.");
            Console.WriteLine("Разовая авторизация Wi-Fi по-прежнему доступна через пункт 2.");
        }
        return 0;
    }

    private static int ResetRegistration(bool quiet = false, Action<string>? progress = null)
    {
        using var app = ApplicationRuntime.Create();
        app.Autostart.Disable();
        ReportMenuBatchProgress(progress, "Автозапуск отключён, фоновый агент остановлен");
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
        using var app = ApplicationRuntime.Create();
        app.Autostart.Disable();
        app.Maintenance.PurgeAllData();
        Console.WriteLine("Автозапуск отключён, все локальные данные приложения удалены.");
        return 0;
    }

    private static int Uninstall(bool quiet = false, Action<string>? progress = null)
    {
        using var app = ApplicationRuntime.Create();
        app.Autostart.Disable();
        ReportMenuBatchProgress(progress, "Автозапуск отключён, фоновый агент остановлен");
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
        using var app = ApplicationRuntime.Create();
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
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var updater = new GitHubUpdateClient(http);
        var update = await updater.CheckForUpdateAsync(
            ProductVersion,
            includePrerelease: ProductVersion.Contains("-alpha.", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);

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
        Action<UpdateApplyProgressStage>? applyProgress = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var updater = new GitHubUpdateClient(http);
        var update = await updater.CheckForUpdateAsync(
            ProductVersion,
            includePrerelease: ProductVersion.Contains("-alpha.", StringComparison.OrdinalIgnoreCase),
            progress: downloadProgress).ConfigureAwait(false);

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
            applyProgress).ConfigureAwait(false);
    }

    private static async Task<bool> PrepareAndScheduleUpdateAsync(
        GitHubUpdateClient updater,
        UpdateDescriptor update,
        bool restartMenu,
        bool quiet,
        Action<UpdateProgressStage>? downloadProgress = null,
        Action<UpdateApplyProgressStage>? applyProgress = null)
    {
        if (!quiet) Console.WriteLine("Скачиваю обновление с GitHub Releases и проверяю SHA-256...");
        var prepared = await updater.DownloadAndVerifyAsync(update, downloadProgress).ConfigureAwait(false);

        try
        {
            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.ValidatingExecutable);
            ValidatePreparedExecutable(prepared.ExecutablePath);
            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.ExecutableValidated);

            using var app = ApplicationRuntime.Create();
            var currentExecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExecutable) || !File.Exists(currentExecutable))
                throw new InvalidOperationException("Не удалось определить текущий IS74Wifi.exe.");

            var installation = new ProgramInstallation();
            _ = app.Autostart.RemoveIfStale(installation.ExecutablePath);
            var autostartWasEnabled = app.Autostart.IsEnabledFor(installation.ExecutablePath);

            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.StoppingAgent);
            AgentProcessControl.StopAgentOrThrow();
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
            File.Copy(currentExecutable, helperPath, overwrite: true);

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

            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить процесс применения обновления.");
            app.Logger.Write(DiagnosticLevel.Info,
                $"update.scheduled from={ProductVersion} to={update.TagName} autostart={autostartWasEnabled}");
            ReportUpdateApplyProgress(applyProgress, UpdateApplyProgressStage.ReplacementScheduled);

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
        if (args.Length != 7 ||
            !int.TryParse(args[0], out var parentPid))
        {
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
            return 2;
        }

        if (!WaitForProcessExit(parentPid, TimeSpan.FromSeconds(30)))
        {
            return 3;
        }
        try
        {
            AgentProcessControl.StopAgentOrThrow(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        catch
        {
            return 3;
        }

        Directory.CreateDirectory(installation.InstallDirectory);
        var staged = installation.ExecutablePath + ".new";
        File.Copy(source, staged, overwrite: true);
        File.Move(staged, installation.ExecutablePath, overwrite: true);
        installation.WriteVersionMarker(version);
        new WindowsInstalledAppRegistration().Register(installation, version);

        if (restartAgent)
        {
            var agent = new ProcessStartInfo(installation.ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            agent.ArgumentList.Add("agent");
            _ = Process.Start(agent);
        }

        if (restartMenu)
        {
            var menu = new ProcessStartInfo(installation.ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = false
            };
            menu.ArgumentList.Add("menu");
            _ = Process.Start(menu);
        }

        ScheduleDirectoryCleanup(workingDirectory);
        return 0;
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

    private static async Task<int> RunMenuAsync()
    {
        var ui = new InteractiveTerminalUi(ProductVersion);
        var showReveal = !string.Equals(
            Environment.GetEnvironmentVariable("IS74W_SKIP_REVEAL"),
            "1",
            StringComparison.Ordinal);

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
                        history.Start("Проверяю подключение к сети Интерсвязи...");
                        ui.ShowActionProgress("АВТОРИЗАЦИЯ WI-FI", history, initialStatus);

                        var stepTwoAccepted = false;
                        var outcome = await RunManualAuthorizationAsync(stage =>
                        {
                            switch (stage)
                            {
                                case AuthorizationProgressStage.TargetWifiConfirmed:
                                    history.CompleteActive("Подключение к сети Интерсвязи подтверждено");
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

                    case InteractiveMenuAction.Exit:
                        Console.Clear();
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
        using var app = ApplicationRuntime.Create();
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

            history.Start("Подготавливаю сетевые адреса...");
            ui.ShowActionProgress("РЕГИСТРАЦИЯ", history, GetInteractiveStatusSnapshot());
            await app.Dns.WarmKnownHostsAsync(TimeSpan.Zero).ConfigureAwait(false);
            history.CompleteActive("Сетевые адреса подготовлены");
            history.AddSuccess("Регистрация завершена");

            await ui.ShowActionHistoryAsync(
                "РЕГИСТРАЦИЯ",
                history,
                GetInteractiveStatusSnapshot()).ConfigureAwait(false);
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
        using var app = ApplicationRuntime.Create();
        var installation = new ProgramInstallation();
        var secrets = app.Secrets.Load();
        var session = app.Session.Load();
        var state = app.RuntimeState.Load();
        var internet = await app.Internet.ProbeAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        var automatic = installation.IsInstalled && app.Autostart.IsEnabledFor(installation.ExecutablePath);
        var installedVersion = installation.ReadInstalledVersion();

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
            $"Регистрация: {(secrets is null ? "нет" : "сохранена")}",
            $"Телефон: {MaskPhone(secrets?.Phone)}",
            $"Автовход: {(automatic ? "включён" : "выключен")}",
            $"Агент: {(AgentProcessControl.IsAgentRunning() ? "работает" : "остановлен")}",
            $"API-сессия: {FormatSessionEnd(session?.AccessEnd)}"
        };

        if (state.LastAuthUtc is { } lastAuth)
            lines.Add($"Последняя Wi-Fi авторизация: {lastAuth.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        if (state.ExpectedExpiryUtc is { } expiry)
            lines.Add($"Ожидаемое окончание окна: {expiry.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(state.LastResult))
            lines.Add($"Последний результат: {state.LastResult}");
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
        InteractiveStatusSnapshot currentStatus)
    {
        var history = new InteractiveActionHistory();
        history.Start("Запрашиваю список GitHub Releases...");
        ui.ShowActionProgress("ОБНОВЛЕНИЯ", history, currentStatus);
        var progressTitle = "ОБНОВЛЕНИЯ";

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
                    history.Start("Распаковываю IS74Wifi.exe...");
                    break;
                case UpdateProgressStage.PackageExtracted:
                    history.CompleteActive("IS74Wifi.exe распакован");
                    break;
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
                    history.Start("Останавливаю фоновый агент...");
                    break;
                case UpdateApplyProgressStage.AgentStopped:
                    history.CompleteActive("Фоновый агент остановлен");
                    break;
                case UpdateApplyProgressStage.PreparingInstalledCopy:
                    history.Start("Подготавливаю установленную копию...");
                    break;
                case UpdateApplyProgressStage.InstalledCopyPrepared:
                    history.CompleteActive("Установленная копия подготовлена");
                    break;
                case UpdateApplyProgressStage.SchedulingReplacement:
                    history.Start("Планирую замену EXE после закрытия программы...");
                    break;
                case UpdateApplyProgressStage.ReplacementScheduled:
                    history.CompleteActive("Замена EXE запланирована");
                    break;
            }
            RenderProgress();
        }

        try
        {
            var update = await updater.CheckForUpdateAsync(
                ProductVersion,
                includePrerelease: ProductVersion.Contains("-alpha.", StringComparison.OrdinalIgnoreCase),
                progress: ReportDownloadProgress).ConfigureAwait(false);

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
                applyProgress: ReportApplyProgress).ConfigureAwait(false);

            if (scheduled)
            {
                history.AddSuccess($"Обновление {update.TagName} подготовлено к установке");
                history.AddInfo("После возврата программа закроется и заменит EXE");
            }

            await ui.ShowActionHistoryAsync(
                "ОБНОВЛЕНИЕ",
                history,
                GetInteractiveStatusSnapshot()).ConfigureAwait(false);
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

    private static InteractiveStatusSnapshot GetInteractiveStatusSnapshot(bool? internetOverride = null)
    {
        using var app = ApplicationRuntime.Create();
        return BuildInteractiveStatusSnapshot(app, internetOverride);
    }

    private static async Task<InteractiveStatusSnapshot> RefreshInteractiveStatusAsync()
    {
        using var app = ApplicationRuntime.Create();
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
        var authorizationActive = runtime.ExpectedExpiryUtc is { } expiry
            ? expiry > DateTimeOffset.UtcNow
            : runtime.LastAuthUtc is not null;

        var internet = internetOverride ?? runtime.InternetConfirmed;
        return new InteractiveStatusSnapshot(
            Installed: installation.IsInstalled,
            Registered: secrets is not null,
            InternetAvailable: internet,
            WifiAuthorizationActive: authorizationActive,
            AutomaticAuthorizationEnabled: automatic,
            AgentRunning: agentRunning,
            MaskedPhone: MaskPhone(secrets?.Phone),
            ApiSessionEnd: FormatSessionEnd(session?.AccessEnd),
            LastResult: string.IsNullOrWhiteSpace(runtime.LastResult) ? "—" : runtime.LastResult,
            Version: ProductVersion);
    }

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

    private static async Task<int> RunAgentAsync()
    {
        using var app = ApplicationRuntime.Create();
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

                var delay = app.Agent.GetSleepDelay();
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
