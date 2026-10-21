using Godot;

namespace Tzar2.Client.Game;

/// <summary>
/// Hardware cursors from the imported <c>IMAGES/CURSORS</c> (TDD §13.8) and Tzar2's own (<see cref="OwnCursors"/>). Each
/// original cursor clicks with the <c>hotspot</c> of its IMAGES.INI software-cursor entry (<c>cs_arrow</c> = <c>s_def</c>
/// at 2, 0; UNK-40); a cursor without an entry clicks with its centre.
/// </summary>
public static class CursorCache
{
    // The IMAGES.INI entry of each cursor picture (the game's cs_* names).
    private static readonly System.Collections.Generic.Dictionary<string, string> s_entries = new(global::System.StringComparer.OrdinalIgnoreCase)
    {
        ["s_def"] = "cs_arrow",
        ["s_attack"] = "cs_attack",
        ["cs_attackset"] = "cs_attackset",
        ["cs_attacktype"] = "cs_attacktype",
        ["s_guard"] = "cs_guard",
        ["s_harvest"] = "cs_harvest",
        ["s_move"] = "cs_move",
        ["s_none"] = "cs_none",
        ["s_patrol"] = "cs_patrol",
        ["s_patrol2"] = "cs_patrol_ex",
        ["s_hand"] = "cs_pick",
        ["s_repair"] = "cs_repair",
        ["s_spell"] = "cs_spell",
        ["s_spell_good"] = "cs_magicmodify",
    };

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

        Vector2 hotspot = s_entries.TryGetValue(name, out string? entry) && ui.ImageInts(entry, "hotspot") is { Length: >= 2 } hot
            ? new Vector2(hot[0], hot[1])
            : texture.GetSize() / 2;
        Input.SetCustomMouseCursor(texture, Input.CursorShape.Arrow, hotspot);
    }

    /// <summary>Restores the system cursor.</summary>
    public static void Reset()
    {
        s_current = null;
        Input.SetCustomMouseCursor(null);
    }
}
