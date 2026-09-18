namespace IS74Wifi.Core;

public sealed class RuntimeStateStore(AppPaths paths, JsonFileStore json)
{
    public RuntimeState Load() => json.Read(paths.RuntimeStateFile, PersistenceJsonContext.Default.RuntimeState) ?? new RuntimeState();

    public void Save(RuntimeState state)
    {
        paths.EnsureDirectories();
        json.Write(paths.RuntimeStateFile, state, PersistenceJsonContext.Default.RuntimeState);
    }
}
