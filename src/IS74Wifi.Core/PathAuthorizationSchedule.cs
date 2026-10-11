namespace IS74Wifi.Core;

/// <summary>
/// Shared scheduling rules for one managed physical network path. The agent and
/// interactive UI use the same calculation so "до проверки" reflects the next
/// probe the background coordinator will actually perform, even when no 24-hour
/// authorization boundary is known yet.
/// </summary>
public static class PathAuthorizationSchedule
{
    public static readonly TimeSpan UnknownInternetRecheck = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FailureRecheck = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumWait = TimeSpan.FromDays(1);
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FastStartLead = TimeSpan.FromSeconds(10);

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
        if (state.NextAutomaticRetryUtc is { } retry && retry > now)
        {
            return Clamp(retry - now);
        }
        if (state.Status is PathAuthorizationStatus.Unknown or PathAuthorizationStatus.Disconnected)
        {
            return MinimumDelay;
        }
        if (state.Status == PathAuthorizationStatus.Captive)
        {
            return MinimumDelay;
        }
        if (state.Status is PathAuthorizationStatus.Unreachable or PathAuthorizationStatus.Ambiguous)
        {
            return DueFromLastProbe(state.LastProbeUtc, FailureRecheck, now);
        }
        if (state.ExpectedExpiryUtc is not { } expiry)
        {
            return DueFromLastProbe(state.LastProbeUtc, UnknownInternetRecheck, now);
        }

        var fastStart = expiry - FastStartLead;
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
        return fromProbe;
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

        return now + GetDelay(state, current, settings, now);
    }

    public static bool SnapshotChanged(PathAuthorizationState previous, NetworkPathSnapshot current) =>
        previous.InterfaceIndex != current.InterfaceIndex ||
        !string.Equals(previous.SourceIPv4, current.SourceIPv4?.ToString(), StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(previous.GatewayIPv4, current.GatewayIPv4?.ToString(), StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(previous.Ssid, current.Ssid, StringComparison.Ordinal);

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
