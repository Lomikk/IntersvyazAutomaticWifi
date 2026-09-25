using System.Diagnostics;

namespace IS74Wifi.Core;

public sealed class AuthorizationTelemetryRecorder(
    string installId,
    TelemetryQueue queue,
    string appVersion)
{
    public AuthorizationTelemetryTrace Begin(AuthorizationAttemptReason reason) =>
        new(installId, queue, appVersion, reason);
}

public sealed class AuthorizationTelemetryTrace
{
    private readonly object gate = new();
    private readonly string installId;
    private readonly TelemetryQueue queue;
    private readonly string appVersion;
    private readonly AuthorizationAttemptReason reason;
    private readonly Stopwatch attemptClock = Stopwatch.StartNew();
    private readonly Dictionary<int, PollDraft> polls = [];
    private readonly List<ProbeDraft> probes = [];
    private readonly List<ErrorDraft> errors = [];
    private PortalDraft? stepOne;
    private PortalDraft? stepTwo;
    private int nextPollIndex;
    private int nextProbeSequence;
    private int mailboxInFlight;
    private int maxMailboxInFlight;
    private int responseOrder;
    private bool sealedTrace;
    private double? baselineMs;
    private double? codeObservedMs;
    private int? pollThatFoundCode;
    private int? stepOneAttempt;

    internal AuthorizationTelemetryTrace(
        string installId,
        TelemetryQueue queue,
        string appVersion,
        AuthorizationAttemptReason reason)
    {
        this.installId = installId;
        this.queue = queue;
        this.appVersion = appVersion;
        this.reason = reason;
        AttemptId = "attempt-" + Guid.NewGuid().ToString("N");
    }

    public string AttemptId { get; }

    public void SetBaseline(double elapsedMilliseconds)
    {
        lock (gate)
        {
            if (!sealedTrace)
            {
                baselineMs = Round(elapsedMilliseconds);
            }
        }
    }

    public void SetStepOneAttempt(int attempt)
    {
        lock (gate)
        {
            if (!sealedTrace)
            {
                stepOneAttempt = attempt <= 0 ? null : attempt;
            }
        }
    }

    public int MailboxPollStarted(
        int targetMilliseconds,
        double actualStartMilliseconds,
        int pageSize)
    {
        lock (gate)
        {
            if (sealedTrace)
            {
                return 0;
            }

            var index = ++nextPollIndex;
            var inFlightAtStart = mailboxInFlight;
            mailboxInFlight++;
            maxMailboxInFlight = Math.Max(maxMailboxInFlight, mailboxInFlight);
            polls[index] = new PollDraft(
                index,
                targetMilliseconds,
                Round(actualStartMilliseconds),
                pageSize,
                inFlightAtStart);
            return index;
        }
    }

    public void MailboxPollCompleted(
        int pollIndex,
        double completedMilliseconds,
        Is74ApiResult<PushMessagePage> result,
        long baselineId,
        bool wifiCodeFound,
        long? wifiMessageId)
    {
        if (pollIndex <= 0)
        {
            return;
        }

        lock (gate)
        {
            if (sealedTrace || !polls.TryGetValue(pollIndex, out var poll) || poll.CompletedMs is not null)
            {
                return;
            }

            mailboxInFlight = Math.Max(0, mailboxInFlight - 1);
            poll.CompletedMs = Round(completedMilliseconds);
            poll.DurationMs = Round(poll.CompletedMs.Value - poll.ActualStartMs);
            poll.ResponseOrder = ++responseOrder;
            poll.WifiCodeFound = wifiCodeFound;

            if (result.IsSuccess)
            {
                var page = result.Value!;
                poll.HttpStatus = page.HttpStatus;
                poll.ReturnedCount = page.Messages.Count;
                poll.FreshMessageCount = page.Messages.Count(message => message.Id > baselineId);
                poll.NewestIdDelta = page.Messages.FirstOrDefault() is { } top
                    ? top.Id - baselineId
                    : null;
                poll.WifiMessageIdDelta = wifiMessageId is { } id ? id - baselineId : null;
                poll.CacheStatus = NormalizeToken(page.CacheStatus, 32);
            }
            else
            {
                var failure = result.Failure!;
                poll.HttpStatus = failure.StatusCode;
                poll.Cancelled = failure.Kind == Is74ApiFailureKind.Transport &&
                                 failure.TransportFailure == TransportFailureKind.Cancelled;
                poll.CancelReason = poll.Cancelled == true
                    ? (codeObservedMs is not null ? "code_found" : "caller_cancelled")
                    : null;
                var expectedCancellation = poll.Cancelled == true && codeObservedMs is not null;
                if (!expectedCancellation)
                {
                    AddErrorUnsafe(
                        "mailbox",
                        MapApiFailure(failure),
                        failure.StatusCode,
                        poll.DurationMs,
                        null,
                        failure.TransportFailure?.ToString(),
                        null);
                }
            }

            if (wifiCodeFound)
            {
                codeObservedMs ??= poll.CompletedMs;
                pollThatFoundCode ??= pollIndex;
            }
        }
    }

