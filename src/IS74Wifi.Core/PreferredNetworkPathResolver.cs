using Windows.Networking.Connectivity;

namespace IS74Wifi.Core;

/// <summary>
/// Resolves the physical path Windows currently reports as its Internet connection
/// profile. This is only an arbitration hint: captive probing remains authoritative.
/// </summary>
public sealed class PreferredNetworkPathResolver(
    Func<IReadOnlyList<NetworkPathSnapshot>, NetworkPathIdentity?>? resolveOverride = null)
{
    public NetworkPathIdentity? Resolve(IReadOnlyList<NetworkPathSnapshot> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (resolveOverride is not null)
        {
            return resolveOverride(paths);
        }

        Guid? adapterId;
        try
        {
            adapterId = NetworkInformation.GetInternetConnectionProfile()?.NetworkAdapter?.NetworkAdapterId;
        }
        catch
        {
            // Some Windows policies/app-container configurations can deny WinRT
            // connectivity APIs. Arbitration then falls back to deterministic order.
            return null;
        }

        if (adapterId is null)
        {
            return null;
        }

        foreach (var path in paths)
        {
            if (!path.CanAutomaticallyAuthorize || !Guid.TryParse(path.AdapterId, out var candidateId))
            {
                continue;
            }
            if (candidateId == adapterId.Value)
            {
                return path.Identity;
            }
        }
        return null;
    }
}
