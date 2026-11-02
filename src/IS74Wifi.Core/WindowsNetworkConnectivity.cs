using Windows.Networking.Connectivity;

namespace IS74Wifi.Core;

/// <summary>Local Windows profile levels; no probes and no global/VPN Internet fallback.</summary>
public static class WindowsNetworkConnectivity
{
    public static IReadOnlyDictionary<string, int>? ReadAdapterLevels()
    {
        try
        {
            var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                if (profile.NetworkAdapter is not { } adapter) continue;
                var key = adapter.NetworkAdapterId.ToString("D");
                var level = (int)profile.GetNetworkConnectivityLevel();
                levels[key] = Math.Max(levels.GetValueOrDefault(key), level);
            }
            return levels;
        }
        catch { return null; } // Unknown is distinct from a missing/disconnected profile.
    }
}
