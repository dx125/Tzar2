using Godot;

namespace Tzar2.Client.Game;

/// <summary>
/// Hardware cursors from the imported <c>IMAGES/CURSORS</c> (TDD §13.8). ⚠ UNK-40: the original hotspots are not
/// decoded yet; the hand points with its top-left pixel and every other cursor with its centre.
/// </summary>
public static class CursorCache
{
    private static string? s_current;

    /// <summary>Shows cursor <paramref name="name"/> (<c>s_hand</c>, <c>s_attack</c>...); unchanged names cost nothing.</summary>
    public static void Apply(UiAssets ui, string name)
    {
        if (name == s_current || DisplayServer.GetName() == "headless")
        {
            return;
        }

        s_current = name;
        if (ui?.Texture($"art/cursors/{name}.t2img") is not { } texture)
        {
            Input.SetCustomMouseCursor(null);
            return;
        }

        Vector2 hotspot = name == "s_hand" ? Vector2.Zero : texture.GetSize() / 2;
        Input.SetCustomMouseCursor(texture, Input.CursorShape.Arrow, hotspot);
    }

    /// <summary>Restores the system cursor.</summary>
    public static void Reset()
    {
        s_current = null;
        Input.SetCustomMouseCursor(null);
    }
}
