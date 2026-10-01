using System.Net;
using System.Text.Json;
using IS74Wifi.Core;

internal static class TelemetryContractTests
{
    public static async Task RunAsync()
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
            .Select(static line => JsonDocument.Parse(line))
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

        TestRegistrationTelemetry();
        TestUploadTimeoutCompatibility();
        await TestUploadConsentGateAsync();
        await TestMixedQueueRetryAsync();
    }

    private static void TestUploadTimeoutCompatibility()
    {
        Assert(TelemetryUploader.GetHttpTimeout(new AppSettings()) == TimeSpan.FromSeconds(10),
            "new installations must allow slow Apps Script responses");
        Assert(TelemetryUploader.GetHttpTimeout(new AppSettings
            { TelemetryHttpTimeoutMilliseconds = 1000 }) == TimeSpan.FromSeconds(10),
            "old persisted 1-second timeout must not strand telemetry");
        Assert(TelemetryUploader.GetHttpTimeout(new AppSettings
            { TelemetryHttpTimeoutMilliseconds = 3000 }) == TimeSpan.FromSeconds(10),
            "old persisted 3-second timeout must not strand telemetry");
        Assert(TelemetryUploader.GetHttpTimeout(new AppSettings
            { TelemetryHttpTimeoutMilliseconds = 60000 }) == TimeSpan.FromSeconds(30),
            "a misconfigured timeout must not stall the agent indefinitely");
    }

    private static void TestRegistrationTelemetry()
    {
        using var temp = TestDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var queue = new TelemetryQueue(paths);
        var installId = new TelemetryIdentityStore(paths).GetOrCreate();
        var recorder = new RegistrationTelemetryRecorder(installId, queue, "0.0.0-test", () => true);
        var trace = recorder.Begin();

        var successCall = HttpCallResult.Success(new HttpResponseData(
            HttpStatusCode.OK,
            "{}",
            null,
            null,
            TimeSpan.FromMilliseconds(241.7),
            Server: "nginx"));
        trace.Record(
            "get_confirm",
            1,
            Is74ApiResult<ConfirmationRequested>.Success(new ConfirmationRequested(), successCall));

        var timeoutCall = HttpCallResult.Failure(
            TransportFailureKind.Timeout,
            null,
            TimeSpan.FromSeconds(15));
        trace.Record(
            "get_token",
            1,
            Is74ApiResult<Is74ApiSession>.Fail(
                new Is74ApiFailure(
                    Is74ApiFailureKind.Transport,
                    "auth.get-token",
                    TransportFailureKind.Timeout),
                timeoutCall));
        trace.Complete();

        var batch = queue.ReadOldestBatch();
        Assert(batch is not null && batch.EventJson.Count == 2, "registration telemetry batch was not queued");
        using var first = JsonDocument.Parse(batch!.EventJson[0]);
        using var second = JsonDocument.Parse(batch.EventJson[1]);
        Assert(first.RootElement.GetProperty("schema").GetInt32() == 4, "registration telemetry schema changed");
        Assert(first.RootElement.GetProperty("event_type").GetString() == "registration_event", "registration event type changed");
        Assert(first.RootElement.GetProperty("http_status").GetInt32() == 200, "registration HTTP status was lost");
        Assert(Math.Abs(first.RootElement.GetProperty("duration_ms").GetDouble() - 241.7) < 0.01, "registration duration was lost");
        Assert(second.RootElement.GetProperty("error_class").GetString() == "timeout", "registration timeout was not classified");

        var combined = string.Join("\n", batch.EventJson);
        foreach (var forbidden in new[] { "phone", "confirmCode", "authId", "Bearer ", "USER_ID", "PROFILE_ID" })
        {
            Assert(!combined.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"registration telemetry leaked sensitive field/value: {forbidden}");
        }

        var declined = new RegistrationTelemetryRecorder(installId, queue, "0.0.0-test", () => false).Begin();
        declined.Record(
            "get_confirm",
            1,
            Is74ApiResult<ConfirmationRequested>.Success(new ConfirmationRequested(), successCall));
        declined.Complete();
        queue.Complete(batch);
        Assert(!queue.HasPending, "declined registration telemetry was queued");
    }

    private static async Task TestUploadConsentGateAsync()
    {
        using var temp = TestDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var queue = new TelemetryQueue(paths);
        queue.Enqueue(["{\"event_type\":\"test\",\"schema\":3}"]);

        var posts = 0;
        using var http = new HttpClient(new DelegateHandler((request, _) =>
        {
            if (request.Method != HttpMethod.Post)
            {
                throw new InvalidOperationException("unexpected telemetry consent request");
            }
            Interlocked.Increment(ref posts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            });
        }));
        var client = new TelemetryClient(http, new Uri("https://telemetry.example.test/exec"));
        var allowed = false;
        var uploader = new TelemetryUploader(
            queue,
            new TelemetryUploadStateStore(paths, new JsonFileStore()),
            client,
            new AppSettings { AnonymousStatisticsConsent = AnonymousStatisticsConsent.Declined },
            new DiagnosticLogger(paths),
            () => allowed);

        await uploader.TryFlushIfDueAsync();
        Assert(posts == 0 && queue.HasPending,
            "telemetry uploader ignored declined anonymous statistics consent");

        allowed = true;
        await uploader.TryFlushIfDueAsync();
        Assert(posts == 1 && !queue.HasPending,
            "telemetry uploader did not resume after anonymous statistics consent");

        queue.Enqueue(["{\"event_type\":\"test\",\"schema\":3}"]);
        queue.ClearPending();
        Assert(!queue.HasPending, "telemetry queue did not clear at a consent boundary");
    }

    private static async Task TestMixedQueueRetryAsync()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "queued-speedtest.json")));
        var events = fixture.RootElement.GetProperty("events").EnumerateArray().ToArray();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var attempt = JsonSerializer.Deserialize<TelemetryAttemptEvent>(events[0], options)!;
        var speed = JsonSerializer.Deserialize<TelemetrySpeedTestEvent>(events[1], options)!;
        var serialized = new[] { TelemetrySerialization.Serialize(attempt), TelemetrySerialization.Serialize(speed) };
        for (var index = 0; index < events.Length; index++)
        {
            using var actual = JsonDocument.Parse(serialized[index]);
            Assert(JsonElement.DeepEquals(events[index], actual.RootElement),
                "client serialization diverged from the shared server fixture");
        }

        using var temp = TestDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var queue = new TelemetryQueue(paths);
        queue.Enqueue([serialized[0]]);
        queue.Enqueue([serialized[1]]);
        var stateStore = new TelemetryUploadStateStore(paths, new JsonFileStore());
        var clock = new UploadTestClock();
        var posts = 0;
        string? firstRequestBody = null;
        using var http = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            Assert(request.RequestUri!.Query.Contains("route=telemetry", StringComparison.Ordinal),
                "a shared queue must use the server's batch route");
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var payload = JsonDocument.Parse(body);
            var types = payload.RootElement.GetProperty("events").EnumerateArray()
                .Select(item => item.GetProperty("event_type").GetString()).Order().ToArray();
            Assert(types.SequenceEqual(new[] { "attempt", "speed_test" }), "mixed queue lost an event type");
            posts++;
            if (posts == 1) firstRequestBody = body;
            else Assert(body == firstRequestBody, "retry changed the pending payload or its dedupe identity");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                // Models the old receiver, then the compatible receiver after rollout.
                Content = new StringContent(posts == 1
                    ? "{\"ok\":false,\"error\":\"route_event_mismatch\"}"
                    : "{\"ok\":true,\"accepted\":2}")
            };
        }));
        var uploader = new TelemetryUploader(queue, stateStore,
            new TelemetryClient(http, new Uri("https://telemetry.example.test/exec")),
            new AppSettings { AnonymousStatisticsConsent = AnonymousStatisticsConsent.Allowed },
            new DiagnosticLogger(paths), timeProvider: clock);

        await uploader.TryFlushIfDueAsync();
        Assert(posts == 1 && queue.GetStatus().PendingFiles == 2,
            "rejected mixed batch must be retained, not silently discarded");
        var deferred = stateStore.Load();
        Assert(deferred.ConsecutiveFailures == 1 && deferred.NextAttemptUtc > clock.Now,
            "failed upload must back off");
        await uploader.TryFlushIfDueAsync();
        Assert(posts == 1, "uploader ignored retry backoff");
        clock.Now = deferred.NextAttemptUtc!.Value;
        await uploader.TryFlushIfDueAsync();
        Assert(posts == 2 && !queue.HasPending, "compatible receiver did not unblock the mixed queue");
        Assert(stateStore.Load().ConsecutiveFailures == 0, "successful retry did not reset failures");
    }

    private sealed class UploadTestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
