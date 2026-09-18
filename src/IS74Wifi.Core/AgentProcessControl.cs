namespace IS74Wifi.Core;

public static class AgentProcessControl
{
    public const string AgentGateName = @"Local\IS74Wifi.CSharp.Agent";
    public const string StopEventName = @"Local\IS74Wifi.CSharp.AgentStop";

    public static EventWaitHandle CreateStopEvent()
    {
        var handle = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        handle.Reset();
        return handle;
    }

    public static void SignalStop()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(StopEventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    public static bool WaitForAgentExit(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            using var lease = NamedSemaphoreLease.TryAcquire(AgentGateName);
            if (lease is not null)
            {
                return true;
            }

            Thread.Sleep(100);
        } while (DateTimeOffset.UtcNow < deadline);

        return false;
    }
}
