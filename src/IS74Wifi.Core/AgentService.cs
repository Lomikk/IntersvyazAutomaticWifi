namespace IS74Wifi.Core;

public static class AgentTiming
{
    private static readonly TimeSpan MinimumTelemetrySafetyWindow = TimeSpan.FromMinutes(1);
    // Outside the five-minute reminder/edge approach there is no authorization
    // work to perform. Keep a bounded housekeeping heartbeat rather than
    // waking the process every AgentPollSeconds (15 s by default).
    private static readonly TimeSpan LongIdleInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ExpiryReminderWindow = TimeSpan.FromMinutes(5);

    // This must not depend on GetSleepDelay: the normal 15-second agent tick
    // says nothing about how far away the next authorization actually is.
    public static bool CanUploadTelemetry(RuntimeState state, AppSettings settings, DateTimeOffset now)
    {
        // A raised batch cap/timeout can extend the upload well beyond a minute.
        // Reserve enough time for the entire flush plus a small scheduling margin.
        var uploadBudget = TelemetryUploader.GetHttpTimeout(settings) *
                           Math.Clamp(settings.TelemetryMaxBatchesPerFlush, 1, 8) +
                           TimeSpan.FromSeconds(10);
        var safetyWindow = uploadBudget > MinimumTelemetrySafetyWindow
            ? uploadBudget
            : MinimumTelemetrySafetyWindow;

        if (state.UserActionRequired || state.ExpectedExpiryUtc is not { } expiry)
        {
            // No automatic authorization is scheduled while the user must act,
            // or before the first successful Wi-Fi authorization on a clean install.
            return true;
        }

        if (expiry - now > safetyWindow)
        {
            return true;
        }

        // Once expiry has been reached, a distant explicit retry leaves a safe
        // idle window in which queued diagnostics can still be delivered.
        return state.NextAutomaticRetryUtc is { } retryAt &&
               retryAt - now > safetyWindow;
    }

    public static TimeSpan GetSleepDelay(RuntimeState state, AppSettings settings, DateTimeOffset now)
    {
        var approachInterval = TimeSpan.FromSeconds(Math.Max(1, settings.AgentPollSeconds));
        if (state.UserActionRequired || state.ExpectedExpiryUtc is not { } expiry)
        {
            // No successful stepTwo baseline exists, or automatic attempts
            // are paused until the user acts. Only maintenance is scheduled.
            return LongIdleInterval;
        }

        var guardSeconds = Math.Max(1, settings.GuardWindowSeconds);
        var guardStart = expiry.AddSeconds(-guardSeconds);
        var guardEnd = expiry.AddSeconds(guardSeconds);
        var guardDelay = TimeSpan.FromMilliseconds(Math.Max(100, settings.GuardProbeIntervalMilliseconds));

        if (now < guardStart)
        {
            var reminderStart = expiry - ExpiryReminderWindow;
            var longIdleEnd = reminderStart < guardStart ? reminderStart : guardStart;
            if (now < longIdleEnd)
            {
                // Wake precisely at the five-minute reminder boundary even if
                // a long idle sleep would otherwise overshoot it.
                return ClampMinimum(Min(LongIdleInterval, longIdleEnd - now));
            }

            // Retain the existing cadence near expiry and never cross the
            // 10-second guard boundary in a single sleep.
            return ClampMinimum(Min(approachInterval, guardStart - now));
        }

        if (now <= guardEnd)
        {
            return guardDelay;
        }

        if (state.NextAutomaticRetryUtc is { } retryAt && now < retryAt)
        {
            var untilRetry = retryAt - now;
            return ClampMinimum(Min(untilRetry, approachInterval));
        }

        var overdue = TimeSpan.FromSeconds(1);
        return Min(approachInterval, overdue);
    }

    // The authorization clock is independent of update checks and queued
    // telemetry. A shorter due time must still wake the agent while it is
    // otherwise waiting in the 15-minute long-idle mode.
    public static TimeSpan BoundSleepByBackgroundWork(
        TimeSpan authorizationDelay,
        DateTimeOffset now,
        DateTimeOffset? updateDueUtc,
        DateTimeOffset? telemetryDueUtc)
    {
        var delay = authorizationDelay;
        delay = BoundByDueTime(delay, now, updateDueUtc);
        delay = BoundByDueTime(delay, now, telemetryDueUtc);
        return ClampMinimum(delay);
    }