    public void PortalStarted(string stage, double startedMilliseconds)
    {
        lock (gate)
        {
            if (sealedTrace)
            {
                return;
            }

            var draft = new PortalDraft(stage, 1, Round(startedMilliseconds));
            if (stage == "step_one")
            {
                stepOne = draft;
            }
            else if (stage == "step_two")
            {
                stepTwo = draft;
            }
        }
    }

    public void PortalCompleted(
        string stage,
        double completedMilliseconds,
        CaptivePortalResult<StepOneResponse> result)
    {
        lock (gate)
        {
            if (sealedTrace)
            {
                return;
            }

            var portal = stage == "step_one" ? stepOne : null;
            if (portal is null)
            {
                return;
            }

            CompletePortalUnsafe(portal, completedMilliseconds, result.Value, result.Failure);
        }
    }

    public void PortalCompleted(
        string stage,
        double completedMilliseconds,
        CaptivePortalResult<StepTwoResponse> result)
    {
        lock (gate)
        {
            if (sealedTrace)
            {
                return;
            }

            var portal = stage == "step_two" ? stepTwo : null;
            if (portal is null)
            {
                return;
            }

            CompletePortalUnsafe(portal, completedMilliseconds, result.Value, result.Failure);
        }
    }

    public int InternetProbeStarted(
        string phase,
        int probeIndex,
        double plannedStartMilliseconds,
        double actualStartMilliseconds)
    {
        lock (gate)
        {
            if (sealedTrace)
            {
                return 0;
            }

            var sequence = ++nextProbeSequence;
            probes.Add(new ProbeDraft(
                sequence,
                phase,
                probeIndex,
                Round(plannedStartMilliseconds),
                Round(actualStartMilliseconds)));
            return sequence;
        }
    }

    public void InternetProbeCompleted(
        int sequence,
        double completedMilliseconds,
        InternetProbeResult result)
    {
        if (sequence <= 0)
        {
            return;
        }

        lock (gate)
        {
            if (sealedTrace)
            {
                return;
            }

            var probe = probes.FirstOrDefault(item => item.Sequence == sequence);
            if (probe is null || probe.CompletedMs is not null)
            {
                return;
            }

            probe.CompletedMs = Round(completedMilliseconds);
            probe.DurationMs = Round(probe.CompletedMs.Value - probe.ActualStartMs);
            probe.HttpStatus = result.StatusCode is { } status ? (int)status : null;
            probe.LocationKind = ClassifyLocation(result.Location);
            probe.Online = result.Online;
            probe.BodyKind = result.HttpResponseReceived && result.StatusCode == System.Net.HttpStatusCode.OK
                ? "other"
                : "none";

            if (!result.HttpResponseReceived && result.FailureKind != TransportFailureKind.None)
            {
                AddErrorUnsafe(
                    "internet_probe",
                    MapTransportFailure(result.FailureKind),
                    null,
                    probe.DurationMs,
                    null,
                    result.FailureKind.ToString(),
                    probe.LocationKind);
            }
        }
    }

    public void BaselineFailed(Is74ApiFailure failure, double elapsedMilliseconds)
    {
        lock (gate)
        {
            if (!sealedTrace)
            {
                baselineMs = Round(elapsedMilliseconds);
                AddErrorUnsafe(
                    "baseline",
                    MapApiFailure(failure),
                    failure.StatusCode,
                    baselineMs,
                    null,
                    failure.TransportFailure?.ToString(),
                    null);
            }
        }
    }

