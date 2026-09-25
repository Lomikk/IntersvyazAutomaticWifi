using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace IS74Wifi.Core;

/// <summary>DNS queries sent over the selected physical interface, not the system/VPN resolver.</summary>
public sealed class InterfaceDnsResolver
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DnsServerTimeout = TimeSpan.FromMilliseconds(900);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IPAddress[] Addresses)> cache = new();

    public async Task<IPAddress[]> ResolveAsync(string hostname, PhysicalAdapter adapter, CancellationToken token)
    {
        var cacheKey = $"{adapter.Id}|{adapter.SourceIPv4}|{adapter.Ssid}|{string.Join(",", adapter.DnsServers.Select(ip => ip.ToString()))}|{hostname}";
        if (cache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Addresses;
        }
        if (adapter.DnsServers.Count == 0)
        {
            throw new DirectNetworkUnavailableException("У выбранного адаптера отсутствует IPv4 DNS-сервер.");
        }

        Exception? lastFailure = null;
        foreach (var dns in adapter.DnsServers.Take(3))
        {
            token.ThrowIfCancellationRequested();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(DnsServerTimeout);
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                DirectNetworkConnector.BindSocket(socket, adapter);
                // Connected UDP accepts replies only from the chosen interface DNS server.
                socket.Connect(new IPEndPoint(dns, 53));
                var id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
                var query = CreateQuery(hostname, id);
                await socket.SendAsync(query.AsMemory(), SocketFlags.None, cts.Token).ConfigureAwait(false);
                var buffer = new byte[1500];
                var size = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, cts.Token).ConfigureAwait(false);
                var addresses = ParseResponse(buffer.AsSpan(0, size), hostname, id);
                if (addresses.Length == 0)
                {
                    throw new IOException("DNS returned no IPv4 addresses.");
                }
                cache[cacheKey] = (DateTimeOffset.UtcNow + CacheLifetime, addresses);
                return addresses;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or IOException or FormatException or OperationCanceledException)
            {
                lastFailure = ex;
            }
        }
        throw new CachedDnsUnavailableException(hostname, lastFailure);
    }

    public static byte[] CreateQuery(string hostname, ushort id)
    {
        var labels = hostname.TrimEnd('.').Split('.');
        using var buffer = new MemoryStream();
        buffer.WriteByte((byte)(id >> 8));
        buffer.WriteByte((byte)id);
        buffer.Write([0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63 || label.Any(ch => ch > 127))
            {
                throw new FormatException("Invalid DNS hostname.");
            }
            buffer.WriteByte((byte)label.Length);
            buffer.Write(Encoding.ASCII.GetBytes(label));
        }
        buffer.Write([0x00, 0x00, 0x01, 0x00, 0x01]); // QTYPE=A, QCLASS=IN
        return buffer.ToArray();
    }

    public static IPAddress[] ParseResponse(ReadOnlySpan<byte> response, string hostname, ushort id)
    {
        if (response.Length < 12 || Read16(response, 0) != id ||
            (response[2] & 0x80) == 0 || (response[2] & 0x02) != 0 ||
            (response[3] & 0x0f) != 0 || Read16(response, 4) != 1)
        {
            throw new FormatException("Unexpected DNS response.");
        }
        var count = Read16(response, 6);
        if (count > 64)
        {
            throw new FormatException("Oversized DNS answer.");
        }
        var cursor = 12;
        var asked = ReadName(response, ref cursor);
        if (!string.Equals(asked, hostname.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ||
            Read16(response, cursor) != 1 || Read16(response, cursor + 2) != 1)
        {
            throw new FormatException("DNS question did not match requested host.");
        }
        cursor += 4;
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var records = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var name = ReadName(response, ref cursor);
            if (cursor + 10 > response.Length) throw new FormatException("Truncated DNS answer.");
            var type = Read16(response, cursor);
            var dnsClass = Read16(response, cursor + 2);
            var dataLength = Read16(response, cursor + 8);
            cursor += 10;
            var end = cursor + dataLength;
            if (end > response.Length) throw new FormatException("Truncated DNS record.");
            if (dnsClass == 1 && type == 1 && dataLength == 4)
            {
                if (!records.TryGetValue(name, out var addresses)) records[name] = addresses = [];
                addresses.Add(new IPAddress(response.Slice(cursor, 4)));
            }
            else if (dnsClass == 1 && type == 5)
            {
                var aliasOffset = cursor;
                var target = ReadName(response, ref aliasOffset);
                if (aliasOffset > end) throw new FormatException("Invalid DNS alias.");
                aliases[name] = target;
            }
            cursor = end;
        }
        var wanted = hostname.TrimEnd('.');
        for (var i = 0; i < 8; i++)
        {
            if (records.TryGetValue(wanted, out var found)) return found.Distinct().ToArray();
            if (!aliases.TryGetValue(wanted, out var next)) break;
            wanted = next;
        }
        return [];
    }

    private static ushort Read16(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 2 > data.Length) throw new FormatException("Truncated DNS response.");
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static string ReadName(ReadOnlySpan<byte> data, ref int offset)
    {
        var cursor = offset;
        var jumped = false;
        var result = new List<string>();
        var totalLength = 0;
        for (var i = 0; i < 32; i++)
        {
            if (cursor >= data.Length) throw new FormatException("Truncated DNS name.");
            var length = data[cursor++];
            if ((length & 0xc0) == 0xc0)
            {
                if (cursor >= data.Length) throw new FormatException("Truncated DNS pointer.");
                var pointer = ((length & 0x3f) << 8) | data[cursor++];
                if (pointer < 12 || pointer >= data.Length) throw new FormatException("Invalid DNS pointer.");
                if (!jumped) offset = cursor;
                cursor = pointer;
                jumped = true;
                continue;
            }
            if ((length & 0xc0) != 0) throw new FormatException("Invalid DNS label.");
            if (length == 0)
            {
                if (!jumped) offset = cursor;
                return string.Join(".", result);
            }
            totalLength += length + 1;
            if (totalLength > 255 || cursor + length > data.Length)
            {
                throw new FormatException("Invalid DNS name length.");
            }
            result.Add(Encoding.ASCII.GetString(data.Slice(cursor, length)));
            cursor += length;
        }
        throw new FormatException("Cyclic DNS name compression.");
    }
}
