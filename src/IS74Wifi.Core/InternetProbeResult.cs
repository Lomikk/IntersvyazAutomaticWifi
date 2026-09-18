using System.Net;

namespace IS74Wifi.Core;

public sealed record InternetProbeResult(
    bool Online,
    bool HttpResponseReceived,
    HttpStatusCode? StatusCode,
    string? Body,
    TransportFailureKind FailureKind,
    TimeSpan Elapsed);
