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
