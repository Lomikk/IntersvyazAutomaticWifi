using System.Net;
using System.Net.NetworkInformation;
using IS74Wifi.Core;

internal static class PathEdgeWatchContractTests
{
    public static async Task RunAsync()
    {
        using var temp = TempDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var json = new JsonFileStore();
        var settings = new AppSettings();
        var expiry = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdvancingTimeProvider(expiry);
        var state = new PathAuthorizationStateStore(paths, json, timeProvider: clock);
        var path = new NetworkPathSnapshot(
            "synthetic-ethernet", "Synthetic Ethernet", NetworkInterfaceType.Ethernet,
            IsUp: true, InterfaceIndex: 7,
            SourceIPv4: IPAddress.Parse("192.0.2.2"),
            GatewayIPv4: IPAddress.Parse("192.0.2.1"),
            DnsServers: [IPAddress.Parse("192.0.2.53")], Ssid: null,
            LooksVirtual: false, HardwareInterface: true, ConnectorPresent: true);
        state.RecordConfirmedAuthorization(path, expiry.AddHours(-24), expiry, expiry.AddSeconds(-1));
        var captive = false;
        var probeCalls = 0;
        var probe = new NetworkPathProbe((_, _, _) =>
        {
            probeCalls++;
            return Task.FromResult(new InternetProbeResult(
                Online: !captive, HttpResponseReceived: true,
                StatusCode: captive ? HttpStatusCode.OK : HttpStatusCode.Found,
                Body: null, FailureKind: TransportFailureKind.None, Elapsed: TimeSpan.Zero,
                Location: captive ? null : InternetConnectivityProbe.ExpectedLocation));
        });
        var manager = new AuthorizationStateManager(new PathRuntimeStateStore(state, path), settings, clock);
        var runner = new ScriptedRunner(manager);
        PathAwareAgentCoordinator CreateCoordinator() => new(
            () => new StoredSecrets("synthetic-token", "9000000000"), () => "synthetic-device",
            () => [path], state, probe, runner, settings, new DiagnosticLogger(paths), clock,
            preferredPathResolver: new PreferredNetworkPathResolver(_ => path.Identity));
        var coordinator = CreateCoordinator();

        await coordinator.TickAsync();
        Assert(runner.Reasons.SequenceEqual([AuthorizationAttemptReason.Automatic]) && probeCalls == 0,
            "the first timer shot must precede any probe and receive AlreadyAuthorized");
        Assert(state.Find(path.Identity) is { EdgeWatchActive: true, AutomaticStepOneAttempts: 1 } &&
               state.Find(path.Identity)!.ExpectedExpiryUtc == expiry,
            "AlreadyAuthorized must keep the old expiry and persist edge-watch");

        clock.Advance(coordinator.GetSleepDelay());
        await coordinator.TickAsync();
        Assert(probeCalls == 1 && !state.Find(path.Identity)!.EdgeWatchActive &&
               PathAuthorizationSchedule.IsConnectivityEventDriven(state.Find(path.Identity)!, path) &&
               PathAuthorizationSchedule.GetNextCheckUtc(state.Find(path.Identity), path, settings, clock.GetUtcNow()) is null,
            "confirmed Internet after the predicted boundary must end periodic checks");
        for (var minute = 0; minute < 30; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            if (minute == 10) coordinator = CreateCoordinator();
            await coordinator.TickAsync();
            Assert(probeCalls == 1 && runner.Reasons.Count == 1 &&
                   coordinator.GetSleepDelay() == PathAuthorizationSchedule.MaximumWait,
                "ordinary wake/restart introduced another request on an authorized Internet path");
            Assert(state.Find(path.Identity)!.ExpectedExpiryUtc == expiry,
                "Internet observation manufactured a new 24-hour authorization window");
        }

        captive = true;
        coordinator.NotifyNetworkConnectivityChanged();
        var probesBeforeCaptive = probeCalls;
        await coordinator.TickAsync();
        Assert(probeCalls == probesBeforeCaptive + 2 && runner.Reasons[^1] == AuthorizationAttemptReason.Retry,
            "late captive must be confirmed twice before the retry");
        var conflicting = state.Find(path.Identity)!;
        Assert(conflicting.EdgeWatchActive && conflicting.AutomaticStepOneAttempts == 2 &&
               conflicting.NextAutomaticRetryUtc == clock.GetUtcNow().AddSeconds(15),
            "contradictory captive/AlreadyAuthorized must retain the retry backoff");
        var callsBeforeBackoff = probeCalls;
        await coordinator.TickAsync();
        Assert(probeCalls == callsBeforeBackoff && runner.Reasons.Count == 2,
            "an early wake must not consume a probe or another send during retry backoff");

        clock.Advance(coordinator.GetSleepDelay());
        await coordinator.TickAsync();
        var recovered = state.Find(path.Identity)!;
        Assert(runner.Reasons.SequenceEqual([
                AuthorizationAttemptReason.Automatic, AuthorizationAttemptReason.Retry, AuthorizationAttemptReason.Retry]),
            "authorization attempts changed across timer, watch and retry");
        Assert(!recovered.EdgeWatchActive && recovered.AutomaticStepOneAttempts == 0 &&
               recovered.NextAutomaticRetryUtc is null && recovered.LastResult == "success" &&
               recovered.LastSuccessfulAuthUtc == clock.GetUtcNow() &&
               recovered.ExpectedExpiryUtc == clock.GetUtcNow().AddHours(settings.AuthWindowHours),
            "MarkSuccess must finish the old watch and establish the next authorization window");
        Assert(coordinator.GetSleepDelay() == TimeSpan.FromHours(24) - TimeSpan.FromSeconds(10),
            "successful renewal must leave edge-watch cadence");
    }

    // Only the network transaction is scripted. State mutations use the production manager
    // and persisted per-path adapter. Real stepOne/poll/stepTwo is covered by authorization-flow.
    private sealed class ScriptedRunner(AuthorizationStateManager manager) : IPathAuthorizationRunner
    {
        public List<AuthorizationAttemptReason> Reasons { get; } = [];

        public Task<AuthorizationOutcome> RunAsync(NetworkPathSnapshot path, AuthorizationRequest request,
            PathAuthorizationExecutionContext? executionContext = null, CancellationToken cancellationToken = default)
        {
            Reasons.Add(request.Reason);
            var budget = manager.RegisterStepOneSend(request.Reason);
            Assert(budget.Allowed, "scripted send unexpectedly exhausted the real budget");
            manager.ClearPreStepFailure();
            if (Reasons.Count <= 2)
            {
                manager.MarkAlreadyAuthorized(request.Reason);
                return Task.FromResult(new AuthorizationOutcome(AuthorizationOutcomeKind.AlreadyAuthorized,
                    null, null, null, null));
            }
            var authorizedAt = manager.MarkSuccess(null, internetConfirmed: true);
            return Task.FromResult(new AuthorizationOutcome(AuthorizationOutcomeKind.Success,
                true, authorizedAt, null, null));
        }
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delay) => now += delay;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
