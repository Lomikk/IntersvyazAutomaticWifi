using System.Diagnostics;

namespace IS74Wifi.Core;

public sealed class AuthorizationFlow(
    IIs74PushClient api,
    ICaptivePortalClient portal,
    IInternetConnectivityProbe internet,
    IWifiEnvironment wifi,
    PushPollingEngine polling,
    AuthorizationStateManager state,
    DiagnosticLogger logger,
    AuthorizationFlowOptions? options = null,
    AuthorizationTelemetryRecorder? telemetry = null) : IAuthorizationRunner
{
    private readonly AuthorizationFlowOptions options = options ?? new AuthorizationFlowOptions();

    public async Task<AuthorizationOutcome> RunAsync(
        AuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var trace = telemetry?.Begin(request.Reason);
        try
        {
            var outcome = await RunCoreAsync(request, trace, cancellationToken).ConfigureAwait(false);
            trace?.Complete(outcome);
            return outcome;
        }
        catch (Exception exception)
        {
            trace?.UnexpectedException(exception);
            trace?.Complete(new AuthorizationOutcome(
                AuthorizationOutcomeKind.UserActionRequired,
                InternetConfirmed: false,
                AuthorizedAtUtc: null,
                RetryAfter: null,
                Timing: null));
            throw;
        }
    }

    private async Task<AuthorizationOutcome> RunCoreAsync(
        AuthorizationRequest request,
        AuthorizationTelemetryTrace? trace,
        CancellationToken cancellationToken)
    {

        if (!wifi.IsTargetWifiConnected())
        {
            return Outcome(AuthorizationOutcomeKind.WrongWifi);
        }
        ReportProgress(request, AuthorizationProgressStage.TargetWifiConfirmed);

        if (!request.Force)
        {
            var initialProbe = await internet.ProbeAsync(
                options.InitialInternetProbeTimeout,
                cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return Cancel(request.Reason);
            }
            if (initialProbe.Online)
            {
                return new AuthorizationOutcome(
                    AuthorizationOutcomeKind.AlreadyOnline,
                    InternetConfirmed: true,
                    AuthorizedAtUtc: null,
                    RetryAfter: null,
                    Timing: null);
            }
        }

        using var authLease = NamedSemaphoreLease.TryAcquire("Local\\IS74Wifi.CSharp.Auth");
        if (authLease is null)
        {
            return Outcome(AuthorizationOutcomeKind.Busy);
        }

        if (request.Reason == AuthorizationAttemptReason.Manual)
        {
            state.ResetAutomaticCycleForManual();
        }

        var connectClock = Stopwatch.StartNew();
        var baseline = await api.GetBaselineAsync(
            request.BearerToken,
            request.DeviceId,
            options.BaselineTimeout,
            cancellationToken).ConfigureAwait(false);
        trace?.SetBaseline(connectClock.Elapsed.TotalMilliseconds);

        if (cancellationToken.IsCancellationRequested)
        {
            return Cancel(request.Reason);
        }

        if (!baseline.IsSuccess)
        {
            trace?.BaselineFailed(baseline.Failure!, connectClock.Elapsed.TotalMilliseconds);
            return HandleBaselineFailure(baseline.Failure!, request.Reason);
        }
        ReportProgress(request, AuthorizationProgressStage.BaselineLoaded);

        var budget = state.RegisterStepOneSend(request.Reason);
        trace?.SetStepOneAttempt(budget.Attempt);
        if (!budget.Allowed)
        {
            return Outcome(AuthorizationOutcomeKind.UserActionRequired);
        }

        var baselineId = baseline.Value!.Id;
        var preStepOneMilliseconds = ElapsedMilliseconds(connectClock);
        var criticalClock = Stopwatch.StartNew();
        using var stepOneCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        trace?.PortalStarted("step_one", criticalClock.Elapsed.TotalMilliseconds);
        var stepOneTask = ObserveStepOneAsync(
            portal.SendStepOneAsync(
                request.Phone,
                options.StepOneTimeout,
                stepOneCts.Token),
            trace,
            criticalClock);
        var pollTask = polling.WaitForFreshCodeAsync(
            request.BearerToken,
            request.DeviceId,
            baselineId,
            criticalClock,
            pollCts.Token,
            trace);
        ReportProgress(request, AuthorizationProgressStage.CaptiveRequestStarted);

        CaptivePortalResult<StepOneResponse>? stepOne = null;
        PushPollingResult? pollResult = null;

        while (pollResult is null)
        {
            if (stepOne is null)
            {
                var completed = await Task.WhenAny(stepOneTask, pollTask).ConfigureAwait(false);
                if (completed == stepOneTask)
                {
                    stepOne = await stepOneTask.ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        pollCts.Cancel();
                        return Cancel(request.Reason);
                    }

                    if (IsGuaranteedNoStepOneSideEffect(stepOne))
                    {
                        pollCts.Cancel();
                        var retryAfter = state.MarkReservedStepOneNotSent(request.Reason);
                        return new AuthorizationOutcome(
                            AuthorizationOutcomeKind.RetryableBeforeStepOne,
                            InternetConfirmed: null,
                            AuthorizedAtUtc: null,
                            RetryAfter: retryAfter,
                            Timing: null);
                    }

                    // From this point the request either reached the portal or its
                    // side effect is ambiguous. Previous pre-step failures no longer
                    // belong to this attempt's retry history.
                    state.ClearPreStepFailure();

                    if (stepOne.IsSuccess && stepOne.Value!.Disposition == StepOneDisposition.AlreadyAuthorized)
                    {
                        pollCts.Cancel();
                        state.MarkAlreadyAuthorized(request.Reason);
                        return Outcome(AuthorizationOutcomeKind.AlreadyAuthorized);
                    }

                    if (stepOne.Failure is { Kind: CaptivePortalFailureKind.HttpStatus } httpFailure &&
                        httpFailure.StatusCode is { } statusCode &&
                        statusCode != 408 && statusCode < 500)
                    {
                        // A terminal 4xx (especially 429) is an explicit server
                        // rejection, not an ambiguous lost response. Waiting through
                        // the full 10 s mailbox schedule only delays the final state.
                        pollCts.Cancel();
                        state.MarkUserActionRequired(
                            statusCode == 429 ? "step-one-rate-limited" : "step-one-rejected",
                            request.Reason);
                        return Outcome(AuthorizationOutcomeKind.UserActionRequired);
                    }

                    if (stepOne.Failure?.Kind == CaptivePortalFailureKind.UnexpectedRedirect)
                    {
                        pollCts.Cancel();
                        state.MarkUserActionRequired("unexpected-step-one-redirect", request.Reason);
                        return Outcome(AuthorizationOutcomeKind.UserActionRequired);
                    }

                    // A normal stepTwo redirect, HTTP error, or lost response does not
                    // stop the mailbox schedule. A fresh server-side code is stronger
                    // evidence that stepOne reached the backend.
                    continue;
                }
            }

            pollResult = await pollTask.ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            stepOneCts.Cancel();
            return Cancel(request.Reason);
        }

        if (pollResult.TerminalFailure?.Kind == Is74ApiFailureKind.Unauthorized)
        {
            stepOneCts.Cancel();
            state.MarkUserActionRequired("bearer-invalid", request.Reason);
            return Outcome(AuthorizationOutcomeKind.BearerInvalid);
        }

        if (!pollResult.CodeFound)
        {
            if (stepOne is null)
            {
                stepOne = await stepOneTask.ConfigureAwait(false);
            }

            if (IsGuaranteedNoStepOneSideEffect(stepOne))
            {
                var retryAfter = state.MarkReservedStepOneNotSent(request.Reason);
                return new AuthorizationOutcome(
                    AuthorizationOutcomeKind.RetryableBeforeStepOne,
                    InternetConfirmed: null,
                    AuthorizedAtUtc: null,
                    RetryAfter: retryAfter,
                    Timing: null);
            }

            state.ClearPreStepFailure();
            return HandleNoCode(
                stepOne,
                request.Reason,
                budget.Attempt,
                preStepOneMilliseconds,
                baselineId,
                pollResult.Observations);
        }

        // A fresh code is positive evidence that stepOne reached the backend even
        // when its browser-facing response is still pending or was lost.
        state.ClearPreStepFailure();

        var candidate = pollResult.Candidate!;
        ReportProgress(request, AuthorizationProgressStage.FreshCodeReceived);
        var codeObservation = pollResult.Observations
            .Where(observation => observation.MessageId == candidate.MessageId && observation.Failure is null)
            .OrderBy(observation => observation.ObservedMilliseconds)
            .FirstOrDefault();

        pollCts.Cancel();
        var stepTwoStartPreciseMilliseconds = criticalClock.Elapsed.TotalMilliseconds;
        var stepTwoStartMilliseconds = (int)Math.Max(0, Math.Round(stepTwoStartPreciseMilliseconds));
        var stepTwoStartedAt = DateTimeOffset.UtcNow;
        trace?.PortalStarted("step_two", stepTwoStartPreciseMilliseconds);
        Uri? observedStepTwoLocation = null;
        if (stepOne?.IsSuccess == true && stepOne.Value!.Disposition == StepOneDisposition.StepTwo)
        {
            observedStepTwoLocation = stepOne.Value.StepTwoUri;
        }

        ReportProgress(request, AuthorizationProgressStage.StepTwoStarted);
        var stepTwo = await portal.SendStepTwoAsync(
            request.Phone,
            candidate.Code,
            observedStepTwoLocation,
            options.StepTwoTimeout,
            cancellationToken).ConfigureAwait(false);
        var stepTwoDonePreciseMilliseconds = criticalClock.Elapsed.TotalMilliseconds;
        var stepTwoDoneMilliseconds = (int)Math.Max(0, Math.Round(stepTwoDonePreciseMilliseconds));
        trace?.PortalCompleted("step_two", stepTwoDonePreciseMilliseconds, stepTwo);
        stepOneCts.Cancel();

        var timing = new AuthorizationTiming(
            preStepOneMilliseconds,
            baselineId,
            budget.Attempt == 0 ? null : budget.Attempt,
            codeObservation is null
                ? null
                : codeObservation.Kind == PushPollKind.Fallback ? WifiCodeSource.Fallback : WifiCodeSource.Primary,
            codeObservation?.TargetMilliseconds,
            codeObservation?.StartMilliseconds,
            codeObservation?.ObservedMilliseconds,
            stepTwoStartMilliseconds,
            stepTwoDoneMilliseconds,
            pollResult.Observations);
        // Once stepTwo has been sent, cancellation must not erase the fact that
        // the captive side effect may already have happened. A lost/cancelled
        // response is handled as an ambiguous stepTwo and is never blindly resent.
        if (!stepTwo.IsSuccess)
        {
            if (stepTwo.Failure?.Kind == CaptivePortalFailureKind.Transport)
            {
                ReportProgress(request, AuthorizationProgressStage.InternetCheckStarted);
                var recovered = await ConfirmInternetOnScheduleAsync(
                    options.LostStepTwoProbeOffsetsMilliseconds,
                    options.RecoveryInternetProbeTimeout,
                    cancellationToken,
                    trace,
                    criticalClock,
                    "recovery").ConfigureAwait(false);
                if (recovered)
                {
                    var authorizedAt = state.MarkSuccess(serverDate: stepTwoStartedAt, internetConfirmed: true);
                    ReportProgress(request, AuthorizationProgressStage.InternetConfirmed);
                    LogTiming(timing);
                    return new AuthorizationOutcome(
                        AuthorizationOutcomeKind.Success,
                        InternetConfirmed: true,
                        AuthorizedAtUtc: authorizedAt,
                        RetryAfter: null,
                        Timing: timing);
                }

                state.MarkUserActionRequired("step-two-ambiguous", request.Reason);
                LogTiming(timing);
                return new AuthorizationOutcome(
                    AuthorizationOutcomeKind.StepTwoAmbiguous,
                    InternetConfirmed: false,
                    AuthorizedAtUtc: null,
                    RetryAfter: null,
                    Timing: timing);
            }

            state.MarkUserActionRequired("step-two-rejected", request.Reason);
            LogTiming(timing);
            return new AuthorizationOutcome(
                AuthorizationOutcomeKind.UserActionRequired,
                InternetConfirmed: false,
                AuthorizedAtUtc: null,
                RetryAfter: null,
                Timing: timing);
        }

        ReportProgress(request, AuthorizationProgressStage.StepTwoAccepted);
        var acceptedAt = state.MarkSuccess(stepTwo.Value!.ServerDate, internetConfirmed: false);
        ReportProgress(request, AuthorizationProgressStage.InternetCheckStarted);
        var internetConfirmed = await ConfirmInternetOnScheduleAsync(
            options.PostSuccessProbeOffsetsMilliseconds,
            options.PostSuccessInternetProbeTimeout,
            cancellationToken,
            trace,
            criticalClock,
            "post_step_two").ConfigureAwait(false);
        if (internetConfirmed)
        {
            state.MarkInternetConfirmed();
            ReportProgress(request, AuthorizationProgressStage.InternetConfirmed);
        }

        LogTiming(timing);
        return new AuthorizationOutcome(
            AuthorizationOutcomeKind.Success,
            internetConfirmed,
            acceptedAt,
            RetryAfter: null,
            Timing: timing);
    }

    private AuthorizationOutcome HandleBaselineFailure(Is74ApiFailure failure, AuthorizationAttemptReason reason)
    {
        if (failure.Kind == Is74ApiFailureKind.Unauthorized)
        {
            state.MarkUserActionRequired("bearer-invalid", reason);
            return Outcome(AuthorizationOutcomeKind.BearerInvalid);
        }

        if (failure.Kind == Is74ApiFailureKind.Transport &&
            failure.TransportFailure == TransportFailureKind.Cancelled)
        {
            return Cancel(reason);
        }

        var retryAfter = state.MarkPreStepFailure(reason);
        return new AuthorizationOutcome(
            AuthorizationOutcomeKind.RetryableBeforeStepOne,
            InternetConfirmed: null,
            AuthorizedAtUtc: null,
            RetryAfter: retryAfter,
            Timing: null);
    }

    private AuthorizationOutcome HandleNoCode(
        CaptivePortalResult<StepOneResponse> stepOne,
        AuthorizationAttemptReason reason,
        int stepOneAttempt,
        int preStepOneMilliseconds,
        long baselineId,
        IReadOnlyList<PushPollObservation> observations)
    {
        var timing = new AuthorizationTiming(
            preStepOneMilliseconds,
            baselineId,
            stepOneAttempt == 0 ? null : stepOneAttempt,
            CodeSource: null,
            CodeTargetMilliseconds: null,
            CodePollStartMilliseconds: null,
            CodeObservedMilliseconds: null,
            StepTwoStartMilliseconds: null,
            StepTwoDoneMilliseconds: null,
            Polls: observations);
        LogTiming(timing);

        if (stepOne.IsSuccess)
        {
            if (stepOne.Value!.Disposition == StepOneDisposition.AlreadyAuthorized)
            {
                state.MarkAlreadyAuthorized(reason);
                return Outcome(AuthorizationOutcomeKind.AlreadyAuthorized, timing);
            }

            return MarkStepOneRetryable(reason, timing);
        }

        var failure = stepOne.Failure!;
        if (failure.Kind == CaptivePortalFailureKind.Transport)
        {
            return MarkStepOneRetryable(reason, timing);
        }

        if (failure.Kind == CaptivePortalFailureKind.HttpStatus &&
            (failure.StatusCode == 408 || failure.StatusCode >= 500))
        {
            return MarkStepOneRetryable(reason, timing);
        }

        state.MarkUserActionRequired(
            failure.StatusCode == 429 ? "step-one-rate-limited" : "step-one-rejected",
            reason);
        return Outcome(AuthorizationOutcomeKind.UserActionRequired, timing);
    }

    private AuthorizationOutcome MarkStepOneRetryable(
        AuthorizationAttemptReason reason,
        AuthorizationTiming timing)
    {
        var retryAfter = state.MarkStepOneRetryable(reason);
        if (reason != AuthorizationAttemptReason.Manual && state.Load().UserActionRequired)
        {
            return Outcome(AuthorizationOutcomeKind.UserActionRequired, timing);
        }

        return new AuthorizationOutcome(
            AuthorizationOutcomeKind.RetryableStepOne,
            InternetConfirmed: null,
            AuthorizedAtUtc: null,
            RetryAfter: retryAfter,
            Timing: timing);
    }

    private async Task<bool> ConfirmInternetOnScheduleAsync(
        IReadOnlyList<int> offsetsMilliseconds,
        TimeSpan probeTimeout,
        CancellationToken cancellationToken,
        AuthorizationTelemetryTrace? trace,
        Stopwatch criticalClock,
        string phase)
    {
        if (offsetsMilliseconds.Count == 0)
        {
            return false;
        }

        var phaseStartMilliseconds = criticalClock.Elapsed.TotalMilliseconds;
        var clock = Stopwatch.StartNew();
        for (var index = 0; index < offsetsMilliseconds.Count; index++)
        {
            var targetMilliseconds = offsetsMilliseconds[index];
            var remaining = targetMilliseconds - clock.Elapsed.TotalMilliseconds;
            if (remaining > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            // Keep the offsets absolute even when a probe endpoint stalls. Without
            // this cap, eight nominal 0..3500 ms checks can serialize into >30 s
            // because every individual probe has its own multi-second timeout.
            var effectiveTimeout = probeTimeout;
            if (index + 1 < offsetsMilliseconds.Count)
            {
                var untilNext = offsetsMilliseconds[index + 1] - clock.Elapsed.TotalMilliseconds;
                if (untilNext > 0)
                {
                    var slot = TimeSpan.FromMilliseconds(Math.Max(100, untilNext));
                    if (slot < effectiveTimeout)
                    {
                        effectiveTimeout = slot;
                    }
                }
            }

            var plannedAbsoluteMilliseconds = phaseStartMilliseconds + targetMilliseconds;
            var actualStartMilliseconds = criticalClock.Elapsed.TotalMilliseconds;
            var telemetryProbe = trace?.InternetProbeStarted(
                phase,
                index + 1,
                plannedAbsoluteMilliseconds,
                actualStartMilliseconds) ?? 0;
            var probe = await internet.ProbeAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
            trace?.InternetProbeCompleted(
                telemetryProbe,
                criticalClock.Elapsed.TotalMilliseconds,
                probe);
            if (probe.Online)
            {
                return true;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        return false;
    }

    private static async Task<CaptivePortalResult<StepOneResponse>> ObserveStepOneAsync(
        Task<CaptivePortalResult<StepOneResponse>> task,
        AuthorizationTelemetryTrace? trace,
        Stopwatch criticalClock)
    {
        var result = await task.ConfigureAwait(false);
        trace?.PortalCompleted("step_one", criticalClock.Elapsed.TotalMilliseconds, result);
        return result;
    }

    private static bool IsGuaranteedNoStepOneSideEffect(CaptivePortalResult<StepOneResponse> stepOne) =>
        !stepOne.IsSuccess &&
        stepOne.Failure is { Kind: CaptivePortalFailureKind.Transport, SideEffectMayHaveOccurred: false };

    private void LogTiming(AuthorizationTiming timing)
    {
        var pollSummary = string.Join(
            ",",
            timing.Polls.Select(observation =>
                $"{observation.Kind}@{observation.TargetMilliseconds}:{observation.StartMilliseconds}>{observation.ObservedMilliseconds}:" +
                (observation.Failure?.Kind.ToString() ?? "ok")));

        logger.Write(
            DiagnosticLevel.Info,
            $"critical.timing preStepOneMs={timing.PreStepOneMilliseconds} baselineId={timing.BaselineId} " +
            $"stepOneAttempt={timing.StepOneAttempt?.ToString() ?? ""} source={timing.CodeSource?.ToString() ?? ""} " +
            $"targetMs={timing.CodeTargetMilliseconds?.ToString() ?? ""} pollStartMs={timing.CodePollStartMilliseconds?.ToString() ?? ""} " +
            $"codeObservedMs={timing.CodeObservedMilliseconds?.ToString() ?? ""} stepTwoStartMs={timing.StepTwoStartMilliseconds?.ToString() ?? ""} " +
            $"stepTwoDoneMs={timing.StepTwoDoneMilliseconds?.ToString() ?? ""} polls=[{pollSummary}]");
    }

    private static void ReportProgress(AuthorizationRequest request, AuthorizationProgressStage stage)
    {
        try
        {
            request.Progress?.Invoke(stage);
        }
        catch
        {
            // Progress reporting is observational and must never change authorization semantics.
        }
    }

    private AuthorizationOutcome Cancel(AuthorizationAttemptReason reason, AuthorizationTiming? timing = null)
    {
        state.MarkCancelled(reason);
        return Outcome(AuthorizationOutcomeKind.Cancelled, timing);
    }

    private static AuthorizationOutcome Outcome(
        AuthorizationOutcomeKind kind,
        AuthorizationTiming? timing = null) =>
        new(kind, InternetConfirmed: null, AuthorizedAtUtc: null, RetryAfter: null, Timing: timing);

    private static int ElapsedMilliseconds(Stopwatch clock) =>
        (int)Math.Max(0, Math.Round(clock.Elapsed.TotalMilliseconds));

    private static void ValidateRequest(AuthorizationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BearerToken))
        {
            throw new ArgumentException("Bearer token is required.", nameof(request));
        }
        if (request.Phone.Length != 10 || request.Phone.Any(c => c is < '0' or > '9'))
        {
            throw new ArgumentException("Phone must contain exactly 10 digits.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            throw new ArgumentException("Device ID is required.", nameof(request));
        }
    }
}
