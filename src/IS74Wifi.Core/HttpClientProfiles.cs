using System.Security.Cryptography.X509Certificates;

namespace IS74Wifi.Core;

public static class HttpClientProfiles
{
    public static HttpClient CreateApiClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 16,
            UseProxy = false
        };
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static HttpClient CreatePortalClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 16,
            UseProxy = false
        };
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static HttpClient CreateTelemetryClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            // The IS74 API and captive portal deliberately bypass system proxies,
            // but the public Google endpoint must respect the user's Windows proxy
            // configuration.  A PAC/corporate proxy can be the only route to
            // script.google.com even while s.is74.ru is directly reachable.
            UseProxy = true,
            MaxConnectionsPerServer = 2
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static HttpClient CreateTelemetryDiagnosticClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Diagnostics follows redirects itself so every hop is observable.
            AllowAutoRedirect = false,
            UseProxy = true,
            MaxConnectionsPerServer = 2
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static HttpClient CreateSpeedTestClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseProxy = false,
            MaxConnectionsPerServer = 8,
            AutomaticDecompression = System.Net.DecompressionMethods.None
        };
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static HttpClient CreateInternetProbeClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            MaxConnectionsPerServer = 2
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

}
