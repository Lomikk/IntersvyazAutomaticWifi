namespace IS74Wifi.App;

internal sealed record InteractiveStatusSnapshot(
    bool Installed,
    bool Registered,
    bool? InternetAvailable,
    bool WifiAuthorizationActive,
    bool AutomaticAuthorizationEnabled,
    bool AgentRunning,
    string NotificationMode,
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
    CycleNotifications,
    ShowDetailedStatus,
    ResetRegistration,
    Uninstall,
    OpenLogs,
    Update,
    SpeedTools,
    Exit
}
