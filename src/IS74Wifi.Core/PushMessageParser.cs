using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IS74Wifi.Core;

public enum PushPageParseStatus
{
    Success,
    InvalidJson,
    UnrecognizedSchema
}

public static partial class PushMessageParser
{
    private static readonly string[] KnownContainers =
        ["items", "data", "messages", "pushMessages", "push_messages", "result", "content"];

    public static PushPageParseStatus ParsePage(string json, out PushMessagePage page)
    {
        page = new PushMessagePage([], false);
        try
        {
            using var document = JsonDocument.Parse(json);
            var messages = FindMessages(document.RootElement, 0);
            if (messages.Count > 0)
            {
                page = new PushMessagePage(messages, false);
                return PushPageParseStatus.Success;
            }

            if (IsKnownEmpty(document.RootElement, 0))
            {
                page = new PushMessagePage([], true);
                return PushPageParseStatus.Success;
            }

            return PushPageParseStatus.UnrecognizedSchema;
        }
        catch (JsonException)
        {
            return PushPageParseStatus.InvalidJson;
        }
    }

    public static WifiCodeCandidate? FindWifiCodeAfterBaseline(PushMessagePage page, long baselineId)
    {
        foreach (var message in page.Messages)
        {
            if (message.Id <= baselineId)
            {
                continue;
            }

            var code = GetWifiCode(message);
            if (code is not null)
            {
                return new WifiCodeCandidate(code, message.Id);
            }
        }

        return null;
    }

    public static string? GetWifiCode(Is74PushMessage message)
    {
        if (!string.Equals(message.Subject, "Ваш код авторизации", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var text in new[] { message.PushMessage, message.FullMessage })
        {
            if (text is null)
            {
                continue;
            }

            var match = WifiCodeMessageRegex().Match(text);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static IReadOnlyList<Is74PushMessage> FindMessages(JsonElement element, int depth)
    {
        if (depth > 4)
        {
            return [];
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var messages = new List<Is74PushMessage>();
            foreach (var item in element.EnumerateArray())
            {
                if (TryReadMessage(item, out var message))
                {
                    messages.Add(message);
                }
            }
            return messages;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (TryReadMessage(element, out var directMessage))
        {
            return [directMessage];
        }

        foreach (var name in KnownContainers)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            var messages = FindMessages(value, depth + 1);
            if (messages.Count > 0)
            {
                return messages;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var messages = FindMessages(property.Value, depth + 1);
                if (messages.Count > 0)
                {
                    return messages;
                }
            }
        }

        return [];
    }

    private static bool IsKnownEmpty(JsonElement element, int depth)
    {
        if (depth > 4)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.GetArrayLength() == 0;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var sawKnownContainer = false;
        foreach (var name in KnownContainers)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            sawKnownContainer = true;
            if (value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() > 0)
                {
                    return false;
                }
                continue;
            }

            if (!IsKnownEmpty(value, depth + 1))
            {
                return false;
            }
        }

        return sawKnownContainer;
    }

    private static bool TryReadMessage(JsonElement element, out Is74PushMessage message)
    {
        message = default!;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("id", out var idElement) ||
            !TryReadInt64(idElement, out var id))
        {
            return false;
        }

        var subject = GetString(element, "subject");
        var pushMessage = GetString(element, "push_message");
        var fullMessage = GetString(element, "full_message");
        if (subject is null && pushMessage is null && fullMessage is null)
        {
            return false;
        }

        message = new Is74PushMessage(id, subject, pushMessage, fullMessage);
        return true;
    }

    private static bool TryReadInt64(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    [GeneratedRegex("^(\\d{4}) код авторизации в приложении \"Интерсвязь\"$", RegexOptions.CultureInvariant)]
    private static partial Regex WifiCodeMessageRegex();
}
