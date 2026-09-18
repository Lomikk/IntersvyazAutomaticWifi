namespace IS74Wifi.Core;

public enum DiagnosticLevel
{
    Info,
    Warn,
    Error
}

public sealed class DiagnosticLogger(
    AppPaths paths,
    long maxBytes = 1024 * 1024,
    int retentionFiles = 5)
{
    private readonly object gate = new();

    public string LogPath => paths.DiagnosticLogFile;

    public void Write(DiagnosticLevel level, string message)
    {
        try
        {
            lock (gate)
            {
                paths.EnsureDirectories();
                RotateIfNeeded();
                var levelText = level.ToString().ToUpperInvariant();
                var safeMessage = LogRedactor.Redact(message);
                var line = $"{DateTimeOffset.UtcNow:O} [{levelText}] [pid={Environment.ProcessId}] {safeMessage}{Environment.NewLine}";
                File.AppendAllText(paths.DiagnosticLogFile, line);
            }
        }
        catch
        {
            // Diagnostics must never break authorization.
        }
    }

    private void RotateIfNeeded()
    {
        var logPath = paths.DiagnosticLogFile;
        if (!File.Exists(logPath) || new FileInfo(logPath).Length < maxBytes)
        {
            return;
        }

        var maxBackup = Math.Max(1, retentionFiles - 1);
        var oldest = Path.Combine(paths.LogDirectory, $"diagnostic.{maxBackup}.log");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = maxBackup - 1; i >= 1; i--)
        {
            var source = Path.Combine(paths.LogDirectory, $"diagnostic.{i}.log");
            var target = Path.Combine(paths.LogDirectory, $"diagnostic.{i + 1}.log");
            if (File.Exists(source))
            {
                File.Move(source, target, overwrite: true);
            }
        }

        File.Move(logPath, Path.Combine(paths.LogDirectory, "diagnostic.1.log"), overwrite: true);
    }
}
