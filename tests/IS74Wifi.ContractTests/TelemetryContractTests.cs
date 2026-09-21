using System.Net;
using System.Text.Json;
using IS74Wifi.Core;

internal static class TelemetryContractTests
{
    public static Task RunAsync()
    {
        using var temp = TestDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var queue = new TelemetryQueue(paths);
        var identity = new TelemetryIdentityStore(paths);
        var recorder = new AuthorizationTelemetryRecorder(identity.GetOrCreate(), queue, "0.0.0-test");

        var firstInstallId = identity.GetOrCreate();
        var secondInstallId = identity.GetOrCreate();
        Assert(firstInstallId == secondInstallId, "telemetry install ID is not stable");
        Assert(firstInstallId.Length == 32 && firstInstallId.All(Uri.IsHexDigit), "telemetry install ID format changed");

        var trace = recorder.Begin(AuthorizationAttemptReason.Automatic);
        trace.SetBaseline(42.125);
        trace.SetStepOneAttempt(1);
        trace.PortalStarted("step_one", 0.0);

        // Start two mailbox requests before either completes. This is the
        // latency-critical behavior we want production telemetry to prove.
        var poll1 = trace.MailboxPollStarted(100, 100.2, 1);
        var poll2 = trace.MailboxPollStarted(150, 150.1, 1);

        var page = Is74ApiResult<PushMessagePage>.Success(new PushMessagePage(
            [new Is74PushMessage(
                101,
                "Ваш код авторизации",
                "4321 код авторизации в приложении \"Интерсвязь\"",
                "secret-full-message")],
            KnownEmpty: false,
            HttpStatus: 200,
            Elapsed: TimeSpan.FromMilliseconds(78.2),
            CacheStatus: "BYPASS"));

        trace.MailboxPollCompleted(poll1, 178.4, page, baselineId: 100, wifiCodeFound: true, wifiMessageId: 101);
        trace.PortalStarted("step_two", 179.0);
        trace.MailboxPollCompleted(poll2, 211.7, page, baselineId: 100, wifiCodeFound: true, wifiMessageId: 101);

        trace.PortalCompleted(
            "step_one",
            207.4,
            CaptivePortalResult<StepOneResponse>.Success(new StepOneResponse(
                StepOneDisposition.StepTwo,
                new Uri("http://w.is74.ru/stepTwo?phone=9123456789&isMp=true"),
                new Uri("http://w.is74.ru/stepTwo?phone=9123456789&isMp=true"),
                null,
                TimeSpan.FromMilliseconds(207.4),
                StatusCode: 302,
                Server: "nginx",
                ContentType: "text/html")));

        trace.PortalCompleted(
            "step_two",
            629.0,
            CaptivePortalResult<StepTwoResponse>.Success(new StepTwoResponse(
                new Uri("http://w.is74.ru/stepThree"),
                DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(450),
                StatusCode: 302,
                Server: "nginx",
                ContentType: "text/html")));

        var probe = trace.InternetProbeStarted("post_step_two", 1, 650.0, 650.2);
        trace.InternetProbeCompleted(probe, 662.1, new InternetProbeResult(
            Online: true,
            HttpResponseReceived: true,
            StatusCode: HttpStatusCode.Found,
            Body: null,
            FailureKind: TransportFailureKind.None,
            Elapsed: TimeSpan.FromMilliseconds(11.9),
            Location: new Uri("https://online.susu.ru/")));

        trace.Complete(new AuthorizationOutcome(
            AuthorizationOutcomeKind.Success,
            InternetConfirmed: true,
            AuthorizedAtUtc: DateTimeOffset.UtcNow,
            RetryAfter: null,
            Timing: null));

        var batch = queue.ReadOldestBatch();
        Assert(batch is not null, "completed authorization trace was not queued");
        Assert(batch!.EventJson.Count >= 6, "authorization trace lost detailed events");
        Assert(batch.BatchId.StartsWith("batch-", StringComparison.Ordinal), "telemetry batch id format changed");

        var combined = string.Join("\n", batch.EventJson);
        foreach (var secret in new[]
                 {
                     "4321",
                     "secret-full-message",
                     "9123456789",
                     "Bearer ",
                     "confirmCode",
                     "push_message"
                 })
        {
            Assert(!combined.Contains(secret, StringComparison.OrdinalIgnoreCase), $"telemetry leaked sensitive value: {secret}");
        }

        using var attemptDoc = JsonDocument.Parse(batch.EventJson.Single(line => line.Contains("\"event_type\":\"attempt\"", StringComparison.Ordinal)));
        var attempt = attemptDoc.RootElement;
        Assert(attempt.GetProperty("max_mailbox_in_flight").GetInt32() == 2, "overlapping mailbox concurrency was not summarized");
        Assert(attempt.GetProperty("overlapping_mailbox_observed").GetBoolean(), "overlapping mailbox flag missing");
        Assert(attempt.GetProperty("code_before_step_one_response").GetBoolean(), "code-before-stepOne race was not captured");
        Assert(attempt.GetProperty("step_two_before_step_one_response").GetBoolean(), "early stepTwo race was not captured");
        Assert(attempt.GetProperty("fast_path_used").GetBoolean(), "fast path was not marked used");
        Assert(attempt.GetProperty("fast_path_success").GetBoolean(), "successful fast path was not marked successful");

        var pollEvents = batch.EventJson
            .Where(line => line.Contains("\"event_type\":\"mailbox_poll\"", StringComparison.Ordinal))
            .Select(JsonDocument.Parse)
            .ToList();
        try
        {
            Assert(pollEvents.Count == 2, "mailbox poll event count changed");
            var secondPoll = pollEvents
                .Select(document => document.RootElement)
                .Single(element => element.GetProperty("poll_index").GetInt32() == 2);
            Assert(secondPoll.GetProperty("in_flight_at_start").GetInt32() == 1,
                "second poll did not record that an earlier request was still in flight");
            Assert(Math.Abs(secondPoll.GetProperty("actual_start_ms").GetDouble() - 150.1) < 0.01,
                "fractional millisecond timing was lost");
        }
        finally
        {
            foreach (var document in pollEvents)
            {
                document.Dispose();
            }
        }

        var repeat = queue.ReadOldestBatch();
        Assert(repeat?.BatchId == batch.BatchId, "batch id is not deterministic across retry reads");
        queue.Complete(batch);
        Assert(!queue.HasPending, "successful telemetry batch was not removed from local queue");

        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
