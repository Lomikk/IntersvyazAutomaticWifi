using System.Net;
using System.Net.Sockets;

namespace IS74Wifi.Core;

public interface IAddressCacheWarmer
{
    Task WarmKnownHostsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
}

public sealed class CachedDnsUnavailableException(string host, Exception? innerException = null)
    : IOException($"DNS resolution failed for {host}.", innerException);

public sealed class CachedDnsConnector : IAddressCacheWarmer
{
    public static readonly string[] DefaultHosts =
    [
        "api.is74.ru",
        "w.is74.ru",
        "www.msftconnecttest.com"
    ];

    private readonly HostAddressCache cache;
    private readonly HashSet<string> trackedHosts;
    private readonly TimeSpan cachedConnectTimeout;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolver;
    private readonly Func<IPAddress, int, TimeSpan, CancellationToken, ValueTask<Stream>> dialer;

    public CachedDnsConnector(
        HostAddressCache cache,
        IEnumerable<string>? trackedHosts = null,
        TimeSpan? cachedConnectTimeout = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        Func<IPAddress, int, TimeSpan, CancellationToken, ValueTask<Stream>>? dialer = null)
    {
        this.cache = cache;
        this.trackedHosts = new HashSet<string>(
            trackedHosts ?? DefaultHosts,
            StringComparer.OrdinalIgnoreCase);
        this.cachedConnectTimeout = cachedConnectTimeout ?? TimeSpan.FromMilliseconds(700);
        this.resolver = resolver ?? ResolveAsync;
        this.dialer = dialer ?? DialAsync;
    }

    public ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken) =>
        ConnectHostAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken);

    public async ValueTask<Stream> ConnectHostAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        var attempted = new HashSet<IPAddress>();
        Exception? lastConnectionFailure = null;

        if (trackedHosts.Contains(host))
        {
            foreach (var address in cache.GetAddresses(host))
            {
                attempted.Add(address);
                try
                {
                    return await dialer(address, port, cachedConnectTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsConnectionFailure(ex))
                {
                    lastConnectionFailure = ex;
                }
            }
        }

        IPAddress[] resolved;
        try
        {
            resolved = (await resolver(host, cancellationToken).ConfigureAwait(false))
                .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .OrderBy(AddressOrder)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException)
        {
            if (lastConnectionFailure is not null)
            {
                throw new IOException($"Cached addresses for {host} failed and DNS refresh was unavailable.", lastConnectionFailure);
            }
            throw new CachedDnsUnavailableException(host, ex);
        }

        if (resolved.Length == 0)
        {
            throw new CachedDnsUnavailableException(host);
        }

        if (trackedHosts.Contains(host))
        {
            cache.Set(host, resolved);
        }

        foreach (var address in resolved)
        {
            if (!attempted.Add(address))
            {
                continue;
            }

            try
            {
                return await dialer(address, port, cachedConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                lastConnectionFailure = ex;
            }
        }

        throw lastConnectionFailure ?? new SocketException((int)SocketError.HostUnreachable);
    }

    public async Task WarmKnownHostsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        foreach (var host in trackedHosts)
        {
            if (cache.IsFresh(host, maxAge))
            {
                continue;
            }

            try
            {
                var addresses = await resolver(host, cancellationToken).ConfigureAwait(false);
                cache.Set(host, addresses);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Warming is an optimization. A failed refresh must never break the agent.
            }
        }
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

    private static async ValueTask<Stream> DialAsync(
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsConnectionFailure(Exception exception) =>
        exception is SocketException or IOException or OperationCanceledException;

    private static int AddressOrder(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1;
}
