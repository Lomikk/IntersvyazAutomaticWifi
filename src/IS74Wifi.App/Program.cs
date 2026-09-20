using System.Diagnostics;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal static class Program
{
    private const string ProductVersion = "v0.1.0-alpha.8";

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "menu";

        if (command == "agent")
        {
            return await RunAgentAsync().ConfigureAwait(false);
        }

        ConsoleSession.EnsureInteractiveConsole();

        try
        {
            return command switch
            {
                "version" or "--version" or "-v" => PrintVersion(),
                "help" or "--help" or "-h" => PrintHelp(),
                "contract" => PrintContract(),
                "register" => await RegisterAsync().ConfigureAwait(false),
                "connect" => await ConnectAsync().ConfigureAwait(false),
                "status" => await PrintStatusAsync().ConfigureAwait(false),
                "install" => InstallAutostart(),
                "disable-autostart" => DisableAutostart(),
                "uninstall" => Uninstall(),
                "reset" => ResetRegistration(),
                "purge" => Purge(),
                "logs" => OpenLogs(),
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
        Console.WriteLine("IS74Wifi C# alpha client");
        Console.WriteLine("Commands: register, connect, status, install, disable-autostart, uninstall, reset, purge, logs, version, help, contract");
        Console.WriteLine("The PowerShell runtime remains the fallback/reference client until field parity is complete.");
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
        if (smsCode.Length == 0 || smsCode.Any(c => c is < '0' or > '9'))
        {
            throw new InvalidOperationException("SMS-код должен состоять из цифр.");
        }

        var checkedCode = await app.Api.CheckConfirmationAsync(phone, smsCode, deviceId).ConfigureAwait(false);
        if (!checkedCode.IsSuccess)
        {
            app.Logger.Write(DiagnosticLevel.Warn, $"registration.failed stage=check-confirm failure={checkedCode.Failure!.Kind}");
            throw new InvalidOperationException(DescribeApiFailure(checkedCode.Failure));
        }

        var confirmation = checkedCode.Value!;
        app.Logger.Write(DiagnosticLevel.Info, $"registration.linked-profiles count={confirmation.Addresses.Count}");
        var selectedUserId = SelectLinkedProfile(confirmation.Addresses);

        var sessionResult = await app.Api.GetTokenAsync(
            confirmation.AuthId,
            deviceId,
            selectedUserId).ConfigureAwait(false);
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
        Console.WriteLine("Регистрация завершена. Bearer и номер защищены DPAPI текущего пользователя.");
        await app.Dns.WarmKnownHostsAsync(TimeSpan.Zero).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(session.AccessEnd))
        {
            Console.WriteLine($"Сессия API действует до: {session.AccessEnd}");
        }
        Console.WriteLine("Первую Wi-Fi авторизацию выполните командой connect; она задаст 24-часовую точку отсчёта для агента.");
        return 0;
    }

    private static string? SelectLinkedProfile(IReadOnlyList<LinkedAddressProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            return null;
        }

        if (profiles.Count == 1)
        {
            Console.WriteLine($"Найден связанный профиль: {profiles[0].DisplayName}");
            return profiles[0].UserId;
        }

        Console.WriteLine();
        Console.WriteLine("С номером телефона связано несколько адресов. Выберите профиль:");
        for (var i = 0; i < profiles.Count; i++)
        {
            Console.WriteLine($"{i + 1}. {profiles[i].DisplayName}");
        }
        Console.WriteLine("0. Отменить регистрацию");

        while (true)
        {
            Console.Write("Выберите профиль: ");
            var input = (Console.ReadLine() ?? string.Empty).Trim();
            if (input == "0")
            {
                throw new InvalidOperationException("Регистрация отменена пользователем.");
            }

            if (int.TryParse(input, out var selected) && selected >= 1 && selected <= profiles.Count)
            {
                return profiles[selected - 1].UserId;
            }

            Console.WriteLine($"Введите число от 1 до {profiles.Count} или 0 для отмены.");
        }
    }

    private static async Task<int> ConnectAsync()
    {
        using var app = ApplicationRuntime.Create();
        app.Logger.Write(DiagnosticLevel.Info, "cli.start command=connect runtime=csharp");
        var secrets = app.Secrets.Load() ?? throw new InvalidOperationException("Устройство не зарегистрировано. Сначала выполните register.");
        var deviceId = app.DeviceIdentity.GetOrCreate();

        Console.WriteLine("Начинаю Wi-Fi авторизацию...");
        var outcome = await app.Authorization.RunAsync(new AuthorizationRequest(
            secrets.Token,
            secrets.Phone,
            deviceId,
            AuthorizationAttemptReason.Manual,
            Force: true)).ConfigureAwait(false);

        return PrintAuthorizationOutcome(outcome);
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
        Console.WriteLine($"Регистрация : {(secrets is null ? "нет" : "есть")}");
        Console.WriteLine($"Телефон     : {MaskPhone(secrets?.Phone)}");
        Console.WriteLine($"Автозапуск  : {(app.Autostart.IsEnabled() ? "включён" : "выключен")}");
        Console.WriteLine($"Интернет    : {(internet.Online ? "доступен" : "не подтверждён")}");
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
            Console.WriteLine($"Авто stepOne                 : {state.AutomaticStepOneAttempts}/{app.Settings.MaxAutomaticStepOneAttempts}");
        }
        if (state.UserActionRequired)
        {
            Console.WriteLine("Требуется действие           : да — автоматические попытки остановлены");
        }
        Console.WriteLine($"Данные приложения            : {app.Paths.Root}");
        Console.WriteLine($"Диагностический журнал       : {app.Paths.DiagnosticLogFile}");
        Console.WriteLine();
        return 0;
    }

    private static int InstallAutostart()
    {
        using var app = ApplicationRuntime.Create();
        if (app.Secrets.Load() is null)
        {
            throw new InvalidOperationException("Сначала зарегистрируйте устройство.");
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("Не удалось определить путь к IS74Wifi.exe.");
        }

        app.Autostart.Enable(executable, startNow: true);
        app.Logger.Write(DiagnosticLevel.Info, "autostart.enabled mode=hkcu-run");
        Console.WriteLine("Автозапуск включён через текущего пользователя Windows. Агент запущен без консольного окна.");
        Console.WriteLine("Перед удалением папки программы выполните uninstall или disable-autostart.");
        return 0;
    }

    private static int DisableAutostart()
    {
        using var app = ApplicationRuntime.Create();
        app.Autostart.Disable();
        _ = AgentProcessControl.WaitForAgentExit(TimeSpan.FromSeconds(5));
        app.Logger.Write(DiagnosticLevel.Info, "autostart.disabled mode=hkcu-run");
        Console.WriteLine("Автозапуск отключён.");
        return 0;
    }

    private static int ResetRegistration()
    {
        using var app = ApplicationRuntime.Create();
        app.Autostart.Disable();
        _ = AgentProcessControl.WaitForAgentExit(TimeSpan.FromSeconds(5));
        app.Maintenance.ResetRegistration();
        app.Logger.Write(DiagnosticLevel.Info, "registration.reset");
        Console.WriteLine("Регистрация и локальная Wi-Fi сессия удалены. Настройки и диагностические логи сохранены.");
        return 0;
    }

    private static int Purge()
    {
        using var app = ApplicationRuntime.Create();
        app.Autostart.Disable();
        _ = AgentProcessControl.WaitForAgentExit(TimeSpan.FromSeconds(5));
        app.Maintenance.PurgeAllData();
        Console.WriteLine("Автозапуск отключён, все локальные данные приложения удалены.");
        return 0;
    }

    private static int Uninstall()
    {
        Purge();
        Console.WriteLine("Теперь можно удалить папку с IS74Wifi.exe.");
        return 0;
    }

    private static int OpenLogs()
    {
        using var app = ApplicationRuntime.Create();
        app.Paths.EnsureDirectories();
        Console.WriteLine($"Журнал: {app.Paths.DiagnosticLogFile}");
        Console.WriteLine("Для диагностики можно прислать diagnostic.log и diagnostic.N.log из этой папки.");
        try
        {
            _ = Process.Start(new ProcessStartInfo(app.Paths.LogDirectory) { UseShellExecute = true });
        }
        catch
        {
        }
        return 0;
    }

    private static async Task<int> RunMenuAsync()
    {
        while (true)
        {
            await PrintStatusAsync().ConfigureAwait(false);
            Console.WriteLine("1. Зарегистрировать устройство");
            Console.WriteLine("2. Авторизовать Wi-Fi сейчас");
            Console.WriteLine("3. Включить автозапуск");
            Console.WriteLine("4. Отключить автозапуск");
            Console.WriteLine("5. Показать статус");
            Console.WriteLine("6. Сбросить регистрацию");
            Console.WriteLine("7. Удалить программу (автозапуск и данные)");
            Console.WriteLine("8. Открыть диагностические логи");
            Console.WriteLine("0. Выход");
            Console.Write("Выберите действие: ");
            var choice = Console.ReadLine()?.Trim();

            try
            {
                switch (choice)
                {
                    case "1": await RegisterAsync().ConfigureAwait(false); break;
                    case "2": await ConnectAsync().ConfigureAwait(false); break;
                    case "3": InstallAutostart(); break;
                    case "4": DisableAutostart(); break;
                    case "5": await PrintStatusAsync().ConfigureAwait(false); break;
                    case "6":
                        Console.Write("Удалить регистрацию и локальную сессию? Введите YES: ");
                        if (Console.ReadLine() == "YES") ResetRegistration();
                        break;
                    case "7":
                        Console.Write("Удалить автозапуск и ВСЕ данные приложения? Введите PURGE: ");
                        if (Console.ReadLine() == "PURGE") Uninstall();
                        break;
                    case "8": OpenLogs(); break;
                    case "0": return 0;
                    default: Console.WriteLine("Неизвестный пункт."); break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }

            Console.WriteLine();
            Console.Write("Enter для продолжения...");
            Console.ReadLine();
        }
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

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        return 2;
    }
}
