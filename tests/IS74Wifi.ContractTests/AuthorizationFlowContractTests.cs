using System.Net;
using IS74Wifi.Core;

internal static class AuthorizationFlowContractTests
{
    public static async Task RunAsync()
    {
        await TestEarlyStepTwoDoesNotWaitForStepOneAsync();
        await TestFallbackDoesNotBlockPrimaryScheduleAsync();
        await TestFallbackCanFindCodeAsync();
        await TestPoll401IsTerminalAsync();
        await TestLostStepTwoRecoveryAsync();
        await TestLostStepTwoWithoutRecoveryStopsAsync();
        await TestCancellationAfterStepTwoRemainsAmbiguousAsync();
        await TestAutomaticStepOneBudgetAsync();
        await TestPreStepFailureDoesNotSpendBudgetAsync();
        await TestWrongWifiStopsBeforeNetworkAsync();
        await TestAlreadyAuthorizedStopsPollingAsync();
        await TestLegacyMutexCanCoexistAsync();
        await TestNamedSemaphoreAcrossThreadsAsync();
        TestProductionSchedules();
    }

    private static async Task TestEarlyStepTwoDoesNotWaitForStepOneAsync()
    {
        using var temp = TestDirectory.Create();
        var api = new ScriptedPushClient(
            baseline: 100,
            async (call, pageSize, cancellationToken) =>
            {
                if (pageSize != 1)
                {
                    return EmptyPage();
                }

                if (call == 1)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return CancelledApi();
                    }
                    return EmptyPage();
                }

                return Page(new Is74PushMessage(
                    101,
                    "Ваш код авторизации",
                    "4321 код авторизации в приложении \"Интерсвязь\"",
                    null));
            });

        var portal = new StalledStepOnePortal();
        var internet = new SequenceInternetProbe(true);
        var flow = CreateFlow(
            temp,
            api,
            portal,
            internet,
            new AlwaysTargetWifi(),
            pollOffsets: [10, 20, 40]);

        var outcome = await flow.RunAsync(Request());

        Assert(outcome.Kind == AuthorizationOutcomeKind.Success, "early-stepTwo scenario did not succeed");
        Assert(portal.StepTwoCalls == 1, "stepTwo was not sent exactly once");
        Assert(portal.StepTwoCalledBeforeStepOneCompleted, "stepTwo waited for the stalled stepOne response");
        Assert(outcome.Timing?.CodeObservedMilliseconds is < 150, "fresh code observation was unexpectedly delayed by stalled stepOne");
        Assert(api.PrimaryCalls >= 2,
            "later primary poll was not launched while the first GET was stalled");
        var timingLog = File.ReadAllText(new AppPaths(temp.Path).DiagnosticLogFile);
        Assert(timingLog.Contains("critical.timing", StringComparison.Ordinal),
            "critical timing telemetry was not flushed after the fast phase");
        Assert(!timingLog.Contains("4321", StringComparison.Ordinal),
            "Wi-Fi confirmation code leaked into timing telemetry");
    }

    private static async Task TestFallbackDoesNotBlockPrimaryScheduleAsync()
    {
        using var temp = TestDirectory.Create();
        var fallbackStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ScriptedPushClient(
            baseline: 100,
            async (call, pageSize, cancellationToken) =>
            {
                if (pageSize == 5)
                {
                    fallbackStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return CancelledApi();
                    }
                    return EmptyPage();
                }

                if (call == 1)
                {
                    return Page(new Is74PushMessage(101, "other", "not a wifi code", null));
                }

                await fallbackStarted.Task.WaitAsync(cancellationToken);
                return Page(new Is74PushMessage(
                    102,
                    "Ваш код авторизации",
                    "5678 код авторизации в приложении \"Интерсвязь\"",
                    null));
            });

        var portal = new ImmediatePortal();
        var flow = CreateFlow(
            temp,
            api,
            portal,
            new SequenceInternetProbe(true),
            new AlwaysTargetWifi(),
            pollOffsets: [5, 15, 30]);

        var outcome = await flow.RunAsync(Request());

        Assert(outcome.Kind == AuthorizationOutcomeKind.Success, "primary-after-fallback scenario did not succeed");
        Assert(outcome.Timing?.CodeSource == WifiCodeSource.Primary, "stalled fallback blocked or replaced primary result");
        Assert(api.PageSize5Calls == 1, "pageSize=5 fallback was not one-shot");
    }

    private static async Task TestFallbackCanFindCodeAsync()
    {
        using var temp = TestDirectory.Create();
        var api = new ScriptedPushClient(
            baseline: 200,
            (call, pageSize, _) => Task.FromResult(pageSize == 5
                ? Page(
                    new Is74PushMessage(201, "other", "other", null),
                    new Is74PushMessage(202, "Ваш код авторизации", "2468 код авторизации в приложении \"Интерсвязь\"", null))
                : call == 1
                    ? Page(new Is74PushMessage(201, "other", "other", null))
                    : EmptyPage()));

        var flow = CreateFlow(
            temp,
            api,
            new ImmediatePortal(),
            new SequenceInternetProbe(true),
            new AlwaysTargetWifi(),
            pollOffsets: [5, 40]);

        var outcome = await flow.RunAsync(Request());

        Assert(outcome.Kind == AuthorizationOutcomeKind.Success, "fallback-code scenario did not succeed");
        Assert(outcome.Timing?.CodeSource == WifiCodeSource.Fallback, "pageSize=5 fallback code was not selected");
        Assert(api.PageSize5Calls == 1, "fallback code path launched more than one pageSize=5 request");
    }

    private static async Task TestPoll401IsTerminalAsync()
    {
        using var temp = TestDirectory.Create();
        var api = new ScriptedPushClient(
            baseline: 10,
            (_, _, _) => Task.FromResult(Is74ApiResult<PushMessagePage>.Fail(new Is74ApiFailure(
                Is74ApiFailureKind.Unauthorized,
                "pushmessages",
                StatusCode: 401))));
        var flow = CreateFlow(
            temp,
            api,
            new ImmediatePortal(),
            new SequenceInternetProbe(false),
            new AlwaysTargetWifi(),
            pollOffsets: [1]);

        var outcome = await flow.RunAsync(Request());
        var state = LoadState(temp);

        Assert(outcome.Kind == AuthorizationOutcomeKind.BearerInvalid, "poll HTTP 401 was not terminal bearer-invalid");
        Assert(state.UserActionRequired && state.LastResult == "bearer-invalid", "bearer-invalid state was not persisted");
    }

    private static async Task TestLostStepTwoRecoveryAsync()
    {
        using var temp = TestDirectory.Create();
        var api = CodeImmediatelyApi();
        var portal = new ImmediatePortal
        {
            StepTwoResult = CaptivePortalResult<StepTwoResponse>.Fail(new CaptivePortalFailure(
                CaptivePortalFailureKind.Transport,
                "portal.stepTwo",
                TransportFailureKind.Timeout,
                SideEffectMayHaveOccurred: true))
        };
        var internet = new SequenceInternetProbe(false, false, true);
        var flow = CreateFlow(
            temp,
            api,
            portal,
            internet,
            new AlwaysTargetWifi(),
            pollOffsets: [1],
            options: FastOptions(lostStepTwo: [0, 2, 5]));

        var outcome = await flow.RunAsync(Request());
        var state = LoadState(temp);

        Assert(outcome.Kind == AuthorizationOutcomeKind.Success && outcome.InternetConfirmed == true,
            "lost stepTwo response was not recovered by Internet side effect");
        Assert(portal.StepTwoCalls == 1, "lost stepTwo response caused a duplicate code POST");
        Assert(state.LastResult == "success" && state.InternetConfirmed == true, "recovered stepTwo success was not persisted");
    }

    private static async Task TestLostStepTwoWithoutRecoveryStopsAsync()
    {
        using var temp = TestDirectory.Create();
        var portal = new ImmediatePortal
        {
            StepTwoResult = CaptivePortalResult<StepTwoResponse>.Fail(new CaptivePortalFailure(
                CaptivePortalFailureKind.Transport,
                "portal.stepTwo",
                TransportFailureKind.ConnectionFailure,
                SideEffectMayHaveOccurred: true))
        };
        var flow = CreateFlow(
            temp,
            CodeImmediatelyApi(),
            portal,
            new SequenceInternetProbe(false, false),
            new AlwaysTargetWifi(),
            pollOffsets: [1],
            options: FastOptions(lostStepTwo: [0, 2]));

        var outcome = await flow.RunAsync(Request());
        var state = LoadState(temp);

        Assert(outcome.Kind == AuthorizationOutcomeKind.StepTwoAmbiguous, "unconfirmed lost stepTwo did not stop as ambiguous");
        Assert(portal.StepTwoCalls == 1, "ambiguous stepTwo was blindly resent");
        Assert(state.UserActionRequired && state.LastResult == "step-two-ambiguous", "ambiguous stepTwo did not require user action");
    }

    private static async Task TestCancellationAfterStepTwoRemainsAmbiguousAsync()
    {
        using var temp = TestDirectory.Create();
        using var cts = new CancellationTokenSource();
        var portal = new CancellingStepTwoPortal(cts);
        var flow = CreateFlow(
            temp,
            CodeImmediatelyApi(),
            portal,
            new SequenceInternetProbe(false),
            new AlwaysTargetWifi(),
            pollOffsets: [1],
            options: FastOptions(lostStepTwo: [0]));

        var outcome = await flow.RunAsync(Request(), cts.Token);
        var state = LoadState(temp);

        Assert(outcome.Kind == AuthorizationOutcomeKind.StepTwoAmbiguous,
            "cancellation after sending stepTwo erased the possible side effect");
        Assert(portal.StepTwoCalls == 1, "cancelled stepTwo was resent");
        Assert(state.UserActionRequired && state.LastResult == "step-two-ambiguous",
            "cancelled stepTwo did not preserve ambiguous terminal state");
    }

    private static async Task TestAutomaticStepOneBudgetAsync()
    {
        using var temp = TestDirectory.Create();
        var portal = new ImmediatePortal();
        var api = new ScriptedPushClient(
            baseline: 1,
            (_, _, _) => Task.FromResult(EmptyPage()));
        var flow = CreateFlow(
            temp,
            api,
            portal,
            new SequenceInternetProbe(false),
            new AlwaysTargetWifi(),
            pollOffsets: [1],
            options: FastOptions());

        AuthorizationOutcome? last = null;
        for (var i = 0; i < ProtocolContract.MaxAutomaticStepOneAttempts; i++)
        {
            last = await flow.RunAsync(Request(AuthorizationAttemptReason.Automatic));
        }
        var state = LoadState(temp);

        Assert(last?.Kind == AuthorizationOutcomeKind.UserActionRequired,
            "fourth exhausted automatic stepOne did not stop the cycle");
        Assert(portal.StepOneCalls == ProtocolContract.MaxAutomaticStepOneAttempts,
            "automatic cycle sent more or fewer than four stepOne requests");
        Assert(state.AutomaticStepOneAttempts == ProtocolContract.MaxAutomaticStepOneAttempts,
            "automatic stepOne budget was not persisted");
        Assert(state.UserActionRequired && state.LastResult == "automatic-step-one-limit",
            "automatic stepOne limit did not become terminal");
    }

    private static async Task TestPreStepFailureDoesNotSpendBudgetAsync()
    {
        using var temp = TestDirectory.Create();
        var api = new ScriptedPushClient(
            baselineFailure: new Is74ApiFailure(
                Is74ApiFailureKind.Transport,
                "push.baseline",
                TransportFailureKind.DnsUnavailable));
        var portal = new ImmediatePortal();
        var flow = CreateFlow(
            temp,
            api,
            portal,
            new SequenceInternetProbe(false),
            new AlwaysTargetWifi(),
            pollOffsets: [1]);

        var outcome = await flow.RunAsync(Request(AuthorizationAttemptReason.Automatic));
        var state = LoadState(temp);

        Assert(outcome.Kind == AuthorizationOutcomeKind.RetryableBeforeStepOne, "baseline DNS failure was not pre-step retryable");
        Assert(portal.StepOneCalls == 0, "pre-step failure still sent stepOne");
        Assert(state.AutomaticStepOneAttempts == 0, "pre-step failure consumed the four-attempt budget");
        Assert(state.PreStepFailureCount == 1 && outcome.RetryAfter == TimeSpan.FromSeconds(1),
            "pre-step retry backoff changed");
    }

    private static async Task TestWrongWifiStopsBeforeNetworkAsync()
    {
        using var temp = TestDirectory.Create();
        var api = CodeImmediatelyApi();
        var portal = new ImmediatePortal();
        var internet = new SequenceInternetProbe(true);
        var flow = CreateFlow(
            temp,
            api,
            portal,
            internet,
            new NeverTargetWifi(),
            pollOffsets: [1]);

        var outcome = await flow.RunAsync(Request());

        Assert(outcome.Kind == AuthorizationOutcomeKind.WrongWifi, "foreign SSID was not rejected");
        Assert(api.BaselineCalls == 0 && api.PushCalls == 0 && portal.StepOneCalls == 0 && internet.Calls == 0,
            "foreign SSID performed network authorization work");
    }

    private static async Task TestAlreadyAuthorizedStopsPollingAsync()
    {
        using var temp = TestDirectory.Create();
        var api = new ScriptedPushClient(
            baseline: 1,
            async (_, _, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return CancelledApi();
                }
                return EmptyPage();
            });
        var portal = new ImmediatePortal
        {
            StepOneResult = CaptivePortalResult<StepOneResponse>.Success(new StepOneResponse(
                StepOneDisposition.AlreadyAuthorized,
                new Uri("https://openwifi.is74.ru/home/connect/formy_connect/landing/pages/wifi/"),
                null,
                null,
                TimeSpan.FromMilliseconds(1)))
        };
        var flow = CreateFlow(
            temp,
            api,
            portal,
            new SequenceInternetProbe(false),
            new AlwaysTargetWifi(),
            pollOffsets: [50]);

        var outcome = await flow.RunAsync(Request(AuthorizationAttemptReason.Automatic));
        var state = LoadState(temp);

        Assert(outcome.Kind == AuthorizationOutcomeKind.AlreadyAuthorized, "already-authorized redirect was not returned immediately");
        Assert(portal.StepTwoCalls == 0, "already-authorized flow sent stepTwo");
        Assert(state.EdgeWatchActive && state.LastResult == "already-authorized", "already-authorized edge-watch state was not preserved");
    }

    private static async Task TestLegacyMutexCanCoexistAsync()
    {
        using var legacyMutex = new Mutex(false, "Local\\IS74Wifi.Auth");
        using var temp = TestDirectory.Create();
        var flow = CreateFlow(
            temp,
            CodeImmediatelyApi(),
            new ImmediatePortal(),
            new SequenceInternetProbe(true),
            new AlwaysTargetWifi(),
            pollOffsets: [1]);

        var outcome = await flow.RunAsync(Request());
        Assert(outcome.Kind == AuthorizationOutcomeKind.Success,
            "C# authorization gate collided with the legacy PowerShell mutex name");
    }

    private static async Task TestNamedSemaphoreAcrossThreadsAsync()
    {
        var name = $"Local\\IS74Wifi.Contract.Async.{Guid.NewGuid():N}";
        var first = NamedSemaphoreLease.TryAcquire(name);
        Assert(first is not null, "named semaphore first acquisition failed");
        await Task.Run(() => first!.Dispose());
        using var second = NamedSemaphoreLease.TryAcquire(name);
        Assert(second is not null, "named semaphore could not be released from another thread");
    }

    private static void TestProductionSchedules()
    {
        Assert(ProtocolContract.PushPollOffsetsMilliseconds.SequenceEqual(
            [100, 150, 200, 250, 350, 500, 700, 1000, 1400, 2000, 3000, 4500, 6500, 10000]),
            "production push polling schedule changed");
        Assert(ProtocolContract.LostStepTwoProbeOffsetsMilliseconds.SequenceEqual([0, 250, 500, 1000, 2000, 4000]),
            "lost-stepTwo recovery schedule changed");
    }

    private static AuthorizationFlow CreateFlow(
        TestDirectory temp,
        IIs74PushClient api,
        ICaptivePortalClient portal,
        IInternetConnectivityProbe internet,
        IWifiEnvironment wifi,
        IEnumerable<int> pollOffsets,
        AuthorizationFlowOptions? options = null)
    {
        var paths = new AppPaths(temp.Path);
        var json = new JsonFileStore();
        var settings = new AppSettings();
        var state = new AuthorizationStateManager(new RuntimeStateStore(paths, json), settings);
        var polling = new PushPollingEngine(api, pollOffsets, TimeSpan.FromMilliseconds(250));
        var logger = new DiagnosticLogger(paths);
        return new AuthorizationFlow(api, portal, internet, wifi, polling, state, logger, options ?? FastOptions());
    }

    private static AuthorizationFlowOptions FastOptions(int[]? lostStepTwo = null) => new()
    {
        InitialInternetProbeTimeout = TimeSpan.FromMilliseconds(20),
        BaselineTimeout = TimeSpan.FromMilliseconds(100),
        StepOneTimeout = TimeSpan.FromMilliseconds(250),
        StepTwoTimeout = TimeSpan.FromMilliseconds(100),
        RecoveryInternetProbeTimeout = TimeSpan.FromMilliseconds(20),
        PostSuccessInternetProbeTimeout = TimeSpan.FromMilliseconds(20),
        LostStepTwoProbeOffsetsMilliseconds = lostStepTwo ?? [0, 2, 5],
        PostSuccessProbeOffsetsMilliseconds = [0]
    };

    private static AuthorizationRequest Request(AuthorizationAttemptReason reason = AuthorizationAttemptReason.Manual) =>
        new("bearer-test", "9123456789", "device-test", reason, Force: true);

    private static RuntimeState LoadState(TestDirectory temp) =>
        new RuntimeStateStore(new AppPaths(temp.Path), new JsonFileStore()).Load();

    private static ScriptedPushClient CodeImmediatelyApi() => new(
        baseline: 100,
        (_, pageSize, _) => Task.FromResult(pageSize == 1
            ? Page(new Is74PushMessage(101, "Ваш код авторизации", "1234 код авторизации в приложении \"Интерсвязь\"", null))
            : EmptyPage()));

    private static Is74ApiResult<PushMessagePage> Page(params Is74PushMessage[] messages) =>
        Is74ApiResult<PushMessagePage>.Success(new PushMessagePage(messages, KnownEmpty: messages.Length == 0));

    private static Is74ApiResult<PushMessagePage> EmptyPage() =>
        Is74ApiResult<PushMessagePage>.Success(new PushMessagePage([], KnownEmpty: true));

    private static Is74ApiResult<PushMessagePage> CancelledApi() =>
        Is74ApiResult<PushMessagePage>.Fail(new Is74ApiFailure(
            Is74ApiFailureKind.Transport,
            "pushmessages",
            TransportFailureKind.Cancelled));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ScriptedPushClient : IIs74PushClient
    {
        private readonly long baseline;
        private readonly Is74ApiFailure? baselineFailure;
        private readonly Func<int, int, CancellationToken, Task<Is74ApiResult<PushMessagePage>>> push;
        private int primaryCalls;

        public ScriptedPushClient(
            long baseline = 0,
            Func<int, int, CancellationToken, Task<Is74ApiResult<PushMessagePage>>>? push = null,
            Is74ApiFailure? baselineFailure = null)
        {
            this.baseline = baseline;
            this.baselineFailure = baselineFailure;
            this.push = push ?? ((_, _, _) => Task.FromResult(EmptyPage()));
        }

        public int BaselineCalls { get; private set; }
        public int PushCalls { get; private set; }
        public int PageSize5Calls { get; private set; }
        public int PrimaryCalls => Volatile.Read(ref primaryCalls);

        public Task<Is74ApiResult<PushBaseline>> GetBaselineAsync(
            string bearerToken,
            string deviceId,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            BaselineCalls++;
            return Task.FromResult(baselineFailure is null
                ? Is74ApiResult<PushBaseline>.Success(new PushBaseline(baseline))
                : Is74ApiResult<PushBaseline>.Fail(baselineFailure));
        }

        public Task<Is74ApiResult<PushMessagePage>> GetPushMessagesAsync(
            string bearerToken,
            string deviceId,
            int pageSize,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            PushCalls++;
            if (pageSize == 5)
            {
                PageSize5Calls++;
                return push(primaryCalls, pageSize, cancellationToken);
            }

            var call = Interlocked.Increment(ref primaryCalls);
            return push(call, pageSize, cancellationToken);
        }
    }

    private sealed class ImmediatePortal : ICaptivePortalClient
    {
        public int StepOneCalls { get; private set; }
        public int StepTwoCalls { get; private set; }

        public CaptivePortalResult<StepOneResponse> StepOneResult { get; set; } =
            CaptivePortalResult<StepOneResponse>.Success(new StepOneResponse(
                StepOneDisposition.StepTwo,
                new Uri("stepTwo?phone=9123456789&isMp=true", UriKind.Relative),
                new Uri("http://w.is74.ru/stepTwo?phone=9123456789&isMp=true"),
                null,
                TimeSpan.FromMilliseconds(1)));

        public CaptivePortalResult<StepTwoResponse> StepTwoResult { get; set; } =
            CaptivePortalResult<StepTwoResponse>.Success(new StepTwoResponse(
                new Uri("stepThree", UriKind.Relative),
                new DateTimeOffset(2026, 9, 18, 11, 0, 0, TimeSpan.Zero),
                TimeSpan.FromMilliseconds(1)));

        public Task<CaptivePortalResult<StepOneResponse>> SendStepOneAsync(
            string phone,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            StepOneCalls++;
            return Task.FromResult(StepOneResult);
        }

        public Task<CaptivePortalResult<StepTwoResponse>> SendStepTwoAsync(
            string phone,
            string confirmCode,
            Uri? observedStepTwoLocation = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            StepTwoCalls++;
            return Task.FromResult(StepTwoResult);
        }
    }

    private sealed class StalledStepOnePortal : ICaptivePortalClient
    {
        private readonly TaskCompletionSource<CaptivePortalResult<StepOneResponse>> stepOneCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StepTwoCalls { get; private set; }
        public bool StepOneCompleted { get; private set; }
        public bool StepTwoCalledBeforeStepOneCompleted { get; private set; }

        public async Task<CaptivePortalResult<StepOneResponse>> SendStepOneAsync(
            string phone,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => stepOneCompletion.TrySetResult(
                CaptivePortalResult<StepOneResponse>.Fail(new CaptivePortalFailure(
                    CaptivePortalFailureKind.Transport,
                    "portal.stepOne",
                    TransportFailureKind.Cancelled,
                    SideEffectMayHaveOccurred: true))));
            var result = await stepOneCompletion.Task;
            StepOneCompleted = true;
            return result;
        }

        public Task<CaptivePortalResult<StepTwoResponse>> SendStepTwoAsync(
            string phone,
            string confirmCode,
            Uri? observedStepTwoLocation = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            StepTwoCalls++;
            StepTwoCalledBeforeStepOneCompleted = !StepOneCompleted;
            stepOneCompletion.TrySetResult(CaptivePortalResult<StepOneResponse>.Success(new StepOneResponse(
                StepOneDisposition.StepTwo,
                new Uri("stepTwo?phone=9123456789&isMp=true", UriKind.Relative),
                new Uri("http://w.is74.ru/stepTwo?phone=9123456789&isMp=true"),
                null,
                TimeSpan.FromMilliseconds(200))));
            return Task.FromResult(CaptivePortalResult<StepTwoResponse>.Success(new StepTwoResponse(
                new Uri("stepThree", UriKind.Relative),
                new DateTimeOffset(2026, 9, 18, 11, 0, 0, TimeSpan.Zero),
                TimeSpan.FromMilliseconds(1))));
        }
    }

    private sealed class CancellingStepTwoPortal(CancellationTokenSource cancellation) : ICaptivePortalClient
    {
        public int StepTwoCalls { get; private set; }

        public Task<CaptivePortalResult<StepOneResponse>> SendStepOneAsync(
            string phone,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CaptivePortalResult<StepOneResponse>.Success(new StepOneResponse(
                StepOneDisposition.StepTwo,
                new Uri("stepTwo?phone=9123456789&isMp=true", UriKind.Relative),
                new Uri("http://w.is74.ru/stepTwo?phone=9123456789&isMp=true"),
                null,
                TimeSpan.Zero)));

        public Task<CaptivePortalResult<StepTwoResponse>> SendStepTwoAsync(
            string phone,
            string confirmCode,
            Uri? observedStepTwoLocation = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            StepTwoCalls++;
            cancellation.Cancel();
            return Task.FromResult(CaptivePortalResult<StepTwoResponse>.Fail(new CaptivePortalFailure(
                CaptivePortalFailureKind.Transport,
                "portal.stepTwo",
                TransportFailureKind.Cancelled,
                SideEffectMayHaveOccurred: true)));
        }
    }

    private sealed class SequenceInternetProbe(params bool[] online) : IInternetConnectivityProbe
    {
        private readonly Queue<bool> values = new(online);
        public int Calls { get; private set; }

        public Task<InternetProbeResult> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            var isOnline = values.Count > 0 && values.Dequeue();
            return Task.FromResult(new InternetProbeResult(
                Online: isOnline,
                HttpResponseReceived: true,
                StatusCode: HttpStatusCode.OK,
                Body: isOnline ? null : "captive",
                FailureKind: TransportFailureKind.None,
                Elapsed: TimeSpan.Zero));
        }
    }

    private sealed class AlwaysTargetWifi : IWifiEnvironment
    {
        public bool IsTargetWifiConnected() => true;
    }

    private sealed class NeverTargetWifi : IWifiEnvironment
    {
        public bool IsTargetWifiConnected() => false;
    }

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path) => Path = path;
        public string Path { get; }

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IS74Wifi-flow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
