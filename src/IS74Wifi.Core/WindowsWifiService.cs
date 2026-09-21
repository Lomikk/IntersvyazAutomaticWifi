using Windows.Networking.Connectivity;

namespace IS74Wifi.Core;

public static class WindowsWifiService
{
    public static IReadOnlyList<string> GetConnectedSsids()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                if (profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
                {
                    continue;
                }

                var ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                if (!string.IsNullOrWhiteSpace(ssid))
                {
                    result.Add(ssid);
                }
            }
        }
        catch
        {
            // Wi-Fi classification is advisory. A missing WinRT projection or
            // OS policy must not prevent authorization or status rendering.
            return [];
        }

        return result.ToArray();
    }

    public static bool IsTargetWifiConnected() => GetConnectedSsids().Any(SsidPolicy.IsTarget);

    public static SpeedTestRadioSnapshot GetSpeedTestRadioSnapshot()
    {
        var ssids = GetConnectedSsids();
        var selected = ssids.FirstOrDefault(SsidPolicy.IsTarget) ?? ssids.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return new SpeedTestRadioSnapshot();
        }

        return new SpeedTestRadioSnapshot(
            WifiSignalBucket: "unknown",
            WifiBand: "unknown",
            ConnectionType: SsidPolicy.IsTarget(selected) ? "campus_wifi" : "other_wifi");
    }

}
