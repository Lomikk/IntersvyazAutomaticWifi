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
