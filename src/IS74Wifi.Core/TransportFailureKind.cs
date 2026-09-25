namespace IS74Wifi.Core;

public enum TransportFailureKind
{
    None,
    DnsUnavailable,
    DirectRouteUnavailable,
    Timeout,
    Cancelled,
    ConnectionFailure,
    Unexpected,
    TlsFailure,
    ResponseTooLarge
}
