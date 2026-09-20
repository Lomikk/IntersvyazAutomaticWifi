using System.Diagnostics;

namespace IS74Wifi.Core;

public static class AgentProcessControl
{
    public const string AgentGateName = @"Local\IS74Wifi.CSharp.Agent";
    public const string StopEventName = @"Local\IS74Wifi.CSharp.AgentStop";

    private const string PidFileName = "agent.pid";

    public static EventWaitHandle CreateStopEvent()
    {
        var handle = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        handle.Reset();
        return handle;
    }

    public static void SignalStop()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(StopEventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    public static bool IsAgentRunning()
    {
        using var lease = NamedSemaphoreLease.TryAcquire(AgentGateName);
        return lease is null;
    }

    public static bool WaitForAgentExit(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            using var lease = NamedSemaphoreLease.TryAcquire(AgentGateName);
            if (lease is not null)
            {
                return true;
            }

            Thread.Sleep(100);
        } while (DateTimeOffset.UtcNow < deadline);

        return false;
    }

    public static string GetPidFilePath(string? localAppDataOverride = null)
    {
        var localAppData = localAppDataOverride ??
                           Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "IS74Wifi", PidFileName);
    }

    public static void RegisterCurrentAgentProcess(string? localAppDataOverride = null)
    {
        var path = GetPidFilePath(localAppDataOverride);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var executable = Environment.ProcessPath ?? string.Empty;
        var temp = path + ".tmp";
        File.WriteAllText(
            temp,
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            Environment.NewLine +
            executable +
            Environment.NewLine);
        File.Move(temp, path, overwrite: true);
    }

    public static void ClearCurrentAgentProcess(string? localAppDataOverride = null)
    {
        var path = GetPidFilePath(localAppDataOverride);
        try
        {
            var record = ReadAgentRecord(path);
            if (record is null || record.Value.ProcessId == Environment.ProcessId)
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void StopAgentOrThrow(
        TimeSpan? gracefulTimeout = null,
        TimeSpan? forcedTimeout = null)
    {
        var graceful = gracefulTimeout ?? TimeSpan.FromSeconds(5);
        var forced = forcedTimeout ?? TimeSpan.FromSeconds(5);

        SignalStop();
        if (WaitForAgentExit(graceful))
        {
            ClearPidFileIfAgentStopped();
            return;
        }

        if (!TryTerminateRecordedInstalledAgent())
        {
            throw new InvalidOperationException(
                "Фоновый агент IS74Wifi не остановился и его процесс не удалось безопасно определить.");
        }

        if (!WaitForAgentExit(forced))
        {
            throw new InvalidOperationException(
                "Фоновый агент IS74Wifi не завершился даже после принудительной остановки.");
        }

        ClearPidFileIfAgentStopped();
    }

    private static bool TryTerminateRecordedInstalledAgent()
    {
        var record = ReadAgentRecord(GetPidFilePath());
        if (record is null || record.Value.ProcessId <= 0 || record.Value.ProcessId == Environment.ProcessId)
        {
            return false;
        }

        var installation = new ProgramInstallation();
        if (!ProgramInstallation.PathsEqual(record.Value.ExecutablePath, installation.ExecutablePath))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(record.Value.ProcessId);
            string? actualExecutable;
            try
            {
                actualExecutable = process.MainModule?.FileName;
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(actualExecutable) ||
                !ProgramInstallation.PathsEqual(actualExecutable, installation.ExecutablePath))
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void ClearPidFileIfAgentStopped()
    {
        if (IsAgentRunning())
        {
            return;
        }

        try
        {
            File.Delete(GetPidFilePath());
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static AgentProcessRecord? ReadAgentRecord(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var lines = File.ReadAllLines(path);
            if (lines.Length < 2 ||
                !int.TryParse(
                    lines[0].Trim(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var processId) ||
                processId <= 0 ||
                string.IsNullOrWhiteSpace(lines[1]))
            {
                return null;
            }

            return new AgentProcessRecord(processId, Path.GetFullPath(lines[1].Trim()));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private readonly record struct AgentProcessRecord(int ProcessId, string ExecutablePath);
}
