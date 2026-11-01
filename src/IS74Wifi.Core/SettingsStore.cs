namespace IS74Wifi.Core;

public sealed class SettingsStore(AppPaths paths, JsonFileStore json)
{
    public AppSettings Load()
    {
        paths.EnsureDirectories();
        var settings = json.Read(paths.SettingsFile, PersistenceJsonContext.Default.AppSettings);
        if (settings is not null)
        {
            return settings;
        }

        settings = new AppSettings { NetworkMode = NetworkMode.Simple };
        json.Write(paths.SettingsFile, settings, PersistenceJsonContext.Default.AppSettings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        paths.EnsureDirectories();
        json.Write(paths.SettingsFile, settings, PersistenceJsonContext.Default.AppSettings);
    }
}
