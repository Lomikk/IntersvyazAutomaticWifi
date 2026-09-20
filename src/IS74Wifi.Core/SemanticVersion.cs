using System.Globalization;

namespace IS74Wifi.Core;

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, int? AlphaNumber) : IComparable<SemanticVersion>
{
    public bool IsPrerelease => AlphaNumber.HasValue;

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        if (AlphaNumber is null && other.AlphaNumber is null) return 0;
        if (AlphaNumber is null) return 1;
        if (other.AlphaNumber is null) return -1;
        return AlphaNumber.Value.CompareTo(other.AlphaNumber.Value);
    }

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var separator = text.IndexOf('-');
        var core = separator >= 0 ? text[..separator] : text;
        var prerelease = separator >= 0 ? text[(separator + 1)..] : null;
        var coreParts = core.Split('.');
        if (coreParts.Length != 3 ||
            !TryParseNumber(coreParts[0], out var major) ||
            !TryParseNumber(coreParts[1], out var minor) ||
            !TryParseNumber(coreParts[2], out var patch))
        {
            return false;
        }

        int? alpha = null;
        if (prerelease is not null)
        {
            const string prefix = "alpha.";
            if (!prerelease.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !TryParseNumber(prerelease[prefix.Length..], out var alphaNumber))
            {
                return false;
            }
            alpha = alphaNumber;
        }

        version = new SemanticVersion(major, minor, patch, alpha);
        return true;
    }

    private static bool TryParseNumber(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;
}
