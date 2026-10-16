using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace IS74Wifi.Core;

public sealed class JsonFileStore
{
    public T? Read<T>(string path, JsonTypeInfo<T> jsonTypeInfo)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), jsonTypeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    /// <summary>
    /// Strict read for durable state: absence, invalid JSON and I/O failures must
    /// remain distinguishable to the caller. Other JSON stores can keep their
    /// legacy best-effort Read behavior.
    /// </summary>
    public T? ReadStrict<T>(string path, JsonTypeInfo<T> jsonTypeInfo) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), jsonTypeInfo);

    /// <summary>
    /// Atomically replace the primary while preserving its previous generation.
    /// The caller must first validate the existing primary; otherwise Replace
    /// could rotate corrupted content into the only good backup.
    /// </summary>
    public void WriteWithBackup<T>(
        string path, string backupPath, T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var serialized = JsonSerializer.Serialize(value, jsonTypeInfo);
            File.WriteAllText(temporaryPath, serialized);
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, backupPath);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    public void Write<T>(string path, T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var json = JsonSerializer.Serialize(value, jsonTypeInfo);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }
}
