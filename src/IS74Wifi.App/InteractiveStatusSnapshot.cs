namespace IS74Wifi.App;

internal sealed record InteractiveStatusSnapshot(
    bool Installed,
    bool Registered,
    bool? InternetAvailable,
    WifiNetworkState WifiNetwork,
    string? WifiSsid,
    WifiAuthorizationState WifiAuthorization,
    bool AuthorizationWithoutCampusSsidAllowed,
    bool AutomaticAuthorizationEnabled,
    bool AgentRunning,
    string NotificationMode,
    string MaskedPhone,
    string ApiSessionEnd,
    string LastResult,
    string Version);

internal enum WifiNetworkState
{
    Unknown,
    Campus,
    Other
}

internal enum WifiAuthorizationState
{
    Unknown,
    Active,
    Expired
}

internal enum InteractiveMenuAction
{
    None,
    Register,
    Connect,
    EnableAutomaticAuthorization,
    DisableAutomaticAuthorization,
    ToggleAuthorizationWithoutCampusSsid,
    CycleNotifications,
    ShowDetailedStatus,
    ResetRegistration,
    Uninstall,
    OpenLogs,
    Update,
    SpeedTools,
    Exit
}
