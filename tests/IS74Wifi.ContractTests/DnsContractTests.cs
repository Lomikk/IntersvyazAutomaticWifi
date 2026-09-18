using System.Net;
using System.Net.Sockets;
using IS74Wifi.Core;

internal static class DnsContractTests
{
    public static async Task RunAsync()
    {
        using var temp = TempDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var json = new JsonFileStore();
        var cache = new HostAddressCache(paths, json);
        var cached = IPAddress.Parse("203.0.113.10");
        var fresh = IPAddress.Parse("203.0.113.20");
        cache.Set("api.is74.ru", [cached]);

        var resolverCalls = 0;
        var dialed = new List<IPAddress>();
        var connector = new CachedDnsConnector(
            cache,
            ["api.is74.ru"],
            resolver: (host, cancellationToken) =>
            {
                resolverCalls++;
                return Task.FromResult(new[] { fresh });
            },
            dialer: (address, port, timeout, cancellationToken) =>
            {
                dialed.Add(address);
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });

        await using (var stream = await connector.ConnectHostAsync("api.is74.ru", 443))
        {
            Assert(stream is MemoryStream, "cached connector did not return the dialed stream");
        }
        Assert(resolverCalls == 0, "cached IP unexpectedly required DNS");
        Assert(dialed.SequenceEqual([cached]), "cached IP was not attempted first");

        dialed.Clear();
        resolverCalls = 0;
        var refreshConnector = new CachedDnsConnector(
            cache,
            ["api.is74.ru"],
            resolver: (host, cancellationToken) =>
            {
                resolverCalls++;
                return Task.FromResult(new[] { fresh });
            },
            dialer: (address, port, timeout, cancellationToken) =>
            {
                dialed.Add(address);
                if (address.Equals(cached))
                {
                    throw new SocketException((int)SocketError.ConnectionRefused);
                }
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });

        await using (var stream = await refreshConnector.ConnectHostAsync("api.is74.ru", 443))
        {
            Assert(stream is MemoryStream, "fresh DNS address was not used after stale cache failure");
        }
        Assert(resolverCalls == 1, "stale cached IP did not trigger one DNS refresh");
        Assert(dialed.SequenceEqual([cached, fresh]), "connector did not fall back from cached IP to refreshed DNS address");
        Assert(cache.GetAddresses("api.is74.ru").SequenceEqual([fresh]), "refreshed DNS address was not persisted");

        var reloaded = new HostAddressCache(paths, json);
        Assert(reloaded.GetAddresses("api.is74.ru").SequenceEqual([fresh]), "DNS cache did not survive process-style reload");

        var unavailable = new CachedDnsConnector(
            new HostAddressCache(new AppPaths(Path.Combine(temp.Path, "empty")), json),
            ["api.is74.ru"],
            resolver: (host, cancellationToken) => throw new SocketException((int)SocketError.HostNotFound),
            dialer: (address, port, timeout, cancellationToken) => ValueTask.FromResult<Stream>(new MemoryStream()));
        try
        {
            await unavailable.ConnectHostAsync("api.is74.ru", 443);
            throw new InvalidOperationException("missing DNS unexpectedly connected");
        }
        catch (CachedDnsUnavailableException)
        {
        }

        using var classifiedClient = new HttpClient(new DelegateHandler((_, _) =>
            throw new HttpRequestException(
                HttpRequestError.ConnectionError,
                "connect callback failed",
                new CachedDnsUnavailableException("api.is74.ru"),
                null)));
        var transport = new HttpTransport(classifiedClient);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.is74.ru/");
        var classified = await transport.SendAsync(request, TimeSpan.FromSeconds(1));
        Assert(classified.FailureKind == TransportFailureKind.DnsUnavailable,
            "wrapped cached-DNS failure lost DnsUnavailable classification");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;
        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IS74Wifi-dns-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
