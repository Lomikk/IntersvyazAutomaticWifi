namespace IS74Wifi.Core;

public sealed class RegistrationTelemetryRecorder(
    string installId,
    TelemetryQueue queue,
    string appVersion,
    Func<bool> anonymousStatisticsAllowed)
{
    public RegistrationTelemetryTrace Begin() =>
        new(installId, queue, appVersion, anonymousStatisticsAllowed);
}

public sealed class RegistrationTelemetryTrace
{
    private readonly string installId;
    private readonly TelemetryQueue queue;
    private readonly string appVersion;
    private readonly Func<bool> statisticsAllowed;
    private readonly List<TelemetryRegistrationEvent> events = [];
    private bool completed;

    internal RegistrationTelemetryTrace(
        string installId,
        TelemetryQueue queue,
        string appVersion,
        Func<bool> statisticsAllowed)
    {
        this.installId = installId;
        this.queue = queue;
        this.appVersion = appVersion;
        this.statisticsAllowed = statisticsAllowed;
        AttemptId = "registration-" + Guid.NewGuid().ToString("N");
    }

    public string AttemptId { get; }

    public void Record<T>(string stage, int requestIndex, Is74ApiResult<T> result)
    {
        if (completed || !StatisticsAllowed())
        {
            return;
        }

        var failure = result.Failure;
        var cancelled = failure is
        {
            Kind: Is74ApiFailureKind.Transport,
            TransportFailure: TransportFailureKind.Cancelled
        };

        events.Add(new TelemetryRegistrationEvent
        {
            AttemptId = AttemptId,
            InstallId = installId,
            AppVersion = appVersion,
            EventId = "event-" + Guid.NewGuid().ToString("N"),
            Stage = stage,
            RequestIndex = Math.Max(0, requestIndex),
            Result = result.IsSuccess ? "success" : cancelled ? "cancelled" : "error",
            DurationMs = ToDurationMs(result.Elapsed),
            HttpStatus = result.HttpStatus ?? failure?.StatusCode,
            ErrorClass = result.IsSuccess ? "none" : ClassifyFailure(failure),
            RetryAfterMs = ToDurationMs(result.RetryAfter),
            Server = SafeHeader(result.Server)
        });
    }

    public void Complete()
    {
        if (completed)
        {
            return;
        }

        completed = true;
        if (events.Count == 0 || !StatisticsAllowed())
        {
            return;
        }

        queue.Enqueue(events.Select(static item => TelemetrySerialization.Serialize(item)).ToArray());
    }

    private bool StatisticsAllowed()
    {
        try
        {
            return statisticsAllowed();
        }
        catch
        {
            return false;
        }
    }

    private static string ClassifyFailure(Is74ApiFailure? failure)
    {
        if (failure is null)
        {
            return "internal";
        }

        return failure.Kind switch
        {
            Is74ApiFailureKind.Transport => failure.TransportFailure switch
            {
                TransportFailureKind.DnsUnavailable => "dns",
                TransportFailureKind.ConnectionFailure => "tcp_connect",
                TransportFailureKind.Timeout => "timeout",
                TransportFailureKind.Cancelled => "cancelled",
                _ => "internal"
            },
            Is74ApiFailureKind.Unauthorized => "http_4xx",
            Is74ApiFailureKind.HttpStatus => failure.StatusCode switch
            {
                >= 400 and <= 499 => "http_4xx",
                >= 500 and <= 599 => "http_5xx",
                _ => "internal"
            },
            Is74ApiFailureKind.InvalidJson => "parse_error",
            Is74ApiFailureKind.InvalidPayload => "invalid_payload",
            Is74ApiFailureKind.MultipleAddresses => "multiple_addresses",
            _ => "internal"
        };
    }

    private static double? ToDurationMs(TimeSpan? value)
    {
        if (value is null)
        {
            return null;
        }

        var milliseconds = value.Value.TotalMilliseconds;
        return double.IsFinite(milliseconds) && milliseconds is >= 0 and <= 300000
            ? Math.Round(milliseconds, 3)
            : null;
    }

    private static string? SafeHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 64 || trimmed.Any(ch =>
                !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '+' or ';' or '=' or '(' or ')' or '/' or ',' or ':' or ' ' or '-')))
        {
            return null;
        }

        return trimmed;
    }
}
