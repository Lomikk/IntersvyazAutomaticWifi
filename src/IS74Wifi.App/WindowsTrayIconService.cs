using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal enum TrayIconSemanticState
{
    Normal,
    Working,
    Question,
    Warning,
    Error
}

internal sealed record TrayIconPresentation(TrayIconSemanticState State, string Tip);

internal static class TrayIconStateResolver
{
    private static readonly HashSet<string> FatalResults = new(StringComparer.Ordinal)
    {
        "automatic-step-one-limit",
        "bearer-invalid",
        "step-two-ambiguous",
        "step-two-rejected",
        "step-one-rate-limited",
        "step-one-rejected",
        "unexpected-step-one-redirect"
    };

    internal static TrayIconPresentation Working { get; } =
        new(TrayIconSemanticState.Working, "IS74Wifi · проверка и авторизация сети");

    internal static TrayIconPresentation TickFailed { get; } =
        new(TrayIconSemanticState.Error, "IS74Wifi · ошибка фонового режима");

    internal static TrayIconPresentation Resolve(PathAuthorizationStateDocument document)
    {
        var active = document.Paths
            .Where(path => path.Status != PathAuthorizationStatus.Disconnected)
            .ToArray();

        if (active.Any(path => path.UserActionRequired ||
                               path.LastResult is not null && FatalResults.Contains(path.LastResult)))
        {
            return new TrayIconPresentation(
                TrayIconSemanticState.Error,
                "IS74Wifi · требуется действие пользователя");
        }

        if (active.Length == 0)
        {
            return new TrayIconPresentation(
                TrayIconSemanticState.Question,
                "IS74Wifi · нет активного сетевого пути");
        }

        if (active.Any(path => path.Status == PathAuthorizationStatus.Captive))
        {
            return new TrayIconPresentation(
                TrayIconSemanticState.Warning,
                "IS74Wifi · требуется авторизация Wi-Fi");
        }

        if (active.Any(path => path.Status is PathAuthorizationStatus.Unreachable or PathAuthorizationStatus.Ambiguous))
        {
            return new TrayIconPresentation(
                TrayIconSemanticState.Warning,
                "IS74Wifi · есть проблема с сетевым путём");
        }

        if (active.Any(path => path.Status == PathAuthorizationStatus.Unknown))
        {
            return new TrayIconPresentation(
                TrayIconSemanticState.Question,
                "IS74Wifi · состояние сети ещё не определено");
        }

        return new TrayIconPresentation(
            TrayIconSemanticState.Normal,
            "IS74Wifi · сеть работает нормально");
    }
}

