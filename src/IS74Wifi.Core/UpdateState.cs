namespace IS74Wifi.Core;

public sealed record UpdateState
{
    public DateTimeOffset? LastCheckedUtc { get; init; }
    public DateTimeOffset? NextCheckUtc { get; init; }
    public string? AvailableVersion { get; init; }
    public string? AvailableReleasePageUrl { get; init; }
    public string? LastNotifiedVersion { get; init; }
    public string? PendingInstalledNotificationVersion { get; init; }
    public int ConsecutiveFailures { get; init; }
    public string? LastError { get; init; }
}

public sealed class UpdateStateStore(AppPaths paths, JsonFileStore json)
{
    public UpdateState Load()
    {
        paths.EnsureDirectories();
        return json.Read(paths.UpdateStateFile, PersistenceJsonContext.Default.UpdateState) ?? new UpdateState();
    }

    public void Save(UpdateState state)
    {
        paths.EnsureDirectories();
        json.Write(paths.UpdateStateFile, state, PersistenceJsonContext.Default.UpdateState);
    }
}

public static class UpdatePolicy
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    public static bool IncludePrereleases(AppSettings settings, string currentVersion) =>
        settings.IncludePrereleaseUpdates ??
        currentVersion.Contains("-alpha.", StringComparison.OrdinalIgnoreCase);

    public static TimeSpan FailureBackoff(int consecutiveFailures)
    {
        var failure = Math.Max(1, consecutiveFailures);
        return failure switch
        {
            1 => TimeSpan.FromMinutes(15),
            2 => TimeSpan.FromMinutes(30),
            3 => TimeSpan.FromHours(1),
            _ => TimeSpan.FromHours(2)
        };
    }
}
