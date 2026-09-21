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
    IAgentNotificationSink? notifications = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private DateTimeOffset? notifiedExpiryUtc;
    private static readonly TimeSpan ExpiryReminderWindow = TimeSpan.FromMinutes(5);

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
        if (remaining > ExpiryReminderWindow || notifiedExpiryUtc == expiry || !IsNetworkPolicySatisfied())
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
