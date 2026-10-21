using Godot;

namespace Tzar2.Client.Game;

/// <summary>
/// Hardware cursors from the imported <c>IMAGES/CURSORS</c> (TDD §13.8) and Tzar2's own (<see cref="OwnCursors"/>).
/// ⚠ UNK-40: the original hotspots are not decoded yet; the hand points with its top-left pixel and every other
/// original cursor with its centre.
/// </summary>
public static class CursorCache
{
    private static readonly System.Collections.Generic.Dictionary<string, ImageTexture> s_own = [];
    private static string? s_current;

    /// <summary>The name that shows the system's own pointer.</summary>
    public const string System = "system";

    /// <summary>The original's default pointer (<c>IMAGES/CURSORS/S_DEF</c>).</summary>
    public const string Default = "s_def";

    /// <summary>Shows cursor <paramref name="name"/> (<c>s_hand</c>, <c>s_attack</c>..., <see cref="System"/>); unchanged names cost nothing.</summary>
    public static void Apply(UiAssets ui, string name)
    {
        if (name == s_current || DisplayServer.GetName() == "headless")
        {
            return;
        }

        s_current = name;
        if (name == System)
        {
            Input.SetCustomMouseCursor(null);
            return;
        }

        if (OwnCursors.Has(name))
        {
            if (!s_own.TryGetValue(name, out ImageTexture? own) && OwnCursors.Paint(name) is { } painted)
            {
                own = ImageTexture.CreateFromImage(painted);
                s_own[name] = own;
            }

            Input.SetCustomMouseCursor(own, Input.CursorShape.Arrow, OwnCursors.Hotspot(name));
            return;
        }

        if (ui?.Texture($"art/cursors/{name}.t2img") is not { } texture)
        {
            Input.SetCustomMouseCursor(null);
            return;
        }

        // The default pointer and the hand point with their top-left pixel (the spearhead's tip is there); the others
        // with their centre (UNK-40).
        Vector2 hotspot = name is "s_hand" or Default ? Vector2.Zero : texture.GetSize() / 2;
        Input.SetCustomMouseCursor(texture, Input.CursorShape.Arrow, hotspot);
    }

    /// <summary>Restores the system cursor.</summary>
    public static void Reset()
    {
        s_current = null;
        Input.SetCustomMouseCursor(null);
    }
}
