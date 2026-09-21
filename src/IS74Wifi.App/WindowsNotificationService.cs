using System.Runtime.InteropServices;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal sealed class WindowsNotificationService(
    SettingsStore settingsStore,
    DiagnosticLogger logger) : IAgentNotificationSink
{
    internal const string InteractiveSessionGateName = @"Local\IS74Wifi.CSharp.Interactive";
    private static int nextIconId = 100;

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

    private static bool IsInteractiveSessionRunning()
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

            // Keep the tray identity alive long enough for Windows to surface the
            // balloon through its normal notification UI. Notifications are rare
            // (normally once per 24-hour auth cycle), so one short-lived task is
            // preferable to keeping a permanent tray process/icon.
            Thread.Sleep(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (added)
            {
                data.uFlags = 0;
                _ = NativeMethods.ShellNotifyIconW(NativeMethods.NimDelete, ref data);
            }
            _ = NativeMethods.DestroyWindow(window);
        }
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

        [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
        internal static partial int DestroyWindow(IntPtr window);

        [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
        internal static partial IntPtr LoadIconW(IntPtr instance, IntPtr iconName);
    }
}
