using System.Text.Json.Serialization;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal sealed record AgentNetworkAdapterOption(
    string Id,
    string Name,
    string? Ssid,
    bool CanConnect,
    bool LooksVirtual);

internal sealed record AgentNetworkAdapterList(AgentNetworkAdapterOption[] Adapters);

internal sealed record AgentNetworkPathSnapshot(
    string Name,
    string? Ssid,
    int InterfaceIndex,
    string? SourceIPv4,
    string? GatewayIPv4,
    PathAuthorizationStatus Status,
    bool Preferred,
    DateTimeOffset? ExpectedExpiryUtc,
    string? LastResult,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? NextCheckUtc,
    bool CheckOnConnectivityEvent,
    DateTimeOffset? LastSuccessfulAuthUtc,
    bool UserActionRequired,
    bool AuthorizationWindowApproximate);

internal sealed record AgentNetworkSnapshot(
    DateTimeOffset CapturedAtUtc,
    bool? InternetAvailable,
    bool VpnActive,
    string? WifiSsid,
    AgentNetworkPathSnapshot[] Paths)
{
    internal static AgentNetworkSnapshot Empty { get; } = new(
        DateTimeOffset.MinValue,
        InternetAvailable: null,
        VpnActive: false,
        WifiSsid: null,
        Paths: []);

    [JsonIgnore]
    internal int PathCount => Paths.Length;

    [JsonIgnore]
    internal int CaptivePathCount => Paths.Count(path => path.Status == PathAuthorizationStatus.Captive);
}

internal sealed class AgentNetworkSnapshotStore(AppPaths paths, JsonFileStore json)
{
    internal AgentNetworkSnapshot? Load() =>
        json.Read(paths.AgentNetworkSnapshotFile, AppJsonContext.Default.AgentNetworkSnapshot);

    internal void Save(AgentNetworkSnapshot snapshot) =>
        json.Write(paths.AgentNetworkSnapshotFile, snapshot, AppJsonContext.Default.AgentNetworkSnapshot);
}