    public void UnexpectedException(Exception exception)
    {
        lock (gate)
        {
            if (!sealedTrace)
            {
                AddErrorUnsafe(
                    "internal",
                    "internal",
                    null,
                    null,
                    NormalizeToken(exception.GetType().Name, 64),
                    null,
                    null);
            }
        }
    }

    public void Complete(AuthorizationOutcome outcome)
    {
        List<string> serialized;
        lock (gate)
        {
            if (sealedTrace)
            {
                return;
            }
            sealedTrace = true;

            foreach (var poll in polls.Values.Where(poll => poll.CompletedMs is null))
            {
                poll.Cancelled = true;
                poll.CancelReason = codeObservedMs is not null ? "code_found" : "attempt_finished";
            }

            if (outcome.Kind is not (AuthorizationOutcomeKind.Success or
                                     AuthorizationOutcomeKind.AlreadyAuthorized or
                                     AuthorizationOutcomeKind.AlreadyOnline or
                                     AuthorizationOutcomeKind.Cancelled) &&
                errors.Count == 0)
            {
                AddErrorUnsafe(
                    "internal",
                    OutcomeErrorClass(outcome.Kind),
                    null,
                    null,
                    null,
                    null,
                    null);
            }

            serialized = BuildEventsUnsafe(outcome);
        }

        queue.Enqueue(serialized);
    }

