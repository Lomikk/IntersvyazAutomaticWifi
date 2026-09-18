namespace IS74Wifi.Core;

public sealed record ConfirmationRequested();

public sealed record ConfirmationChecked(string AuthId, int AddressesCount);

public sealed record Is74ApiSession(
    string Token,
    string? UserId,
    string? ProfileId,
    string? AccessBegin,
    string? AccessEnd);

public sealed record DeviceMetadataRegistration(
    string DeviceId,
    string Phone,
    string OsVersion,
    string DeviceModel);

public sealed record DeviceMetadataAccepted();

public sealed record Is74PushMessage(
    long Id,
    string? Subject,
    string? PushMessage,
    string? FullMessage);

public sealed record PushMessagePage(
    IReadOnlyList<Is74PushMessage> Messages,
    bool KnownEmpty);

public sealed record PushBaseline(long Id);

public sealed record WifiCodeCandidate(string Code, long MessageId);
