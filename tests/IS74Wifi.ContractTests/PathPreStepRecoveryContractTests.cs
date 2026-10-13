using System.Net;
using System.Net.NetworkInformation;
using IS74Wifi.Core;

internal static class PathPreStepRecoveryContractTests
{
    public static async Task RunAsync()
    {
        await FiniteFailuresStopAndNotifyAsync();
        await LateFirstShotDoesNotRetryPastWindowAsync();
        await DnsRollbackAndPreExpiryFailureAsync();
        await ConfirmedInternetStopsTimerRecoveryAsync();
    }

    private static async Task FiniteFailuresStopAndNotifyAsync()
    {
        using var fixture = new Fixture();
        fixture.ThrowDirectUnavailable = true;
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.ProbesBeforeFirstCall == 0 && fixture.Probes == 1,
            "the preferred timer shot must run before its post-failure observation");
        Assert(fixture.LastProbeTimeout == TimeSpan.FromSeconds(1),
            "post-failure probe exceeded the first retry's backoff window");

        var ticks = 0;
        while (fixture.Clock.GetUtcNow() < fixture.Expiry + PathAuthorizationSchedule.ObservationAfterExpiry)
        {
            Assert(ticks++ < 100, "pre-step retry failed to reach the observation deadline");
            fixture.Clock.Advance(fixture.Agent.GetSleepDelay());
            await fixture.Agent.TickAsync();
        }
        var stopped = fixture.State.Find(fixture.Path.Identity)!;
        Assert(stopped.Status == PathAuthorizationStatus.Unreachable &&
               stopped.NextAutomaticRetryUtc is null && !stopped.EdgeWatchActive &&
               PathAuthorizationSchedule.IsConnectivityEventDriven(stopped, fixture.Path) &&
               stopped.ConnectivityErrorNotifiedExpiryUtc == fixture.Expiry &&
               fixture.Runner.Calls > 1 && fixture.Runner.Calls < 20 &&
               fixture.Notifications.Items.Count(item => item.Title == "Не удалось проверить Wi-Fi" &&
                   item.Importance == AgentNotificationImportance.Important) == 1,
            "unreachable direct route must stop retries and send one terminal notice at +3 minutes");
        var callsAtStop = fixture.Runner.Calls;
        var probesAtStop = fixture.Probes;
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        fixture.Agent = fixture.CreateAgent(preferred: false);
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.Calls == callsAtStop && fixture.Probes == probesAtStop &&
               fixture.Notifications.Items.Count(item => item.Title == "Не удалось проверить Wi-Fi") == 1,
            "restart after failed observation resumed probes or repeated the notification");

        // The same expired window must never spam additional notifications,
        // even when Windows delivers repeated genuine connectivity events.
        fixture.Agent.NotifyNetworkConnectivityChanged();
        await fixture.Agent.TickAsync();
        Assert(fixture.Notifications.Items.Count(item => item.Title == "Не удалось проверить Wi-Fi") == 1 &&
               fixture.Runner.Calls == callsAtStop,
            "failed bound probe after an event repeated the exhausted-window notification");

        // An explicit network event may recover even after the watch has closed.
        fixture.ThrowDirectUnavailable = false;
        fixture.CaptiveProbe = true;
        fixture.Runner.Recovered = true;
        fixture.Agent.NotifyNetworkConnectivityChanged();
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.Calls == callsAtStop + 1 &&
               fixture.State.Find(fixture.Path.Identity)!.LastResult == "already-authorized",
            "a confirmed late captive event must still use the common authorization flow");
    }

    private static async Task LateFirstShotDoesNotRetryPastWindowAsync()
    {
        using var fixture = new Fixture();
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        fixture.ProbeDuration = TimeSpan.FromSeconds(2);
        await fixture.Agent.TickAsync();
        var state = fixture.State.Find(fixture.Path.Identity)!;
        Assert(fixture.Runner.Calls == 1 && state.NextAutomaticRetryUtc is null &&
               PathAuthorizationSchedule.HasCompletedObservationWindow(state) &&
               PathAuthorizationSchedule.GetNextCheckUtc(state, fixture.Path, fixture.Settings, fixture.Clock.GetUtcNow()) is null,
            "a late first timer shot with live Internet must not enter speculative retries");
    }

    private static async Task DnsRollbackAndPreExpiryFailureAsync()
    {
        using var fixture = new Fixture();
        fixture.Runner.RollbackReservation = true;
        fixture.ThrowDirectUnavailable = true;
        await fixture.Agent.TickAsync();
        Assert(fixture.State.Find(fixture.Path.Identity) is { AutomaticStepOneAttempts: 0, PreStepFailureCount: 1 },
            "DNS rollback spent the send budget or lost the recovery retry");
        fixture.Clock.Advance(fixture.Agent.GetSleepDelay());
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.Calls == 2 && fixture.Probes == 2 &&
               fixture.State.Find(fixture.Path.Identity)!.AutomaticStepOneAttempts == 0,
            "DNS rollback reintroduced starvation on the next timer retry");

        using var preExpiry = new Fixture();
        preExpiry.Clock.Advance(TimeSpan.FromSeconds(-1));
        preExpiry.Manager.MarkPreStepFailure(AuthorizationAttemptReason.Automatic);
        preExpiry.Clock.Advance(TimeSpan.FromSeconds(1));
        await preExpiry.Agent.TickAsync();
        Assert(preExpiry.Runner.ProbesBeforeFirstCall == 0 && preExpiry.Runner.Calls == 1,
            "a pre-expiry failure inserted a probe before the first boundary shot");
    }

    private static async Task ConfirmedInternetStopsTimerRecoveryAsync()
    {
        using var fixture = new Fixture();
        await fixture.Agent.TickAsync();
        var first = fixture.State.Find(fixture.Path.Identity)!;
        Assert(first.Status == PathAuthorizationStatus.Internet && first.NextAutomaticRetryUtc is null &&
               first.PreStepFailureCount == 0 && !PathAuthorizationSchedule.IsConnectivityEventDriven(first, fixture.Path),
            "live Internet must stop speculative stepOne retries, not the finite probes");
        var probesBeforeWatch = fixture.Probes;
        var ticks = 0;
        while (fixture.Clock.GetUtcNow() < fixture.Expiry + PathAuthorizationSchedule.ObservationAfterExpiry)
        {
            Assert(ticks++ < 150, "the healthy recovery watch did not complete");
            fixture.Clock.Advance(fixture.Agent.GetSleepDelay());
            await fixture.Agent.TickAsync();
        }
        var completed = fixture.State.Find(fixture.Path.Identity)!;
        Assert(fixture.Runner.Calls == 1 && fixture.Probes > probesBeforeWatch &&
               PathAuthorizationSchedule.IsConnectivityEventDriven(completed, fixture.Path) &&
               fixture.Agent.CanUploadTelemetry(),
            "the completed observation did not stop probes or free background maintenance");
        var probesAtEnd = fixture.Probes;
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.Agent.TickAsync();
        Assert(fixture.Probes == probesAtEnd, "ordinary wake started probes after the window");
        fixture.Agent.NotifyNetworkConnectivityChanged();
        await fixture.Agent.TickAsync();
        Assert(fixture.Probes == probesAtEnd + 1 && fixture.Runner.Calls == 1,
            "connectivity event did not synchronize the path without speculative authorization");
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
