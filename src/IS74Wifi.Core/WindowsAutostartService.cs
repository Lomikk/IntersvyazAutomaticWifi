using Microsoft.Win32;
using System.Diagnostics;

namespace IS74Wifi.Core;

public enum AutostartRegistrationState
{
    Disabled,
    Enabled,
    Stale
}

public sealed class WindowsAutostartService(string valueName = "IS74Wifi")
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabledFor(string executablePath) =>
        GetRegistrationState(executablePath) == AutostartRegistrationState.Enabled;

    public AutostartRegistrationState GetRegistrationState(string executablePath)
    {
        var command = GetCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            return AutostartRegistrationState.Disabled;
        }

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath) || !CommandMatchesExecutable(command, fullPath))
        {
            return AutostartRegistrationState.Stale;
        }

        return AutostartRegistrationState.Enabled;
    }

    public bool RemoveIfStale(string executablePath)
    {
        if (GetRegistrationState(executablePath) != AutostartRegistrationState.Stale)
        {
            return false;
        }

        DeleteRunValue();
        return true;
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
        DeleteRunValue();
        AgentProcessControl.StopAgentOrThrow();
    }

    private void DeleteRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public static string BuildCommand(string executablePath) => $"\"{Path.GetFullPath(executablePath)}\" agent";

    public static bool CommandMatchesExecutable(string? command, string executablePath) =>
        !string.IsNullOrWhiteSpace(command) &&
        string.Equals(command.Trim(), BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase);
}
