using System.Collections.Concurrent;
using IS74Wifi.Core;

namespace IS74Wifi.App;

// The authorization callback only enqueues stages. History and terminal output
// belong to the consumer, never to the timing-sensitive portal request path.
internal static class ManualAuthorizationRunner
{
    internal static async Task<AuthorizationOutcome> RunAsync(
        Func<Action<AuthorizationProgressStage>, Task<AuthorizationOutcome>> authorize,
        Action<AuthorizationProgressStage> applyStage,
        Action render)
    {
        var pending = new ConcurrentQueue<AuthorizationProgressStage>();
        var operation = Task.Run(() => authorize(pending.Enqueue));

        void DrainProgress()
        {
            var changed = false;
            while (pending.TryDequeue(out var stage))
            {
                applyStage(stage);
                changed = true;
            }

            if (changed)
            {
                render();
            }
        }

        try
        {
            while (!operation.IsCompleted)
            {
                DrainProgress();
                await Task.WhenAny(operation, Task.Delay(100)).ConfigureAwait(false);
            }

            // Preserve the last stages even when authorization finishes between frames.
            DrainProgress();
            return await operation.ConfigureAwait(false);
        }
        finally
        {
            // A terminal/history failure must not leave an unobserved authorization
            // running while the caller returns to the menu or starts another action.
            await operation.ConfigureAwait(false);
        }
    }
}
