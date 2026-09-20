using Microsoft.Win32;
using System.Net;
using IS74Wifi.Core;

internal static class AgentContractTests
{
    public static async Task RunAsync()
    {
        TestSleepPolicy();
        TestAutostartCommand();
        TestAutostartRegistrationState();
        TestAgentPidRecord();
        await TestExpiryIsAuthoritativeAsync();
        await TestPreExpiryNeedsTwoCaptiveResponsesAsync();
        await TestPreExpiryTransportFailureDoesNotAuthorizeAsync();
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

        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddMinutes(-1)) == TimeSpan.FromSeconds(15),
            "agent idle sleep changed far before guard");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddSeconds(-20)) == TimeSpan.FromSeconds(10),
            "agent sleep crossed guard start");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddSeconds(-5)) == TimeSpan.FromMilliseconds(250),
            "agent guard cadence changed");
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddSeconds(20)) == TimeSpan.FromSeconds(1),
            "overdue agent did not wake promptly");

        state = state with { NextAutomaticRetryUtc = expiry.AddSeconds(30) };
        Assert(AgentTiming.GetSleepDelay(state, settings, expiry.AddSeconds(20)) == TimeSpan.FromSeconds(10),
            "agent did not wake at scheduled retry");
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

    private static InternetProbeResult Probe(
        bool online,
        bool responseReceived,
        TransportFailureKind failure = TransportFailureKind.None) =>
        new(
            Online: online,
            HttpResponseReceived: responseReceived,
            StatusCode: responseReceived ? HttpStatusCode.OK : null,
            Body: responseReceived ? (online ? "Microsoft Connect Test" : "captive") : null,
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
            ScriptedInternetProbe internet)
        {
            Root = root;
            StateStore = stateStore;
            Agent = agent;
            Authorization = authorization;
            Internet = internet;
        }

        public string Root { get; }
        public RuntimeStateStore StateStore { get; }
        public AgentService Agent { get; }
        public RecordingAuthorizationRunner Authorization { get; }
        public ScriptedInternetProbe Internet { get; }

        public static AgentFixture Create(
            DateTimeOffset now,
            AppSettings? settings = null,
            ScriptedInternetProbe? internet = null)
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
            var logger = new DiagnosticLogger(paths);
            var agent = new AgentService(
                secrets,
                device,
                stateManager,
                authorization,
                internet,
                new TargetWifi(),
                settings,
                logger,
                new FixedTimeProvider(now));
            return new AgentFixture(root, stateStore, agent, authorization, internet);
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

    private sealed class TargetWifi : IWifiEnvironment
    {
        public bool IsTargetWifiConnected() => true;
    }
}
