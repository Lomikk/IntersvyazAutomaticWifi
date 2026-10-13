using System.Net;
using System.Net.NetworkInformation;
using IS74Wifi.Core;

internal static class PathPreStepRecoveryContractTests
{
    public static async Task RunAsync()
    {
        await ThirtyMinutesOfFailuresKeepRetryAndObservationsAsync();
        await OverdueRetryDoesNotWaitForProbeCadenceAsync();
        await DnsRollbackAndPreExpiryFailureAsync();
        await ConfirmedInternetStopsTimerRecoveryAsync();
    }

    private static async Task ThirtyMinutesOfFailuresKeepRetryAndObservationsAsync()
    {
        using var fixture = new Fixture();
        fixture.ThrowDirectUnavailable = true;
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.ProbesBeforeFirstCall == 0 && fixture.Probes == 1,
            "the first timer shot must run before its post-failure observation");
        Assert(fixture.LastProbeTimeout == TimeSpan.FromSeconds(1),
            "the post-failure probe budget exceeded the first retry's backoff window");
        var expectedBackoffs = new[] { 1, 2, 5, 15, 30, 60 };
        var restarted = false;
        var unavailableObserved = false;
        for (var ticks = 0; fixture.Clock.GetUtcNow() < fixture.Expiry.AddMinutes(30); ticks++)
        {
            Assert(ticks < 100, "pre-step recovery entered fast edge polling");
            var before = fixture.State.Find(fixture.Path.Identity)!;
            var now = fixture.Clock.GetUtcNow();
            var expected = TimeSpan.FromSeconds(expectedBackoffs[Math.Min(fixture.Runner.Calls - 1, 5)]);
            Assert(fixture.Agent.GetSleepDelay() == expected &&
                   PathAuthorizationSchedule.GetNextCheckUtc(before, fixture.Path, fixture.Settings, now) == now + expected,
                "pre-step retry backoff diverged from worker/snapshot deadlines");
            var calls = fixture.Runner.Calls;
            await fixture.Agent.TickAsync();
            Assert(fixture.Runner.Calls == calls && fixture.Probes == calls,
                "an early scheduler/IPC wake introduced another request during retry backoff");
            fixture.Clock.Advance(expected);
            await fixture.Agent.TickAsync();
            var after = fixture.State.Find(fixture.Path.Identity)!;
            Assert(fixture.Probes == fixture.Runner.Calls && after.LastProbeUtc == fixture.Clock.GetUtcNow(),
                "each failed timer retry must publish exactly one new observation");
            Assert(after.AutomaticStepOneAttempts == 0 && !after.EdgeWatchActive &&
                   after.PreStepFailureCount == fixture.Runner.Calls && after.ExpectedExpiryUtc == fixture.Expiry,
                "pre-step recovery changed send budget, expiry or AlreadyAuthorized semantics");
            unavailableObserved |= after.Status == PathAuthorizationStatus.Unreachable;
            if (!restarted && fixture.Clock.GetUtcNow() >= fixture.Expiry.AddMinutes(10))
            {
                // Persisted retry also survives restart and loss of preferred-path status.
                fixture.Agent = fixture.CreateAgent(preferred: false);
                restarted = true;
            }
        }
        Assert(unavailableObserved && fixture.Runner.Calls < 40,
            "unavailable observations stopped retry recovery or generated excess requests");

