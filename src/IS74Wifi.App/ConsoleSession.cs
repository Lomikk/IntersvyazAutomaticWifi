using System.Runtime.InteropServices;

namespace IS74Wifi.App;

internal static partial class ConsoleSession
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void EnsureInteractiveConsole()
    {
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            return;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            _ = AllocConsole();
        }

        // WinExe starts without initialized console streams. Rebind them after
        // attaching/allocating a console so the same binary can provide a CLI.
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();
}
