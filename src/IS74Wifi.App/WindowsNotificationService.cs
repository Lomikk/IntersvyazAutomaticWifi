using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal sealed partial class WindowsNotificationService(
    SettingsStore settingsStore,
    DiagnosticLogger logger) : IAgentNotificationSink
{
    internal const string InteractiveSessionGateName = @"Local\IS74Wifi.CSharp.Interactive";
    private const uint CallbackMessage = 0x8000 + 74; // WM_APP + 74
    private static int nextIconId = 100;
    private static readonly WindowProcedure NotificationWindowProcedure = NotificationWndProc;
    private static readonly IntPtr NotificationWindowProcedurePointer = Marshal.GetFunctionPointerForDelegate(NotificationWindowProcedure);
    private static readonly ConcurrentDictionary<IntPtr, WindowRegistration> NotificationWindows = new();

    public void Publish(AgentNotification notification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = settingsStore.Load().NotificationMode;
        if (mode == NotificationMode.Off ||
            (mode == NotificationMode.Important && notification.Importance == AgentNotificationImportance.Routine) ||
            IsInteractiveSessionRunning())
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                ShowBalloon(notification);
            }
            catch (Exception ex)
            {
                logger.Write(DiagnosticLevel.Warn,
                    $"notification.windows-failed error={ex.GetType().Name}:{ex.Message}");
            }
        });
    }

    internal static NamedSemaphoreLease? TryMarkInteractiveSession() =>
        NamedSemaphoreLease.TryAcquire(InteractiveSessionGateName);

    internal static bool IsInteractiveSessionRunning()
    {
        using var probe = NamedSemaphoreLease.TryAcquire(InteractiveSessionGateName);
        return probe is null;
    }

    private static unsafe void ShowBalloon(AgentNotification notification)
    {
        var window = NativeMethods.CreateWindowExW(
            0,
            "STATIC",
            "IS74WifiNotification",
            0,
            0,
            0,
            0,
            0,
            new IntPtr(-3), // HWND_MESSAGE
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            return;
        }

        var previousProcedure = IntPtr.Zero;
        if (notification.Action != AgentNotificationAction.None)
        {
            previousProcedure = NativeMethods.SetWindowLongPtrW(
                window,
                NativeMethods.GwlpWndProc,
                NotificationWindowProcedurePointer);
            if (previousProcedure != IntPtr.Zero)
            {
                NotificationWindows[window] = new WindowRegistration(previousProcedure, notification.Action);
            }
        }

        var iconId = unchecked((uint)Interlocked.Increment(ref nextIconId));
        var icon = NativeMethods.LoadIconW(IntPtr.Zero, new IntPtr(32516)); // IDI_INFORMATION
        var data = new NotifyIconData
        {
            cbSize = (uint)sizeof(NotifyIconData),
            hWnd = window,
            uID = iconId,
            uFlags = NativeMethods.NifIcon | NativeMethods.NifTip,
            hIcon = icon
        };
        if (previousProcedure != IntPtr.Zero)
        {
            data.uFlags |= NativeMethods.NifMessage;
            data.uCallbackMessage = CallbackMessage;
        }
        SetTip(ref data, "IS74Wifi");

        var added = false;
        try
        {
            if (NativeMethods.ShellNotifyIconW(NativeMethods.NimAdd, ref data) == 0)
            {
                return;
            }
            added = true;

            data.uFlags = NativeMethods.NifInfo;
            data.dwInfoFlags = notification.Severity switch
            {
                AgentNotificationSeverity.Error => NativeMethods.NiifError,
                AgentNotificationSeverity.Warning => NativeMethods.NiifWarning,
                _ => NativeMethods.NiifInfo
            };
            if (notification.Importance == AgentNotificationImportance.Routine)
            {
                data.dwInfoFlags |= NativeMethods.NiifNoSound;
            }

            SetInfoTitle(ref data, notification.Title);
            SetInfo(ref data, notification.Message);
            _ = NativeMethods.ShellNotifyIconW(NativeMethods.NimModify, ref data);

            // Actionable notifications need a short message pump so Shell_NotifyIcon
            // can deliver NIN_BALLOONUSERCLICK to the hidden window. Non-actionable
            // notifications keep the older, cheaper sleep path.
            if (previousProcedure != IntPtr.Zero)
            {
                PumpWindowMessages(TimeSpan.FromSeconds(15));
            }
            else
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            if (added)
            {
                data.uFlags = 0;
                _ = NativeMethods.ShellNotifyIconW(NativeMethods.NimDelete, ref data);
            }

            if (previousProcedure != IntPtr.Zero)
            {
                NotificationWindows.TryRemove(window, out _);
                _ = NativeMethods.SetWindowLongPtrW(window, NativeMethods.GwlpWndProc, previousProcedure);
            }
            _ = NativeMethods.DestroyWindow(window);
        }
    }

    private static void PumpWindowMessages(TimeSpan duration)
    {
        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            while (NativeMethods.PeekMessageW(out var message, IntPtr.Zero, 0, 0, NativeMethods.PmRemove) != 0)
            {
                _ = NativeMethods.TranslateMessage(ref message);
                _ = NativeMethods.DispatchMessageW(ref message);
            }
            Thread.Sleep(25);
        }
    }

    private static IntPtr NotificationWndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!NotificationWindows.TryGetValue(window, out var registration))
        {
            return NativeMethods.DefWindowProcW(window, message, wParam, lParam);
        }

        try
        {
            if (message == CallbackMessage && unchecked((uint)lParam.ToInt64()) == NativeMethods.NinBalloonUserClick)
            {
                LaunchNotificationAction(registration.Action);
                return IntPtr.Zero;
            }
        }
        catch
        {
            // A shell callback must never propagate an exception across the native
            // window-procedure boundary.
        }

        return NativeMethods.CallWindowProcW(registration.PreviousProcedure, window, message, wParam, lParam);
    }

    private static void LaunchNotificationAction(AgentNotificationAction action)
    {
        if (action != AgentNotificationAction.OpenUpdates)
        {
            return;
        }

        var installation = new ProgramInstallation();
        if (!installation.IsInstalled)
        {
            return;
        }

        var startInfo = new ProcessStartInfo(installation.ExecutablePath)
        {
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("menu");
        startInfo.ArgumentList.Add("updates");
        _ = Process.Start(startInfo);
    }

    private static unsafe void SetTip(ref NotifyIconData data, string value)
    {
        fixed (char* buffer = data.szTip)
        {
            SetFixedString(buffer, 128, value);
        }
    }

    private static unsafe void SetInfo(ref NotifyIconData data, string value)
    {
        fixed (char* buffer = data.szInfo)
        {
            SetFixedString(buffer, 256, value);
        }
    }

    private static unsafe void SetInfoTitle(ref NotifyIconData data, string value)
    {
        fixed (char* buffer = data.szInfoTitle)
        {
            SetFixedString(buffer, 64, value);
        }
    }

    private static unsafe void SetFixedString(char* destination, int capacity, string? value)
    {
        if (capacity <= 0)
        {
            return;
        }

        var text = value ?? string.Empty;
        var length = Math.Min(text.Length, capacity - 1);
        for (var i = 0; i < length; i++)
        {
            destination[i] = text[i];
        }
        destination[length] = '\0';
        for (var i = length + 1; i < capacity; i++)
        {
            destination[i] = '\0';
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private sealed record WindowRegistration(IntPtr PreviousProcedure, AgentNotificationAction Action);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hWnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public NativePoint pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private static partial class NativeMethods
    {
        internal const uint NimAdd = 0x00000000;
        internal const uint NimModify = 0x00000001;
        internal const uint NimDelete = 0x00000002;
        internal const uint NifMessage = 0x00000001;
        internal const uint NifIcon = 0x00000002;
        internal const uint NifTip = 0x00000004;
        internal const uint NifInfo = 0x00000010;
        internal const uint NiifInfo = 0x00000001;
        internal const uint NiifWarning = 0x00000002;
        internal const uint NiifError = 0x00000003;
        internal const uint NiifNoSound = 0x00000010;
        internal const uint NinBalloonUserClick = 0x0400 + 5; // WM_USER + 5
        internal const uint PmRemove = 0x0001;
        internal const int GwlpWndProc = -4;

        [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
        internal static partial int ShellNotifyIconW(uint message, ref NotifyIconData data);

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr CreateWindowExW(
            uint exStyle,
            string className,
            string? windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
        internal static partial int DestroyWindow(IntPtr window);

        [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
        internal static partial IntPtr LoadIconW(IntPtr instance, IntPtr iconName);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static partial IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);

        [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
        internal static partial IntPtr CallWindowProcW(
            IntPtr previousProcedure,
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static partial IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
        internal static partial int PeekMessageW(
            out NativeMessage message,
            IntPtr window,
            uint messageFilterMin,
            uint messageFilterMax,
            uint removeMessage);

        [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
        internal static partial int TranslateMessage(ref NativeMessage message);

        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static partial IntPtr DispatchMessageW(ref NativeMessage message);
    }
}
