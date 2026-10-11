namespace IS74Wifi.Core;

public sealed record ManualAuthorizationPathSelection(
    NetworkPathSnapshot Path,
    bool Preferred);

/// <summary>
/// Selects one explicit physical path for a user-requested authorization without
/// performing an Internet/captive probe. Manual authorization is an explicit
/// action, so SUSU is not a gate; the selected path's portal preflight remains
/// the transport safety check before stepOne.
/// </summary>
public sealed class ManualAuthorizationPathSelector(
    Func<IReadOnlyList<NetworkPathSnapshot>> enumeratePaths,
    PreferredNetworkPathResolver preferredPathResolver)
{
    public ManualAuthorizationPathSelection? Select(string? configuredAdapterId)
    {
        var current = enumeratePaths()
            .Where(path => path.CanAutomaticallyAuthorize)
            .GroupBy(path => path.Identity)
            .Select(group => group.First())
            .ToArray();

        if (current.Length == 0)
        {
            return null;
        }

        var preferredId = preferredPathResolver.Resolve(current);
        var preferred = preferredId is null
            ? null
            : current.FirstOrDefault(path => SameIdentity(path.Identity, preferredId));

        if (!string.IsNullOrWhiteSpace(configuredAdapterId) &&
            !string.Equals(configuredAdapterId, PhysicalAdapterSelection.SystemRoute, StringComparison.OrdinalIgnoreCase))
        {
            var configured = current.FirstOrDefault(path =>
                string.Equals(path.AdapterId, configuredAdapterId, StringComparison.OrdinalIgnoreCase));
            return configured is null
                ? null
                : new ManualAuthorizationPathSelection(
                    configured,
                    preferred is not null && SameIdentity(configured.Identity, preferred.Identity));
        }

        if (preferred is not null)
        {
            return new ManualAuthorizationPathSelection(preferred, Preferred: true);
        }

        return current.Length == 1
            ? new ManualAuthorizationPathSelection(current[0], Preferred: true)
            : null;
    }

    private static bool SameIdentity(NetworkPathIdentity left, NetworkPathIdentity right) =>
        left.InterfaceType == right.InterfaceType &&
        string.Equals(left.AdapterId, right.AdapterId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.NetworkDiscriminator, right.NetworkDiscriminator, StringComparison.OrdinalIgnoreCase);
}
