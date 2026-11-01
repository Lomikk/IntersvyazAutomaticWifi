using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace IS74Wifi.Core;

public sealed record PathAddressEntry(string[] Addresses, DateTimeOffset ResolvedAtUtc,
    DnsAddressSource Source, DateTimeOffset ConnectedAtUtc);

public sealed record PathAddressDocument
{
    public int Version { get; init; } = 1;
    public Dictionary<string, PathAddressEntry> Entries { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Physical-path address candidates, independent of HTTP captive classification.
/// TCP reachability is not server identity: TLS and authorization gates remain mandatory.</summary>
public sealed class PathAddressBook(AppPaths paths, JsonFileStore json, DiagnosticLogger? logger = null,
    Func<string, PhysicalAdapter, CancellationToken, Task<DnsResolution>>? lookup = null,
    Func<string, PhysicalAdapter, IPAddress, CancellationToken, Task>? validate = null,
    Func<PhysicalAdapter, IPAddress, int, CancellationToken, ValueTask<Stream>>? testDialer = null)
{
    public static IReadOnlyList<string> Hosts { get; } = Array.AsReadOnly(new[] { "online.susu.ru", "w.is74.ru", "api.is74.ru" });
    private readonly SemaphoreSlim resolveGate = new(1, 1);
    private readonly object writeGate = new();
    private readonly ConcurrentDictionary<string, (DnsResolution Resolution, DateTimeOffset At)> candidates = new();
    private readonly ConcurrentDictionary<string, byte> persistedConnections = new();
    private readonly string fileGateName = "Local\\IS74Wifi.CampusAddresses." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(paths.PathAddressFile).ToUpperInvariant())));

    public DirectNetworkConnector CreateConnector(NetworkPathSnapshot path) =>
        new(path, testDialer: testDialer, addressLookup: ResolveAsync, connected: RememberConnected);

    public Task RefreshAsync(NetworkPathSnapshot path, CancellationToken token) =>
        RefreshAsync(path.ToPhysicalAdapter(), token);

    public async Task<DnsResolution> ResolveAsync(string host, PhysicalAdapter adapter, CancellationToken token)
    {
        var key = Key(host, adapter);
        token.ThrowIfCancellationRequested();
        await resolveGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (candidates.TryGetValue(key, out var known)) return known.Resolution with { FromCache = true };
            var document = Read();
            if (document.Entries.TryGetValue(key, out var entry) && entry is not null)
            {
                var addresses = Parse(entry.Addresses);
                if (addresses.Length > 0)
                {
                    var persisted = new DnsResolution(addresses, DnsAddressSource.PersistentAddress, true);
                    candidates[key] = (persisted, entry.ResolvedAtUtc);
                    foreach (var address in addresses) persistedConnections[key + "|" + address] = 0;
                    logger?.Write(DiagnosticLevel.Info, $"network.address source=persistent host={host}");
                    return persisted;
                }
            }
            var resolution = await LookupAsync(host, adapter, token).ConfigureAwait(false);
            candidates[key] = (resolution, DateTimeOffset.UtcNow);
            logger?.Write(DiagnosticLevel.Info, $"network.address source={resolution.Source} host={host}");
            return resolution;
        }
        finally { resolveGate.Release(); }
    }

    // Called only after an actual TCP connection. It never refreshes DNS or marks
    // a captive HTTP 200 as proof of destination identity. Disk failure is best-effort.
    public void RememberConnected(string host, PhysicalAdapter adapter, IPAddress address)
    {
        var key = Key(host, adapter);
        if (persistedConnections.ContainsKey(key + "|" + address)) return;
        if (!candidates.TryGetValue(key, out var learned) || !learned.Resolution.Addresses.Contains(address)) return;
        try
        {
            lock (writeGate)
            {
                using var lease = NamedSemaphoreLease.TryAcquire(fileGateName);
                if (lease is null) return;
                var data = Read();
                if (data.Entries.TryGetValue(key, out var existing) && existing is not null && Parse(existing.Addresses).Contains(address))
                { persistedConnections[key + "|" + address] = 0; return; }
                var values = new[] { address.ToString() }.Concat(existing?.Addresses ?? []).Distinct().Take(4).ToArray();
                data.Entries[key] = new(values, learned.At, learned.Resolution.Source, DateTimeOffset.UtcNow);
                json.Write(paths.PathAddressFile, data, PersistenceJsonContext.Default.PathAddressDocument);
                persistedConnections[key + "|" + address] = 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger?.Write(DiagnosticLevel.Warn, $"network.address persist-failed error={ex.GetType().Name}"); }
    }

    /// <summary>Explicit maintenance only. Failed DNS/TCP/TLS preserves the old entry.
    /// Each successful host is updated atomically; no background refresh on Unreachable.</summary>
    public async Task RefreshAsync(PhysicalAdapter adapter, CancellationToken token)
    {
        await resolveGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var failures = 0;
            foreach (var host in Hosts)
            {
                var key = Key(host, adapter);
                try
                {
                    var fresh = await LookupAsync(host, adapter, token).ConfigureAwait(false);
                    var reachable = new List<IPAddress>();
                    foreach (var address in fresh.Addresses.Take(4))
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        try
                        {
                            await (validate ?? ValidateAsync)(host, adapter, address, timeout.Token).ConfigureAwait(false);
                            reachable.Add(address);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException) { }
                    }
                    if (reachable.Count == 0) throw new IOException("Новые адреса недоступны.");
                    var now = DateTimeOffset.UtcNow;
                    lock (writeGate)
                    {
                        using var lease = NamedSemaphoreLease.TryAcquire(fileGateName) ?? throw new IOException("Хранилище адресов занято.");
                        var data = Read();
                        data.Entries[key] = new(reachable.Select(ip => ip.ToString()).ToArray(), now, fresh.Source, now);
                        json.Write(paths.PathAddressFile, data, PersistenceJsonContext.Default.PathAddressDocument);
                        candidates[key] = (new(reachable.ToArray(), DnsAddressSource.PersistentAddress, true), now);
                        foreach (var address in reachable) persistedConnections[key + "|" + address] = 0;
                    }
                    logger?.Write(DiagnosticLevel.Info, $"network.address refreshed host={host}");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or OperationCanceledException)
                {
                    failures++;
                    logger?.Write(DiagnosticLevel.Warn, $"network.address refresh-failed host={host} error={ex.GetType().Name}");
                }
            }
            if (failures > 0) throw new IOException($"Не обновлено серверов: {failures} из {Hosts.Count}. Для них сохранены предыдущие адреса; остальные обновлены.");
        }
        finally { resolveGate.Release(); }
    }

    private async Task<DnsResolution> LookupAsync(string host, PhysicalAdapter adapter, CancellationToken token)
    {
        // A fresh resolver for explicit refresh; never its short-lived old DNS entry.
        var resolution = await (lookup is null
            ? new InterfaceDnsResolver().ResolveWithSourceAsync(host, adapter, token)
            : lookup(host, adapter, token)).ConfigureAwait(false);
        var addresses = Parse(resolution.Addresses.Select(ip => ip.ToString()));
        if (addresses.Length == 0) throw new IOException("DNS не вернул пригодных IPv4.");
        return resolution with { Addresses = addresses, FromCache = false };
    }

    private PathAddressDocument Read()
    {
        var data = json.Read(paths.PathAddressFile, PersistenceJsonContext.Default.PathAddressDocument);
        return data is { Version: 1, Entries: not null } ? data : new();
    }

    private static string Key(string host, PhysicalAdapter adapter)
    {
        var path = NetworkPathSnapshot.FromAdapter(adapter);
        if (!Hosts.Contains(host, StringComparer.OrdinalIgnoreCase) || !path.IsAutomaticAuthorizationCandidate)
            throw new DirectNetworkUnavailableException("Сохранённые IP разрешены только для подтверждённого физического сетевого пути.");
        // Keep existing Wi-Fi keys so installed Campus records remain usable.
        // The USB gateway scopes candidates, but does not identify LTE vs Campus.
        var scope = path.IsWifi && !string.IsNullOrWhiteSpace(path.Ssid)
            ? path.Ssid.ToUpperInvariant()
            : $"{(int)path.InterfaceType}:{path.Identity.NetworkDiscriminator}";
        return $"{adapter.Id.ToUpperInvariant()}|{scope}|{host.ToLowerInvariant()}";
    }

    private static IPAddress[] Parse(IEnumerable<string>? values) => (values ?? [])
        .Select(value => IPAddress.TryParse(value, out var ip) ? ip : null)
        .Where(ip => ip is { AddressFamily: AddressFamily.InterNetwork } && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.Broadcast))
        .Cast<IPAddress>().Distinct().Take(4).ToArray();

    private static async Task ValidateAsync(string host, PhysicalAdapter adapter, IPAddress address, CancellationToken token)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        DirectNetworkConnector.BindSocket(socket, adapter);
        await socket.ConnectAsync(new IPEndPoint(address, host == "api.is74.ru" ? 443 : 80), token).ConfigureAwait(false);
        if (host == "api.is74.ru")
        {
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var tls = new SslStream(stream);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, token).ConfigureAwait(false);
        }
    }
}
