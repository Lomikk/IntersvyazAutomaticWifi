using Windows.Networking.Connectivity;

namespace IS74Wifi.Core;

/// <summary>
/// Resolves the physical path that should receive priority when multiple paths
/// need attention. Windows connectivity profiles are only arbitration hints:
/// captive probing remains authoritative.
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

        var candidates = paths.Where(path => path.CanAutomaticallyAuthorize).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        // A single live physical path is unambiguous even when Windows reports a
        // VPN/virtual profile as the machine-wide Internet connection.
        if (candidates.Length == 1)
        {
            return candidates[0].Identity;
        }

        Guid? primaryAdapterId = null;
        IReadOnlyCollection<Guid> internetAdapterIds = Array.Empty<Guid>();
        try
        {
            primaryAdapterId = NetworkInformation.GetInternetConnectionProfile()?.NetworkAdapter?.NetworkAdapterId;
            internetAdapterIds = NetworkInformation.GetConnectionProfiles()
                .Where(profile => profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess)
                .Select(profile => profile.NetworkAdapter?.NetworkAdapterId)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .Distinct()
                .ToArray();
        }
        catch
        {
            // Some Windows policies/app-container configurations can deny WinRT
            // connectivity APIs. Fall through to conservative physical hints.
        }

        return ResolveFromSignals(candidates, primaryAdapterId, internetAdapterIds);
    }

    public static NetworkPathIdentity? ResolveFromSignals(
        IReadOnlyList<NetworkPathSnapshot> candidates,
        Guid? primaryAdapterId,
        IReadOnlyCollection<Guid> internetAdapterIds)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(internetAdapterIds);

        var live = candidates.Where(path => path.CanAutomaticallyAuthorize).ToArray();
        if (live.Length == 0) return null;
        if (live.Length == 1) return live[0].Identity;

        if (primaryAdapterId is { } primary)
        {
            var exact = live.FirstOrDefault(path => AdapterGuid(path) == primary);
            if (exact is not null)
            {
                return exact.Identity;
            }
        }

        var internetMatches = live
            .Where(path => AdapterGuid(path) is { } id && internetAdapterIds.Contains(id))
            .ToArray();
        if (internetMatches.Length == 1)
        {
            return internetMatches[0].Identity;
        }

        // When Windows reports only virtual/VPN profiles, a single physical path
        // with a default gateway is still an unambiguous underlay. Do not guess
        // between multiple gateway-capable physical paths.
        var gatewayMatches = live.Where(path => path.GatewayIPv4 is not null).ToArray();
        return gatewayMatches.Length == 1 ? gatewayMatches[0].Identity : null;
    }

    private static Guid? AdapterGuid(NetworkPathSnapshot path) =>
        Guid.TryParse(path.AdapterId, out var id) ? id : null;
}
