using IS74Wifi.Core;

namespace IS74Wifi.App;

internal sealed record InteractiveStatusSnapshot(
    bool Installed,
    bool Registered,
    bool? InternetAvailable,
    WifiNetworkState WifiNetwork,
    string? WifiSsid,
    DateTimeOffset? AuthorizationExpectedExpiryUtc,
    bool AuthorizationAlreadyActive,
    bool NetworkCheckIgnored,
    AnonymousStatisticsConsent AnonymousStatisticsConsent,
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
    OpenSettings,
    OpenMaintenance,
    Back,
    Register,
    Connect,
    EnableAutomaticAuthorization,
    DisableAutomaticAuthorization,
    ToggleNetworkCheck,
    ToggleAnonymousStatistics,
    CycleNotifications,
    ShowDetailedStatus,
    ResetRegistration,
    Uninstall,
    OpenLogs,
    Update,
    SpeedTools,
    Exit
}
