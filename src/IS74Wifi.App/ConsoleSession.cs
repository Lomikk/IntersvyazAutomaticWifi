using System.Runtime.InteropServices;
using System.Text;

namespace IS74Wifi.App;

internal static partial class ConsoleSession
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const uint Utf8CodePage = 65001;

    public static void EnsureInteractiveConsole()
    {
        if (GetConsoleWindow() != IntPtr.Zero)
        {
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
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AllocConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetConsoleCP(uint codePageId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetConsoleOutputCP(uint codePageId);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();
}
