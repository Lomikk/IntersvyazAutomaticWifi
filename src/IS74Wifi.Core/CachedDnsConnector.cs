using System.Diagnostics;
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
    // Experimental/diagnostic connector. Production HttpClient profiles do not
    // wire this into the authorization path after field testing showed that a
    // stale cached address can cost far more than ordinary captive DNS.
    public static readonly string[] DefaultHosts =
    [
        "api.is74.ru",
        "w.is74.ru"
    ];

    private readonly HostAddressCache cache;
    private readonly HashSet<string> trackedHosts;
    private readonly TimeSpan cachedConnectTimeout;
    private readonly TimeSpan warmResolveTimeout;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolver;
    private readonly Func<IPAddress, int, TimeSpan, CancellationToken, ValueTask<Stream>> dialer;

    public CachedDnsConnector(
        HostAddressCache cache,
        IEnumerable<string>? trackedHosts = null,
        TimeSpan? cachedConnectTimeout = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        Func<IPAddress, int, TimeSpan, CancellationToken, ValueTask<Stream>>? dialer = null,
        TimeSpan? warmResolveTimeout = null)
    {
        this.cache = cache;
        this.trackedHosts = new HashSet<string>(
            trackedHosts ?? DefaultHosts,
            StringComparer.OrdinalIgnoreCase);
        this.cachedConnectTimeout = cachedConnectTimeout ?? TimeSpan.FromMilliseconds(700);
        this.warmResolveTimeout = warmResolveTimeout ?? TimeSpan.FromSeconds(1);
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
            // cachedConnectTimeout is a budget for the cached phase as a whole,
            // not for every stale address. Otherwise a multi-address stale cache
            // can consume most/all of the caller's baseline timeout before DNS is
            // even attempted.
            var cachedClock = Stopwatch.StartNew();
            foreach (var address in cache.GetAddresses(host))
            {
                var remaining = cachedConnectTimeout - cachedClock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                attempted.Add(address);
                try
                {
                    return await dialer(address, port, remaining, cancellationToken).ConfigureAwait(false);
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

        foreach (var address in resolved)
        {
            if (!attempted.Add(address))
            {
                continue;
            }

            try
            {
                var stream = await dialer(address, port, cachedConnectTimeout, cancellationToken).ConfigureAwait(false);
                if (trackedHosts.Contains(host))
                {
                    TrySetCache(host, resolved);
                }
                return stream;
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
        var staleHosts = trackedHosts
            .Where(host => !cache.IsFresh(host, maxAge))
            .ToArray();
        if (staleHosts.Length == 0 || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Warming is only an optimization. Resolve stale hosts in parallel and
        // bound every lookup so a wedged system DNS resolver cannot block the
        // single agent loop long enough to miss the expiry edge.
        var tasks = staleHosts.Select(host => WarmHostAsync(host, cancellationToken)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task WarmHostAsync(string host, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(warmResolveTimeout);
        try
        {
            var addresses = await resolver(host, timeoutCts.Token).ConfigureAwait(false);
            cache.Set(host, addresses);
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation or the small warm-only timeout both stop this
            // optimization silently. The real request path still has its own DNS
            // fallback and typed failure handling.
        }
        catch
        {
            // Warming is an optimization. A failed refresh must never break the agent.
        }
    }

    private void TrySetCache(string host, IEnumerable<IPAddress> addresses)
    {
        try
        {
            cache.Set(host, addresses);
        }
        catch
        {
            // The persisted cache is an optimization. A successful live connection
            // must not be failed just because the cache file cannot be updated.
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
