namespace IS74Wifi.Core;

public sealed record StepOneBudgetDecision(bool Allowed, int Attempt, bool UserActionRequired);

public sealed class AuthorizationStateManager(
    RuntimeStateStore store,
    AppSettings settings,
    TimeProvider? timeProvider = null)
{
    private static readonly int[] PreStepRetryDelaysSeconds = [1, 2, 5, 15, 30, 60];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public RuntimeState Load() => store.Load();

    public void ResetAutomaticCycleForManual()
    {
        var state = store.Load() with
        {
            AutomaticStepOneAttempts = 0,
            PreStepFailureCount = 0,
            NextAutomaticRetryUtc = null,
            UserActionRequired = false,
            EdgeWatchActive = false
        };
        store.Save(state);
    }

    public StepOneBudgetDecision RegisterStepOneSend(AuthorizationAttemptReason reason)
    {
        if (reason == AuthorizationAttemptReason.Manual)
        {
            return new StepOneBudgetDecision(true, 0, false);
        }

        var state = store.Load();
        if (state.UserActionRequired)
        {
            return new StepOneBudgetDecision(false, state.AutomaticStepOneAttempts, true);
        }

        if (state.AutomaticStepOneAttempts >= settings.MaxAutomaticStepOneAttempts)
        {
            store.Save(state with
            {
                UserActionRequired = true,
                NextAutomaticRetryUtc = null,
                LastResult = "automatic-step-one-limit"
            });
            return new StepOneBudgetDecision(false, state.AutomaticStepOneAttempts, true);
        }

        var attempt = state.AutomaticStepOneAttempts + 1;
        store.Save(state with
        {
            AutomaticStepOneAttempts = attempt,
            PreStepFailureCount = 0,
            NextAutomaticRetryUtc = null,
            LastAttemptUtc = clock.GetUtcNow(),
            LastAttemptReason = reason == AuthorizationAttemptReason.Retry ? "retry" : "automatic",
            LastResult = "step-one-sent"
        });
        return new StepOneBudgetDecision(true, attempt, false);
    }

    public TimeSpan? MarkStepOneRetryable(AuthorizationAttemptReason reason)
    {
        var state = store.Load() with
        {
            LastAttemptUtc = clock.GetUtcNow(),
            LastAttemptReason = ReasonText(reason),
            LastResult = "step-one-retryable-error"
        };

        if (reason == AuthorizationAttemptReason.Manual)
        {
            store.Save(state);
            return null;
        }

        if (state.AutomaticStepOneAttempts >= settings.MaxAutomaticStepOneAttempts)
        {
            store.Save(state with
            {
                UserActionRequired = true,
                NextAutomaticRetryUtc = null,
                LastResult = "automatic-step-one-limit"
            });
            return null;
        }

        var delays = settings.AutomaticRetryDelaysSeconds;
        var delaySeconds = delays.Length == 0
            ? 60
            : delays[Math.Clamp(state.AutomaticStepOneAttempts - 1, 0, delays.Length - 1)];
        var retryAt = clock.GetUtcNow().AddSeconds(delaySeconds);
        store.Save(state with { NextAutomaticRetryUtc = retryAt });
        return TimeSpan.FromSeconds(delaySeconds);
    }

    public TimeSpan? MarkPreStepFailure(AuthorizationAttemptReason reason)
    {
        var state = store.Load() with
        {
            LastAttemptUtc = clock.GetUtcNow(),
            LastAttemptReason = ReasonText(reason),
            LastResult = "pre-step-retryable-error"
        };

        if (reason == AuthorizationAttemptReason.Manual)
        {
            store.Save(state);
            return null;
        }

        var count = state.PreStepFailureCount + 1;
        var delaySeconds = PreStepRetryDelaysSeconds[Math.Clamp(count - 1, 0, PreStepRetryDelaysSeconds.Length - 1)];
        store.Save(state with
        {
            PreStepFailureCount = count,
            NextAutomaticRetryUtc = clock.GetUtcNow().AddSeconds(delaySeconds)
        });
        return TimeSpan.FromSeconds(delaySeconds);
    }

    public void ClearPreStepFailure()
    {
        var state = store.Load();
        if (state.PreStepFailureCount == 0 && state.NextAutomaticRetryUtc is null)
        {
            return;
        }

        store.Save(state with
        {
            PreStepFailureCount = 0,
            NextAutomaticRetryUtc = null
        });
    }

    public void MarkAlreadyAuthorized(AuthorizationAttemptReason reason)
    {
        var state = store.Load() with
        {
            LastAttemptUtc = clock.GetUtcNow(),
            LastAttemptReason = ReasonText(reason),
            LastResult = "already-authorized"
        };

        if (reason != AuthorizationAttemptReason.Manual)
        {
            state = state with
            {
                EdgeWatchActive = true,
                NextAutomaticRetryUtc = null
            };
        }
        store.Save(state);
    }

    public void MarkUserActionRequired(string result, AuthorizationAttemptReason reason)
    {
        var state = store.Load() with
        {
            LastAttemptUtc = clock.GetUtcNow(),
            LastAttemptReason = ReasonText(reason),
            LastResult = result,
            UserActionRequired = true,
            NextAutomaticRetryUtc = null
        };
        store.Save(state);
    }

    public void MarkCancelled(AuthorizationAttemptReason reason)
    {
        var state = store.Load() with
        {
            LastAttemptUtc = clock.GetUtcNow(),
            LastAttemptReason = ReasonText(reason),
            LastResult = "cancelled"
        };
        store.Save(state);
    }

    public DateTimeOffset MarkSuccess(DateTimeOffset? serverDate, bool internetConfirmed)
    {
        var now = clock.GetUtcNow();
        var authorizedAt = serverDate?.ToUniversalTime() ?? now;
        store.Save(new RuntimeState
        {
            LastAuthUtc = authorizedAt,
            ExpectedExpiryUtc = authorizedAt.AddHours(settings.AuthWindowHours),
            LastAttemptUtc = now,
            LastAttemptReason = "success",
            LastResult = "success",
            InternetConfirmed = internetConfirmed,
            AutomaticStepOneAttempts = 0,
            PreStepFailureCount = 0,
            NextAutomaticRetryUtc = null,
            UserActionRequired = false,
            EdgeWatchActive = false
        });
        return authorizedAt;
    }

    public void MarkInternetConfirmed()
    {
        var state = store.Load();
        if (state.LastResult == "success" && state.InternetConfirmed != true)
        {
            store.Save(state with { InternetConfirmed = true });
        }
    }

    private static string ReasonText(AuthorizationAttemptReason reason) => reason switch
    {
        AuthorizationAttemptReason.Automatic => "automatic",
        AuthorizationAttemptReason.Retry => "retry",
        _ => "manual"
    };
}
