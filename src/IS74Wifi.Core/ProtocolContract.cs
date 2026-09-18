namespace IS74Wifi.Core;

/// <summary>
/// Stable behavior carried over from the experimentally validated PowerShell client.
/// Values in this type should change only with explicit protocol evidence/decision.
/// </summary>
public static class ProtocolContract
{
    public const string CampusSsidPrefix = "Campus Wi-Fi";
    public const int MaxAutomaticStepOneAttempts = 4;
    public const string AppVersion = "2.18.0-RS-95aa9b78";
    public const int BuildCode = 2026061111;

    public static ReadOnlySpan<int> PushPollOffsetsMilliseconds =>
    [
        100, 150, 200, 250, 350, 500, 700,
        1000, 1400, 2000, 3000, 4500, 6500, 10000
    ];

    public static ReadOnlySpan<int> LostStepTwoProbeOffsetsMilliseconds =>
        [0, 250, 500, 1000, 2000, 4000];
}
