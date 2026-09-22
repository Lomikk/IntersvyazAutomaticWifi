using System.Globalization;
using System.Text;

namespace IS74Wifi.Core;

/// <summary>
/// Public leaderboard JSON is untrusted, even when served by our own backend.
/// This policy produces terminal-safe display values before the UI sees them.
/// Keep the same limits in the Apps Script leaderboard response.
/// </summary>
public static class LeaderboardDisplayPolicy
{
    public const int MaximumEntries = 250;
    public const int MaximumNicknameScalars = 32;
    public const double MaximumSpeedMbps = 10_000;
    public const double MaximumLatencyMs = 60_000;
    public const double MaximumPacketLossPct = 100;

    public static string SanitizeNickname(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        // Work is bounded even if an otherwise valid JSON document contains a huge string.
        var result = new StringBuilder(MaximumNicknameScalars);
        var scanned = 0;
        var scalars = 0;
        var lastWasSpace = false;
        foreach (var rune in raw.EnumerateRunes())
        {
            if (++scanned > 256 || scalars >= MaximumNicknameScalars) break;
            if (rune.Value == ' ' || Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator)
            {
                if (result.Length > 0 && !lastWasSpace)
                {
                    result.Append(' ');
                    scalars++;
                    lastWasSpace = true;
                }
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            var permitted = category is
                UnicodeCategory.UppercaseLetter or
                UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.ModifierLetter or
                UnicodeCategory.OtherLetter or
                UnicodeCategory.DecimalDigitNumber;
            // A small punctuation set avoids terminal control sequences, bidi controls,
            // combining/zero-width marks and variable-width symbol rendering.
            if (!permitted && rune.Value is not ('.' or '_' or '-')) continue;

            result.Append(rune.ToString());
            scalars++;
            lastWasSpace = false;
        }

        return result.ToString().TrimEnd();
    }

    public static int SafeRank(int? rank, int sequentialRank) =>
        rank is >= 1 and <= MaximumEntries ? rank.Value : sequentialRank;

    public static double? SafeMetric(double? value, double maximum) =>
        value is { } number && double.IsFinite(number) && number >= 0 && number <= maximum
            ? number
            : null;
}
