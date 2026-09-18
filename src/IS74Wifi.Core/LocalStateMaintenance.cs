namespace IS74Wifi.Core;

public sealed class LocalStateMaintenance(AppPaths paths)
{
    public void ResetRegistration()
    {
        foreach (var path in new[]
                 {
                     paths.SecretsFile,
                     paths.SessionMetaFile,
                     paths.DeviceMetadataFile,
                     paths.RuntimeStateFile,
                     paths.DeviceIdFile
                 })
        {
            TryDeleteFile(path);
        }
    }

    public void PurgeAllData()
    {
        if (!Directory.Exists(paths.Root))
        {
            return;
        }

        try
        {
            Directory.Delete(paths.Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
