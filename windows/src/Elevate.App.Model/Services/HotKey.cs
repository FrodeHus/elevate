namespace Elevate.App.Services;

/// <summary>
/// One global shortcut: a virtual-key code and <c>RegisterHotKey</c> modifier flags, plus the text
/// Settings and the diagnostics report show for it. Port of the macOS <c>HotKeyBinding</c>.
/// </summary>
public sealed record HotKeyBinding(uint Modifiers, uint Key, string Display)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>A shortcut needs at least one of Ctrl, Alt or Win, or every plain key press would fire it.</summary>
    public bool HasRequiredModifier => (Modifiers & (ModAlt | ModControl | ModWin)) != 0;

    /// <summary>"Ctrl+Shift+E" style text for a modifier set and a key name.</summary>
    public static string DisplayFor(uint modifiers, string keyName)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(keyName);
        return string.Join("+", parts);
    }
}

/// <summary>
/// Registers one global hot key. The app's implementation registers it on the tray's hidden
/// window; the model tests use <see cref="NoopHotKeyCenter"/>. Port of the macOS <c>HotKeyCenter</c>.
/// </summary>
public interface IHotKeyCenter
{
    /// <summary>Raised on the UI thread when the shortcut is pressed.</summary>
    Action? OnFire { get; set; }

    /// <summary>Registers <paramref name="binding"/>, replacing any earlier one.</summary>
    /// <exception cref="InvalidOperationException">The shortcut could not be registered, typically because another app owns it.</exception>
    void Register(HotKeyBinding binding);

    void Unregister();
}

public sealed class NoopHotKeyCenter : IHotKeyCenter
{
    public Action? OnFire { get; set; }

    public HotKeyBinding? Registered { get; private set; }

    public void Register(HotKeyBinding binding) => Registered = binding;

    public void Unregister() => Registered = null;
}
