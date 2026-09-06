using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Elevate.App;

public static class Program
{
    /// <summary>Named mutex that keeps the tray app single-instance per user session.</summary>
    private const string InstanceMutexName = "Reothor.Elevate";

    /// <summary>Broadcast by a second launch so the running instance opens its flyout.</summary>
    public const string OpenMessageName = "Reothor.Elevate.Open";

    [STAThread]
    public static int Main(string[] args)
    {
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // A second launch (Start menu, winget's post-install run) hands over to the running
            // instance, which owns the tray icon: ask it to open the flyout and leave.
            var message = Windows.Win32.PInvoke.RegisterWindowMessage(OpenMessageName);
            Windows.Win32.PInvoke.PostMessage(Windows.Win32.Foundation.HWND.HWND_BROADCAST, message, 0, 0);
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
