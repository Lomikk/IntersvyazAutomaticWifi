namespace IS74Wifi.Core;

/// <summary>Adapts the existing authorization-cycle state machine to one immutable physical path.</summary>
public sealed class PathRuntimeStateStore(
    PathAuthorizationStateStore store,
    NetworkPathSnapshot path) : IRuntimeStateStore
{
    public RuntimeState Load() => store.LoadRuntime(path);

    public void Save(RuntimeState state) => store.SaveRuntime(path, state);
}
