using IS74Wifi.Core;

namespace IS74Wifi.App;

internal sealed record UpdateCheckResult(
    UpdateDescriptor? Descriptor,
    UpdateState State,
    bool PerformedNetworkCheck);

internal sealed class UpdateMaintenanceService(
    string currentVersion,
    AppPaths paths,
    JsonFileStore json,
    DiagnosticLogger logger,
    TimeProvider? timeProvider = null)
{
    private readonly SettingsStore settingsStore = new(paths, json);
    private readonly UpdateStateStore stateStore = new(paths, json);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public AppSettings LoadSettings() => settingsStore.Load();

    public bool IncludePrereleases(AppSettings? settings = null) =>
        UpdatePolicy.IncludePrereleases(settings ?? settingsStore.Load(), currentVersion);

    public UpdateState LoadState()
    {
        var state = stateStore.Load();
        if (string.IsNullOrWhiteSpace(state.AvailableVersion) ||
            !SemanticVersion.TryParse(currentVersion, out var current))
        {
            return state;
        }

        if (SemanticVersion.TryParse(state.AvailableVersion, out var available) &&
            available.CompareTo(current) > 0)
        {
            return state;
        }

        var normalized = state with
        {
            AvailableVersion = null,
            AvailableReleasePageUrl = null,
            LastNotifiedVersion = null
        };
        stateStore.Save(normalized);
        return normalized;
    }

    public async Task<UpdateCheckResult> CheckAsync(
        GitHubUpdateClient updater,
        bool force,
        Action<UpdateProgressStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var state = LoadState();
        if (!force && state.NextCheckUtc is { } nextCheck && nextCheck > now)
        {
            return new UpdateCheckResult(null, state, PerformedNetworkCheck: false);
        }

        try
        {
            var settings = settingsStore.Load();
            var update = await updater.CheckForUpdateAsync(
                currentVersion,
                IncludePrereleases(settings),
                progress,
                cancellationToken).ConfigureAwait(false);

            var updatedState = state with
            {
                LastCheckedUtc = now,
                NextCheckUtc = now + UpdatePolicy.CheckInterval,
                AvailableVersion = update?.TagName,
                AvailableReleasePageUrl = update?.ReleasePageUrl,
                LastNotifiedVersion = update is not null &&
                                      string.Equals(state.AvailableVersion, update.TagName, StringComparison.Ordinal)
                    ? state.LastNotifiedVersion
                    : null,
                ConsecutiveFailures = 0,
                LastError = null
            };
            stateStore.Save(updatedState);
            return new UpdateCheckResult(update, updatedState, PerformedNetworkCheck: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var failures = state.ConsecutiveFailures + 1;
            var failedState = state with
            {
                LastCheckedUtc = now,
                NextCheckUtc = now + UpdatePolicy.FailureBackoff(failures),
                ConsecutiveFailures = failures,
                LastError = ex.GetType().Name
            };
            stateStore.Save(failedState);
            logger.Write(DiagnosticLevel.Warn,
                $"update.background-check failed error={ex.GetType().Name} retryAt={failedState.NextCheckUtc:O}");
            throw;
        }
    }

    public void MarkNotified(string version)
    {
        var state = LoadState();
        if (!string.Equals(state.AvailableVersion, version, StringComparison.Ordinal))
        {
            return;
        }
        stateStore.Save(state with { LastNotifiedVersion = version });
    }

    public void MarkInstalled(string version, bool notify)
    {
        var state = stateStore.Load();
        stateStore.Save(state with
        {
            AvailableVersion = null,
            AvailableReleasePageUrl = null,
            LastNotifiedVersion = null,
            PendingInstalledNotificationVersion = notify ? version : null,
            ConsecutiveFailures = 0,
            LastError = null
        });
    }

    public void ClearPendingInstalledNotification()
    {
        var state = stateStore.Load();
        if (string.IsNullOrWhiteSpace(state.PendingInstalledNotificationVersion))
        {
            return;
        }
        stateStore.Save(state with { PendingInstalledNotificationVersion = null });
    }

    public void ForceNextCheck()
    {
        var state = stateStore.Load();
        stateStore.Save(state with
        {
            NextCheckUtc = null,
            AvailableVersion = null,
            AvailableReleasePageUrl = null,
            LastNotifiedVersion = null,
            ConsecutiveFailures = 0,
            LastError = null
        });
    }
}