        // The portal recovers while the probe still says Unreachable. It must not
        // require a successful captive confirmation before retrying the interrupted shot.
        fixture.Runner.Recovered = true;
        var callsBeforeRecovery = fixture.Runner.Calls;
        fixture.Clock.Advance(fixture.Agent.GetSleepDelay());
        await fixture.Agent.TickAsync();
        var recovered = fixture.State.Find(fixture.Path.Identity)!;
        Assert(fixture.Runner.Calls == callsBeforeRecovery + 1 && recovered.EdgeWatchActive &&
               recovered.LastResult == "already-authorized" && recovered.NextAutomaticRetryUtc is null &&
               recovered.PreStepFailureCount == 0 && recovered.AutomaticStepOneAttempts == 1,
            "recovered portal must finish pre-step retry rather than wait indefinitely for captive");
        fixture.ThrowDirectUnavailable = false;
        fixture.Clock.Advance(fixture.Agent.GetSleepDelay());
        await fixture.Agent.TickAsync();
        Assert(fixture.Agent.CanUploadTelemetry(),
            "completed pre-step recovery left an expired retry blocking late edge-watch telemetry");
        Assert(fixture.Runner.Calls == callsBeforeRecovery + 1,
            "Internet after AlreadyAuthorized introduced another speculative send");
    }

    private static async Task OverdueRetryDoesNotWaitForProbeCadenceAsync()
    {
        using var fixture = new Fixture();
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        fixture.ProbeDuration = TimeSpan.FromSeconds(2);
        fixture.ThrowDirectUnavailable = true;
        await fixture.Agent.TickAsync();
        var state = fixture.State.Find(fixture.Path.Identity)!;
        Assert(state.NextAutomaticRetryUtc < fixture.Clock.GetUtcNow(),
            "test setup did not make the retry overdue during the observation");
        Assert(fixture.Agent.GetSleepDelay() == PathAuthorizationSchedule.MinimumDelay &&
               PathAuthorizationSchedule.GetNextCheckUtc(state, fixture.Path, fixture.Settings, fixture.Clock.GetUtcNow()) ==
               fixture.Clock.GetUtcNow() + PathAuthorizationSchedule.MinimumDelay,
            "a post-failure observation postponed an overdue retry to a one-minute probe interval");
        fixture.Runner.Recovered = true;
        fixture.Clock.Advance(fixture.Agent.GetSleepDelay());
        await fixture.Agent.TickAsync();
        Assert(fixture.Runner.Calls == 2 && fixture.State.Find(fixture.Path.Identity)!.EdgeWatchActive,
            "overdue pre-step retry failed to recover immediately after the observation");
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
        var state = fixture.State.Find(fixture.Path.Identity)!;
        Assert(state.Status == PathAuthorizationStatus.Internet && state.NextAutomaticRetryUtc is null &&
               !state.EdgeWatchActive && state.PreStepFailureCount == 0 &&
               fixture.Agent.GetSleepDelay() == PathAuthorizationSchedule.MaximumWait &&
               PathAuthorizationSchedule.GetNextCheckUtc(state, fixture.Path, fixture.Settings, fixture.Clock.GetUtcNow()) is null,
            "Internet after a failed timer shot retained a periodic authorization retry");
        for (var minute = 0; minute < 30; minute++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            if (minute == 10) fixture.Agent = fixture.CreateAgent();
            await fixture.Agent.TickAsync();
        }
        Assert(fixture.Runner.Calls == 1 && fixture.Probes == 1 && fixture.Agent.CanUploadTelemetry(),
            "an authorized path woke for more portal attempts/probes or blocked telemetry");
        fixture.Agent.NotifyNetworkConnectivityChanged();
        await fixture.Agent.TickAsync();
        Assert(fixture.Probes == 2 && fixture.Runner.Calls == 1,
            "connectivity event did not synchronize the path without a speculative authorization");
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
        public int Probes { get; private set; }
        public bool ThrowDirectUnavailable { get; set; }
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
                return Task.FromResult(new InternetProbeResult(true, true, HttpStatusCode.Found,
                    null, TransportFailureKind.None, TimeSpan.Zero, InternetConnectivityProbe.ExpectedLocation));
            });
            Agent = CreateAgent();
        }

        public PathAwareAgentCoordinator CreateAgent(bool preferred = true) => new(
            () => new StoredSecrets("synthetic-token", "9000000000"), () => "synthetic-device",
            () => [Path], State, probe, Runner, Settings, new DiagnosticLogger(new AppPaths(temp.Path)), Clock,
            preferredPathResolver: new PreferredNetworkPathResolver(_ => preferred ? Path.Identity : null));

        public void Dispose() => temp.Dispose();
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
