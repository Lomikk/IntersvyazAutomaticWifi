using System.Net;
using System.Net.Sockets;

namespace IS74Wifi.Core;

public sealed record HostAddressCacheEntry(string[] Addresses, DateTimeOffset ResolvedAtUtc);

public sealed record HostAddressCacheDocument
{
    public Dictionary<string, HostAddressCacheEntry> Hosts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class HostAddressCache(AppPaths paths, JsonFileStore json, TimeProvider? timeProvider = null)
{
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private HostAddressCacheDocument? document;

    public HostAddressCacheEntry? GetEntry(string host)
    {
        lock (gate)
        {
            var data = LoadLocked();
            return data.Hosts.TryGetValue(NormalizeHost(host), out var entry) ? entry : null;
        }
    }

    public IReadOnlyList<IPAddress> GetAddresses(string host)
    {
        var entry = GetEntry(host);
        if (entry is null)
        {
            return [];
        }

        return entry.Addresses
            .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
            .Where(address => address is not null)
            .Cast<IPAddress>()
            .OrderBy(AddressOrder)
            .ToArray();
    }

    public void Set(string host, IEnumerable<IPAddress> addresses)
    {
        var values = addresses
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct()
            .OrderBy(AddressOrder)
            .Select(address => address.ToString())
            .ToArray();
        if (values.Length == 0)
        {
            return;
        }

        lock (gate)
        {
            var data = LoadLocked();
            data.Hosts[NormalizeHost(host)] = new HostAddressCacheEntry(values, clock.GetUtcNow());
            paths.EnsureDirectories();
            json.Write(paths.DnsCacheFile, data);
        }
    }

    public bool IsFresh(string host, TimeSpan maxAge)
    {
        var entry = GetEntry(host);
        return entry is not null && clock.GetUtcNow() - entry.ResolvedAtUtc <= maxAge;
    }

    private HostAddressCacheDocument LoadLocked()
    {
        if (document is not null)
        {
            return document;
        }

        document = json.Read<HostAddressCacheDocument>(paths.DnsCacheFile) ?? new HostAddressCacheDocument();
        return document;
    }

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    private static int AddressOrder(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1;
}
