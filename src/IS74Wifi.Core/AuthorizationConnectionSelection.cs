using System.Text;

namespace IS74Wifi.Core;

public sealed record SelectedNetworkAdapter(string Id, string Name);

/// <summary>User permission to use a Wi-Fi SSID or a wired physical adapter, never proof of a portal.</summary>
public sealed record AuthorizationConnectionSelection
{
    public const string CampusWifi = "Campus Wi-Fi";
    public string[] WifiSsids { get; init; } = [CampusWifi];
    public SelectedNetworkAdapter[] Adapters { get; init; } = [];

    public static bool IsValidSsid(string? ssid) => !string.IsNullOrWhiteSpace(ssid) &&
        !ssid.Any(char.IsControl) && Encoding.UTF8.GetByteCount(ssid) <= 32;

    public AuthorizationConnectionSelection Normalize() => this with
    {
        WifiSsids = (WifiSsids ?? []).Where(IsValidSsid).Distinct(StringComparer.Ordinal).ToArray(),
        Adapters = (Adapters ?? []).Where(adapter => adapter is not null &&
                !string.IsNullOrWhiteSpace(adapter.Id) &&
                !string.Equals(adapter.Id, PhysicalAdapterSelection.SystemRoute, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase).ToArray()
    };

    public bool Allows(NetworkPathSnapshot path) => Allows(path.IsWifi, path.Ssid, path.AdapterId);

    public bool Allows(bool isWifi, string? currentSsid, string adapterId) => isWifi
        ? WifiSsids.Any(ssid => string.Equals(ssid, currentSsid, StringComparison.Ordinal) ||
            ssid == CampusWifi && SsidPolicy.IsTarget(currentSsid))
        : Adapters.Any(adapter => string.Equals(adapter.Id, adapterId, StringComparison.OrdinalIgnoreCase));

    public string Describe() => $"Wi-Fi: {WifiSsids.Length}, USB/Ethernet: {Adapters.Length}";

    public static AppSettings Migrate(AppSettings settings)
    {
        var selection = settings.AuthorizationConnections;
        if (selection is null)
        {
            selection = new AuthorizationConnectionSelection();
            if (settings.NetworkMode != global::IS74Wifi.Core.NetworkMode.Simple &&
                !string.IsNullOrWhiteSpace(settings.DirectNetworkAdapterId) &&
                !string.Equals(settings.DirectNetworkAdapterId, PhysicalAdapterSelection.SystemRoute, StringComparison.OrdinalIgnoreCase))
                selection = selection with { Adapters = [new(settings.DirectNetworkAdapterId, settings.DirectNetworkAdapterId)] };
        }
        return settings with { AuthorizationConnections = selection.Normalize(), NetworkMode = null, DirectNetworkAdapterId = null };
    }
}
