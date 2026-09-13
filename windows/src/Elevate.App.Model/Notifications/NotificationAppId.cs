namespace Elevate.App.Notifications;

/// <summary>One <c>HKCU\Software\Classes\AppUserModelId\*</c> key: the id and its <c>CustomActivator</c> CLSID, if any.</summary>
public sealed record AppUserModelEntry(string Id, string? ActivatorClsid);

/// <summary>
/// Finds the Application User Model ID the Windows App SDK registered for this executable.
/// <c>AppNotificationManager.Register()</c> derives the id from a hash of the process path and
/// writes it with a <c>CustomActivator</c> CLSID whose <c>LocalServer32</c> command launches the
/// same executable; that id is what <c>ToastNotificationManager.CreateToastNotifier</c> needs so
/// scheduled toasts land under the same app entry and activate through the same COM server.
/// </summary>
public static class NotificationAppId
{
    /// <summary>
    /// The first id whose activator's <paramref name="localServer32"/> command names
    /// <paramref name="exePath"/>, or null when the SDK has not registered this executable.
    /// </summary>
    public static string? Find(string exePath, IEnumerable<AppUserModelEntry> entries, Func<string, string?> localServer32)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(localServer32);

        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.ActivatorClsid))
            {
                continue;
            }

            var command = localServer32(entry.ActivatorClsid);
            if (command is not null && command.Contains(exePath, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Id;
            }
        }

        return null;
    }
}
