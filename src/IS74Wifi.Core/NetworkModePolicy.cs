namespace IS74Wifi.Core;

public static class NetworkModePolicy
{
    public static bool Allows(NetworkMode mode, NetworkPathSnapshot path) =>
        mode != NetworkMode.Simple || path.IsWifi && SsidPolicy.IsTarget(path.Ssid);

    public static string DisplayName(NetworkMode mode) =>
        mode == NetworkMode.Simple ? "простой · Campus Wi-Fi" : "продвинутый · прежняя логика";
}
