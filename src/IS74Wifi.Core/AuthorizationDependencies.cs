namespace IS74Wifi.Core;

public interface IAuthorizationRunner
{
    Task<AuthorizationOutcome> RunAsync(
        AuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IIs74PushClient
{
    Task<Is74ApiResult<PushBaseline>> GetBaselineAsync(
        string bearerToken,
        string deviceId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<Is74ApiResult<PushMessagePage>> GetPushMessagesAsync(
        string bearerToken,
        string deviceId,
        int pageSize,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public interface ICaptivePortalClient
{
    Task<CaptivePortalResult<StepOneResponse>> SendStepOneAsync(
        string phone,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<CaptivePortalResult<StepTwoResponse>> SendStepTwoAsync(
        string phone,
        string confirmCode,
        Uri? observedStepTwoLocation = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public interface IInternetConnectivityProbe
{
    Task<InternetProbeResult> ProbeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public interface IWifiEnvironment
{
    bool IsTargetWifiConnected();
}

public sealed class WindowsWifiEnvironment : IWifiEnvironment
{
    public bool IsTargetWifiConnected() => WindowsWifiService.IsTargetWifiConnected();
}
