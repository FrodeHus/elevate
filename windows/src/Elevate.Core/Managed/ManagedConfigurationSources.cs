using Microsoft.Win32;

namespace Elevate.Core.Managed;

/// <summary>Chooses the platform-appropriate managed configuration source.</summary>
public static class ManagedConfigurationSources
{
    /// <summary>Registry path, relative to HKLM/HKCU, that GPO/registry policy is read from.</summary>
    public const string RegistryPath = @"SOFTWARE\Policies\Reothor\Elevate";

    /// <summary>The registry source on Windows, or the managed.json file on every other platform.</summary>
    public static IManagedConfigurationSource Default()
    {
        if (OperatingSystem.IsWindows())
        {
            return new RegistryManagedSource(
                new WindowsRegistryView(RegistryHive.LocalMachine),
                new WindowsRegistryView(RegistryHive.CurrentUser));
        }

        return new JsonFileManagedSource(JsonFileManagedSource.DefaultPath);
    }
}
