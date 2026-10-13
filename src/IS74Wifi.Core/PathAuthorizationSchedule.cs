namespace IS74Wifi.Core;

/// <summary>
/// Shared scheduling rules for one managed physical network path. Known
/// authorization windows use the same deadline calculation in the agent and UI;
/// observed paths without any trustworthy boundary are event-driven by Windows
/// connectivity/NCSI changes instead of periodic HTTP polling.
/// </summary>
public static class PathAuthorizationSchedule
{
    public static readonly TimeSpan FailureRecheck = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumWait = TimeSpan.FromDays(1);
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan ObservationAfterExpiry = TimeSpan.FromMinutes(3);

    public static TimeSpan GetDelay(
        PathAuthorizationState state,
        NetworkPathSnapshot current,
        AppSettings settings,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(settings);

        if (SnapshotChanged(state, current))
        {
            return MinimumDelay;
        }

        return GetDelay(state, settings, now);
    }

    public static TimeSpan GetDelay(
        PathAuthorizationState state,
        AppSettings settings,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);

        if (state.UserActionRequired)
        {
            return MaximumWait;
        }
        if (HasCompletedObservationWindow(state))
        {
            // A bound probe has completed the finite post-expiry watch.
            // Further probes require a Windows connectivity event or explicit refresh.
            return MaximumWait;
        }
        if (state.NextAutomaticRetryUtc is { } retry && retry > now)
        {
            var untilRetry = Clamp(retry - now);
            if (state.ExpectedExpiryUtc is not { } retryExpiry || now < retryExpiry)
            {
                return untilRetry;
            }

            var retryWatchEnd = retryExpiry + ObservationAfterExpiry;
            if (now >= retryWatchEnd)
            {
                // A confirmed captive path waiting for the next permitted /stepOne
                // must respect its backoff, not probe every 100 ms past the edge.
                // Other states still need a final bound probe to close the watch.
                return state.Status == PathAuthorizationStatus.Captive ? untilRetry : MinimumDelay;
            }
            return Min(untilRetry, Clamp(retryWatchEnd - now));
        }
        if (IsPreStepRetryPending(state))
        {
            // A fresh post-failure probe must not move an overdue explicit retry
            // to the next edge-watch interval. The coordinator consumes this retry.
            return MinimumDelay;
        }
        if (state.Status is PathAuthorizationStatus.Unknown or PathAuthorizationStatus.Disconnected)
        {
            return MinimumDelay;
        }
        if (state.Status == PathAuthorizationStatus.Captive)
        {
            return MinimumDelay;
        }
        if (state.ExpectedExpiryUtc is null)
        {
            // Once an untimed path has produced an observation, there is no
            // trustworthy deadline to poll toward. Network/NCSI events wake the
            // worker when Windows notices a connectivity change; ordinary
            // background wakes must not turn this state into periodic HTTP probes.
            return MaximumWait;
        }
        var expiry = state.ExpectedExpiryUtc.Value;
        var watchEnd = expiry + ObservationAfterExpiry;
        if (now >= watchEnd)
        {
            // Complete the watch with one final bound observation, even after
            // suspend/resume, before switching to event-driven checks.
            return MinimumDelay;
        }
        if (state.Status is PathAuthorizationStatus.Unreachable or PathAuthorizationStatus.Ambiguous)
        {
            return Min(DueFromLastProbe(state.LastProbeUtc, FailureRecheck, now),
                Clamp(watchEnd - now));
        }

        var fastStart = expiry - TimeSpan.FromSeconds(Math.Max(10, settings.GuardWindowSeconds));
        if (now < fastStart)
        {
            return Clamp(fastStart - now);
        }
        var interval = AgentTiming.GetEdgeProbeInterval(settings, expiry, now);
        var fromProbe = DueFromLastProbe(state.LastProbeUtc, interval, now);
        if (now < expiry)
        {
            return Min(fromProbe, Clamp(expiry - now));
        }
        return Min(fromProbe, Clamp(watchEnd - now));
    }

    public static DateTimeOffset? GetNextCheckUtc(
        PathAuthorizationState? state,
        NetworkPathSnapshot current,
        AppSettings settings,
        DateTimeOffset now,
        bool agentRunning = true)
    {
        if (!agentRunning || state is null || state.UserActionRequired)
        {
            return null;
        }
        if (IsConnectivityEventDriven(state, current))
        {
            return null;
        }

        return now + GetDelay(state, current, settings, now);
    }

    public static bool IsConnectivityEventDriven(
        PathAuthorizationState state,
        NetworkPathSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(current);

        return (state.ExpectedExpiryUtc is null && state.NextAutomaticRetryUtc is null ||
                HasCompletedObservationWindow(state)) &&
               !state.UserActionRequired &&
               !SnapshotChanged(state, current) &&
               (state.Status is PathAuthorizationStatus.Internet or
                   PathAuthorizationStatus.Unreachable or
                   PathAuthorizationStatus.Ambiguous);
    }

    public static bool SnapshotChanged(PathAuthorizationState previous, NetworkPathSnapshot current) =>
        previous.InterfaceIndex != current.InterfaceIndex ||
        !string.Equals(previous.SourceIPv4, current.SourceIPv4?.ToString(), StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(previous.GatewayIPv4, current.GatewayIPv4?.ToString(), StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(previous.Ssid, current.Ssid, StringComparison.Ordinal);

    public static bool IsPreStepRetryPending(PathAuthorizationState state) =>
        !state.UserActionRequired && !state.EdgeWatchActive &&
        state.PreStepFailureCount > 0 && state.NextAutomaticRetryUtc is not null &&
        state.ExpectedExpiryUtc is { } expiry && state.LastAttemptUtc >= expiry;

    public static bool HasObservedInternetPastExpiry(PathAuthorizationState state) =>
        state.Status == PathAuthorizationStatus.Internet &&
        state.ExpectedExpiryUtc is { } expiry && state.LastProbeUtc >= expiry;

    // Based on the actual last observation, not the wall clock alone: a machine
    // waking after the window must still examine its own physical network path.
    public static bool HasCompletedObservationWindow(PathAuthorizationState state) =>
        state.ExpectedExpiryUtc is { } expiry &&
        state.LastProbeUtc is { } lastProbe && lastProbe >= expiry + ObservationAfterExpiry &&
        state.Status is PathAuthorizationStatus.Internet or PathAuthorizationStatus.Unreachable or
            PathAuthorizationStatus.Ambiguous;

    private static TimeSpan DueFromLastProbe(DateTimeOffset? lastProbe, TimeSpan interval, DateTimeOffset now)
    {
        if (lastProbe is null)
        {
            return MinimumDelay;
        }
        var due = lastProbe.Value + interval;
        return due <= now ? MinimumDelay : Clamp(due - now);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static TimeSpan Clamp(TimeSpan value) =>
        value < MinimumDelay ? MinimumDelay :
        value > MaximumWait ? MaximumWait : value;
}
