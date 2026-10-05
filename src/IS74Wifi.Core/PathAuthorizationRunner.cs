namespace IS74Wifi.Core;

public interface IPathAuthorizationRunner
{
    Task<AuthorizationOutcome> RunAsync(
        NetworkPathSnapshot path,
        AuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs the entire captive transaction through one immutable physical path.
/// Every API/portal/probe client shares the same bound connector; transport
/// continuity is revalidated before stepOne and again before stepTwo.
/// </summary>
public sealed class PathAuthorizationRunner(
    PathAuthorizationStateStore pathState,
    AppSettings settings,
    DiagnosticLogger logger,
    AuthorizationTelemetryRecorder? telemetry = null,
    Func<IReadOnlyList<PhysicalAdapter>>? enumerateAdapters = null,
    TimeProvider? timeProvider = null) : IPathAuthorizationRunner
{
    private readonly Func<IReadOnlyList<PhysicalAdapter>> enumerate =
        enumerateAdapters ?? PhysicalAdapterSelection.Enumerate;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<AuthorizationOutcome> RunAsync(
        NetworkPathSnapshot path,
        AuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.CanAutomaticallyAuthorize)
        {
            throw new DirectNetworkUnavailableException(
                "Сетевой путь не подтвержден Windows как физический интерфейс для автоматической авторизации.");
        }

        var connector = new DirectNetworkConnector(path);
        using var apiHttp = HttpClientProfiles.CreateApiClient(connector);
        using var portalHttp = HttpClientProfiles.CreatePortalClient(connector);
        using var internetHttp = HttpClientProfiles.CreateInternetProbeClient(connector);
        var api = new Is74ApiClient(new HttpTransport(apiHttp, logger));
        var portal = new CaptivePortalClient(new HttpTransport(portalHttp, logger));
        var internet = new InternetConnectivityProbe(new HttpTransport(internetHttp, logger));
        var runtimeStore = new PathRuntimeStateStore(pathState, path);
        var state = new AuthorizationStateManager(runtimeStore, settings, clock);
        var polling = new PushPollingEngine(api);

        Task<bool> PathStillCurrent(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var current = new NetworkPathEnumerator(enumerate)
                .EnumerateAll()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.AdapterId, path.AdapterId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(current is not null && path.HasSameTransportBinding(current));
        }

        var flow = new AuthorizationFlow(
            api,
            portal,
            internet,
            new WindowsWifiEnvironment(),
            polling,
            state,
            logger,
            telemetry: telemetry,
            options: new AuthorizationFlowOptions { BaselineTimeout = TimeSpan.FromSeconds(5) },
            ignoreNetworkCheck: true,
            preStepNetworkCheck: token => connector.CanReachPortalAsync(TimeSpan.FromSeconds(4), token),
            pathContinuityCheck: PathStillCurrent);

        return await flow.RunAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
