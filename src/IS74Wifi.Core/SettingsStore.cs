namespace IS74Wifi.Core;

public sealed class SettingsStore(AppPaths paths, JsonFileStore json)
{
    public AppSettings Load()
    {
        paths.EnsureDirectories();
        var settings = json.Read<AppSettings>(paths.SettingsFile) ?? new AppSettings();
        if (!File.Exists(paths.SettingsFile))
        {
            json.Write(paths.SettingsFile, settings);
        }
        return settings;
    }
}
