namespace IS74Wifi.Core;

public static class SsidPolicy
{
    public static bool IsTarget(string? ssid) =>
        !string.IsNullOrWhiteSpace(ssid) &&
        ssid.StartsWith(ProtocolContract.CampusSsidPrefix, StringComparison.OrdinalIgnoreCase);
}
