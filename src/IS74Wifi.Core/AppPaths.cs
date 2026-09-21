namespace IS74Wifi.Core;

public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IS74Wifi");
    }

    public string Root { get; }
    public string DeviceIdFile => Path.Combine(Root, "device-id.txt");
    public string SecretsFile => Path.Combine(Root, "secrets.dpapi");
    public string SessionMetaFile => Path.Combine(Root, "session-meta.json");
    public string RuntimeStateFile => Path.Combine(Root, "runtime-state.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string DeviceMetadataFile => Path.Combine(Root, "device-metadata.json");
    public string DnsCacheFile => Path.Combine(Root, "dns-cache.json");
    public string LogDirectory => Path.Combine(Root, "logs");
    public string DiagnosticLogFile => Path.Combine(LogDirectory, "diagnostic.log");
    public string TelemetryDirectory => Path.Combine(Root, "telemetry");
    public string TelemetryPendingDirectory => Path.Combine(TelemetryDirectory, "pending");
    public string TelemetryRejectedDirectory => Path.Combine(TelemetryDirectory, "rejected");
    public string TelemetryInstallIdFile => Path.Combine(TelemetryDirectory, "install-id.txt");
    public string TelemetryUploadStateFile => Path.Combine(TelemetryDirectory, "upload-state.json");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(TelemetryDirectory);
        Directory.CreateDirectory(TelemetryPendingDirectory);
    }
}
