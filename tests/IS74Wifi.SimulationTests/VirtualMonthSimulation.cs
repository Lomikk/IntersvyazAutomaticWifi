using System.Net;
using System.Net.NetworkInformation;
using IS74Wifi.Core;

namespace IS74Wifi.SimulationTests;

/// <summary>
/// Event-driven, deterministic model of the *scheduler* (not an HTTP timing
/// emulator). Every tick executes the production PathAwareAgentCoordinator and
/// its real persisted state/scheduling rules with isolated synthetic inputs.
/// </summary>
internal sealed class VirtualMonthSimulation : IDisposable
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly SimulationOptions options;
    private readonly int seed;
    private readonly Random random;
    private readonly VirtualClock clock = new(Epoch);
    private readonly DateTimeOffset end;
    private readonly string directory;
    private readonly PathAuthorizationStateStore state;
    private readonly DiagnosticLogger logger;
    private readonly AppSettings settings = new();
    private readonly List<SimulatedPath> paths = [];
    private readonly PriorityQueue<WorldEvent, (DateTimeOffset, int)> events = new();
    private readonly List<SimulationFinding> findings = [];
    private readonly List<SimulationTrace> timeline = [];
    private readonly List<double> outageSeconds = [];
    private readonly int[] probeCountPerDay;
    private PathAwareAgentCoordinator coordinator = null!;
    private DateTimeOffset nextTick;
    private int sequence;
    private int ticks;
    private int probes;
    private int stepOneSends;
    private int renewals;
    private int restarts;
    private int idleTicks;
    private int outages;
    private int lastHourTicks;
    private DateTimeOffset hourStart = Epoch;
    private bool disposed;

    public VirtualMonthSimulation(SimulationOptions options, int seed)
    {
        this.options = options;
        this.seed = seed;
        random = new Random(seed);
        end = Epoch.AddDays(options.Days);
        probeCountPerDay = new int[options.Days];
        directory = Path.Combine(Path.GetTempPath(), "is74-simulation-" + Guid.NewGuid().ToString("N"));
        var appPaths = new AppPaths(directory);
        var json = new JsonFileStore();
        state = new PathAuthorizationStateStore(appPaths, json, timeProvider: clock);
        logger = new DiagnosticLogger(appPaths);

        for (var i = 0; i < options.Paths; i++)
        {
            var snapshot = CreatePath(i);
            var path = new SimulatedPath(snapshot);
            paths.Add(path);
            // The simulation starts with a legitimate known expiry, rather than
            // a freshly installed device with no established auth boundary.
            state.RecordConfirmedAuthorization(snapshot, Epoch, Epoch.AddHours(settings.AuthWindowHours), Epoch);
            ScheduleNewCycle(path, Epoch);
        }

        // False positive Windows/NCSI notifications and coordinator restarts.
        // Both are deterministic and never invoke real OS network APIs.
        for (var day = 1; day < options.Days; day++)
        {
            if (random.NextDouble() < 0.35)
                Enqueue(Epoch.AddDays(day).AddMinutes(random.Next(1440)), WorldEventKind.WindowsWake);
            if (random.NextDouble() < 0.08)
                Enqueue(Epoch.AddDays(day).AddMinutes(random.Next(1440)), WorldEventKind.Restart);
        }
        coordinator = CreateCoordinator();
        nextTick = Epoch + coordinator.GetSleepDelay();
    }

    public async Task<SimulationReport> RunAsync()
    {
        // Guard simulation bugs as well as client busy loops; no test can hang CI.
        const int maximumTicks = 500_000;
        while (clock.GetUtcNow() < end)
        {
            var external = events.TryPeek(out _, out var priority) ? priority.Item1 : end;
            var next = Min(Min(nextTick, external), end);
            // Virtual confirmation/settling delay can cross an event boundary.
            // Deliver that now-overdue event on the current virtual instant.
            if (next < clock.GetUtcNow()) next = clock.GetUtcNow();
            clock.Set(next);
            if (next == end) break;

            // Drain all exogenous events due before the next coordinator tick.
            // A server expiry with no Windows signal is intentionally *not* a wake.
            var wake = false;
            while (events.TryPeek(out _, out var due) && due.Item1 <= clock.GetUtcNow())
            {
                var item = events.Dequeue();
                if (item.Path is not null && item.Generation != item.Path.Generation)
                    continue; // Old expiry/notification from a previous renewal.
                switch (item.Kind)
                {
                    case WorldEventKind.Expiry:
                        Trace("server_expired", item.Path!.Snapshot.AdapterId, "Internet access revoked");
                        if (item.Path.OutageStart is null)
                        {
                            item.Path.OutageStart = item.When;
                            outages++;
                        }
                        break;
                    case WorldEventKind.WindowsWake:
                        Trace("windows_event", item.Path?.Snapshot.AdapterId ?? "system", "NCSI / connectivity notification");
                        coordinator.NotifyNetworkConnectivityChanged();
                        if (item.Path is not null)
                            Enqueue(clock.GetUtcNow().AddMinutes(20), WorldEventKind.RecoveryCheck, item.Path);
                        wake = true;
                        break;
                    case WorldEventKind.RecoveryCheck:
                        if (item.Path!.OutageStart is not null)
                            findings.Add(new SimulationFinding("warning", "no_recovery_after_event",
                                clock.GetUtcNow(), $"{item.Path.Snapshot.AdapterId} is still captive 20 minutes after Windows notification."));
                        break;
                    case WorldEventKind.Restart:
                        Trace("restart", "system", "Coordinator recreated using persisted state");
                        coordinator = CreateCoordinator();
                        restarts++;
                        wake = true;
                        break;
                }
            }
            if (wake || clock.GetUtcNow() >= nextTick)
            {
                // A restart/Windows event may make the existing sleep deadline
                // obsolete, but never schedule one tick *per* obsolete deadline.
                if (clock.GetUtcNow() - hourStart >= TimeSpan.FromHours(1))
                {
                    lastHourTicks = 0;
                    hourStart = clock.GetUtcNow();
                }
                if (++lastHourTicks > 10_000 || ++ticks > maximumTicks)
                {
                    Error("busy_loop", "Scheduler requested an excessive number of wake-ups without useful progress.");
                    break;
                }
                var probesBefore = probes;
                var sendsBefore = stepOneSends;
                await coordinator.TickAsync();
                if (probes == probesBefore && stepOneSends == sendsBefore) idleTicks++;
                var delay = coordinator.GetSleepDelay();
                if (delay < PathAuthorizationSchedule.MinimumDelay || delay > PathAuthorizationSchedule.MaximumWait)
                    Error("invalid_sleep", $"Scheduler requested {delay}.");
                nextTick = clock.GetUtcNow() + delay;
            }
        }

        // Open outages count until the end of the simulated time range.
        foreach (var path in paths)
        {
            if (path.OutageStart is { } started)
                RecordOutage(path, started, end, ongoing: true);
        }
        for (var day = 0; day < probeCountPerDay.Length; day++)
        {
            if (probeCountPerDay[day] > 1500 * options.Paths)
                findings.Add(new SimulationFinding("warning", "probe_burst", Epoch.AddDays(day),
                    $"{probeCountPerDay[day]} HTTP probes on simulated day {day + 1}."));
        }
        var sorted = outageSeconds.OrderBy(x => x).ToArray();
        return new SimulationReport(
            seed, options.Days, options.Paths, ticks, probes, stepOneSends, renewals,
            restarts, outages, idleTicks, sorted.Length == 0 ? 0 : sorted.Max(),
            Percentile(sorted, 0.95), findings.ToArray(), timeline.ToArray());
    }

    private PathAwareAgentCoordinator CreateCoordinator()
    {
        // All dependencies of this coordinator are synthetic. No network route,
        // token, phone, DPAPI store, OS notification or telemetry is accessed.
        var probe = new NetworkPathProbe((path, _, _) =>
        {
            probes++;
            var dayIndex = (int)(clock.GetUtcNow() - Epoch).TotalDays;
            if (dayIndex >= 0 && dayIndex < probeCountPerDay.Length) probeCountPerDay[dayIndex]++;
            var world = Find(path);
            var unavailable = random.NextDouble() < 0.005;
            var online = world.IsOnline(clock.GetUtcNow());
            return Task.FromResult(new InternetProbeResult(
                Online: !unavailable && online,
                HttpResponseReceived: !unavailable,
                StatusCode: unavailable ? null : online ? HttpStatusCode.Found : HttpStatusCode.OK,
                Body: null,
                FailureKind: unavailable ? TransportFailureKind.Timeout : TransportFailureKind.None,
                Elapsed: TimeSpan.Zero,
                Location: unavailable || !online ? null : InternetConnectivityProbe.ExpectedLocation));
        });
        return new PathAwareAgentCoordinator(
            () => new StoredSecrets("synthetic-only", "0000000000"),
            () => "synthetic-device",
            () => paths.Select(x => x.Snapshot).ToArray(),
            state, probe, new VirtualAuthorizationRunner(this), settings, logger,
            timeProvider: clock,
            preferredPathResolver: new PreferredNetworkPathResolver(_ => paths[0].Snapshot.Identity),
            delayAsync: (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                clock.Advance(delay);
                return Task.CompletedTask;
            });
    }

    private Task<AuthorizationOutcome> AuthorizeAsync(NetworkPathSnapshot path, AuthorizationRequest request)
    {
        var world = Find(path);
        var manager = new AuthorizationStateManager(new PathRuntimeStateStore(state, path), settings, clock);
        Trace("authorization", path.AdapterId, request.Reason.ToString());
        // Deliberately simulate pre-send failures separately: they must NOT
        // spend the finite /stepOne budget.
        if (random.NextDouble() < 0.025)
        {
            Trace("pre_step_failure", path.AdapterId, "No portal request sent");
            manager.MarkPreStepFailure(request.Reason);
            return Task.FromResult(Outcome(AuthorizationOutcomeKind.RetryableBeforeStepOne));
        }
        var budget = manager.RegisterStepOneSend(request.Reason);
        if (!budget.Allowed)
        {
            manager.MarkUserActionRequired("simulation-budget-exhausted", request.Reason);
            return Task.FromResult(Outcome(AuthorizationOutcomeKind.UserActionRequired));
        }
        stepOneSends++;
        world.SendsSinceRenewal++;
        if (world.SendsSinceRenewal > settings.MaxAutomaticStepOneAttempts)
            Error("step_one_budget", $"{world.Snapshot.AdapterId}: sent too many /stepOne without success.");
        manager.ClearPreStepFailure();

        if (random.NextDouble() < 0.025)
        {
            Trace("step_one_failure", path.AdapterId, "Transient portal response");
            manager.MarkStepOneRetryable(request.Reason);
            return Task.FromResult(Outcome(AuthorizationOutcomeKind.RetryableStepOne));
        }
        if (world.IsOnline(clock.GetUtcNow()))
        {
            Trace("already_authorized", path.AdapterId, "Server reports valid session");
            manager.MarkAlreadyAuthorized(request.Reason);
            return Task.FromResult(Outcome(AuthorizationOutcomeKind.AlreadyAuthorized));
        }
        var authorizedAt = manager.MarkSuccess(null, internetConfirmed: true);
        // A confirmation delay may straddle the exact server-expiry event.
        if (world.OutageStart is null && clock.GetUtcNow() >= world.ExpiresAt)
        {
            world.OutageStart = world.ExpiresAt;
            outages++;
        }
        if (world.OutageStart is { } start)
            RecordOutage(world, start, clock.GetUtcNow(), ongoing: false);
        world.OutageStart = null;
        world.SendsSinceRenewal = 0;
        renewals++;
        Trace("renewed", path.AdapterId, "Session renewed");
        ScheduleNewCycle(world, authorizedAt);
        return Task.FromResult(new AuthorizationOutcome(AuthorizationOutcomeKind.Success,
            true, authorizedAt, null, null));
    }

    private void ScheduleNewCycle(SimulatedPath path, DateTimeOffset authorizedAt)
    {
        path.Generation++;
        // Mostly near 24 h, sometimes up to five minutes late or one minute
        // early. A late expiry without NCSI will expose the event-driven blind spot.
        var jitterSeconds = random.NextDouble() < 0.08
            ? random.Next(-60, 301)
            : random.Next(-12, 91);
        var expires = authorizedAt.AddHours(settings.AuthWindowHours).AddSeconds(jitterSeconds);
        path.ExpiresAt = expires;
        if (expires >= end) return;
        Enqueue(expires, WorldEventKind.Expiry, path);
        // NCSI changes are not guaranteed. With missing events, the model must
        // not quietly wake the coordinator merely because time passed.
        if (random.NextDouble() < 0.80)
            Enqueue(expires.AddSeconds(random.Next(0, 91)), WorldEventKind.WindowsWake, path);
    }

    private void RecordOutage(SimulatedPath path, DateTimeOffset started, DateTimeOffset ended, bool ongoing)
    {
        var seconds = Math.Max(0, (ended - started).TotalSeconds);
        outageSeconds.Add(seconds);
        if (seconds > 300)
        {
            findings.Add(new SimulationFinding("warning", "long_outage", started,
                $"{path.Snapshot.AdapterId}: {seconds:F1}s without Internet" +
                (ongoing ? " (unresolved at simulation end)" : "")));
        }
    }

    private void Enqueue(DateTimeOffset when, WorldEventKind kind, SimulatedPath? path = null)
    {
        if (when < Epoch || when >= end) return;
        events.Enqueue(new WorldEvent(kind, when, path, path?.Generation ?? 0), (when, sequence++));
    }

    private SimulatedPath Find(NetworkPathSnapshot path) =>
        paths.First(x => x.Snapshot.AdapterId == path.AdapterId);

    private void Error(string code, string detail) =>
        findings.Add(new SimulationFinding("error", code, clock.GetUtcNow(), detail));

    private void Trace(string kind, string path, string description)
    {
        // Significant scenario events only, never every 500 ms probe. Bound
        // long-running campaigns without hiding reproducibility by seed.
        if (timeline.Count < 100_000)
            timeline.Add(new SimulationTrace(clock.GetUtcNow(), kind, path, description));
    }

    private static AuthorizationOutcome Outcome(AuthorizationOutcomeKind kind) =>
        new(kind, null, null, null, null);

    private static NetworkPathSnapshot CreatePath(int index) => new(
        index == 0 ? "sim-wifi" : "sim-ethernet", "Virtual physical adapter",
        index == 0 ? NetworkInterfaceType.Wireless80211 : NetworkInterfaceType.Ethernet,
        IsUp: true, InterfaceIndex: index + 10,
        SourceIPv4: IPAddress.Parse(index == 0 ? "192.0.2.10" : "198.51.100.10"),
        GatewayIPv4: IPAddress.Parse(index == 0 ? "192.0.2.1" : "198.51.100.1"),
        DnsServers: [IPAddress.Parse("192.0.2.53")], Ssid: index == 0 ? "Campus Wi-Fi" : null,
        LooksVirtual: false, HardwareInterface: true, ConnectorPresent: true);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
    private static double Percentile(double[] sorted, double percentile) =>
        sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { /* diagnostic files may briefly remain open */ }
        catch (UnauthorizedAccessException) { /* leave isolated temp state */ }
    }

    private sealed class VirtualAuthorizationRunner(VirtualMonthSimulation simulation) : IPathAuthorizationRunner
    {
        public Task<AuthorizationOutcome> RunAsync(NetworkPathSnapshot path, AuthorizationRequest request,
            PathAuthorizationExecutionContext? executionContext = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return simulation.AuthorizeAsync(path, request);
        }
    }

    private sealed class SimulatedPath(NetworkPathSnapshot snapshot)
    {
        public NetworkPathSnapshot Snapshot { get; } = snapshot;
        public int Generation { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset? OutageStart { get; set; }
        public int SendsSinceRenewal { get; set; }
        public bool IsOnline(DateTimeOffset now) => now < ExpiresAt;
    }

    private enum WorldEventKind { Expiry, WindowsWake, Restart, RecoveryCheck }
    private sealed record WorldEvent(WorldEventKind Kind, DateTimeOffset When, SimulatedPath? Path, int Generation);
}

internal sealed class VirtualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;
    public override DateTimeOffset GetUtcNow() => current;
    public void Set(DateTimeOffset value)
    {
        if (value < current) throw new InvalidOperationException("Virtual time cannot move backwards.");
        current = value;
    }
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));
        current += delta;
    }
}

internal sealed record SimulationFinding(string Severity, string Code, DateTimeOffset At, string Detail);
internal sealed record SimulationTrace(DateTimeOffset At, string Event, string Path, string Detail);

internal sealed record SimulationReport(
    int Seed, int Days, int Paths, int Ticks, int Probes, int StepOneSends,
    int Renewals, int Restarts, int Outages, int IdleTicks, double MaxOutageSeconds,
    double P95OutageSeconds, IReadOnlyList<SimulationFinding> Findings,
    IReadOnlyList<SimulationTrace> Timeline);
