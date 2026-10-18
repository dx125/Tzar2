using Godot;
using Tzar2.Game.Input;

namespace Tzar2.Client.Game;

/// <summary>Maps Godot keys onto the engine-free <see cref="GameKey"/>s of the hotkey table.</summary>
public static class KeyMap
{
    /// <summary>The game key of a Godot key, or <see cref="GameKey.None"/>.</summary>
    public static GameKey From(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            return GameKey.A + (int)(key - Key.A);
        }

        if (key >= Key.Key0 && key <= Key.Key9)
        {
            return GameKey.D0 + (int)(key - Key.Key0);
        }

        if (key >= Key.Kp0 && key <= Key.Kp9)
        {
            return GameKey.D0 + (int)(key - Key.Kp0);
        }

        if (key >= Key.F1 && key <= Key.F12)
        {
            return GameKey.F1 + (int)(key - Key.F1);
        }

        return key switch
        {
            Key.Space => GameKey.Space,
            Key.Enter or Key.KpEnter => GameKey.Enter,
            Key.Escape => GameKey.Escape,
            Key.Backspace => GameKey.Backspace,
            Key.Tab => GameKey.Tab,
            Key.Delete => GameKey.Delete,
            Key.Home => GameKey.Home,
            Key.End => GameKey.End,
            Key.Pageup => GameKey.PageUp,
            Key.Pagedown => GameKey.PageDown,
            Key.Left => GameKey.Left,
            Key.Right => GameKey.Right,
            Key.Up => GameKey.Up,
            Key.Down => GameKey.Down,
            Key.Plus or Key.Equal or Key.KpAdd => GameKey.Plus,
            Key.Minus or Key.KpSubtract => GameKey.Minus,
            Key.Quoteleft => GameKey.Backquote,
            Key.Slash or Key.KpDivide => GameKey.Slash,
            Key.Backslash => GameKey.Backslash,
            Key.Bracketleft => GameKey.BracketLeft,
            Key.Bracketright => GameKey.BracketRight,
            Key.Comma or Key.Less => GameKey.Comma,
            Key.Period or Key.Greater => GameKey.Period,
            Key.Pause => GameKey.Pause,
            _ => GameKey.None,
        };
    }

    /// <summary>The modifiers of an input event.</summary>
    public static KeyMods Mods(InputEventWithModifiers e)
        => (e.ShiftPressed ? KeyMods.Shift : KeyMods.None) | (e.CtrlPressed || e.MetaPressed ? KeyMods.Ctrl : KeyMods.None) | (e.AltPressed ? KeyMods.Alt : KeyMods.None);
}