/// <summary>
/// Persistent notification-area icon for the background agent. The implementation
/// intentionally stays on the same Shell_NotifyIcon/Win32 path as notifications,
/// so NativeAOT does not need WinForms/WPF just to own a tray icon.
/// </summary>
internal sealed partial class WindowsTrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 75; // WM_APP + 75
    private const uint RefreshMessage = 0x8000 + 76;  // WM_APP + 76
    private const uint IconId = 74;
    private static readonly WindowProcedure WindowProcedureDelegate = TrayWndProc;
    private static readonly IntPtr WindowProcedurePointer = Marshal.GetFunctionPointerForDelegate(WindowProcedureDelegate);
    private static readonly ConcurrentDictionary<IntPtr, WindowsTrayIconService> Windows = new();

    private readonly AppPaths paths;
    private readonly DiagnosticLogger logger;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly object stateGate = new();
    private readonly Dictionary<TrayIconSemanticState, IntPtr> icons = [];
    private TrayIconPresentation desired = TrayIconStateResolver.Working;
    private IntPtr window;
    private IntPtr previousProcedure;
    private bool iconAdded;
    private bool disposed;
    private uint taskbarCreatedMessage;

    private WindowsTrayIconService(AppPaths paths, DiagnosticLogger logger)
    {
        this.paths = paths;
        this.logger = logger;
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "IS74Wifi tray"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal static WindowsTrayIconService? TryCreate(
        AppPaths paths,
        SettingsStore settingsStore,
        DiagnosticLogger logger)
    {
        if (!OperatingSystem.IsWindows() || !settingsStore.Load().ShowTrayIcon)
        {
            return null;
        }

        var service = new WindowsTrayIconService(paths, logger);
        if (!service.ready.Wait(TimeSpan.FromSeconds(3)) || service.window == IntPtr.Zero)
        {
            service.Dispose();
            return null;
        }

        return service;
    }

    internal void SetState(TrayIconPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        lock (stateGate)
        {
            desired = presentation;
        }

        var target = window;
        if (target != IntPtr.Zero)
        {
            _ = NativeMethods.PostMessageW(target, RefreshMessage, UIntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        var target = window;
        if (target != IntPtr.Zero)
        {
            _ = NativeMethods.PostMessageW(target, NativeMethods.WmClose, UIntPtr.Zero, IntPtr.Zero);
        }
        if (thread.IsAlive && Thread.CurrentThread != thread)
        {
            _ = thread.Join(TimeSpan.FromSeconds(3));
        }
        ready.Dispose();
    }

    private void Run()
    {
        try
        {
            LoadIcons();
            taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
            window = NativeMethods.CreateWindowExW(
                0,
                "STATIC",
                "IS74WifiTray",
                0,
                0,
                0,
                0,
                0,
                IntPtr.Zero, // hidden top-level window receives TaskbarCreated broadcast
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (window == IntPtr.Zero)
            {
                logger.Write(DiagnosticLevel.Warn, "tray.unavailable reason=create-window");
                return;
            }

            previousProcedure = NativeMethods.SetWindowLongPtrW(window, NativeMethods.GwlpWndProc, WindowProcedurePointer);
            if (previousProcedure == IntPtr.Zero)
            {
                logger.Write(DiagnosticLevel.Warn, "tray.unavailable reason=subclass-window");
                _ = NativeMethods.DestroyWindow(window);
                window = IntPtr.Zero;
                return;
            }

            Windows[window] = this;
            ApplyDesiredState(forceAdd: true);
            ready.Set();

            while (NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = NativeMethods.TranslateMessage(ref message);
                _ = NativeMethods.DispatchMessageW(ref message);
            }
        }
        catch (Exception ex)
        {
            logger.Write(DiagnosticLevel.Warn,
                $"tray.unavailable error={ex.GetType().Name}:{ex.Message}");
        }
        finally
        {
            ready.Set();
            CleanupNativeResources();
        }
    }

    private void LoadIcons()
    {
        Directory.CreateDirectory(paths.TrayIconDirectory);
        foreach (var state in Enum.GetValues<TrayIconSemanticState>())
        {
            var name = state.ToString().ToLowerInvariant();
            var resourceName = $"IS74Wifi.Tray.{name}.ico";
            var target = Path.Combine(paths.TrayIconDirectory, $"IS74Wifi-tray-{name}.ico");
            using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded tray icon is missing: {resourceName}");
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                source.CopyTo(output);
            }

            var icon = NativeMethods.LoadImageW(
                IntPtr.Zero,
                target,
                NativeMethods.ImageIcon,
                0,
                0,
                NativeMethods.LrLoadFromFile | NativeMethods.LrDefaultSize);
            if (icon == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Windows failed to load tray icon: {name}");
            }
            icons[state] = icon;
        }
    }

    private unsafe void ApplyDesiredState(bool forceAdd = false)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        TrayIconPresentation presentation;
        lock (stateGate)
        {
            presentation = desired;
        }

        var data = new NotifyIconData
        {
            cbSize = (uint)sizeof(NotifyIconData),
            hWnd = window,
            uID = IconId,
            uFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = icons[presentation.State]
        };
        SetTip(ref data, presentation.Tip);

        if (!iconAdded || forceAdd)
        {
            if (iconAdded)
            {
                _ = NativeMethods.ShellNotifyIconW(NativeMethods.NimDelete, ref data);
                iconAdded = false;
            }
            if (NativeMethods.ShellNotifyIconW(NativeMethods.NimAdd, ref data) == 0)
            {
                logger.Write(DiagnosticLevel.Warn, "tray.unavailable reason=shell-add");
                return;
            }
            iconAdded = true;
            data.uTimeoutOrVersion = NativeMethods.NotifyIconVersion4;
            _ = NativeMethods.ShellNotifyIconW(NativeMethods.NimSetVersion, ref data);
            return;
        }

        _ = NativeMethods.ShellNotifyIconW(NativeMethods.NimModify, ref data);
    }

    private void CleanupNativeResources()
    {
        var target = window;
        if (target != IntPtr.Zero)
        {
            unsafe
            {
                var data = new NotifyIconData
                {
                    cbSize = (uint)sizeof(NotifyIconData),
                    hWnd = target,
                    uID = IconId
                };
                if (iconAdded)
                {
                    _ = NativeMethods.ShellNotifyIconW(NativeMethods.NimDelete, ref data);
                    iconAdded = false;
                }
            }

            Windows.TryRemove(target, out _);
            if (previousProcedure != IntPtr.Zero)
            {
                _ = NativeMethods.SetWindowLongPtrW(target, NativeMethods.GwlpWndProc, previousProcedure);
                previousProcedure = IntPtr.Zero;
            }
            _ = NativeMethods.DestroyWindow(target);
            window = IntPtr.Zero;
        }

        foreach (var icon in icons.Values)
        {
            if (icon != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyIcon(icon);
            }
        }
        icons.Clear();
    }

    private static IntPtr TrayWndProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (!Windows.TryGetValue(window, out var service))
        {
            return NativeMethods.DefWindowProcW(window, message, wParam, lParam);
        }

        try
        {
            if (message == RefreshMessage)
            {
                service.ApplyDesiredState();
                return IntPtr.Zero;
            }

            if (message == service.taskbarCreatedMessage && message != 0)
            {
                service.iconAdded = false;
                service.ApplyDesiredState(forceAdd: true);
                return IntPtr.Zero;
            }

            if (message == CallbackMessage && (unchecked((uint)lParam.ToInt64()) & 0xFFFF) == NativeMethods.WmLButtonDblClk)
            {
                LaunchMenu();
                return IntPtr.Zero;
            }

            if (message == NativeMethods.WmClose)
            {
                service.CleanupNativeResources();
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            service.logger.Write(DiagnosticLevel.Warn,
                $"tray.callback error={ex.GetType().Name}:{ex.Message}");
        }

        return service.previousProcedure != IntPtr.Zero
            ? NativeMethods.CallWindowProcW(service.previousProcedure, window, message, wParam, lParam)
            : NativeMethods.DefWindowProcW(window, message, wParam, lParam);
    }

    private static void LaunchMenu()
    {
        if (WindowsNotificationService.IsInteractiveSessionRunning())
        {
            return;
        }

        var installation = new ProgramInstallation();
        var executable = installation.IsInstalled
            ? installation.ExecutablePath
            : Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("menu");
        _ = Process.Start(startInfo);
    }

    private static unsafe void SetTip(ref NotifyIconData data, string value)
    {
        fixed (char* buffer = data.szTip)
        {
            var length = Math.Min(value.Length, 127);
            for (var i = 0; i < length; i++)
            {
                buffer[i] = value[i];
            }
            buffer[length] = '\0';
            for (var i = length + 1; i < 128; i++)
            {
                buffer[i] = '\0';
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

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
        internal const uint NimSetVersion = 0x00000004;
        internal const uint NifMessage = 0x00000001;
        internal const uint NifIcon = 0x00000002;
        internal const uint NifTip = 0x00000004;
        internal const uint NotifyIconVersion4 = 4;
        internal const uint WmClose = 0x0010;
        internal const uint WmLButtonDblClk = 0x0203;
        internal const uint ImageIcon = 1;
        internal const uint LrLoadFromFile = 0x0010;
        internal const uint LrDefaultSize = 0x0040;
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

        [LibraryImport("user32.dll", EntryPoint = "DestroyIcon")]
        internal static partial int DestroyIcon(IntPtr icon);

        [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr LoadImageW(
            IntPtr instance,
            string name,
            uint type,
            int desiredWidth,
            int desiredHeight,
            uint loadFlags);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static partial IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);

        [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
        internal static partial IntPtr CallWindowProcW(
            IntPtr previousProcedure,
            IntPtr window,
            uint message,
            UIntPtr wParam,
            IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static partial IntPtr DefWindowProcW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
        internal static partial int GetMessageW(
            out NativeMessage message,
            IntPtr window,
            uint messageFilterMin,
            uint messageFilterMax);

        [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
        internal static partial int TranslateMessage(ref NativeMessage message);

        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static partial IntPtr DispatchMessageW(ref NativeMessage message);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
        internal static partial void PostQuitMessage(int exitCode);

        [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial uint RegisterWindowMessageW(string message);
    }
}
