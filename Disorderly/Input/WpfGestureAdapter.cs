using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using Disorderly.Routing;

namespace Disorderly.Input;

public readonly record struct KeyboardTranslation(string? Text, bool IsDeadKey = false);

public interface IKeyboardTranslator
{
    KeyboardTranslation Translate(Key key);
}

/// <summary>Only key-down dispatches. Composition/text events update ownership but never route.</summary>
public sealed class WpfGestureAdapter(IKeyboardTranslator? translator = null)
{
    private readonly IKeyboardTranslator translator = translator ?? new WindowsKeyboardTranslator();
    private bool composing;

    public void BeginComposition() => this.composing = true;
    public void CompleteComposition() => this.composing = false;
    public void Reset() => this.composing = false;

    public bool TryTranslate(Key key, Key systemKey, ModifierKeys modifiers, bool repeat, bool rightAltDown,
        bool contextualInput, out LogicalGesture gesture)
    {
        gesture = default;
        if (this.composing && key == Key.Escape)
        {
            // Let WPF cancel composition, without also navigating the context.
            this.composing = false;
            return false;
        }

        if (!contextualInput || repeat || this.composing || key is Key.ImeProcessed or Key.DeadCharProcessed
            || (rightAltDown && modifiers.HasFlag(ModifierKeys.Control)))
        {
            return false;
        }

        key = key == Key.System ? systemKey : key;
        GestureModifiers logicalModifiers = GestureModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            logicalModifiers |= GestureModifiers.Control;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            logicalModifiers |= GestureModifiers.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            logicalModifiers |= GestureModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            logicalModifiers |= GestureModifiers.Meta;
        }

        string? named = key switch
        {
            Key.Escape => "Escape", Key.Tab => "Tab", Key.Back => "Backspace", Key.Return => "Enter",
            Key.Space => "Space", Key.Delete => "Delete", Key.Insert => "Insert", Key.Home => "Home",
            Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",
            >= Key.F1 and <= Key.F24 => key.ToString(),
            _ => null,
        };
        if (named is not null)
        {
            gesture = new LogicalGesture(named, logicalModifiers);
            return true;
        }

        if (key is Key.None or Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return false;
        }

        KeyboardTranslation translation = this.translator.Translate(key);
        if (translation.IsDeadKey)
        {
            this.composing = true;
            return false;
        }

        gesture = new LogicalGesture(translation.Text!, logicalModifiers);
        return gesture.IsValid;
    }
}

/// <summary>Thread-layout translation with ToUnicodeEx's non-mutating mode (Windows 10 1607+).</summary>
public sealed class WindowsKeyboardTranslator : IKeyboardTranslator
{
    public KeyboardTranslation Translate(Key key)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            return default;
        }

        byte[] state = new byte[256];
        if (!GetKeyboardState(state))
        {
            return default;
        }

        // Ctrl/Alt may produce control characters rather than logical key identities.
        // AltGr is excluded by the adapter before this call. Preserve Shift/Caps/NumLock.
        foreach (int modifier in new[] { 0x11, 0x12, 0xA2, 0xA3, 0xA4, 0xA5 })
        {
            state[modifier] = 0;
        }

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        nint layout = GetKeyboardLayout(0);
        StringBuilder text = new(8);
        int count = ToUnicodeEx(virtualKey, MapVirtualKeyEx(virtualKey, 0, layout), state, text, text.Capacity, 4, layout);
        // Never flush a dead key with a second mutating translation. Multi-scalar output is ambiguous.
        return count < 0 ? new KeyboardTranslation(null, true)
            : count > 0 ? new KeyboardTranslation(text.ToString(0, count)) : default;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint key, uint scanCode, byte[] state, StringBuilder text, int capacity, uint flags, nint layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKeyEx(uint key, uint mapType, nint layout);
}