    private static TimeSpan BoundByDueTime(TimeSpan delay, DateTimeOffset now, DateTimeOffset? dueUtc)
    {
        if (dueUtc is not { } due)
        {
            return delay;
        }

        // The due task was already attempted on this iteration. Avoid a busy
        // loop if its state could not be persisted or another process holds
        // the upload lease.
        var untilDue = due > now ? due - now : TimeSpan.FromMinutes(1);
        return Min(delay, untilDue);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

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
    IAgentNotificationSink? notifications = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private DateTimeOffset? notifiedExpiryUtc;
    public TimeSpan GetSleepDelay() => AgentTiming.GetSleepDelay(state.Load(), settings, clock.GetUtcNow());

    public bool CanUploadTelemetry() => AgentTiming.CanUploadTelemetry(state.Load(), settings, clock.GetUtcNow());

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

        MaybeNotifyUpcomingExpiry(runtime, expiry, now);

        if (now < guardStart)
        {
            return;
        }

        if (runtime.UserActionRequired || !IsNetworkPolicySatisfied())
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
            PublishNotification(new AgentNotification(
                "Автоавторизация остановлена",
                "Достигнут лимит автоматических попыток. Откройте IS74Wifi для подробностей.",
                AgentNotificationImportance.Important,
                AgentNotificationSeverity.Error));
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
        PublishNotification(new AgentNotification(
            "Автоматическая авторизация Wi-Fi",
            reason == AuthorizationAttemptReason.Retry
                ? "Начинаю повторную попытку авторизации."
                : "Начинаю авторизацию Wi-Fi.",
            AgentNotificationImportance.Routine,
            AgentNotificationSeverity.Info));

        var deviceId = deviceIdentity.GetOrCreate();
        var outcome = await authorization.RunAsync(
            new AuthorizationRequest(
                storedSecrets.Token,
                storedSecrets.Phone,
                deviceId,
                reason,
                Force: true),
            cancellationToken).ConfigureAwait(false);

        NotifyAuthorizationOutcome(outcome);

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


    private void MaybeNotifyUpcomingExpiry(RuntimeState runtime, DateTimeOffset expiry, DateTimeOffset now)
    {
        if (notifications is null || runtime.UserActionRequired || now >= expiry)
        {
            return;
        }

        var remaining = expiry - now;
        if (remaining > AgentTiming.ExpiryReminderWindow || notifiedExpiryUtc == expiry || !IsNetworkPolicySatisfied())
        {
            return;
        }

        notifiedExpiryUtc = expiry;
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        PublishNotification(new AgentNotification(
            "Скоро может потребоваться авторизация",
            $"Повторная авторизация Wi-Fi может потребоваться примерно через {minutes} мин.",
            AgentNotificationImportance.Routine,
            AgentNotificationSeverity.Info));
    }

    private void NotifyAuthorizationOutcome(AuthorizationOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case AuthorizationOutcomeKind.Success:
                PublishNotification(new AgentNotification(
                    "Wi-Fi авторизован",
                    outcome.InternetConfirmed == true
                        ? "Автоматическая авторизация завершена успешно; Интернет доступен."
                        : "Авторизация принята, но доступ в Интернет пока не подтверждён.",
                    AgentNotificationImportance.Important,
                    outcome.InternetConfirmed == true ? AgentNotificationSeverity.Success : AgentNotificationSeverity.Warning));
                break;

            case AuthorizationOutcomeKind.AlreadyAuthorized:
                PublishNotification(new AgentNotification(
                    "Wi-Fi уже авторизован",
                    "Дополнительная авторизация сейчас не требуется.",
                    AgentNotificationImportance.Routine,
                    AgentNotificationSeverity.Success));
                break;

            case AuthorizationOutcomeKind.AlreadyOnline:
                PublishNotification(new AgentNotification(
                    "Интернет уже доступен",
                    "Дополнительная Wi-Fi авторизация сейчас не требуется.",
                    AgentNotificationImportance.Routine,
                    AgentNotificationSeverity.Success));
                break;

            case AuthorizationOutcomeKind.RetryableBeforeStepOne:
            case AuthorizationOutcomeKind.RetryableStepOne:
                PublishNotification(new AgentNotification(
                    "Авторизация Wi-Fi отложена",
                    outcome.RetryAfter is { } retryAfter
                        ? $"Возникла временная ошибка. Повторная попытка примерно через {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} сек."
                        : "Возникла временная ошибка; программа повторит попытку автоматически.",
                    AgentNotificationImportance.Routine,
                    AgentNotificationSeverity.Warning));
                break;

            case AuthorizationOutcomeKind.BearerInvalid:
                PublishNotification(new AgentNotification(
                    "Требуется повторная регистрация",
                    "API-сессия больше не действует. Откройте IS74Wifi и зарегистрируйте устройство снова.",
                    AgentNotificationImportance.Important,
                    AgentNotificationSeverity.Error));
                break;

            case AuthorizationOutcomeKind.StepTwoAmbiguous:
                PublishNotification(new AgentNotification(
                    "Не удалось подтвердить Wi-Fi",
                    "Ответ captive portal потерян, а Интернет не подтвердился. Откройте IS74Wifi для подробностей.",
                    AgentNotificationImportance.Important,
                    AgentNotificationSeverity.Error));
                break;

            case AuthorizationOutcomeKind.UserActionRequired:
                PublishNotification(new AgentNotification(
                    "Автоавторизация остановлена",
                    "Для продолжения требуется действие пользователя. Откройте IS74Wifi для подробностей.",
                    AgentNotificationImportance.Important,
                    AgentNotificationSeverity.Error));
                break;
        }
    }

    private void PublishNotification(AgentNotification notification)
    {
        if (notifications is null)
        {
            return;
        }

        try
        {
            notifications.Publish(notification);
        }
        catch (Exception ex)
        {
            logger.Write(DiagnosticLevel.Warn,
                $"notification.failed type={notification.Severity} error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private bool IsNetworkPolicySatisfied() =>
        settings.IgnoreNetworkCheck || wifi.IsTargetWifiConnected();

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