    private List<string> BuildEventsUnsafe(AuthorizationOutcome outcome)
    {
        var result = outcome.Kind switch
        {
            AuthorizationOutcomeKind.Success or AuthorizationOutcomeKind.AlreadyAuthorized or AuthorizationOutcomeKind.AlreadyOnline => "success",
            AuthorizationOutcomeKind.Cancelled => "cancelled",
            _ => "error"
        };

        var firstPoll = polls.Values
            .Where(poll => poll.ActualStartMs >= 0)
            .OrderBy(poll => poll.ActualStartMs)
            .FirstOrDefault();
        var internetConfirmedMs = probes
            .Where(probe => probe.Online == true && probe.CompletedMs is not null)
            .Select(probe => probe.CompletedMs)
            .Min();
        var fastPathUsed = stepTwo?.StartedMs is { } stepTwoStarted &&
                           (stepOne?.CompletedMs is null || stepTwoStarted < stepOne.CompletedMs);
        var codeBeforeStepOne = codeObservedMs is { } codeMs &&
                                (stepOne?.CompletedMs is null || codeMs < stepOne.CompletedMs);

        var attempt = new TelemetryAttemptEvent
        {
            AttemptId = AttemptId,
            InstallId = installId,
            AppVersion = appVersion,
            EventId = NewEventId(),
            Result = result,
            Trigger = reason switch
            {
                AuthorizationAttemptReason.Manual => "manual",
                AuthorizationAttemptReason.Automatic => "auto",
                AuthorizationAttemptReason.Retry => "retry",
                _ => "unknown"
            },
            BaselineMs = baselineMs,
            StepOneResponseMs = stepOne?.CompletedMs,
            StepOneStatus = stepOne?.HttpStatus,
            FirstPollStartedMs = firstPoll?.ActualStartMs,
            CodeObservedMs = codeObservedMs,
            PollThatFoundCode = pollThatFoundCode,
            MailboxRequests = polls.Count,
            StepTwoStartedMs = stepTwo?.StartedMs,
            StepTwoResponseMs = stepTwo?.CompletedMs,
            StepTwoStatus = stepTwo?.HttpStatus,
            InternetConfirmedMs = internetConfirmedMs,
            InternetProbeRequests = probes.Count,
            CodeBeforeStepOneResponse = codeObservedMs is null ? null : codeBeforeStepOne,
            StepOneResponseMinusCodeMs = stepOne?.CompletedMs is { } oneDone && codeObservedMs is { } observed
                ? RoundSigned(oneDone - observed)
                : null,
            StepOneAttempts = stepOneAttempt,
            StepTwoAttempts = stepTwo is null ? 0 : 1,
            TotalMs = Round(attemptClock.Elapsed.TotalMilliseconds),
            StepTwoBeforeStepOneResponse = stepTwo is null ? null : fastPathUsed,
            CodeToStepTwoMs = stepTwo?.StartedMs is { } twoStart && codeObservedMs is { } code
                ? Round(twoStart - code)
                : null,
            FastPathUsed = stepTwo is null ? null : fastPathUsed,
            FastPathSuccess = stepTwo is null ? null : fastPathUsed &&
                outcome.Kind == AuthorizationOutcomeKind.Success &&
                outcome.InternetConfirmed == true,
            MaxMailboxInFlight = maxMailboxInFlight,
            OverlappingMailboxObserved = polls.Count == 0 ? null : maxMailboxInFlight > 1,
            StepOneLocationKind = stepOne?.LocationKind,
            StepTwoLocationKind = stepTwo?.LocationKind,
            InternetConfirmedAfterStepTwoMs = internetConfirmedMs is { } internetMs && stepTwo?.StartedMs is { } start
                ? Round(internetMs - start)
                : null
        };

        var events = new List<string>
        {
            TelemetrySerialization.Serialize(attempt)
        };

        foreach (var poll in polls.Values.OrderBy(poll => poll.Index))
        {
            events.Add(TelemetrySerialization.Serialize(new TelemetryMailboxPollEvent
            {
                AttemptId = AttemptId,
                InstallId = installId,
                AppVersion = appVersion,
                EventId = NewEventId(),
                PollIndex = poll.Index,
                PlannedStartMs = poll.PlannedStartMs,
                ActualStartMs = poll.ActualStartMs,
                CompletedMs = poll.CompletedMs,
                DurationMs = poll.DurationMs,
                ResponseOrder = poll.ResponseOrder,
                HttpStatus = poll.HttpStatus,
                PageSize = poll.PageSize,
                ReturnedCount = poll.ReturnedCount,
                FreshMessageCount = poll.FreshMessageCount,
                WifiCodeFound = poll.WifiCodeFound,
                NewestIdDelta = poll.NewestIdDelta,
                WifiMessageIdDelta = poll.WifiMessageIdDelta,
                InFlightAtStart = poll.InFlightAtStart,
                CacheStatus = poll.CacheStatus,
                Cancelled = poll.Cancelled,
                CancelReason = poll.CancelReason
            }));
        }

        foreach (var probe in probes.OrderBy(probe => probe.Sequence))
        {
            events.Add(TelemetrySerialization.Serialize(new TelemetryInternetProbeEvent
            {
                AttemptId = AttemptId,
                InstallId = installId,
                AppVersion = appVersion,
                EventId = NewEventId(),
                Phase = probe.Phase,
                ProbeIndex = probe.ProbeIndex,
                PlannedStartMs = probe.PlannedStartMs,
                ActualStartMs = probe.ActualStartMs,
                CompletedMs = probe.CompletedMs,
                DurationMs = probe.DurationMs,
                HttpStatus = probe.HttpStatus,
                LocationKind = probe.LocationKind,
                Online = probe.Online,
                BodyKind = probe.BodyKind,
                InFlightAtStart = 0
            }));
        }

        foreach (var portal in new[] { stepOne, stepTwo }.OfType<PortalDraft>())
        {
            events.Add(TelemetrySerialization.Serialize(new TelemetryPortalResponseEvent
            {
                AttemptId = AttemptId,
                InstallId = installId,
                AppVersion = appVersion,
                EventId = NewEventId(),
                Stage = portal.Stage,
                RequestIndex = portal.RequestIndex,
                StartedMs = portal.StartedMs,
                CompletedMs = portal.CompletedMs,
                DurationMs = portal.DurationMs,
                HttpStatus = portal.HttpStatus,
                LocationKind = portal.LocationKind,
                Server = portal.Server,
                ContentType = portal.ContentType,
                ContentLength = portal.ContentLength,
                RetryAfterMs = portal.RetryAfterMs,
                BodyKind = portal.BodyKind,
                BodySha256 = portal.BodySha256
            }));
        }

        foreach (var error in errors)
        {
            events.Add(TelemetrySerialization.Serialize(new TelemetryErrorEvent
            {
                AttemptId = AttemptId,
                InstallId = installId,
                AppVersion = appVersion,
                EventId = NewEventId(),
                Stage = error.Stage,
                ErrorClass = error.ErrorClass,
                HttpStatus = error.HttpStatus,
                DurationMs = error.DurationMs,
                ExceptionType = error.ExceptionType,
                NativeError = error.NativeError,
                LocationKind = error.LocationKind
            }));
        }

        return events;
    }

