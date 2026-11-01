namespace IS74Wifi.Core;

public sealed class SettingsStore(AppPaths paths, JsonFileStore json)
{
    public AppSettings Load()
    {
        paths.EnsureDirectories();
        var settings = json.Read(paths.SettingsFile, PersistenceJsonContext.Default.AppSettings);
        if (settings is not null)
        {
            if (settings.AuthorizationConnections is null || settings.NetworkMode is not null || settings.DirectNetworkAdapterId is not null)
            {
                settings = AuthorizationConnectionSelection.Migrate(settings);
                Save(settings);
            }
            return settings with { AuthorizationConnections = settings.AuthorizationConnections!.Normalize() };
        }

        settings = AuthorizationConnectionSelection.Migrate(new AppSettings { InitialSetupCompleted = false });
        Save(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        paths.EnsureDirectories();
        json.Write(paths.SettingsFile, AuthorizationConnectionSelection.Migrate(settings), PersistenceJsonContext.Default.AppSettings);
    }
}
