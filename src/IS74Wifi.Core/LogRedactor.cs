using System.Text.RegularExpressions;

namespace IS74Wifi.Core;

public static partial class LogRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var value = AuthorizationHeader().Replace(text, "$1<redacted>");
        value = JsonSecret().Replace(value, "$1<redacted>$2");
        value = QuerySecret().Replace(value, "$1<redacted>");
        value = FormSecret().Replace(value, "$1<redacted>");
        value = WifiCode().Replace(value, "<redacted-code>");
        value = RussianPhone().Replace(value, "<redacted-phone>");
        return Newlines().Replace(value, " ");
    }

    [GeneratedRegex(@"(?i)(Authorization\s*[:=]\s*Bearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex("(?i)(\\\"(?:TOKEN|token|bearer|access_token|authId|confirmCode|phone|AUTHORIZE_PHONE)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")", RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecret();

    [GeneratedRegex(@"(?i)([?&](?:phone|confirmCode|authId)=)[^&\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecret();

    [GeneratedRegex(@"(?i)((?:phone|confirmCode|authId)\s*=\s*)[^&\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex FormSecret();

    [GeneratedRegex(@"\b\d{4}(?=\s+код авторизации)", RegexOptions.CultureInvariant)]
    private static partial Regex WifiCode();

    [GeneratedRegex(@"(?<!\d)(?:\+?7|8)\d{10}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex RussianPhone();

    [GeneratedRegex(@"[\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex Newlines();
}
