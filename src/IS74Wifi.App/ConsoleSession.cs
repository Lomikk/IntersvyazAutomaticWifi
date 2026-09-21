using System.Runtime.InteropServices;
using System.Text;

namespace IS74Wifi.App;

internal static partial class ConsoleSession
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const uint Utf8CodePage = 65001;
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    public static bool SupportsVirtualTerminal { get; private set; }

    public static void EnsureInteractiveConsole()
    {
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            SupportsVirtualTerminal = TryEnableVirtualTerminal();
            return;
        }

        var allocatedConsole = false;
        if (AttachConsole(AttachParentProcess) == 0)
        {
            if (AllocConsole() != 0)
            {
                allocatedConsole = true;
            }
        }

        // WinExe starts without initialized console streams. Rebind them after
        // attaching/allocating a console so the same binary can provide a CLI.
        // A console allocated by us can safely use UTF-8. When attaching to a
        // parent console, preserve its code pages instead of changing them for
        // the caller.
        Encoding inputEncoding;
        Encoding outputEncoding;
        if (allocatedConsole)
        {
            _ = SetConsoleCP(Utf8CodePage);
            _ = SetConsoleOutputCP(Utf8CodePage);
            inputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            outputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        else
        {
            inputEncoding = Console.InputEncoding;
            outputEncoding = Console.OutputEncoding;
        }

        Console.SetIn(new StreamReader(Console.OpenStandardInput(), inputEncoding));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), outputEncoding) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), outputEncoding) { AutoFlush = true });
        SupportsVirtualTerminal = TryEnableVirtualTerminal();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AllocConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetConsoleCP(uint codePageId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetConsoleOutputCP(uint codePageId);

    private static bool TryEnableVirtualTerminal()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            if (GetConsoleMode(handle, out var mode) == 0) return false;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing) != 0;
        }
        catch
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetConsoleMode(IntPtr consoleHandle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetConsoleMode(IntPtr consoleHandle, uint mode);
}
