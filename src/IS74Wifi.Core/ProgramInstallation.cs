namespace IS74Wifi.Core;

public sealed class ProgramInstallation
{
    private const string ProgramDirectoryName = "IS74Wifi";
    private const string ExecutableName = "IS74Wifi.exe";
    private const string VersionMarkerName = "version.txt";

    public ProgramInstallation(string? localAppDataOverride = null)
    {
        var localAppData = localAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        InstallDirectory = Path.Combine(localAppData, "Programs", ProgramDirectoryName);
        ExecutablePath = Path.Combine(InstallDirectory, ExecutableName);
        VersionMarkerPath = Path.Combine(InstallDirectory, VersionMarkerName);
    }

    public string InstallDirectory { get; }
    public string ExecutablePath { get; }
    public string VersionMarkerPath { get; }

    public bool IsInstalled => File.Exists(ExecutablePath);

    public bool IsInstalledExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        return PathsEqual(executablePath, ExecutablePath);
    }

    public string? ReadInstalledVersion()
    {
        try
        {
            if (!File.Exists(VersionMarkerPath)) return null;
            var value = File.ReadAllText(VersionMarkerPath).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string InstallFrom(string sourceExecutablePath, string sourceVersion)
    {
        if (string.IsNullOrWhiteSpace(sourceExecutablePath))
            throw new ArgumentException("Source executable path is required.", nameof(sourceExecutablePath));
        if (!File.Exists(sourceExecutablePath))
            throw new FileNotFoundException("IS74Wifi executable was not found.", sourceExecutablePath);

        if (SemanticVersion.TryParse(sourceVersion, out var source) &&
            SemanticVersion.TryParse(ReadInstalledVersion(), out var installed) &&
            installed.CompareTo(source) > 0)
        {
            throw new InvalidOperationException($"Установленная версия {ReadInstalledVersion()} новее запускаемой {sourceVersion}. Установка более старой версии отменена.");
        }

        Directory.CreateDirectory(InstallDirectory);

        if (!IsInstalledExecutable(sourceExecutablePath))
        {
            var tempPath = Path.Combine(InstallDirectory, ExecutableName + ".new");
            File.Copy(sourceExecutablePath, tempPath, overwrite: true);
            File.Move(tempPath, ExecutablePath, overwrite: true);
        }

        WriteVersionMarker(sourceVersion);
        return ExecutablePath;
    }

    public void WriteVersionMarker(string version)
    {
        Directory.CreateDirectory(InstallDirectory);
        var tempPath = VersionMarkerPath + ".tmp";
        File.WriteAllText(tempPath, version.Trim() + Environment.NewLine);
        File.Move(tempPath, VersionMarkerPath, overwrite: true);
    }

    public void DeleteInstalledFilesIfNotRunning(string? currentExecutablePath)
    {
        if (!Directory.Exists(InstallDirectory)) return;
        if (IsInstalledExecutable(currentExecutablePath))
            throw new InvalidOperationException("Нельзя удалить установленный IS74Wifi.exe, пока он выполняется.");

        try
        {
            Directory.Delete(InstallDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    public static bool PathsEqual(string first, string second)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
