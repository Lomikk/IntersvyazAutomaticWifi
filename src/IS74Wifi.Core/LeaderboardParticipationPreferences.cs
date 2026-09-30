namespace IS74Wifi.Core;

public sealed record LeaderboardLocalState(
    bool Known,
    bool Active,
    bool HasHistory,
    string? PublishedNickname,
    int? RenameRemaining,
    DateTimeOffset? RenameAvailableAtUtc,
    DateTimeOffset? RejoinAvailableAtUtc,
    bool FirstPublishWarningAcknowledged);

/// <summary>
/// Persists only UI hints about public leaderboard state. The server remains the
/// authority for participation and rate limits; this cache is never trusted to
/// grant an operation the server would reject.
/// </summary>
public sealed class LeaderboardParticipationPreferences(SettingsStore settings)
{
    public LeaderboardLocalState Load()
    {
        var value = settings.Load();
        return new LeaderboardLocalState(
            value.LeaderboardParticipationKnown,
            value.LeaderboardParticipating,
            value.LeaderboardHasHistory,
            value.LeaderboardPublishedNickname,
            value.LeaderboardRenameRemaining,
            value.LeaderboardRenameAvailableAtUtc,
            value.LeaderboardRejoinAvailableAtUtc,
            value.LeaderboardFirstPublishWarningAcknowledged);
    }

    public void ApplyServerState(LeaderboardControlState state)
    {
        var current = settings.Load();
        settings.Save(current with
        {
            LeaderboardParticipationKnown = true,
            LeaderboardParticipating = state.Active,
            LeaderboardHasHistory = state.HasHistory,
            LeaderboardPublishedNickname = state.Nickname,
            LeaderboardRenameRemaining = state.RenameRemaining,
            LeaderboardRenameAvailableAtUtc = state.RenameAvailableAtUtc,
            LeaderboardRejoinAvailableAtUtc = state.RejoinAvailableAtUtc
        });
    }

    public void AcknowledgeFirstPublishWarning()
    {
        var current = settings.Load();
        if (!current.LeaderboardFirstPublishWarningAcknowledged)
        {
            settings.Save(current with { LeaderboardFirstPublishWarningAcknowledged = true });
        }
    }

    public bool RenameObviouslyRateLimited(DateTimeOffset now, out DateTimeOffset? availableAtUtc)
    {
        var state = Load();
        availableAtUtc = state.RenameAvailableAtUtc;
        return state.RenameRemaining == 0 &&
               state.RenameAvailableAtUtc is { } available &&
               available > now;
    }

    public bool RejoinObviouslyRateLimited(DateTimeOffset now, out DateTimeOffset? availableAtUtc)
    {
        var state = Load();
        availableAtUtc = state.RejoinAvailableAtUtc;
        return state.RejoinAvailableAtUtc is { } available && available > now;
    }
}
