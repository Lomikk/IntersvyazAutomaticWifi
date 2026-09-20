using Microsoft.Win32;

namespace IS74Wifi.Core;

public sealed class WindowsInstalledAppRegistration(string productKeyName = "IS74Wifi")
{
    private const string UninstallRootPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string DisplayName = "IS74Wifi";
    private const string Publisher = "Lomikk";

    public string RegistryKeyPath => $@"{UninstallRootPath}\{productKeyName}";

    public void Register(ProgramInstallation installation, string productVersion)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!installation.IsInstalled)
        {
            throw new InvalidOperationException("Нельзя зарегистрировать удаление IS74Wifi: установленная копия программы не найдена.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, writable: true)
                        ?? throw new InvalidOperationException("Не удалось зарегистрировать IS74Wifi в списке установленных приложений Windows.");

        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", ToDisplayVersion(productVersion), RegistryValueKind.String);
        key.SetValue("Publisher", Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", installation.InstallDirectory, RegistryValueKind.String);
        key.SetValue("DisplayIcon", installation.ExecutablePath, RegistryValueKind.String);
        key.SetValue("UninstallString", BuildUninstallCommand(installation.ExecutablePath), RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public void Unregister()
    {
        using var root = Registry.CurrentUser.OpenSubKey(UninstallRootPath, writable: true);
        root?.DeleteSubKeyTree(productKeyName, throwOnMissingSubKey: false);
    }

    public bool IsRegisteredFor(ProgramInstallation installation, string productVersion)
    {
        ArgumentNullException.ThrowIfNull(installation);
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
        if (key is null) return false;

        return string.Equals(key.GetValue("InstallLocation") as string, installation.InstallDirectory, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(key.GetValue("UninstallString") as string, BuildUninstallCommand(installation.ExecutablePath), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(key.GetValue("DisplayVersion") as string, ToDisplayVersion(productVersion), StringComparison.OrdinalIgnoreCase);
    }

    public bool Reconcile(ProgramInstallation installation, string productVersion)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.IsInstalled)
        {
            if (IsRegisteredFor(installation, productVersion))
            {
                return false;
            }

            Register(installation, productVersion);
            return true;
        }

        using (var existing = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false))
        {
            if (existing is null)
            {
                return false;
            }
        }

        Unregister();
        return true;
    }

    public static string BuildUninstallCommand(string executablePath) =>
        $"\"{Path.GetFullPath(executablePath)}\" uninstall";

    public static string ToDisplayVersion(string productVersion) =>
        productVersion.Trim().TrimStart('v', 'V');
}
