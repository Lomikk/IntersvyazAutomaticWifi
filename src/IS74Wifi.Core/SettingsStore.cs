namespace IS74Wifi.Core;

public sealed class SettingsStore(AppPaths paths, JsonFileStore json)
{
    public AppSettings Load()
    {
        paths.EnsureDirectories();
        var settings = json.Read(paths.SettingsFile, PersistenceJsonContext.Default.AppSettings) ?? new AppSettings();
        if (settings.ExpiryWatchPolicyVersion == 0)
        {
            // Do not silently keep the old production defaults on installed clients.
            // Retain deliberate custom values (including a custom 10 s window).
            var oldDefaults = settings.GuardWindowSeconds == 10 &&
                              settings.GuardProbeIntervalMilliseconds == 250;
            settings = settings with
            {
                GuardWindowSeconds = oldDefaults ? 30 : settings.GuardWindowSeconds,
                GuardProbeIntervalMilliseconds = oldDefaults ? 500 : settings.GuardProbeIntervalMilliseconds,
                ExpiryWatchPolicyVersion = 1
            };
            json.Write(paths.SettingsFile, settings, PersistenceJsonContext.Default.AppSettings);
        }
        return settings;
    }

    public void Save(AppSettings settings)
    {
        paths.EnsureDirectories();
        json.Write(paths.SettingsFile, settings, PersistenceJsonContext.Default.AppSettings);
    }
}
