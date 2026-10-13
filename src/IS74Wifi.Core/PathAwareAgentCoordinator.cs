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
    PreferredNetworkPathResolver? preferredPathResolver = null,
    NetworkPathTelemetryRecorder? telemetry = null,
    Func<bool>? isVpnActive = null)
{
    private static readonly TimeSpan NoPathRecheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PathSettlingRetryDelay = TimeSpan.FromSeconds(1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private NetworkPathIdentity? lastPreferredPath;
    private int connectivityChangePending;

    public void NotifyNetworkConnectivityChanged() =>
        Interlocked.Exchange(ref connectivityChangePending, 1);

    public TimeSpan GetSleepDelay()
    {
        var now = clock.GetUtcNow();
        var current = CurrentPaths();
        if (current.Count == 0)
        {
            return NoPathRecheck;
        }

        var delay = PathAuthorizationSchedule.MaximumWait;
        foreach (var path in current)
        {
            var existing = state.Find(path.Identity);
            if (existing is null || existing.Status == PathAuthorizationStatus.Disconnected || PathAuthorizationSchedule.SnapshotChanged(existing, path))
            {
                return TimeSpan.FromMilliseconds(100);
            }

            delay = Min(delay, PathAuthorizationSchedule.GetDelay(existing, path, settings, now));
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
            if (PathAuthorizationSchedule.HasObservedInternetPastExpiry(pathState)) continue;
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
        var connectivityChanged = Interlocked.Exchange(ref connectivityChangePending, 0) != 0;
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
                // Preserve the latency-first edge path: do not add VPN/interface
                // diagnostics before the immediate timer-triggered stepOne.
                await RunAuthorizationAsync(
                    preferredSnapshot,
                    secrets,
                    preferred: true,
                    vpnActive: null,
                    cancellationToken,
                    refreshAfterPreStepFailure: true).ConfigureAwait(false);
                return;
            }
        }

        var vpnActiveNow = telemetry is null ? false : SafeVpnActive();
        NetworkPathSnapshot? authorizationCandidate = null;
        foreach (var path in current
                     .OrderByDescending(path => lastPreferredPath is not null && SameIdentity(path.Identity, lastPreferredPath))
                     .ThenBy(PathSortKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = state.Find(path.Identity);
            var observationReason = ObservationReason(before, path);
            var changed = observationReason != "scheduled_recheck";
            var observed = state.Observe(path, now);
            if (!changed && PathAuthorizationSchedule.IsPreStepRetryPending(observed) &&
                observed.NextAutomaticRetryUtc <= now)
            {
                // Continue a previously started timer recovery even if its last
                // observation was Unreachable or Windows changed path preference.
                await RunAuthorizationAsync(path, secrets, IsPreferred(path), vpnActiveNow,
                    cancellationToken, refreshAfterPreStepFailure: true).ConfigureAwait(false);
                return;
            }
            var connectivityTriggeredProbe = connectivityChanged &&
                                             PathAuthorizationSchedule.IsConnectivityEventDriven(observed, path);
            if (!changed && !connectivityTriggeredProbe &&
                PathAuthorizationSchedule.GetDelay(observed, path, settings, now) > TimeSpan.FromMilliseconds(100))
            {
                continue;
            }

            var probeOutcome = await ProbePathAsync(path, changed, cancellationToken).ConfigureAwait(false);
            if (probeOutcome is null)
            {
                continue;
            }

            var result = probeOutcome.Result;
            var updated = state.RecordProbe(path, result, clock.GetUtcNow());
            logger.Write(DiagnosticLevel.Info,
                $"agent.path-probe adapter={Safe(path.Name)} status={result.Status} expiry={updated.ExpectedExpiryUtc:O}");

            if (result.Status != NetworkPathProbeStatus.Captive &&
                ShouldRecordObservation(before, observationReason, probeOutcome))
            {
                telemetry?.RecordObservation(
                    path,
                    before,
                    observationReason,
                    probeOutcome.InitialStatus,
                    result.Status,
                    probeOutcome.SettlingRetryUsed,
                    probeOutcome.DurationMs,
                    IsPreferred(path),
                    vpnActiveNow,
                    clock.GetUtcNow());
            }

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

        await RunAuthorizationAsync(
            authorizationCandidate,
            secrets,
            IsPreferred(authorizationCandidate),
            vpnActiveNow,
            cancellationToken).ConfigureAwait(false);
    }


    private async Task<ProbePathOutcome?> ProbePathAsync(
        NetworkPathSnapshot path,
        bool settlingAllowed,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        NetworkPathProbeResult first;
        try
        {
            first = await probe.ProbeAsync(path, timeout ?? InitialProbeTimeout, cancellationToken).ConfigureAwait(false);
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
            return new ProbePathOutcome(
                first,
                first.Status,
                SettlingRetryUsed: false,
                stopwatch.Elapsed.TotalMilliseconds);
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
            return new ProbePathOutcome(
                retry,
                first.Status,
                SettlingRetryUsed: true,
                stopwatch.Elapsed.TotalMilliseconds);
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
            pathState.EdgeWatchActive || PathAuthorizationSchedule.HasObservedInternetPastExpiry(pathState) ||
            PathAuthorizationSchedule.SnapshotChanged(pathState, current))
        {
            return false;
        }

        if (pathState.NextAutomaticRetryUtc is { } retry && retry > now)
        {
            return false;
        }

        if (PathAuthorizationSchedule.IsPreStepRetryPending(pathState))
        {
            return true;
        }
        if (pathState.Status != PathAuthorizationStatus.Internet)
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
        bool preferred,
        bool? vpnActive,
        CancellationToken cancellationToken,
        bool refreshAfterPreStepFailure = false)
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
            new PathAuthorizationExecutionContext(preferred, vpnActive, candidateState),
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
        if (refreshAfterPreStepFailure && outcome.Kind == AuthorizationOutcomeKind.RetryableBeforeStepOne &&
            !cancellationToken.IsCancellationRequested)
        {
            // The critical transaction has already ended. Observe once per failed
            // retry, not at fast edge cadence. Internet ends speculative recovery;
            // other outcomes preserve the retry deadline.
            var current = CurrentPaths().FirstOrDefault(item => SameIdentity(item.Identity, path.Identity));
            if (current is null || !path.HasSameTransportBinding(current)) return;
            var retryAt = state.Find(path.Identity)?.NextAutomaticRetryUtc;
            var remaining = retryAt - clock.GetUtcNow();
            var probeBudget = remaining is { } left && left > PathAuthorizationSchedule.MinimumDelay
                ? Min(InitialProbeTimeout, left) : PathAuthorizationSchedule.MinimumDelay;
            var observation = await ProbePathAsync(path, settlingAllowed: false, cancellationToken,
                probeBudget).ConfigureAwait(false);
            var result = observation?.Result ?? new NetworkPathProbeResult(path.Identity,
                NetworkPathProbeStatus.Unreachable,
                new InternetProbeResult(false, false, null, null,
                    TransportFailureKind.DirectRouteUnavailable, TimeSpan.Zero));
            var updated = state.RecordProbe(path, result, clock.GetUtcNow());
            logger.Write(DiagnosticLevel.Info,
                $"agent.path-probe adapter={Safe(path.Name)} status={result.Status} expiry={updated.ExpectedExpiryUtc:O} reason=pre-step-failure");
        }
    }

    private bool SafeVpnActive()
    {
        try
        {
            return isVpnActive?.Invoke() == true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsPreferred(NetworkPathSnapshot path) =>
        lastPreferredPath is not null && SameIdentity(path.Identity, lastPreferredPath);

    private static string ObservationReason(PathAuthorizationState? before, NetworkPathSnapshot path)
    {
        if (before is null) return "appeared";
        if (before.Status == PathAuthorizationStatus.Disconnected) return "reappeared";
        return PathAuthorizationSchedule.SnapshotChanged(before, path) ? "changed" : "scheduled_recheck";
    }

    private static bool ShouldRecordObservation(
        PathAuthorizationState? before,
        string reason,
        ProbePathOutcome outcome) =>
        !string.Equals(reason, "scheduled_recheck", StringComparison.Ordinal) ||
        outcome.SettlingRetryUsed ||
        outcome.Result.Status != NetworkPathProbeStatus.Internet ||
        before?.Status != PathAuthorizationStatus.Internet;

    private sealed record ProbePathOutcome(
        NetworkPathProbeResult Result,
        NetworkPathProbeStatus InitialStatus,
        bool SettlingRetryUsed,
        double DurationMs);

    private IReadOnlyList<NetworkPathSnapshot> CurrentPaths() =>
        enumeratePaths()
            .Where(path => path.CanAutomaticallyAuthorize)
            .GroupBy(path => path.Identity)
            .Select(group => group.First())
            .ToArray();

    private static string PathSortKey(NetworkPathSnapshot path) =>
        $"{path.AdapterId}\u001f{path.Identity.NetworkDiscriminator}";

    private static bool SameIdentity(NetworkPathIdentity left, NetworkPathIdentity right) =>
        left.InterfaceType == right.InterfaceType &&
        string.Equals(left.AdapterId, right.AdapterId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.NetworkDiscriminator, right.NetworkDiscriminator, StringComparison.OrdinalIgnoreCase);

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
    private static TimeSpan Clamp(TimeSpan value) =>
        value < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) :
        value > PathAuthorizationSchedule.MaximumWait ? PathAuthorizationSchedule.MaximumWait : value;
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
