namespace IS74Wifi.Core;

public sealed class RuntimeStateStore(AppPaths paths, JsonFileStore json)
{
    public RuntimeState Load() => json.Read<RuntimeState>(paths.RuntimeStateFile) ?? new RuntimeState();

    public void Save(RuntimeState state)
    {
        paths.EnsureDirectories();
        json.Write(paths.RuntimeStateFile, state);
    }
}
