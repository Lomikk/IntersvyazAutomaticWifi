using System.Diagnostics;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal static class AgentSupervisorPolicy
{
    private static readonly TimeSpan[] RestartBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30)
    ];

    internal static TimeSpan StableRunThreshold { get; } = TimeSpan.FromMinutes(5);

    internal static TimeSpan GetRestartDelay(int consecutiveFailures)
    {
        var index = Math.Clamp(consecutiveFailures - 1, 0, RestartBackoff.Length - 1);
        return RestartBackoff[index];
    }
}

/// <summary>
/// Keeps the real background worker in a separate process. HKCU Run only starts
/// applications at logon; it does not restart a process that later crashes or is
/// terminated. Keeping the tiny supervisor outside the networking/tray runtime
/// gives automatic authorization a recovery path after a hard worker exit.
/// </summary>
internal static class AgentSupervisor
{
    internal static async Task<int> RunAsync(string executablePath, DiagnosticLogger logger)
    {
        using var lease = NamedSemaphoreLease.TryAcquire(AgentProcessControl.AgentGateName);
        if (lease is null)
        {
            logger.Write(DiagnosticLevel.Info, "agent.start skipped reason=already-running runtime=csharp");
            return 0;
        }

        using var stopEvent = AgentProcessControl.CreateStopEvent();
        AgentProcessControl.RegisterCurrentAgentProcess();
        logger.Write(DiagnosticLevel.Info, "agent.start runtime=csharp mode=supervisor");

        var consecutiveFailures = 0;
        try
        {
            while (!stopEvent.WaitOne(0))
            {
                Process worker;
                try
                {
                    worker = StartWorker(executablePath);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    consecutiveFailures++;
                    var startDelay = AgentSupervisorPolicy.GetRestartDelay(consecutiveFailures);
                    logger.Write(DiagnosticLevel.Warn,
                        $"agent.worker launch-failed type={ex.GetType().Name} restartInMs={(long)startDelay.TotalMilliseconds}");
                    if (await DelayUnlessStoppedAsync(startDelay, stopEvent).ConfigureAwait(false))
                    {
                        break;
                    }
                    continue;
                }

                using (worker)
                {
                    var started = Stopwatch.StartNew();
                    logger.Write(DiagnosticLevel.Info, $"agent.worker launched pid={worker.Id}");

                    using var stopWait = new WaitHandleTask(stopEvent);
                    var exitTask = worker.WaitForExitAsync();
                    var completed = await Task.WhenAny(exitTask, stopWait.Task).ConfigureAwait(false);

                    if (completed == stopWait.Task || stopEvent.WaitOne(0))
                    {
                        StopWorker(worker, logger);
                        break;
                    }

                    await exitTask.ConfigureAwait(false);
                    var uptime = started.Elapsed;
                    if (uptime >= AgentSupervisorPolicy.StableRunThreshold)
                    {
                        consecutiveFailures = 0;
                    }
                    consecutiveFailures++;

                    var delay = AgentSupervisorPolicy.GetRestartDelay(consecutiveFailures);
                    logger.Write(DiagnosticLevel.Warn,
                        $"agent.worker exited exitCode={worker.ExitCode} uptimeMs={(long)uptime.TotalMilliseconds} " +
                        $"restartInMs={(long)delay.TotalMilliseconds}");

                    if (await DelayUnlessStoppedAsync(delay, stopEvent).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            logger.Write(DiagnosticLevel.Error,
                $"agent.supervisor error={ex.GetType().Name}:{ex.Message}");
            return 1;
        }
        finally
        {
            AgentProcessControl.SignalWorkerStop();
            AgentProcessControl.ClearCurrentAgentProcess();
            logger.Write(DiagnosticLevel.Info, "agent.stop runtime=csharp mode=supervisor");
        }
    }


    private static async Task<bool> DelayUnlessStoppedAsync(TimeSpan delay, WaitHandle stopEvent)
    {
        using var stopWait = new WaitHandleTask(stopEvent);
        return await Task.WhenAny(Task.Delay(delay), stopWait.Task).ConfigureAwait(false) == stopWait.Task;
    }

    private static Process StartWorker(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("agent-worker");
        return Process.Start(startInfo) ??
               throw new InvalidOperationException("Не удалось запустить рабочий процесс фоновой авторизации.");
    }

    private static void StopWorker(Process worker, DiagnosticLogger logger)
    {
        AgentProcessControl.SignalWorkerStop();
        try
        {
            if (worker.WaitForExit(5000))
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        try
        {
            worker.Kill(entireProcessTree: true);
            _ = worker.WaitForExit(5000);
            logger.Write(DiagnosticLevel.Warn, $"agent.worker forced-stop pid={worker.Id}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.Write(DiagnosticLevel.Warn,
                $"agent.worker forced-stop failed type={ex.GetType().Name}");
        }
    }

    private sealed class WaitHandleTask : IDisposable
    {
        private readonly TaskCompletionSource<bool> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RegisteredWaitHandle? registration;

        internal WaitHandleTask(WaitHandle handle)
        {
            registration = ThreadPool.RegisterWaitForSingleObject(
                handle,
                static (state, _) => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                completion,
                Timeout.Infinite,
                executeOnlyOnce: true);
        }

        internal Task Task => completion.Task;

        public void Dispose()
        {
            registration?.Unregister(null);
            registration = null;
        }
    }
}
