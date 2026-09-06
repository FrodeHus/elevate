using Elevate.App.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Elevate.App.Views;

/// <summary>
/// Click, press a combination; Escape cancels. Requires at least one of Ctrl, Alt or Win. Port of
/// the macOS <c>HotKeyRecorder</c>.
/// </summary>
public sealed partial class HotKeyRecorder : Button
{
    private HotKeyBinding? _binding;
    private bool _recording;

    public HotKeyRecorder()
    {
        MinWidth = 150;
        Click += (_, _) => Start();
        KeyDown += OnKey;
        LostFocus += (_, _) => Stop();
        Apply();
    }

    public HotKeyBinding? Binding
    {
        get => _binding;
        set
        {
            _binding = value;
            Apply();
        }
    }

    /// <summary>Raised when a combination was recorded (or the recording was cancelled with no change).</summary>
    public event EventHandler? BindingChanged;

    private void Start()
    {
        _recording = true;
        Content = "Press keys…";
    }

    private void Stop()
    {
        _recording = false;
        Apply();
    }

    private void Apply() => Content = _recording ? "Press keys…" : (_binding?.Display ?? "Record shortcut");

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            Stop();
            return;
        }

        if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        uint modifiers = 0;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= HotKeyBinding.ModControl;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= HotKeyBinding.ModAlt;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= HotKeyBinding.ModShift;
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            modifiers |= HotKeyBinding.ModWin;
        }

        var candidate = new HotKeyBinding(modifiers, (uint)e.Key, HotKeyBinding.DisplayFor(modifiers, KeyName(e.Key)));
        if (!candidate.HasRequiredModifier)
        {
            // A plain key or Shift alone is not a shortcut; keep listening.
            return;
        }

        _binding = candidate;
        Stop();
        BindingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A short name for the key: the character for letters and digits, the enum name otherwise.</summary>
    internal static string KeyName(VirtualKey key)
    {
        var code = (int)key;
        if (code is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)code).ToString();
        }

        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
        {
            return "Num" + (code - (int)VirtualKey.NumberPad0);
        }

        return key switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Enter => "Enter",
            VirtualKey.Tab => "Tab",
            VirtualKey.Back => "Backspace",
            VirtualKey.Delete => "Delete",
            VirtualKey.Insert => "Insert",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Up => "Up",
            VirtualKey.Down => "Down",
            VirtualKey.Left => "Left",
            VirtualKey.Right => "Right",
            _ => key.ToString(),
        };
    }
}
