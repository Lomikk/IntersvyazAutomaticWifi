namespace IS74Wifi.Core;

/// <summary>
/// Schedules independent physical authorization paths. It never treats VPN or
/// other non-hardware adapters as authorization targets and runs at most one
/// SMS authorization flow per tick.
/// </summary>
public sealed class PathAwareAgentCoordinator(
    Func<StoredSecrets?> loadSecrets,
    Func<string> getDeviceId,
    Func<IReadOnlyList<NetworkPathSnapshot>> enumeratePaths,
    PathAuthorizationStateStore state,
    NetworkPathProbe probe,
    IPathAuthorizationRunner authorization,
    AppSettings settings,
    DiagnosticLogger logger,
    TimeProvider? timeProvider = null,
    IAgentNotificationSink? notifications = null,
    PreferredNetworkPathResolver? preferredPathResolver = null)
{
    private static readonly TimeSpan UnknownInternetRecheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureRecheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NoPathRecheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromDays(1);
    private static readonly TimeSpan InitialProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PathSettlingRetryDelay = TimeSpan.FromSeconds(1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private NetworkPathIdentity? lastPreferredPath;

    public TimeSpan GetSleepDelay()
    {
        var now = clock.GetUtcNow();
        var current = CurrentPaths();
        if (current.Count == 0)
        {
            return NoPathRecheck;
        }

        var delay = MaximumWait;
        foreach (var path in current)
        {
            var existing = state.Find(path.Identity);
            if (existing is null || existing.Status == PathAuthorizationStatus.Disconnected || SnapshotChanged(existing, path))
            {
                return TimeSpan.FromMilliseconds(100);
            }

            delay = Min(delay, GetPathDelay(existing, now));
        }
        return Clamp(delay);
    }

    public bool CanUploadTelemetry()
    {
        var now = clock.GetUtcNow();
        var currentIds = CurrentPaths().Select(path => path.Identity).ToArray();
        foreach (var pathState in state.Load().Paths)
        {
            if (!currentIds.Any(id => SameIdentity(id, pathState.Identity)) || pathState.UserActionRequired)
            {
                continue;
            }
            if (pathState.Status == PathAuthorizationStatus.Captive)
            {
                return false;
            }
            if (pathState.NextAutomaticRetryUtc is { } retry && retry - now <= TimeSpan.FromMinutes(1))
            {
                return false;
            }
            if (!AgentTiming.CanUploadTelemetry(PathAuthorizationStateStore.ToRuntimeState(pathState), settings, now))
            {
                return false;
            }
        }
        return true;
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var secrets = loadSecrets();
        if (secrets is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var current = CurrentPaths();
        state.MarkMissingAsDisconnected(current.Select(path => path.Identity).ToArray());
        if (current.Count == 0)
        {
            return;
        }

        var preferredPath = preferredPathResolver?.Resolve(current);
        if (preferredPath is not null)
        {
            lastPreferredPath = preferredPath;
        }
        else if (lastPreferredPath is not null &&
                 !current.Any(path => SameIdentity(path.Identity, lastPreferredPath)))
        {
            lastPreferredPath = null;
        }

        var preferredSnapshot = lastPreferredPath is null
            ? null
            : current.FirstOrDefault(path => SameIdentity(path.Identity, lastPreferredPath));
        if (preferredSnapshot is not null)
        {
            var preferredState = state.Find(preferredSnapshot.Identity);
            if (ShouldFirePreferredTimerImmediately(preferredState, preferredSnapshot, now))
            {
                await RunAuthorizationAsync(preferredSnapshot, secrets, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        NetworkPathSnapshot? authorizationCandidate = null;
        foreach (var path in current
                     .OrderByDescending(path => lastPreferredPath is not null && SameIdentity(path.Identity, lastPreferredPath))
                     .ThenBy(PathSortKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = state.Find(path.Identity);
            var changed = before is null || before.Status == PathAuthorizationStatus.Disconnected || SnapshotChanged(before, path);
            var observed = state.Observe(path, now);
            if (!changed && GetPathDelay(observed, now) > TimeSpan.FromMilliseconds(100))
            {
                continue;
            }

            var result = await ProbePathAsync(path, changed, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                continue;
            }

            var updated = state.RecordProbe(path, result, clock.GetUtcNow());
            logger.Write(DiagnosticLevel.Info,
                $"agent.path-probe adapter={Safe(path.Name)} status={result.Status} expiry={updated.ExpectedExpiryUtc:O}");

            if (result.Status != NetworkPathProbeStatus.Captive || updated.UserActionRequired)
            {
                continue;
            }
            if (updated.NextAutomaticRetryUtc is { } retryAt && retryAt > clock.GetUtcNow())
            {
                continue;
            }

            if (!await ConfirmCaptiveAsync(path, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            // Only one SMS-producing flow may run in one tick. Because the
            // preferred Internet path is ordered first, it wins arbitration and
            // starts immediately instead of waiting for probes of backup paths.
            authorizationCandidate = path;
            break;
        }

        if (authorizationCandidate is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await RunAuthorizationAsync(authorizationCandidate, secrets, cancellationToken).ConfigureAwait(false);
    }


    private async Task<NetworkPathProbeResult?> ProbePathAsync(
        NetworkPathSnapshot path,
        bool settlingAllowed,
        CancellationToken cancellationToken)
    {
        NetworkPathProbeResult first;
        try
        {
            first = await probe.ProbeAsync(path, InitialProbeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectNetworkUnavailableException ex)
        {
            logger.Write(DiagnosticLevel.Warn,
                $"agent.path-probe unavailable adapter={Safe(path.Name)} error={ex.GetType().Name}");
            return null;
        }

        if (!settlingAllowed ||
            first.Status is not (NetworkPathProbeStatus.Unreachable or NetworkPathProbeStatus.Ambiguous))
        {
            return first;
        }

        logger.Write(DiagnosticLevel.Info,
            $"agent.path-probe settling adapter={Safe(path.Name)} first={first.Status}");
        try
        {
            await Task.Delay(PathSettlingRetryDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        // The interface may have changed again while DHCP/ICS/NAT was settling.
        // Do not publish a stale failure for the old transport binding; the next
        // tick will immediately observe and probe the new snapshot instead.
        var current = CurrentPaths().FirstOrDefault(candidate => SameIdentity(candidate.Identity, path.Identity));
        if (current is null || !path.HasSameTransportBinding(current))
        {
            logger.Write(DiagnosticLevel.Info,
                $"agent.path-probe settling-changed adapter={Safe(path.Name)}");
            return null;
        }

        try
        {
            var retry = await probe.ProbeAsync(path, InitialProbeTimeout, cancellationToken).ConfigureAwait(false);
            logger.Write(DiagnosticLevel.Info,
                $"agent.path-probe settling-retry adapter={Safe(path.Name)} status={retry.Status}");
            return retry;
        }
        catch (DirectNetworkUnavailableException ex)
        {
            logger.Write(DiagnosticLevel.Warn,
                $"agent.path-probe settling-unavailable adapter={Safe(path.Name)} error={ex.GetType().Name}");
            return null;
        }
    }

    private bool ShouldFirePreferredTimerImmediately(
        PathAuthorizationState? pathState,
        NetworkPathSnapshot current,
        DateTimeOffset now)
    {
        if (pathState is null || pathState.UserActionRequired ||
            pathState.ExpectedExpiryUtc is not { } expiry || now < expiry ||
            pathState.EdgeWatchActive || SnapshotChanged(pathState, current) ||
            pathState.Status != PathAuthorizationStatus.Internet)
        {
            return false;
        }
        if (pathState.NextAutomaticRetryUtc is { } retry && retry > now)
        {
            return false;
        }

        var lastAutomaticSendWasBeforeExpiry = pathState.AutomaticStepOneAttempts > 0 &&
                                                pathState.LastAttemptUtc is { } lastAttempt &&
                                                lastAttempt < expiry;
        return pathState.AutomaticStepOneAttempts == 0 || lastAutomaticSendWasBeforeExpiry;
    }

    private async Task<bool> ConfirmCaptiveAsync(
        NetworkPathSnapshot path,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(100, settings.GuardProbeIntervalMilliseconds));
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Max(100, settings.GuardProbeTimeoutMilliseconds));
        NetworkPathProbeResult confirmation;
        try
        {
            confirmation = await probe.ProbeAsync(path, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectNetworkUnavailableException ex)
        {
            logger.Write(DiagnosticLevel.Warn,
                $"agent.path-probe-confirm unavailable adapter={Safe(path.Name)} error={ex.GetType().Name}");
            return false;
        }

        var updated = state.RecordProbe(path, confirmation, clock.GetUtcNow());
        logger.Write(DiagnosticLevel.Info,
            $"agent.path-probe-confirm adapter={Safe(path.Name)} status={confirmation.Status} expiry={updated.ExpectedExpiryUtc:O}");
        return confirmation.Status == NetworkPathProbeStatus.Captive && !updated.UserActionRequired;
    }

    private async Task RunAuthorizationAsync(
        NetworkPathSnapshot path,
        StoredSecrets secrets,
        CancellationToken cancellationToken)
    {
        var candidateState = state.Find(path.Identity) ?? state.Observe(path);
        var reason = candidateState.AutomaticStepOneAttempts == 0
            ? AuthorizationAttemptReason.Automatic
            : AuthorizationAttemptReason.Retry;
        Publish(new AgentNotification(
            "Автоматическая авторизация Wi-Fi",
            $"Проверенный captive-путь: {path.Name}.",
            AgentNotificationImportance.Routine,
            AgentNotificationSeverity.Info));

        var outcome = await authorization.RunAsync(
            path,
            new AuthorizationRequest(
                secrets.Token,
                secrets.Phone,
                getDeviceId(),
                reason,
                Force: true),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Kind == AuthorizationOutcomeKind.Busy)
        {
            new AuthorizationStateManager(
                new PathRuntimeStateStore(state, path),
                settings,
                clock).ScheduleAutomaticRetry(TimeSpan.FromSeconds(1));
        }

        logger.Write(DiagnosticLevel.Info,
            $"agent.path-auth adapter={Safe(path.Name)} result={outcome.Kind} confirmed={outcome.InternetConfirmed}");
        NotifyOutcome(path, outcome);
    }

    private IReadOnlyList<NetworkPathSnapshot> CurrentPaths() =>
        enumeratePaths()
            .Where(path => path.CanAutomaticallyAuthorize)
            .GroupBy(path => path.Identity)
            .Select(group => group.First())
            .ToArray();

    private TimeSpan GetPathDelay(PathAuthorizationState path, DateTimeOffset now)
    {
        if (path.UserActionRequired)
        {
            return MaximumWait;
        }
        if (path.NextAutomaticRetryUtc is { } retry && retry > now)
        {
            return Clamp(retry - now);
        }
        if (path.Status is PathAuthorizationStatus.Unknown or PathAuthorizationStatus.Disconnected)
        {
            return TimeSpan.FromMilliseconds(100);
        }
        if (path.Status == PathAuthorizationStatus.Captive)
        {
            return TimeSpan.FromMilliseconds(100);
        }
        if (path.Status is PathAuthorizationStatus.Unreachable or PathAuthorizationStatus.Ambiguous)
        {
            return DueFromLastProbe(path.LastProbeUtc, FailureRecheck, now);
        }
        if (path.ExpectedExpiryUtc is not { } expiry)
        {
            return DueFromLastProbe(path.LastProbeUtc, UnknownInternetRecheck, now);
        }

        var fastStart = expiry - TimeSpan.FromSeconds(10);
        if (now < fastStart)
        {
            return Clamp(fastStart - now);
        }
        var interval = AgentTiming.GetEdgeProbeInterval(settings, expiry, now);
        var fromProbe = DueFromLastProbe(path.LastProbeUtc, interval, now);
        if (now < expiry)
        {
            return Min(fromProbe, Clamp(expiry - now));
        }
        return fromProbe;
    }

    private static TimeSpan DueFromLastProbe(DateTimeOffset? lastProbe, TimeSpan interval, DateTimeOffset now)
    {
        if (lastProbe is null)
        {
            return TimeSpan.FromMilliseconds(100);
        }
        var due = lastProbe.Value + interval;
        return due <= now ? TimeSpan.FromMilliseconds(100) : Clamp(due - now);
    }

    private static bool SnapshotChanged(PathAuthorizationState previous, NetworkPathSnapshot current) =>
        previous.InterfaceIndex != current.InterfaceIndex ||
        !string.Equals(previous.SourceIPv4, current.SourceIPv4?.ToString(), StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(previous.GatewayIPv4, current.GatewayIPv4?.ToString(), StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(previous.Ssid, current.Ssid, StringComparison.Ordinal);

    private static string PathSortKey(NetworkPathSnapshot path) =>
        $"{path.AdapterId}\u001f{path.Identity.NetworkDiscriminator}";

    private static bool SameIdentity(NetworkPathIdentity left, NetworkPathIdentity right) =>
        left.InterfaceType == right.InterfaceType &&
        string.Equals(left.AdapterId, right.AdapterId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.NetworkDiscriminator, right.NetworkDiscriminator, StringComparison.OrdinalIgnoreCase);

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
    private static TimeSpan Clamp(TimeSpan value) =>
        value < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) :
        value > MaximumWait ? MaximumWait : value;
    private static string Safe(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    private void NotifyOutcome(NetworkPathSnapshot path, AuthorizationOutcome outcome)
    {
        if (outcome.Kind == AuthorizationOutcomeKind.Success)
        {
            Publish(new AgentNotification(
                "Wi-Fi авторизован",
                $"Путь {path.Name} авторизован{(outcome.InternetConfirmed == true ? "; Интернет подтверждён" : string.Empty)}.",
                AgentNotificationImportance.Important,
                outcome.InternetConfirmed == true ? AgentNotificationSeverity.Success : AgentNotificationSeverity.Warning));
        }
        else if (outcome.Kind is AuthorizationOutcomeKind.BearerInvalid or AuthorizationOutcomeKind.UserActionRequired)
        {
            Publish(new AgentNotification(
                "Автоавторизация остановлена",
                $"Путь {path.Name}: требуется действие пользователя.",
                AgentNotificationImportance.Important,
                AgentNotificationSeverity.Error));
        }
    }

    private void Publish(AgentNotification notification)
    {
        if (notifications is null) return;
        try { notifications.Publish(notification); }
        catch (Exception ex)
        {
            logger.Write(DiagnosticLevel.Warn,
                $"notification.failed type={notification.Severity} error={ex.GetType().Name}:{ex.Message}");
        }
    }
}
