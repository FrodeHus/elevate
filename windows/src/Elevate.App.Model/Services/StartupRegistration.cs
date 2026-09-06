using Microsoft.Win32;

namespace Elevate.App.Services;

/// <summary>
/// Whether Elevate starts with the user's sign-in, through the per-user <c>Run</c> registry key.
/// Read from the registry every time: the user can change it in Task Manager or Settings, so a
/// stored copy would go stale behind our back.
/// </summary>
public static class StartupRegistration
{
    public const string ValueName = "Elevate";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The command the Run entry should hold: this executable, quoted.</summary>
    public static string Command => "\"" + (Environment.ProcessPath ?? string.Empty) + "\"";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Registers or unregisters the Run entry. Throws so the view can show the reason and put the toggle back.</summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("The Run key could not be opened");
        if (enabled)
        {
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
