using IS74Wifi.App;
using IS74Wifi.Core;

internal static class ManualAuthorizationProgressContractTests
{
    public static async Task RunAsync()
    {
        await SlowRendererDoesNotBlockAuthorizationAsync();
        await CompletedAndFaultedOperationsAsync();
        await RendererFailureWaitsForAuthorizationAsync();
    }

    private static AuthorizationOutcome Success() => new(
        AuthorizationOutcomeKind.Success, true, DateTimeOffset.UtcNow, null, null);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task SlowRendererDoesNotBlockAuthorizationAsync()
    {
        var rendering = Signal();
        var codeSent = Signal();
        var seen = new List<AuthorizationProgressStage>();
        var renders = 0;
        var expected = Success();

        var result = await ManualAuthorizationRunner.RunAsync(async report =>
        {
            report(AuthorizationProgressStage.FreshCodeReceived);
            await rendering.Task.WaitAsync(TimeSpan.FromSeconds(5));
            report(AuthorizationProgressStage.StepTwoStarted);
            // Models the portal send continuing while terminal output is blocked.
            codeSent.SetResult();
            report(AuthorizationProgressStage.StepTwoAccepted);
            report(AuthorizationProgressStage.InternetConfirmed);
            return expected;
        }, seen.Add, () =>
        {
            renders++;
            rendering.TrySetResult();
            codeSent.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        });

        Assert(ReferenceEquals(result, expected), "authorization outcome changed");
        Assert(seen.SequenceEqual(new[]
        {
            AuthorizationProgressStage.FreshCodeReceived,
            AuthorizationProgressStage.StepTwoStarted,
            AuthorizationProgressStage.StepTwoAccepted,
            AuthorizationProgressStage.InternetConfirmed
        }), "progress was lost, reordered or applied more than once");
        Assert(renders >= 2, "final queued progress was not rendered");
    }

    private static async Task CompletedAndFaultedOperationsAsync()
    {
        var stages = new List<AuthorizationProgressStage>();
        var frames = 0;
        await ManualAuthorizationRunner.RunAsync(report =>
        {
            report(AuthorizationProgressStage.InternetConfirmed);
            return Task.FromResult(Success());
        }, stages.Add, () => frames++);
        Assert(stages.Count == 1 && frames == 1, "synchronous completion lost its final frame");

        frames = 0;
        await ManualAuthorizationRunner.RunAsync(_ => Task.FromResult(Success()),
            _ => throw new InvalidOperationException("unexpected stage"), () => frames++);
        Assert(frames == 0, "empty progress should not produce redundant frames");

        var failure = new InvalidOperationException("fake authorization failure");
        try
        {
            await ManualAuthorizationRunner.RunAsync(_ => throw failure, _ => { }, () => { });
            throw new Exception("authorization failure was swallowed");
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
    }

    private static async Task RendererFailureWaitsForAuthorizationAsync()
    {
        var rendering = Signal();
        var complete = Signal();
        var finished = false;
        var failure = new InvalidOperationException("fake terminal failure");
        var run = ManualAuthorizationRunner.RunAsync(async report =>
        {
            report(AuthorizationProgressStage.BaselineLoaded);
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
            finished = true;
            return Success();
        }, _ => { }, () =>
        {
            rendering.TrySetResult();
            throw failure;
        });
        await rendering.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!run.IsCompleted, "renderer failure abandoned a running authorization");
        complete.SetResult();
        try
        {
            await run;
            throw new Exception("renderer failure was swallowed");
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
        Assert(finished, "authorization was not awaited after renderer failure");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
