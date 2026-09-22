namespace IS74Wifi.Core;

public enum TransportFailureKind
{
    None,
    DnsUnavailable,
    Timeout,
    Cancelled,
    ConnectionFailure,
    Unexpected,
    TlsFailure,
    ResponseTooLarge
}
