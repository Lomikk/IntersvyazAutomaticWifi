using System.Security.Cryptography.X509Certificates;

namespace IS74Wifi.Core;

public static class HttpClientProfiles
{
    public static HttpClient CreateApiClient(CachedDnsConnector? connector = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 16,
            UseProxy = false
        };
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
        if (connector is not null)
        {
            handler.ConnectCallback = connector.ConnectAsync;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static HttpClient CreatePortalClient(CachedDnsConnector? connector = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 16,
            UseProxy = false
        };
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
        if (connector is not null)
        {
            handler.ConnectCallback = connector.ConnectAsync;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static HttpClient CreateInternetProbeClient(CachedDnsConnector? connector = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            MaxConnectionsPerServer = 2
        };
        if (connector is not null)
        {
            handler.ConnectCallback = connector.ConnectAsync;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
