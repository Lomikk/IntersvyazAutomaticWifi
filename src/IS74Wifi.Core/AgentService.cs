namespace IS74Wifi.Core;

public static class AgentTiming
{
    public static TimeSpan GetSleepDelay(RuntimeState state, AppSettings settings, DateTimeOffset now)
    {
        var idle = TimeSpan.FromSeconds(Math.Max(1, settings.AgentPollSeconds));
        if (state.ExpectedExpiryUtc is not { } expiry)
        {
            return idle;
        }

        var guardSeconds = Math.Max(1, settings.GuardWindowSeconds);
        var guardStart = expiry.AddSeconds(-guardSeconds);
        var guardEnd = expiry.AddSeconds(guardSeconds);
        var guardDelay = TimeSpan.FromMilliseconds(Math.Max(100, settings.GuardProbeIntervalMilliseconds));

        if (now < guardStart)
        {
            var untilGuard = guardStart - now;
            return ClampMinimum(untilGuard < idle ? untilGuard : idle);
        }

        if (now <= guardEnd)
        {
            return guardDelay;
        }

        if (state.NextAutomaticRetryUtc is { } retryAt && now < retryAt)
        {
            var untilRetry = retryAt - now;
            return ClampMinimum(untilRetry < idle ? untilRetry : idle);
        }

        var overdue = TimeSpan.FromSeconds(1);
        return idle < overdue ? idle : overdue;
    }

    private static TimeSpan ClampMinimum(TimeSpan value) =>
        value < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : value;
}

public sealed class AgentService(
    DpapiSecretStore secrets,
    DeviceIdentityStore deviceIdentity,
    AuthorizationStateManager state,
    IAuthorizationRunner authorization,
    IInternetConnectivityProbe internet,
    IWifiEnvironment wifi,
    AppSettings settings,
    DiagnosticLogger logger,
    TimeProvider? timeProvider = null,
    IAddressCacheWarmer? addressCacheWarmer = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public TimeSpan GetSleepDelay() => AgentTiming.GetSleepDelay(state.Load(), settings, clock.GetUtcNow());

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var storedSecrets = secrets.Load();
        if (storedSecrets is null)
        {
            return;
        }

        var runtime = state.Load();
        if (runtime.ExpectedExpiryUtc is not { } expiry)
        {
            // A successful stepTwo is the only trustworthy 24-hour reference.
            // The first Wi-Fi authorization remains an explicit user action.
            return;
        }

        var now = clock.GetUtcNow();
        var guardSeconds = Math.Max(1, settings.GuardWindowSeconds);
        var guardStart = expiry.AddSeconds(-guardSeconds);
        var guardEnd = expiry.AddSeconds(guardSeconds);

        if (now < guardStart)
        {
            if (addressCacheWarmer is not null)
            {
                await addressCacheWarmer.WarmKnownHostsAsync(TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (runtime.UserActionRequired || !wifi.IsTargetWifiConnected())
        {
            return;
        }

        if (runtime.NextAutomaticRetryUtc is { } retryAt && now < retryAt)
        {
            return;
        }

        if (runtime.AutomaticStepOneAttempts >= settings.MaxAutomaticStepOneAttempts)
        {
            state.MarkUserActionRequired("automatic-step-one-limit", AuthorizationAttemptReason.Retry);
            logger.Write(DiagnosticLevel.Error,
                $"Automatic stepOne limit reached attempts={runtime.AutomaticStepOneAttempts}/{settings.MaxAutomaticStepOneAttempts}");
            return;
        }

        var shouldAuthorize = false;
        if (now < expiry)
        {
            var probeTimeoutMs = Math.Max(100, settings.GuardProbeTimeoutMilliseconds);
            var confirmDelayMs = Math.Max(100, settings.GuardProbeIntervalMilliseconds);
            var guardProbeBudget = TimeSpan.FromMilliseconds((2 * probeTimeoutMs) + confirmDelayMs + 100);
            if (expiry - now <= guardProbeBudget)
            {
                return;
            }

            shouldAuthorize = await ConfirmCaptiveBeforeExpiryAsync(
                TimeSpan.FromMilliseconds(confirmDelayMs),
                TimeSpan.FromMilliseconds(probeTimeoutMs),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var lastAutomaticSendWasBeforeExpiry = runtime.AutomaticStepOneAttempts > 0 &&
                                                    runtime.LastAttemptUtc is { } lastAttempt &&
                                                    lastAttempt < expiry;

            if (runtime.AutomaticStepOneAttempts == 0 || lastAutomaticSendWasBeforeExpiry)
            {
                shouldAuthorize = true;
            }
            else if (runtime.EdgeWatchActive && now <= guardEnd)
            {
                shouldAuthorize = await ConfirmCaptiveBeforeExpiryAsync(
                    TimeSpan.FromMilliseconds(Math.Max(100, settings.GuardProbeIntervalMilliseconds)),
                    TimeSpan.FromMilliseconds(Math.Max(100, settings.GuardProbeTimeoutMilliseconds)),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (runtime.EdgeWatchActive && now > guardEnd)
            {
                shouldAuthorize = true;
            }
            else if (string.Equals(runtime.LastResult, "step-one-retryable-error", StringComparison.Ordinal) ||
                     string.Equals(runtime.LastResult, "pre-step-retryable-error", StringComparison.Ordinal))
            {
                // Pre-step API/DNS failures are safe to retry because no captive
                // side effect happened and they do not consume a stepOne attempt.
                shouldAuthorize = true;
            }
        }

        if (!shouldAuthorize || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var reason = runtime.AutomaticStepOneAttempts == 0
            ? AuthorizationAttemptReason.Automatic
            : AuthorizationAttemptReason.Retry;
        var deviceId = deviceIdentity.GetOrCreate();
        var outcome = await authorization.RunAsync(
            new AuthorizationRequest(
                storedSecrets.Token,
                storedSecrets.Phone,
                deviceId,
                reason,
                Force: true),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Kind == AuthorizationOutcomeKind.AlreadyAuthorized && clock.GetUtcNow() > guardEnd)
        {
            var current = state.Load();
            var delays = settings.AutomaticRetryDelaysSeconds;
            var delaySeconds = delays.Length == 0
                ? 60
                : delays[Math.Clamp(current.AutomaticStepOneAttempts - 1, 0, delays.Length - 1)];
            state.ScheduleAutomaticRetry(TimeSpan.FromSeconds(delaySeconds));
        }
    }

    private async Task<bool> ConfirmCaptiveBeforeExpiryAsync(
        TimeSpan delay,
        TimeSpan probeTimeout,
        CancellationToken cancellationToken)
    {
        var first = await internet.ProbeAsync(probeTimeout, cancellationToken).ConfigureAwait(false);
        if (first.Online || !first.HttpResponseReceived || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        var second = await internet.ProbeAsync(probeTimeout, cancellationToken).ConfigureAwait(false);
        return !second.Online && second.HttpResponseReceived;
    }
}
