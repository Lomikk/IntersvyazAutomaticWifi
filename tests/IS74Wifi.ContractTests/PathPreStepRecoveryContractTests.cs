using System.Net;
using System.Net.NetworkInformation;
using IS74Wifi.Core;

internal static class PathPreStepRecoveryContractTests
{
    public static async Task RunAsync()
    {
        await UnreachableNeverAuthorizesAndNotifiesOnceAsync();
        await LateResumeProbesInsteadOfSendingAsync();
        await CaptivePreStepFailureNeedsFreshCaptiveRetryAsync();
        await InternetAtExpiryKeepsWatchingAsync();
    }

    private static async Task UnreachableNeverAuthorizesAndNotifiesOnceAsync()
    {
        using var fixture = new Fixture();
        fixture.ThrowDirectUnavailable = true;
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.Calls == 0 && fixture.Probes == 1,
            "unreachable Internet probe sent an unconfirmed /stepOne at T");
        Assert(fixture.LastProbeTimeout == TimeSpan.FromSeconds(2),
            "unreachable probe changed the established 2-second timeout");

        var ticks = 0;
        while (fixture.Clock.GetUtcNow() < fixture.Expiry + PathAuthorizationSchedule.ObservationAfterExpiry)
        {
            Assert(ticks++ < 150, "unreachable edge watch never reached its deadline");
            fixture.Clock.Advance(fixture.Agent.GetSleepDelay());
            await fixture.Agent.TickAsync();
            Assert(fixture.Runner.Calls == 0,
                "unreachable or ambiguous probing authorized without captive confirmation");
        }
        var stopped = fixture.State.Find(fixture.Path.Identity)!;
        Assert(stopped.Status == PathAuthorizationStatus.Unreachable &&
               stopped.NextAutomaticRetryUtc is null &&
               PathAuthorizationSchedule.IsConnectivityEventDriven(stopped, fixture.Path) &&
               stopped.ConnectivityErrorNotifiedExpiryUtc == fixture.Expiry &&
               fixture.Notifications.Items.Count(item => item.Title == "Не удалось проверить Wi-Fi" &&
                   item.Importance == AgentNotificationImportance.Important) == 1,
            "unreachable path must stop at T+3min with one user notification");
        var probesAtStop = fixture.Probes;
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        fixture.Agent = fixture.CreateAgent(preferred: false);
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.Calls == 0 && fixture.Probes == probesAtStop,
            "restart unexpectedly sent stepOne or probed without a Windows event");

        fixture.Agent.NotifyNetworkConnectivityChanged();
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.Calls == 0 &&
               fixture.Notifications.Items.Count(item => item.Title == "Не удалось проверить Wi-Fi") == 1,
            "unreachable Windows event spammed authorization or duplicate notifications");

        // Once the captive portal actually appears, the same expired path can
        // authorize on the next network event, after two positive observations.
        fixture.ThrowDirectUnavailable = false;
        fixture.CaptiveProbe = true;
        fixture.Runner.Recovered = true;
        var probesBefore = fixture.Probes;
        fixture.Agent.NotifyNetworkConnectivityChanged();
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.Calls == 1 &&
               fixture.Probes == probesBefore + 2 &&
               fixture.Runner.ProbesBeforeFirstCall == fixture.Probes,
            "late genuine captive did not go through two probes before stepOne");
    }

    private static async Task LateResumeProbesInsteadOfSendingAsync()
    {
        using var fixture = new Fixture();
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        fixture.ProbeDuration = TimeSpan.FromSeconds(2);
        await fixture.Agent.TickAsync();
        var state = fixture.State.Find(fixture.Path.Identity)!;
        Assert(fixture.Runner.Calls == 0 && fixture.Probes == 1 &&
               state.NextAutomaticRetryUtc is null &&
               PathAuthorizationSchedule.HasCompletedObservationWindow(state) &&
               PathAuthorizationSchedule.GetNextCheckUtc(state, fixture.Path, fixture.Settings, fixture.Clock.GetUtcNow()) is null,
            "late resume while online must probe once, never fire a speculative stepOne");
    }

    private static async Task CaptivePreStepFailureNeedsFreshCaptiveRetryAsync()
    {
        using var fixture = new Fixture();
        fixture.CaptiveProbe = true;
        fixture.Runner.RollbackReservation = true;
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.Calls == 1 && fixture.Runner.ProbesBeforeFirstCall == 2 &&
               fixture.Probes == 2 &&
               fixture.State.Find(fixture.Path.Identity) is { AutomaticStepOneAttempts: 0, PreStepFailureCount: 1 },
            "pre-step rollback must be preceded by two captive probes and preserve attempt budget");

        fixture.Clock.Advance(fixture.Agent.GetSleepDelay());
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.Calls == 2 && fixture.Probes == 4 &&
               fixture.State.Find(fixture.Path.Identity)!.AutomaticStepOneAttempts == 0,
            "retry after pre-step rollback bypassed captive reprobe or lost budget");

        // When Internet returns during backoff, the retry must stop without
        // attempting /stepOne based only on a stale captive state.
        fixture.CaptiveProbe = false;
        fixture.Clock.Advance(fixture.Agent.GetSleepDelay());
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.Calls == 2 && fixture.Probes == 5 &&
               fixture.State.Find(fixture.Path.Identity)!.Status == PathAuthorizationStatus.Internet,
            "Internet recovery must cancel stale pre-step retry before any send");

        // Persisted pre-step failures created by an older build must not allow
        // the old timer-only shortcut after upgrade.
        using var migrated = new Fixture();
        migrated.Clock.Advance(TimeSpan.FromSeconds(-1));
        migrated.Manager.MarkPreStepFailure(AuthorizationAttemptReason.Automatic);
        migrated.Clock.Advance(TimeSpan.FromSeconds(1));
        await migrated.Agent.TickAsync();
        Assert(migrated.Runner.Calls == 0,
            "legacy persisted pre-step failure triggered a speculative boundary attempt");
        migrated.Clock.Advance(migrated.Agent.GetSleepDelay());
        await migrated.Agent.TickAsync();
        Assert(migrated.Runner.Calls == 0,
            "legacy failure retry ignored a healthy Internet probe");
    }

    private static async Task InternetAtExpiryKeepsWatchingAsync()
    {
        using var fixture = new Fixture();
        await fixture.Agent.TickAsync();
        var first = fixture.State.Find(fixture.Path.Identity)!;
        Assert(first.Status == PathAuthorizationStatus.Internet &&
               first.NextAutomaticRetryUtc is null &&
               first.AutomaticStepOneAttempts == 0 &&
               !PathAuthorizationSchedule.IsConnectivityEventDriven(first, fixture.Path),
            "live Internet at T must retain the finite window and preserve attempt budget");
        var probesBeforeWatch = fixture.Probes;
        var ticks = 0;
        while (fixture.Clock.GetUtcNow() < fixture.Expiry + PathAuthorizationSchedule.ObservationAfterExpiry)
        {
            Assert(ticks++ < 150, "the healthy edge watch did not complete");
            fixture.Clock.Advance(fixture.Agent.GetSleepDelay());
            await fixture.Agent.TickAsync();
            Assert(fixture.Runner.Calls == 0, "healthy Internet during edge watch sent /stepOne");
        }
        var completed = fixture.State.Find(fixture.Path.Identity)!;
        Assert(fixture.Probes > probesBeforeWatch && fixture.Runner.Calls == 0 &&
               PathAuthorizationSchedule.IsConnectivityEventDriven(completed, fixture.Path) &&
               fixture.Agent.CanUploadTelemetry(),
            "completed Internet observation must stop probes and permit telemetry");
        var probesAtEnd = fixture.Probes;
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.Agent.TickAsync();
        Assert(fixture.Probes == probesAtEnd, "ordinary wake restarted probes after the window");
        fixture.Agent.NotifyNetworkConnectivityChanged();
        await fixture.Agent.TickAsync();
        Assert(fixture.Probes == probesAtEnd + 1 && fixture.Runner.Calls == 0,
            "Windows event must probe once without a speculative /stepOne");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory temp = TempDirectory.Create();
        private readonly NetworkPathProbe probe;
        public DateTimeOffset Expiry { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public AppSettings Settings { get; } = new();
        public Clock Clock { get; }
        public PathAuthorizationStateStore State { get; }
        public NetworkPathSnapshot Path { get; } = new("synthetic-pre-step", "Synthetic Ethernet",
            NetworkInterfaceType.Ethernet, true, 7, IPAddress.Parse("192.0.2.2"),
            IPAddress.Parse("192.0.2.1"), [], null, false, true, true);
        public AuthorizationStateManager Manager { get; }
        public Runner Runner { get; }
        public PathAwareAgentCoordinator Agent { get; set; }
        public RecordingNotifications Notifications { get; } = new();
        public int Probes { get; private set; }
        public bool ThrowDirectUnavailable { get; set; }
        public bool CaptiveProbe { get; set; }
        public TimeSpan ProbeDuration { get; set; }
        public TimeSpan LastProbeTimeout { get; private set; }

        public Fixture()
        {
            Clock = new Clock(Expiry);
            var paths = new AppPaths(temp.Path);
            State = new PathAuthorizationStateStore(paths, new JsonFileStore(), timeProvider: Clock);
            State.RecordConfirmedAuthorization(Path, Expiry.AddHours(-24), Expiry, Expiry.AddSeconds(-1));
            Manager = new AuthorizationStateManager(new PathRuntimeStateStore(State, Path), Settings, Clock);
            Runner = new Runner(this);
            probe = new NetworkPathProbe((_, timeout, _) =>
            {
                Probes++;
                LastProbeTimeout = timeout;
                Clock.Advance(ProbeDuration);
                if (ThrowDirectUnavailable) throw new DirectNetworkUnavailableException("synthetic unavailable route");
                return Task.FromResult(CaptiveProbe
                    ? new InternetProbeResult(false, true, HttpStatusCode.OK, null,
                        TransportFailureKind.None, TimeSpan.Zero)
                    : new InternetProbeResult(true, true, HttpStatusCode.Found, null,
                        TransportFailureKind.None, TimeSpan.Zero, InternetConnectivityProbe.ExpectedLocation));
            });
            Agent = CreateAgent();
        }

        public PathAwareAgentCoordinator CreateAgent(bool preferred = true) => new(
            () => new StoredSecrets("synthetic-token", "9000000000"), () => "synthetic-device",
            () => [Path], State, probe, Runner, Settings, new DiagnosticLogger(new AppPaths(temp.Path)), Clock,
            notifications: Notifications,
            preferredPathResolver: new PreferredNetworkPathResolver(_ => preferred ? Path.Identity : null));

        public void Dispose() => temp.Dispose();
    }

    private sealed class RecordingNotifications : IAgentNotificationSink
    {
        public List<AgentNotification> Items { get; } = [];
        public void Publish(AgentNotification notification) => Items.Add(notification);
    }

    private sealed class Runner(Fixture fixture) : IPathAuthorizationRunner
    {
        public int Calls { get; private set; }
        public int ProbesBeforeFirstCall { get; private set; }
        public bool Recovered { get; set; }
        public bool RollbackReservation { get; set; }

        public Task<AuthorizationOutcome> RunAsync(NetworkPathSnapshot path, AuthorizationRequest request,
            PathAuthorizationExecutionContext? executionContext = null, CancellationToken cancellationToken = default)
        {
            if (Calls++ == 0) ProbesBeforeFirstCall = fixture.Probes;
            if (Recovered)
            {
                Assert(fixture.Manager.RegisterStepOneSend(request.Reason).Allowed, "recovery lost its send budget");
                fixture.Manager.ClearPreStepFailure();
                fixture.Manager.MarkAlreadyAuthorized(request.Reason);
                return Task.FromResult(new AuthorizationOutcome(AuthorizationOutcomeKind.AlreadyAuthorized,
                    null, null, null, null));
            }
            TimeSpan? retry;
            if (RollbackReservation)
            {
                fixture.Manager.RegisterStepOneSend(request.Reason);
                retry = fixture.Manager.MarkReservedStepOneNotSent(request.Reason);
            }
            else retry = fixture.Manager.MarkPreStepFailure(request.Reason);
            return Task.FromResult(new AuthorizationOutcome(AuthorizationOutcomeKind.RetryableBeforeStepOne,
                null, null, retry, null));
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delay) => now += delay;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
