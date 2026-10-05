namespace IS74Wifi.Core;

public sealed record NetworkPathDiagnostic(
    NetworkPathSnapshot Path,
    bool Preferred,
    NetworkPathProbeStatus Status,
    PathAuthorizationState State);

/// <summary>
/// Read-only/live diagnostics for the same physical-path model used by the agent.
/// It never authorizes a path and never considers VPN/virtual adapters candidates.
/// Probe observations are persisted so UI/status output and the background agent
/// share one view of the latest path state.
/// </summary>
public sealed class NetworkPathDiagnosticsService(
    Func<IReadOnlyList<NetworkPathSnapshot>> enumeratePaths,
    PathAuthorizationStateStore state,
    NetworkPathProbe probe,
    PreferredNetworkPathResolver preferredPathResolver,
    DiagnosticLogger? logger = null)
{
    public async Task<IReadOnlyList<NetworkPathDiagnostic>> InspectAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var current = enumeratePaths()
            .Where(path => path.CanAutomaticallyAuthorize)
            .GroupBy(path => path.Identity)
            .Select(group => group.First())
            .ToArray();

        state.MarkMissingAsDisconnected(current.Select(path => path.Identity).ToArray());
        if (current.Length == 0)
        {
            return [];
        }

        var preferred = preferredPathResolver.Resolve(current);
        var ordered = current
            .OrderByDescending(path => preferred is not null && SameIdentity(path.Identity, preferred))
            .ThenBy(PathSortKey, StringComparer.Ordinal)
            .ToArray();

        var result = new List<NetworkPathDiagnostic>(ordered.Length);
        foreach (var path in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Observe(path);

            NetworkPathProbeResult probeResult;
            try
            {
                probeResult = await probe.ProbeAsync(path, timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectNetworkUnavailableException ex)
            {
                logger?.Write(DiagnosticLevel.Warn,
                    $"network.path-diagnostics unavailable adapter={Safe(path.Name)} error={ex.GetType().Name}");
                probeResult = new NetworkPathProbeResult(
                    path.Identity,
                    NetworkPathProbeStatus.Unreachable,
                    new InternetProbeResult(
                        Online: false,
                        HttpResponseReceived: false,
                        StatusCode: null,
                        Body: null,
                        FailureKind: TransportFailureKind.DirectRouteUnavailable,
                        Elapsed: TimeSpan.Zero));
            }

            var persisted = state.RecordProbe(path, probeResult);
            result.Add(new NetworkPathDiagnostic(
                path,
                preferred is not null && SameIdentity(path.Identity, preferred),
                probeResult.Status,
                persisted));
        }

        return result;
    }

    private static string PathSortKey(NetworkPathSnapshot path) =>
        $"{path.AdapterId}\u001f{path.Identity.NetworkDiscriminator}";

    private static bool SameIdentity(NetworkPathIdentity left, NetworkPathIdentity right) =>
        left.InterfaceType == right.InterfaceType &&
        string.Equals(left.AdapterId, right.AdapterId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.NetworkDiscriminator, right.NetworkDiscriminator, StringComparison.OrdinalIgnoreCase);

    private static string Safe(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}
