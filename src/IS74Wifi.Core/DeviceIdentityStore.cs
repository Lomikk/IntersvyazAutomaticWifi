using System.Security.Cryptography;

namespace IS74Wifi.Core;

public sealed class DeviceIdentityStore(AppPaths paths)
{
    public string? Read()
    {
        if (!File.Exists(paths.DeviceIdFile))
        {
            return null;
        }

        var value = File.ReadAllText(paths.DeviceIdFile).Trim();
        return value.Length == 16 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }

    public string GetOrCreate()
    {
        paths.EnsureDirectories();
        var existing = Read();
        if (existing is not null)
        {
            return existing;
        }

        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var value = Convert.ToHexString(bytes).ToLowerInvariant();
        File.WriteAllText(paths.DeviceIdFile, value);
        return value;
    }
}
