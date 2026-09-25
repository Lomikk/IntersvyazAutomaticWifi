using System.Net;
using System.Net.Sockets;

namespace IS74Wifi.Core;

public sealed class DirectNetworkUnavailableException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>
/// ConnectCallback used only by the three authorization transports. Binding the
/// source IP alone is insufficient on Windows: IP_UNICAST_IF pins the route.
/// TLS/SNI/hostname validation remains the responsibility of SocketsHttpHandler.
/// </summary>
public sealed class DirectNetworkConnector(
    Func<IReadOnlyList<PhysicalAdapter>> enumerate,
    string? preferredAdapterId,
    InterfaceDnsResolver? resolver = null,
    Func<PhysicalAdapter, IPAddress, int, CancellationToken, ValueTask<Stream>>? testDialer = null)
{
    // Winsock IP_UNICAST_IF takes an IPv4 interface index in network byte order.
    private const int IpUnicastIf = 31;
    private readonly InterfaceDnsResolver dns = resolver ?? new InterfaceDnsResolver();
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.is74.ru", "w.is74.ru", "online.susu.ru"
    };

    public PhysicalAdapter? SelectedAdapter => PhysicalAdapterSelection.Select(enumerate(), preferredAdapterId);

    public Task<DnsResolution> ResolveHostForDiagnosticsAsync(string host, CancellationToken token)
    {
        if (!AllowedHosts.Contains(host)) throw new DirectNetworkUnavailableException("Недопустимый сервер прямого маршрута.");
        return dns.ResolveWithSourceAsync(host, GetAdapter(), token);
    }

    private PhysicalAdapter GetAdapter() => SelectedAdapter ?? throw new DirectNetworkUnavailableException(
        string.IsNullOrWhiteSpace(preferredAdapterId)
            ? "Нет активного физического сетевого адаптера с IPv4. Выберите адаптер в настройках."
            : "Выбранный адаптер недоступен. Измените выбор в настройках.");

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token) =>
        await ConnectHostAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token).ConfigureAwait(false);

    public async ValueTask<Stream> ConnectHostAsync(string host, int port, CancellationToken token)
    {
        if (!AllowedHosts.Contains(host))
        {
            throw new DirectNetworkUnavailableException("Прямой маршрут разрешён только для серверов авторизации и проверки Интернета.");
        }
        var adapter = GetAdapter();
        var addresses = await dns.ResolveAsync(host, adapter, token).ConfigureAwait(false);
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            token.ThrowIfCancellationRequested();
            Socket? socket = null;
            try
            {
                if (testDialer is not null)
                {
                    // Test seam: production always uses the real bound Winsock socket below.
                    return await testDialer(adapter, address, port, token).ConfigureAwait(false);
                }
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                BindSocket(socket, adapter);
                await socket.ConnectAsync(new IPEndPoint(address, port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (SocketException ex)
            {
                lastError = ex;
            }
            finally
            {
                // NetworkStream owns the connected socket; failed sockets need closing.
                if (socket is { Connected: false }) socket.Dispose();
            }
        }
        throw new DirectNetworkUnavailableException(
            "Не удалось открыть прямое TCP-соединение через выбранный адаптер (возможно, VPN kill switch).",
            lastError);
    }

    /// <summary>Side-effect-free TCP handshake used before reserving a stepOne attempt.</summary>
    public async Task<bool> CanReachPortalAsync(TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            // Captive portal is HTTP/80; this handshake sends no HTTP request or code.
            await using var stream = await ConnectHostAsync("w.is74.ru", 80, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    internal static void BindSocket(Socket socket, PhysicalAdapter adapter)
    {
        if (adapter.SourceIPv4 is null || adapter.IPv4Index <= 0)
        {
            throw new DirectNetworkUnavailableException("У выбранного адаптера нет корректного IPv4 или индекса.");
        }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf,
                    BitConverter.GetBytes(IPAddress.HostToNetworkOrder(adapter.IPv4Index)));
            }
            else
            {
                throw new DirectNetworkUnavailableException("Выбор исходящего интерфейса поддерживается только в Windows.");
            }
            socket.Bind(new IPEndPoint(adapter.SourceIPv4, 0));
        }
        catch (SocketException ex)
        {
            throw new DirectNetworkUnavailableException("Не удалось привязать сокет к физическому адаптеру.", ex);
        }
    }
}
