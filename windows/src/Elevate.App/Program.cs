using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Elevate.App;

public static class Program
{
    /// <summary>Named mutex that keeps the tray app single-instance per user session.</summary>
    private const string InstanceMutexName = "Reothor.Elevate";

    [STAThread]
    public static int Main(string[] args)
    {
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // A second launch has nothing to add: the running instance owns the tray icon.
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