    private void CompletePortalUnsafe<T>(
        PortalDraft portal,
        double completedMilliseconds,
        T? value,
        CaptivePortalFailure? failure)
    {
        portal.CompletedMs = Round(completedMilliseconds);
        portal.DurationMs = Round(portal.CompletedMs.Value - portal.StartedMs);

        switch (value)
        {
            case StepOneResponse one:
                portal.HttpStatus = one.StatusCode;
                portal.LocationKind = ClassifyLocation(one.Location);
                portal.Server = NormalizeHeader(one.Server, 64);
                portal.ContentType = NormalizeHeader(one.ContentType, 96);
                portal.ContentLength = one.ContentLength;
                portal.RetryAfterMs = one.RetryAfter?.TotalMilliseconds;
                break;
            case StepTwoResponse two:
                portal.HttpStatus = two.StatusCode;
                portal.LocationKind = ClassifyLocation(two.Location);
                portal.Server = NormalizeHeader(two.Server, 64);
                portal.ContentType = NormalizeHeader(two.ContentType, 96);
                portal.ContentLength = two.ContentLength;
                portal.RetryAfterMs = two.RetryAfter?.TotalMilliseconds;
                break;
        }

        if (failure is not null)
        {
            portal.HttpStatus = failure.StatusCode;
            portal.LocationKind = ClassifyLocation(failure.Location);
            portal.Server = NormalizeHeader(failure.Server, 64);
            portal.ContentType = NormalizeHeader(failure.ContentType, 96);
            portal.ContentLength = failure.ContentLength;
            portal.RetryAfterMs = failure.RetryAfter?.TotalMilliseconds;
            portal.BodyKind = failure.BodyKind;
            portal.BodySha256 = failure.BodySha256;
            var expectedFastPathCancellation =
                portal.Stage == "step_one" &&
                failure.Kind == CaptivePortalFailureKind.Transport &&
                failure.TransportFailure == TransportFailureKind.Cancelled &&
                codeObservedMs is not null;

            if (!expectedFastPathCancellation)
            {
                AddErrorUnsafe(
                    portal.Stage,
                    MapPortalFailure(failure),
                    failure.StatusCode,
                    portal.DurationMs,
                    null,
                    failure.TransportFailure?.ToString(),
                    portal.LocationKind);
            }
        }
    }

    private void AddErrorUnsafe(
        string stage,
        string errorClass,
        int? status,
        double? duration,
        string? exceptionType,
        string? nativeError,
        string? locationKind)
    {
        errors.Add(new ErrorDraft(
            stage,
            errorClass,
            status,
            duration,
            NormalizeToken(exceptionType, 64),
            NormalizeToken(nativeError, 96),
            locationKind));
    }

    private static string MapApiFailure(Is74ApiFailure failure) => failure.Kind switch
    {
        Is74ApiFailureKind.Unauthorized => "bearer_invalid",
        Is74ApiFailureKind.HttpStatus when failure.StatusCode >= 500 => "http_5xx",
        Is74ApiFailureKind.HttpStatus => "http_4xx",
        Is74ApiFailureKind.InvalidJson or Is74ApiFailureKind.InvalidPayload => "parse_error",
        Is74ApiFailureKind.Transport => MapTransportFailure(failure.TransportFailure ?? TransportFailureKind.Unexpected),
        _ => "internal"
    };

    private static string MapPortalFailure(CaptivePortalFailure failure) => failure.Kind switch
    {
        CaptivePortalFailureKind.UnexpectedRedirect => "unexpected_redirect",
        CaptivePortalFailureKind.HttpStatus when failure.StatusCode >= 500 => "http_5xx",
        CaptivePortalFailureKind.HttpStatus => "http_4xx",
        CaptivePortalFailureKind.Transport => MapTransportFailure(failure.TransportFailure ?? TransportFailureKind.Unexpected),
        _ => "internal"
    };

    private static string MapTransportFailure(TransportFailureKind failure) => failure switch
    {
        TransportFailureKind.DnsUnavailable => "dns",
        TransportFailureKind.DirectRouteUnavailable => "direct_route",
        TransportFailureKind.ConnectionFailure => "tcp_connect",
        TransportFailureKind.TlsFailure => "tls",
        TransportFailureKind.ResponseTooLarge => "parse_error",
        TransportFailureKind.Timeout => "timeout",
        TransportFailureKind.Cancelled => "cancelled",
        _ => "internal"
    };

