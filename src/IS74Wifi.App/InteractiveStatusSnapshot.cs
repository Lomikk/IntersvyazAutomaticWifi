using IS74Wifi.Core;

namespace IS74Wifi.App;

internal sealed record InteractiveNetworkPathStatus(
    string Name,
    PathAuthorizationStatus Status,
    bool Preferred,
    DateTimeOffset? ExpectedExpiryUtc,
    string? LastResult,
    bool AuthorizationWindowApproximate = false);

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
    string Version,
    int ActivePhysicalPathCount = 0,
    int InternetPathCount = 0,
    int CaptivePathCount = 0,
    int ProblemPathCount = 0,
    IReadOnlyList<InteractiveNetworkPathStatus>? NetworkPaths = null,
    bool VpnActive = false);

internal enum WifiNetworkState
{
    Unknown,
    Campus,
    Other
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
