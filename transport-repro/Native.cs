using System.Runtime.InteropServices;

internal static partial class Native
{
    [LibraryImport("kernel32.dll")]
    internal static partial nint GetConsoleWindow();
}
