using Microsoft.Win32;
using System.Net;
using IS74Wifi.Core;

internal static class AgentContractTests
{
    public static async Task RunAsync()
    {
        TestSleepPolicy();
        TestTelemetryUploadSafety();
        TestBackgroundDueScheduling();
        TestPowerResumeEvents();
        TestAutostartCommand();
        TestAutostartRegistrationState();
        TestAgentPidRecord();
        await TestDaytimeTickDoesNotProbeAsync();
        await TestExpiryIsAuthoritativeAsync();
        await TestNetworkCheckPolicyAsync();
        await TestPreExpiryNeedsTwoCaptiveResponsesAsync();
        await TestPreExpiryTransportFailureDoesNotAuthorizeAsync();
        await TestAfterExpiryEdgeWatchConfirmsCaptiveAsync();
        await TestNotificationLifecycleAsync();
        await TestFinalAttemptShowsSingleTerminalToastAsync();
        await TestExhaustedCycleNotifiesWithoutTargetWifiAsync();
    }

    private static void TestSleepPolicy()
    {
        var settings = new AppSettings
        {
            AgentPollSeconds = 15,
            GuardWindowSeconds = 10,
            GuardProbeIntervalMilliseconds = 250
        };
        var expiry = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var state = new RuntimeState { ExpectedExpiryUtc = expiry };

        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddHours(-12)) ==
               TimeSpan.FromHours(11) + TimeSpan.FromMinutes(55),
            "healthy daytime agent should sleep straight to the five-minute reminder");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddHours(-23)) ==
               TimeSpan.FromHours(22) + TimeSpan.FromMinutes(55),
            "healthy agent should not poll throughout its 23-hour idle period");
        Assert(AgentTiming.GetSleepDelay(new RuntimeState(), settings, expiry.AddHours(-12)) == TimeSpan.FromMinutes(15),
            "clean install without an expiry baseline must also stay idle");
        Assert(AgentTiming.GetSleepDelay(state with { UserActionRequired = true }, settings,
                   expiry.AddHours(1)) == TimeSpan.FromMinutes(15),
            "terminal state must not keep waking once a second after expiry");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddMinutes(-20)) == TimeSpan.FromMinutes(15),
            "long sleep must stop at the five-minute reminder boundary");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddMinutes(-6)) == TimeSpan.FromMinutes(1),
            "long sleep overshot the expiry reminder boundary");
        var widenedGuard = settings with { GuardWindowSeconds = 600 };
        Assert(AgentTiming.GetSleepDelay(state, widenedGuard, expiry.AddMinutes(-15)) == TimeSpan.FromMinutes(5),
            "wide guard must interrupt long idle before its earlier guard start");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddMinutes(-5)) == TimeSpan.FromSeconds(15),
            "five-minute approach should retain the old lightweight tick");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddMinutes(-1)) == TimeSpan.FromSeconds(15),
            "pre-guard approach cadence changed");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddSeconds(-20)) == TimeSpan.FromSeconds(10),
            "agent sleep crossed guard start");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddSeconds(-5)) == TimeSpan.FromMilliseconds(250),
            "agent guard cadence changed");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddSeconds(20)) == TimeSpan.FromMinutes(1),
            "overdue agent must not spin on one-second housekeeping checks");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddSeconds(20), networkPolicySatisfied: false) ==
               TimeSpan.FromMinutes(5),
            "overdue agent on unrelated Wi-Fi must wait for a network event or sparse fallback");

        state = state with { NextAutomaticRetryUtc = expiry.AddSeconds(30) };
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddSeconds(20)) == TimeSpan.FromSeconds(10),
            "agent did not wake at scheduled retry");
        Assert(AgentTiming.GetSleepDelay(state with { NextAutomaticRetryUtc = expiry.AddHours(1) },
                   settings, expiry.AddSeconds(20)) == TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(40),
            "a scheduled retry must not be interrupted by an unnecessary 15-second poll");
        Assert(AgentTiming.GetSleepDelay(state with { NextAutomaticRetryUtc = expiry.AddHours(1) },
                   settings, expiry.AddSeconds(20), networkPolicySatisfied: false) == TimeSpan.FromMinutes(5),
            "missed network changes still need a sparse fallback before a distant retry");
    }

    private static void TestTelemetryUploadSafety()
    {
        var now = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        var settings = new AppSettings();

        // Upload permission must be based on the authorization deadline, not
        // on whichever short or long sleep cadence the agent currently uses.
        var healthy = new RuntimeState { ExpectedExpiryUtc = now.AddHours(12) };
        Assert(AgentTiming.GetSleepDelay(healthy, settings, now) ==
               TimeSpan.FromHours(11) + TimeSpan.FromMinutes(55),
            "default agent must wait until its next actual authorization deadline");
        Assert(AgentTiming.CanUploadTelemetry(healthy, settings, now),
            "background telemetry is still blocked by the default 15-second tick");

        Assert(AgentTiming.CanUploadTelemetry(new RuntimeState(), settings, now),
            "clean install should upload registration telemetry without a Wi-Fi expiry baseline");
        Assert(AgentTiming.CanUploadTelemetry(healthy with { UserActionRequired = true }, settings, now),
            "terminal authorization state should not strand the telemetry queue");

        Assert(AgentTiming.CanUploadTelemetry(healthy with { ExpectedExpiryUtc = now.AddMinutes(2) }, settings, now),
            "telemetry was blocked despite a safe interval before expiry");
        Assert(!AgentTiming.CanUploadTelemetry(healthy with { ExpectedExpiryUtc = now.AddMinutes(1) }, settings, now),
            "telemetry must stop at the one-minute safety boundary");
        Assert(!AgentTiming.CanUploadTelemetry(healthy with { ExpectedExpiryUtc = now.AddSeconds(10) }, settings, now),
            "telemetry must not compete with the approaching guard window");
        Assert(!AgentTiming.CanUploadTelemetry(healthy with { ExpectedExpiryUtc = now.AddSeconds(10) },
                    settings, now, networkPolicySatisfied: false),
            "a changing SSID must not let telemetry delay the active guard");
        Assert(!AgentTiming.CanUploadTelemetry(healthy with { ExpectedExpiryUtc = now.AddSeconds(-10) }, settings, now),
            "an imminent automatic retry must block telemetry");

        var expired = healthy with { ExpectedExpiryUtc = now.AddMinutes(-5) };
        Assert(AgentTiming.CanUploadTelemetry(expired with { NextAutomaticRetryUtc = now.AddMinutes(3) }, settings, now),
            "a safely deferred automatic retry should allow queued telemetry to flush");
        Assert(!AgentTiming.CanUploadTelemetry(expired with { NextAutomaticRetryUtc = now.AddSeconds(30) }, settings, now),
            "telemetry must not run when the scheduled retry is imminent");
        Assert(AgentTiming.CanUploadTelemetry(expired, settings, now, networkPolicySatisfied: false),
            "telemetry must resume away from the target Wi-Fi once the active guard has passed");

        var largerUpload = settings with
        {
            TelemetryHttpTimeoutMilliseconds = 30000,
            TelemetryMaxBatchesPerFlush = 8
        };
        Assert(!AgentTiming.CanUploadTelemetry(
                healthy with { ExpectedExpiryUtc = now.AddMinutes(4) }, largerUpload, now),
            "larger upload settings must reserve enough time before authorization");
        Assert(AgentTiming.CanUploadTelemetry(
                healthy with { ExpectedExpiryUtc = now.AddMinutes(5) }, largerUpload, now),
            "safety budget unnecessarily blocked an upload with enough time");
    }

    private static void TestBackgroundDueScheduling()
    {
        var now = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        var idle = TimeSpan.FromMinutes(15);
        Assert(AgentTiming.BoundSleepByBackgroundWork(idle, now, null, null) == idle,
            "no background work should preserve long idle sleep");
        Assert(AgentTiming.BoundSleepByBackgroundWork(idle, now, now.AddHours(2), now.AddMinutes(5)) ==
               TimeSpan.FromMinutes(5),
            "telemetry retry must wake the agent before long idle timeout");
        Assert(AgentTiming.BoundSleepByBackgroundWork(idle, now, now.AddMinutes(3), now.AddMinutes(5)) ==
               TimeSpan.FromMinutes(3),
            "next update check must not be delayed by telemetry or idle heartbeat");
        Assert(AgentTiming.BoundSleepByBackgroundWork(TimeSpan.FromMilliseconds(250), now,
                   now.AddMinutes(3), now.AddMinutes(5)) == TimeSpan.FromMilliseconds(250),
            "background scheduling must not slow the active guard");
        Assert(AgentTiming.BoundSleepByBackgroundWork(idle, now, now.AddSeconds(-1), null) ==
               TimeSpan.FromMinutes(1),
            "overdue maintenance must not cause a busy loop");
        Assert(AgentTiming.BoundSleepByBackgroundWork(idle, now, null, now.AddMilliseconds(50)) ==
               TimeSpan.FromMilliseconds(100),
            "very near background deadlines must respect minimum sleep");
        var dayIdle = AgentTiming.GetSleepDelay(
            new RuntimeState { ExpectedExpiryUtc = now.AddHours(24) }, new AppSettings(), now);
        Assert(AgentTiming.BoundSleepByBackgroundWork(dayIdle, now,
                   now.AddHours(18), now.AddHours(12)) == TimeSpan.FromHours(12),
            "12-hour telemetry maintenance must wake a day-long authorization sleep");
    }

    private static void TestPowerResumeEvents()
    {
        Assert(AgentPowerResumeMonitor.IsResumeEvent(0x12),
            "automatic resume must wake the agent to recalculate its UTC deadlines");
        Assert(AgentPowerResumeMonitor.IsResumeEvent(0x07),
            "user-initiated resume must wake the agent");
        Assert(!AgentPowerResumeMonitor.IsResumeEvent(0x04),
            "suspend notification must not start an authorization");
        if (OperatingSystem.IsWindows())
        {
            using var monitor = AgentPowerResumeMonitor.TryRegister(
                () => { }, error => throw new InvalidOperationException(
                    $"Windows power-resume registration failed: {error}"));
            Assert(monitor is not null, "native Windows power-resume monitor is unavailable");
        }
        else
        {
            Assert(AgentPowerResumeMonitor.TryRegister(() => { }) is null,
                "non-Windows test environments must not attempt native power registration");
        }
    }

    private static void TestAutostartCommand()
    {
        var path = Path.Combine("C:\\Program Files", "IS74 Wifi", "IS74Wifi.exe");
        var command = WindowsAutostartService.BuildCommand(path);
        Assert(command == $"\"{Path.GetFullPath(path)}\" agent", "HKCU Run command quoting changed");
        Assert(WindowsAutostartService.CommandMatchesExecutable(command, path), "canonical HKCU Run command was not recognized");
        Assert(!WindowsAutostartService.CommandMatchesExecutable($"\"{Path.GetFullPath(path)}\" menu", path),
            "non-agent HKCU Run command was accepted as canonical");
    }

    private static void TestAutostartRegistrationState()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var valueName = "IS74WifiContract-" + Guid.NewGuid().ToString("N");
        var executable = Path.Combine(Path.GetTempPath(), valueName + ".exe");
        File.WriteAllText(executable, "placeholder");
        var service = new WindowsAutostartService(valueName);

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true)!)
            {
                key.SetValue(valueName, WindowsAutostartService.BuildCommand(executable), RegistryValueKind.String);
            }

            Assert(service.GetRegistrationState(executable) == AutostartRegistrationState.Enabled,
                "canonical HKCU Run registration was not recognized as enabled");

            File.Delete(executable);
            Assert(service.GetRegistrationState(executable) == AutostartRegistrationState.Stale,
                "HKCU Run registration targeting a missing EXE was not marked stale");
            Assert(service.RemoveIfStale(executable), "stale HKCU Run registration was not removed");
            Assert(service.GetRegistrationState(executable) == AutostartRegistrationState.Disabled,
                "removed HKCU Run registration still appeared enabled");
        }
        finally
        {
            try { File.Delete(executable); } catch { }
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private static void TestAgentPidRecord()
    {
        var root = Path.Combine(Path.GetTempPath(), "IS74Wifi-agent-pid-" + Guid.NewGuid().ToString("N"));
        try
        {
            AgentProcessControl.RegisterCurrentAgentProcess(root);
            var path = AgentProcessControl.GetPidFilePath(root);
            Assert(File.Exists(path), "agent PID file was not created");

            var lines = File.ReadAllLines(path);
            Assert(lines.Length >= 2, "agent PID file format changed");
            Assert(lines[0] == Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "agent PID file did not record the current process");
            Assert(ProgramInstallation.PathsEqual(lines[1], Environment.ProcessPath!),
                "agent PID file did not record the current executable");

            AgentProcessControl.ClearCurrentAgentProcess(root);
            Assert(!File.Exists(path), "agent PID file was not cleared by its owner");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task TestDaytimeTickDoesNotProbeAsync()
    {
        var now = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);
        using var fixture = AgentFixture.Create(now);
        fixture.SaveState(new RuntimeState { ExpectedExpiryUtc = now.AddHours(12) });

        await fixture.Agent.TickAsync();
        Assert(fixture.Internet.Calls == 0,
            "daytime tick must not probe Internet before the active guard");
        Assert(fixture.Authorization.Calls == 0,
            "daytime tick must not attempt authorization before the active guard");
        Assert(fixture.Agent.GetSleepDelay() == TimeSpan.FromHours(11) + TimeSpan.FromMinutes(55),
            "daytime tick must wait until the five-minute expiry reminder, not poll every 15 minutes");
    }

    private static async Task TestExpiryIsAuthoritativeAsync()
    {
        using var fixture = AgentFixture.Create(new DateTimeOffset(2026, 9, 18, 12, 0, 1, TimeSpan.Zero));
        fixture.SaveState(new RuntimeState
        {
            ExpectedExpiryUtc = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero)
        });

        await fixture.Agent.TickAsync();
        Assert(fixture.Authorization.Calls == 1, "agent did not authorize immediately at/after expiry");
        Assert(fixture.Internet.Calls == 0, "agent spent time probing Internet at/after authoritative expiry");
        Assert(fixture.Authorization.LastRequest?.Reason == AuthorizationAttemptReason.Automatic,
            "first automatic attempt reason changed");
        Assert(fixture.Authorization.LastRequest?.Force == true, "agent did not force the expiry-triggered authorization");
    }

    private static async Task TestNetworkCheckPolicyAsync()
    {
        var expiry = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        using (var guarded = AgentFixture.Create(
                   expiry.AddSeconds(1),
                   wifi: new NonTargetWifi()))
        {
            guarded.SaveState(new RuntimeState { ExpectedExpiryUtc = expiry });
            await guarded.Agent.TickAsync();
            Assert(guarded.Authorization.Calls == 0,
                "agent authorized on a non-target network while the network check was enabled");
        }

        using var ignored = AgentFixture.Create(
            expiry.AddSeconds(1),
            new AppSettings { IgnoreNetworkCheck = true },
            wifi: new NonTargetWifi());
        ignored.SaveState(new RuntimeState { ExpectedExpiryUtc = expiry });
        await ignored.Agent.TickAsync();
        Assert(ignored.Authorization.Calls == 1,
            "agent did not authorize when the user chose to ignore the network check");
    }

    private static async Task TestPreExpiryNeedsTwoCaptiveResponsesAsync()
    {
        var now = new DateTimeOffset(2026, 9, 18, 11, 59, 0, TimeSpan.Zero);
        using var fixture = AgentFixture.Create(now, new AppSettings
        {
            GuardWindowSeconds = 120,
            GuardProbeIntervalMilliseconds = 100,
            GuardProbeTimeoutMilliseconds = 100
        },
        new ScriptedInternetProbe(
            Probe(false, true),
            Probe(false, true)));
        fixture.SaveState(new RuntimeState { ExpectedExpiryUtc = now.AddSeconds(30) });

        await fixture.Agent.TickAsync();
        Assert(fixture.Internet.Calls == 2, "pre-expiry captive confirmation did not use two HTTP observations");
        Assert(fixture.Authorization.Calls == 1, "confirmed captive state did not trigger pre-expiry authorization");
    }

    private static async Task TestPreExpiryTransportFailureDoesNotAuthorizeAsync()
    {
        var now = new DateTimeOffset(2026, 9, 18, 11, 59, 0, TimeSpan.Zero);
        using var fixture = AgentFixture.Create(now, new AppSettings
        {
            GuardWindowSeconds = 120,
            GuardProbeIntervalMilliseconds = 100,
            GuardProbeTimeoutMilliseconds = 100
        },
        new ScriptedInternetProbe(Probe(false, false, TransportFailureKind.DnsUnavailable)));
        fixture.SaveState(new RuntimeState { ExpectedExpiryUtc = now.AddSeconds(30) });

        await fixture.Agent.TickAsync();
        Assert(fixture.Internet.Calls == 1, "ambiguous pre-expiry transport failure unexpectedly retried immediately");
        Assert(fixture.Authorization.Calls == 0, "DNS failure before expiry burned an authorization attempt");
    }

    private static async Task TestAfterExpiryEdgeWatchConfirmsCaptiveAsync()
    {
        var expiry = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        using var fixture = AgentFixture.Create(
            expiry.AddSeconds(5),
            internet: new ScriptedInternetProbe(Probe(false, true), Probe(false, true)));
        fixture.SaveState(new RuntimeState
        {
            ExpectedExpiryUtc = expiry,
            AutomaticStepOneAttempts = 1,
            LastAttemptUtc = expiry.AddSeconds(1),
            EdgeWatchActive = true
        });

        await fixture.Agent.TickAsync();
        Assert(fixture.Internet.Calls == 2,
            "post-expiry edge watch should confirm captive state with two observations");
        Assert(fixture.Authorization.Calls == 1,
            "confirmed post-expiry captive state must trigger automatic authorization");
    }

    private static async Task TestFinalAttemptShowsSingleTerminalToastAsync()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 20, TimeSpan.Zero);
        using var fixture = AgentFixture.Create(now);
        fixture.SaveState(new RuntimeState
        {
            ExpectedExpiryUtc = now.AddMinutes(-1),
            AutomaticStepOneAttempts = 3,
            LastResult = "step-one-retryable-error"
        });
        fixture.Authorization.Outcome = new AuthorizationOutcome(
            AuthorizationOutcomeKind.RetryableStepOne, null, null, null, null);
        fixture.Authorization.StateEffect = _ => fixture.SaveState(fixture.StateStore.Load() with
        {
            AutomaticStepOneAttempts = 4,
            UserActionRequired = true,
            NextAutomaticRetryUtc = null,
            LastResult = "automatic-step-one-limit"
        });

        await fixture.Agent.TickAsync();
        Assert(fixture.Authorization.Calls == 1, "final available attempt was not executed");
        Assert(fixture.Notifications.Items.Count(n => n.Severity == AgentNotificationSeverity.Error) == 1,
            "the last retryable stepOne failure must emit one important terminal toast");
        Assert(fixture.Notifications.Items.All(n => !n.Title.Contains("отложена", StringComparison.Ordinal)),
            "terminal limit must not show a misleading deferred-retry notification");
        var count = fixture.Notifications.Items.Count;
        await fixture.Agent.TickAsync();
        Assert(fixture.Authorization.Calls == 1 && fixture.Notifications.Items.Count == count,
            "terminal state must neither retry nor show duplicate toasts");
        Assert(fixture.Agent.GetSleepDelay() == TimeSpan.FromMinutes(15),
            "exhausted attempts must stop the active wake-up loop");
    }

    private static async Task TestExhaustedCycleNotifiesWithoutTargetWifiAsync()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 1, 0, TimeSpan.Zero);
        using var fixture = AgentFixture.Create(now, wifi: new NonTargetWifi());
        fixture.SaveState(new RuntimeState
        {
            ExpectedExpiryUtc = now.AddMinutes(-1),
            AutomaticStepOneAttempts = 4,
            NextAutomaticRetryUtc = now.AddHours(1)
        });

        await fixture.Agent.TickAsync();
        Assert(fixture.StateStore.Load().UserActionRequired,
            "an exhausted cycle must stop regardless of the current SSID or pending retry");
        Assert(fixture.Authorization.Calls == 0, "an exhausted cycle must never start another attempt");
        Assert(fixture.Notifications.Items.Count(n => n.Severity == AgentNotificationSeverity.Error) == 1,
            "an exhausted cycle must show one terminal toast even away from the target network");
        await fixture.Agent.TickAsync();
        Assert(fixture.Notifications.Items.Count == 1, "terminal toast must not repeat on later ticks");
    }

    private static async Task TestNotificationLifecycleAsync()
    {
        var reminderNow = new DateTimeOffset(2026, 9, 18, 11, 56, 0, TimeSpan.Zero);
        using (var reminderFixture = AgentFixture.Create(reminderNow))
        {
            reminderFixture.SaveState(new RuntimeState { ExpectedExpiryUtc = reminderNow.AddMinutes(4) });
            await reminderFixture.Agent.TickAsync();

            Assert(reminderFixture.Authorization.Calls == 0, "expiry reminder unexpectedly started authorization");
            Assert(reminderFixture.Notifications.Items.Count == 1, "five-minute notification was not emitted exactly once");
            Assert(reminderFixture.Notifications.Items[0].Importance == AgentNotificationImportance.Routine,
                "expiry reminder became an important notification");

            await reminderFixture.Agent.TickAsync();
            Assert(reminderFixture.Notifications.Items.Count == 1, "expiry reminder repeated for the same auth window");
        }

        var expiry = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        using var successFixture = AgentFixture.Create(expiry.AddSeconds(1));
        successFixture.SaveState(new RuntimeState { ExpectedExpiryUtc = expiry });
        await successFixture.Agent.TickAsync();

        Assert(successFixture.Notifications.Items.Count == 2, "automatic authorization did not emit start and result notifications");
        Assert(successFixture.Notifications.Items[0].Importance == AgentNotificationImportance.Routine,
            "authorization start notification importance changed");
        Assert(successFixture.Notifications.Items[1].Importance == AgentNotificationImportance.Important,
            "successful authorization should be an important notification");
        Assert(successFixture.Notifications.Items[1].Severity == AgentNotificationSeverity.Success,
            "successful authorization notification severity changed");
    }

    private static InternetProbeResult Probe(
        bool online,
        bool responseReceived,
        TransportFailureKind failure = TransportFailureKind.None) =>
        new(
            Online: online,
            HttpResponseReceived: responseReceived,
            StatusCode: responseReceived ? HttpStatusCode.OK : null,
            Body: responseReceived ? (online ? null : "captive") : null,
            FailureKind: failure,
            Elapsed: TimeSpan.Zero);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class AgentFixture : IDisposable
    {
        private AgentFixture(
            string root,
            RuntimeStateStore stateStore,
            AgentService agent,
            RecordingAuthorizationRunner authorization,
            ScriptedInternetProbe internet,
            RecordingNotificationSink notifications)
        {
            Root = root;
            StateStore = stateStore;
            Agent = agent;
            Authorization = authorization;
            Internet = internet;
            Notifications = notifications;
        }

        public string Root { get; }
        public RuntimeStateStore StateStore { get; }
        public AgentService Agent { get; }
        public RecordingAuthorizationRunner Authorization { get; }
        public ScriptedInternetProbe Internet { get; }
        public RecordingNotificationSink Notifications { get; }

        public static AgentFixture Create(
            DateTimeOffset now,
            AppSettings? settings = null,
            ScriptedInternetProbe? internet = null,
            IWifiEnvironment? wifi = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "IS74Wifi-agent-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(root);
            var json = new JsonFileStore();
            settings ??= new AppSettings();
            var stateStore = new RuntimeStateStore(paths, json);
            var stateManager = new AuthorizationStateManager(stateStore, settings, new FixedTimeProvider(now));
            var secrets = new DpapiSecretStore(paths);
            secrets.Save(new StoredSecrets("test-bearer", "9123456789"));
            var device = new DeviceIdentityStore(paths);
            internet ??= new ScriptedInternetProbe();
            var authorization = new RecordingAuthorizationRunner();
            var notifications = new RecordingNotificationSink();
            var logger = new DiagnosticLogger(paths);
            var agent = new AgentService(
                secrets,
                device,
                stateManager,
                authorization,
                internet,
                wifi ?? new TargetWifi(),
                settings,
                logger,
                new FixedTimeProvider(now),
                notifications: notifications);
            return new AgentFixture(root, stateStore, agent, authorization, internet, notifications);
        }

        public void SaveState(RuntimeState state) => StateStore.Save(state);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal sealed class RecordingAuthorizationRunner : IAuthorizationRunner
    {
        public int Calls { get; private set; }
        public AuthorizationRequest? LastRequest { get; private set; }
        public Action<AuthorizationRequest>? StateEffect { get; set; }
        public AuthorizationOutcome Outcome { get; set; } = new(
            AuthorizationOutcomeKind.Success,
            InternetConfirmed: true,
            AuthorizedAtUtc: null,
            RetryAfter: null,
            Timing: null);

        public Task<AuthorizationOutcome> RunAsync(
            AuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            StateEffect?.Invoke(request);
            return Task.FromResult(Outcome);
        }
    }

    internal sealed class ScriptedInternetProbe(params InternetProbeResult[] results) : IInternetConnectivityProbe
    {
        private readonly Queue<InternetProbeResult> queue = new(results);
        public int Calls { get; private set; }

        public Task<InternetProbeResult> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue());
            }
            return Task.FromResult(Probe(true, true));
        }
    }

    internal sealed class RecordingNotificationSink : IAgentNotificationSink
    {
        public List<AgentNotification> Items { get; } = [];

        public void Publish(AgentNotification notification) => Items.Add(notification);
    }

    private sealed class TargetWifi : IWifiEnvironment
    {
        public bool IsTargetWifiConnected() => true;
    }

    private sealed class NonTargetWifi : IWifiEnvironment
    {
        public bool IsTargetWifiConnected() => false;
    }
}
