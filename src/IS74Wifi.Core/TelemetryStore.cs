using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IS74Wifi.Core;

public sealed class TelemetryIdentityStore(AppPaths paths)
{
    private readonly object gate = new();

    public string GetOrCreate()
    {
        lock (gate)
        {
            paths.EnsureDirectories();
            var path = paths.TelemetryInstallIdFile;
            if (File.Exists(path))
            {
                try
                {
                    var existing = File.ReadAllText(path).Trim();
                    if (existing.Length == 32 && existing.All(Uri.IsHexDigit))
                    {
                        return existing.ToLowerInvariant();
                    }
                }
                catch
                {
                    // Fall through and repair the identity file.
                }
            }

            var installId = Guid.NewGuid().ToString("N");
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, installId);
            File.Move(temp, path, overwrite: true);
            return installId;
        }
    }
}

public sealed record TelemetryQueueBatch(
    string BatchId,
    IReadOnlyList<string> EventJson,
    IReadOnlyList<string> Files);

public sealed class TelemetryQueue(
    AppPaths paths,
    long maxQueueBytes = 10 * 1024 * 1024,
    int maxQueueFiles = 1024)
{
    public void Enqueue(IReadOnlyList<string> eventJson)
    {
        if (eventJson.Count == 0)
        {
            return;
        }

        try
        {
            paths.EnsureDirectories();
            Directory.CreateDirectory(paths.TelemetryPendingDirectory);

            var name = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.jsonl";
            var finalPath = Path.Combine(paths.TelemetryPendingDirectory, name);
            var tempPath = finalPath + ".tmp";

            File.WriteAllLines(tempPath, eventJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, finalPath, overwrite: false);
            TrimBestEffort();
        }
        catch
        {
            // Telemetry is observational and must never affect authorization.
        }
    }

    public TelemetryQueueBatch? ReadOldestBatch(
        int maxEvents = TelemetryContract.MaxUploadEvents,
        int maxPayloadBytes = TelemetryContract.MaxUploadPayloadBytes)
    {
        try
        {
            if (!Directory.Exists(paths.TelemetryPendingDirectory))
            {
                return null;
            }

            var selectedFiles = new List<string>();
            var events = new List<string>();
            var approximateBytes = 64;

            foreach (var file in Directory.EnumerateFiles(paths.TelemetryPendingDirectory, "*.jsonl")
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file)
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .ToArray();
                }
                catch
                {
                    continue;
                }

                if (lines.Length == 0)
                {
                    TryDelete(file);
                    continue;
                }

                var fileBytes = lines.Sum(line => Encoding.UTF8.GetByteCount(line) + 1);
                if (events.Count > 0 &&
                    (events.Count + lines.Length > maxEvents || approximateBytes + fileBytes > maxPayloadBytes))
                {
                    break;
                }

                if (lines.Length > maxEvents || fileBytes > maxPayloadBytes)
                {
                    // An individual trace should never reach this size. Quarantine it
                    // rather than permanently blocking every later telemetry upload.
                    Quarantine(file);
                    continue;
                }

                selectedFiles.Add(file);
                events.AddRange(lines);
                approximateBytes += fileBytes;

                if (events.Count >= maxEvents || approximateBytes >= maxPayloadBytes)
                {
                    break;
                }
            }

            if (events.Count == 0)
            {
                return null;
            }

            var hashInput = Encoding.UTF8.GetBytes(string.Join('\n', events));
            var hash = Convert.ToHexString(SHA256.HashData(hashInput)).ToLowerInvariant();
            return new TelemetryQueueBatch(
                "batch-" + hash[..24],
                events,
                selectedFiles);
        }
        catch
        {
            return null;
        }
    }

    public void Complete(TelemetryQueueBatch batch)
    {
        foreach (var file in batch.Files)
        {
            TryDelete(file);
        }
    }

    public bool HasPending
    {
        get
        {
            try
            {
                return Directory.Exists(paths.TelemetryPendingDirectory) &&
                       Directory.EnumerateFiles(paths.TelemetryPendingDirectory, "*.jsonl").Any();
            }
            catch
            {
                return false;
            }
        }
    }

    private void TrimBestEffort()
    {
        try
        {
            var files = Directory.EnumerateFiles(paths.TelemetryPendingDirectory, "*.jsonl")
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            long total = files.Sum(info => info.Exists ? info.Length : 0L);
            while (files.Count > maxQueueFiles || total > maxQueueBytes)
            {
                var oldest = files[0];
                files.RemoveAt(0);
                total -= oldest.Exists ? oldest.Length : 0L;
                TryDelete(oldest.FullName);
            }
        }
        catch
        {
            // Best-effort retention only.
        }
    }

    private void Quarantine(string file)
    {
        try
        {
            Directory.CreateDirectory(paths.TelemetryRejectedDirectory);
            var target = Path.Combine(paths.TelemetryRejectedDirectory, Path.GetFileName(file));
            File.Move(file, target, overwrite: true);
        }
        catch
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

public sealed class TelemetryUploadStateStore(AppPaths paths, JsonFileStore json)
{
    public TelemetryUploadState Load() =>
        json.Read(paths.TelemetryUploadStateFile, PersistenceJsonContext.Default.TelemetryUploadState)
        ?? new TelemetryUploadState(null, null, 0);

    public void Save(TelemetryUploadState state) =>
        json.Write(paths.TelemetryUploadStateFile, state, PersistenceJsonContext.Default.TelemetryUploadState);
}

public static class TelemetrySerialization
{
    public static string Serialize(TelemetryAttemptEvent value) =>
        JsonSerializer.Serialize(value, TelemetryJsonContext.Default.TelemetryAttemptEvent);

    public static string Serialize(TelemetryMailboxPollEvent value) =>
        JsonSerializer.Serialize(value, TelemetryJsonContext.Default.TelemetryMailboxPollEvent);

    public static string Serialize(TelemetryInternetProbeEvent value) =>
        JsonSerializer.Serialize(value, TelemetryJsonContext.Default.TelemetryInternetProbeEvent);

    public static string Serialize(TelemetryPortalResponseEvent value) =>
        JsonSerializer.Serialize(value, TelemetryJsonContext.Default.TelemetryPortalResponseEvent);

    public static string Serialize(TelemetryErrorEvent value) =>
        JsonSerializer.Serialize(value, TelemetryJsonContext.Default.TelemetryErrorEvent);

    public static string Serialize(TelemetrySpeedTestEvent value) =>
        JsonSerializer.Serialize(value, TelemetryJsonContext.Default.TelemetrySpeedTestEvent);

    public static string Serialize(TelemetryLeaderboardEntry value) =>
        JsonSerializer.Serialize(value, TelemetryJsonContext.Default.TelemetryLeaderboardEntry);
}
