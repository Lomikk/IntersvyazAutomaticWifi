namespace IS74Wifi.Core;

public sealed class SettingsStore(AppPaths paths, JsonFileStore json)
{
    public AppSettings Load()
    {
        paths.EnsureDirectories();
        var settings = json.Read(paths.SettingsFile, PersistenceJsonContext.Default.AppSettings) ?? new AppSettings();
        if (!File.Exists(paths.SettingsFile))
        {
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