    private static string OutcomeErrorClass(AuthorizationOutcomeKind kind) => kind switch
    {
        AuthorizationOutcomeKind.BearerInvalid => "bearer_invalid",
        AuthorizationOutcomeKind.RetryableStepOne => "code_not_received",
        AuthorizationOutcomeKind.StepTwoAmbiguous => "timeout",
        _ => "internal"
    };

    public static string ClassifyLocation(Uri? location)
    {
        if (location is null)
        {
            return "none";
        }
        if (PortalRedirectClassifier.TryResolveStepTwo(location, out _))
        {
            return "stepTwo";
        }
        if (PortalRedirectClassifier.IsStepThree(location))
        {
            return "stepThree";
        }
        if (PortalRedirectClassifier.IsAlreadyAuthorized(location))
        {
            return "landing";
        }
        if (location.IsAbsoluteUri &&
            string.Equals(location.Host, "online.susu.ru", StringComparison.OrdinalIgnoreCase) &&
            location.Scheme == Uri.UriSchemeHttps)
        {
            return "susu_https";
        }
        if (location.IsAbsoluteUri &&
            string.Equals(location.Host, "w.is74.ru", StringComparison.OrdinalIgnoreCase))
        {
            return "w_is74";
        }
        return "other_host";
    }

    private static string? NormalizeHeader(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var safe = new string(value.Where(c => c >= 32 && c <= 126).ToArray()).Trim();
        return safe.Length == 0 ? null : safe[..Math.Min(safe.Length, maxLength)];
    }

    private static string? NormalizeToken(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var safe = new string(value.Where(c => char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '+' or '-').ToArray());
        return safe.Length == 0 ? null : safe[..Math.Min(safe.Length, maxLength)];
    }

    private static string NewEventId() => "event-" + Guid.NewGuid().ToString("N");
    private static double Round(double value) => Math.Round(Math.Max(0, value), 3, MidpointRounding.AwayFromZero);
    private static double RoundSigned(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private sealed class PollDraft(
        int index,
        double plannedStartMs,
        double actualStartMs,
        int pageSize,
        int inFlightAtStart)
    {
        public int Index { get; } = index;
        public double PlannedStartMs { get; } = plannedStartMs;
        public double ActualStartMs { get; } = actualStartMs;
        public int PageSize { get; } = pageSize;
        public int InFlightAtStart { get; } = inFlightAtStart;
        public double? CompletedMs { get; set; }
        public double? DurationMs { get; set; }
        public int? ResponseOrder { get; set; }
        public int? HttpStatus { get; set; }
        public int? ReturnedCount { get; set; }
        public int? FreshMessageCount { get; set; }
        public bool? WifiCodeFound { get; set; }
        public long? NewestIdDelta { get; set; }
        public long? WifiMessageIdDelta { get; set; }
        public string? CacheStatus { get; set; }
        public bool? Cancelled { get; set; }
        public string? CancelReason { get; set; }
    }

    private sealed class ProbeDraft(
        int sequence,
        string phase,
        int probeIndex,
        double plannedStartMs,
        double actualStartMs)
    {
        public int Sequence { get; } = sequence;
        public string Phase { get; } = phase;
        public int ProbeIndex { get; } = probeIndex;
        public double PlannedStartMs { get; } = plannedStartMs;
        public double ActualStartMs { get; } = actualStartMs;
        public double? CompletedMs { get; set; }
        public double? DurationMs { get; set; }
        public int? HttpStatus { get; set; }
        public string? LocationKind { get; set; }
        public bool? Online { get; set; }
        public string? BodyKind { get; set; }
    }

    private sealed class PortalDraft(string stage, int requestIndex, double startedMs)
    {
        public string Stage { get; } = stage;
        public int RequestIndex { get; } = requestIndex;
        public double StartedMs { get; } = startedMs;
        public double? CompletedMs { get; set; }
        public double? DurationMs { get; set; }
        public int? HttpStatus { get; set; }
        public string? LocationKind { get; set; }
        public string? Server { get; set; }
        public string? ContentType { get; set; }
        public long? ContentLength { get; set; }
        public double? RetryAfterMs { get; set; }
        public string? BodyKind { get; set; }
        public string? BodySha256 { get; set; }
    }

    private sealed record ErrorDraft(
        string Stage,
        string ErrorClass,
        int? HttpStatus,
        double? DurationMs,
        string? ExceptionType,
        string? NativeError,
        string? LocationKind);
}
