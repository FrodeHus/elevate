using Elevate.App.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Elevate.App.Tray;

/// <summary>
/// One global hot key over <c>RegisterHotKey</c> on the tray's hidden window, whose procedure
/// receives <c>WM_HOTKEY</c>. Port of the macOS <c>HotKeyCenter</c>.
/// </summary>
internal sealed class HotKeyCenter : IHotKeyCenter
{
    private const int Id = 1;

    private readonly HWND _hwnd;
    private bool _registered;

    public HotKeyCenter(TrayIcon tray)
    {
        ArgumentNullException.ThrowIfNull(tray);
        _hwnd = tray.Handle;
        tray.HotKeyPressed += () => OnFire?.Invoke();
    }

    public Action? OnFire { get; set; }

    public void Register(HotKeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Unregister();
        var modifiers = (HOT_KEY_MODIFIERS)binding.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (!PInvoke.RegisterHotKey(_hwnd, Id, modifiers, binding.Key))
        {
            throw new InvalidOperationException("The shortcut could not be registered; it may be in use by another app.");
        }

        _registered = true;
    }

    public void Unregister()
    {
        if (_registered)
        {
            PInvoke.UnregisterHotKey(_hwnd, Id);
            _registered = false;
        }
    }
}
