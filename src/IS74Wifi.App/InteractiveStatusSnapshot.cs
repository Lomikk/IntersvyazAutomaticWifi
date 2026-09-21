namespace IS74Wifi.App;

internal sealed record InteractiveStatusSnapshot(
    bool Installed,
    bool Registered,
    bool? InternetAvailable,
    bool WifiAuthorizationActive,
    bool AutomaticAuthorizationEnabled,
    bool AgentRunning,
    string MaskedPhone,
    string ApiSessionEnd,
    string LastResult,
    string Version);

internal enum InteractiveMenuAction
{
    None,
    Register,
    Connect,
    EnableAutomaticAuthorization,
    DisableAutomaticAuthorization,
    ShowDetailedStatus,
    ResetRegistration,
    Uninstall,
    OpenLogs,
    Update,
    Exit
}
