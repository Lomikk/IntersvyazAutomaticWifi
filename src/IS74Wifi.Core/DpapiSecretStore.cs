using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IS74Wifi.Core;

public sealed class DpapiSecretStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public void Save(StoredSecrets secrets)
    {
        paths.EnsureDirectories();
        var plaintext = JsonSerializer.Serialize(secrets, JsonOptions);
        var plaintextBytes = Encoding.Unicode.GetBytes(plaintext);
        try
        {
            var protectedBytes = ProtectedData.Protect(plaintextBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllText(paths.SecretsFile, Convert.ToHexString(protectedBytes).ToLowerInvariant(), Encoding.ASCII);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public StoredSecrets? Load()
    {
        if (!File.Exists(paths.SecretsFile))
        {
            return null;
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromHexString(File.ReadAllText(paths.SecretsFile).Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        byte[]? plaintextBytes = null;
        try
        {
            plaintextBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var plaintext = Encoding.Unicode.GetString(plaintextBytes);
            return JsonSerializer.Deserialize<StoredSecrets>(plaintext, JsonOptions);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintextBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
    }
}
