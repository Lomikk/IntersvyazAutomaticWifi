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
    string DirectNetworkMode,
    AnonymousStatisticsConsent AnonymousStatisticsConsent,
    bool AutomaticUpdates,
    bool IncludePrereleaseUpdates,
    string? AvailableUpdateVersion,
    DateTimeOffset? LastUpdateCheckUtc,
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
    OpenUpdates,
    Back,
    Register,
    Connect,
    EnableAutomaticAuthorization,
    DisableAutomaticAuthorization,
    ToggleNetworkCheck,
    ChooseDirectNetworkAdapter,
    DiagnoseDirectNetwork,
    ToggleAnonymousStatistics,
    ToggleAutomaticUpdates,
    TogglePrereleaseUpdates,
    CheckUpdates,
    CycleNotifications,
    ShowDetailedStatus,
    ResetRegistration,
    Uninstall,
    OpenLogs,
    Update,
    OpenUpdateReleasePage,
    SpeedTools,
    Exit
}
