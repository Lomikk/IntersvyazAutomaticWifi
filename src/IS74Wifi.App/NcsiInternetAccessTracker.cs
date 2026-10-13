namespace IS74Wifi.App;

/// <summary>
/// Filters noisy Windows NCSI callbacks down to real changes in the only state
/// event-driven authorization paths care about: global InternetAccess and
/// per-adapter profile levels. Repeated callbacks with the same state are ignored.
/// </summary>
internal sealed class NcsiInternetAccessTracker(bool? initialInternetAccess = null, string? initialProfileState = null)
{
    private string? lastProfileState = initialProfileState;
    private const int Unknown = -1;
    private int lastInternetAccess = initialInternetAccess is null
        ? Unknown
        : initialInternetAccess.Value ? 1 : 0;

    public bool Observe(bool hasInternetAccess)
    {
        var next = hasInternetAccess ? 1 : 0;
        var previous = Interlocked.Exchange(ref lastInternetAccess, next);

        // If startup could not read NCSI, the first successful status callback
        // is still worth one synchronization probe. Afterwards only an actual
        // InternetAccess <-> non-InternetAccess transition is significant.
        return previous == Unknown || previous != next;
    }

    public bool ObserveProfiles(string profileState)
    {
        var previous = Interlocked.Exchange(ref lastProfileState, profileState);
        return previous is null || !string.Equals(previous, profileState, StringComparison.Ordinal);
    }
}
