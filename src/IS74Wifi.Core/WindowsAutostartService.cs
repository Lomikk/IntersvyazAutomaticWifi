using Microsoft.Win32;
using System.Diagnostics;

namespace IS74Wifi.Core;

public sealed class WindowsAutostartService(string valueName = "IS74Wifi")
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public string? GetCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void Enable(string executablePath, bool startNow = true)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path is required.", nameof(executablePath));
        }

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("IS74Wifi executable was not found.", fullPath);
        }

        AgentProcessControl.StopAgentOrThrow();

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("Could not open the current-user Run registry key.");
        key.SetValue(valueName, BuildCommand(fullPath), RegistryValueKind.String);

        if (startNow)
        {
            var startInfo = new ProcessStartInfo(fullPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("agent");
            _ = Process.Start(startInfo);
        }
    }

    public void Disable()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
        {
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }

        AgentProcessControl.StopAgentOrThrow();
    }

    public static string BuildCommand(string executablePath) => $"\"{Path.GetFullPath(executablePath)}\" agent";
}
