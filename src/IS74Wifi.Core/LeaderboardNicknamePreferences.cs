namespace IS74Wifi.Core;

/// <summary>
/// Remembers a voluntary public nickname locally. Identity for telemetry and
/// leaderboard deduplication remains the independent, private install_id.
/// </summary>
public sealed class LeaderboardNicknamePreferences(SettingsStore settings)
{
    public const string DefaultNickname = "Гость";
    public const int MaximumNicknameLength = 18;

    public string Load() => Normalize(settings.Load().LeaderboardNickname) ?? DefaultNickname;

    public bool TrySave(string? proposed, out string nickname)
    {
        var normalized = Normalize(proposed);
        if (normalized is null)
        {
            nickname = Load();
            return false;
        }

        // Read fresh settings on every change so unrelated preferences (including
        // telemetry consent) cannot be overwritten by an old UI snapshot.
        var current = settings.Load();
        if (current.LeaderboardNickname != normalized)
        {
            settings.Save(current with { LeaderboardNickname = normalized });
        }

        nickname = normalized;
        return true;
    }

    public static string? Normalize(string? proposed)
    {
        if (string.IsNullOrWhiteSpace(proposed)) return null;
        var safe = LeaderboardDisplayPolicy.SanitizeNickname(proposed);
        if (safe.Length == 0 || safe[0] == '-') return null;

        // The terminal editor uses 18 UTF-16 characters; keep persisted values
        // within the same bound without splitting surrogate pairs.
        if (safe.Length > MaximumNicknameLength)
        {
            var maxLength = MaximumNicknameLength;
            if (char.IsHighSurrogate(safe[maxLength - 1])) maxLength--;
            safe = safe[..maxLength].TrimEnd();
        }

        return safe.Length > 0 ? safe : null;
    }
}
