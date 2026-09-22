using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IS74Wifi.Core;

/// <summary>
/// Delivers Windows resume events to an already-running agent. It does not
/// schedule system wake timers, request execution state, or inhibit sleep.
/// </summary>
public sealed class AgentPowerResumeMonitor : IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint ResumeFromSuspend = 0x07;
    private const uint ResumeAutomatic = 0x12;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint PowerNotificationCallback(nint context, uint eventType, nint setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters
    {
        public nint Callback;
        public nint Context;
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerRegisterSuspendResumeNotification(
        uint flags, ref SubscribeParameters parameters, out nint registration);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerUnregisterSuspendResumeNotification(nint registration);

    private readonly Action onResume;
    // Keep the managed callback rooted throughout the native registration.
    private readonly PowerNotificationCallback callback;
    private nint registration;

    private AgentPowerResumeMonitor(Action onResume)
    {
        this.onResume = onResume;
        callback = OnPowerNotification;
        var parameters = new SubscribeParameters
        {
            Callback = Marshal.GetFunctionPointerForDelegate(callback)
        };
        var error = PowerRegisterSuspendResumeNotification(
            DeviceNotifyCallback, ref parameters, out registration);
        if (error != 0)
        {
            throw new Win32Exception((int)error);
        }
    }

    public static AgentPowerResumeMonitor? TryRegister(Action onResume, Action<string>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(onResume);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            return new AgentPowerResumeMonitor(onResume);
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or
                                   EntryPointNotFoundException or MarshalDirectiveException or
                                   PlatformNotSupportedException or NotSupportedException)
        {
            onFailure?.Invoke(ex.GetType().Name);
            return null;
        }
    }

    public static bool IsResumeEvent(uint eventType) =>
        eventType is ResumeFromSuspend or ResumeAutomatic;

    private uint OnPowerNotification(nint context, uint eventType, nint setting)
    {
        if (IsResumeEvent(eventType))
        {
            onResume();
        }
        return 0;
    }

    public void Dispose()
    {
        var active = Interlocked.Exchange(ref registration, 0);
        if (active != 0)
        {
            PowerUnregisterSuspendResumeNotification(active);
        }
        GC.KeepAlive(callback);
    }
}
