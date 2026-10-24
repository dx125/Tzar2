using Godot;
using Tzar2.Game.Ui;

namespace Tzar2.Client.Game;

/// <summary>
/// Hardware cursors from the imported <c>IMAGES/CURSORS</c> (TDD §13.8), named by the original's IMAGES.INI software
/// cursor entries (<c>cs_*</c>, <see cref="OrderCursors"/>). Each clicks with its entry's <c>hotspot</c> (CTRL-014;
/// <c>cs_arrow</c> = <c>s_def</c> at 2, 0; UNK-40); a cursor without an entry clicks with its centre.
/// </summary>
public static class CursorCache
{
    // The picture of each entry (its IMAGES.INI image, cursors\*.bmp). cs_magicharm shares s_spell with its own hotspot.
    private static readonly System.Collections.Generic.Dictionary<string, string> s_pictures = new(global::System.StringComparer.OrdinalIgnoreCase)
    {
        [OrderCursors.Arrow] = "s_def",
        [OrderCursors.Attack] = "s_attack",
        [OrderCursors.AttackSet] = "cs_attackset",
        [OrderCursors.AttackType] = "cs_attacktype",
        [OrderCursors.Guard] = "s_guard",
        [OrderCursors.Harvest] = "s_harvest",
        [OrderCursors.MagicHarm] = "s_spell",
        ["cs_magicmodify"] = "s_spell_good",
        [OrderCursors.Move] = "s_move",
        [OrderCursors.None] = "s_none",
        ["cs_patrol"] = "s_patrol",
        [OrderCursors.Patrol] = "s_patrol2",
        [OrderCursors.Pick] = "s_hand",
        [OrderCursors.Repair] = "s_repair",
        [OrderCursors.Spell] = "s_spell",
    };

    private static string? s_current;

    /// <summary>The name that shows the system's own pointer.</summary>
    public const string System = "system";

    /// <summary>The original's default pointer (<c>IMAGES/CURSORS/S_DEF</c>).</summary>
    public const string Default = OrderCursors.Arrow;

    /// <summary>Shows cursor <paramref name="name"/> (a <c>cs_*</c> entry, or <see cref="System"/>); unchanged names cost nothing.</summary>
    public static void Apply(UiAssets ui, string name)
    {
        if (name == s_current || DisplayServer.GetName() == "headless")
        {
            return;
        }

        s_current = name;
        string picture = s_pictures.TryGetValue(name, out string? p) ? p : name;
        if (name == System || ui?.Texture($"art/cursors/{picture}.t2img") is not { } texture)
        {
            Input.SetCustomMouseCursor(null);
            return;
        }

        Vector2 hotspot = ui.ImageInts(name, "hotspot") is { Length: >= 2 } hot ? new Vector2(hot[0], hot[1]) : texture.GetSize() / 2;
        Input.SetCustomMouseCursor(texture, Input.CursorShape.Arrow, hotspot);
    }

    /// <summary>Restores the system cursor.</summary>
    public static void Reset()
    {
        s_current = null;
        Input.SetCustomMouseCursor(null);
    }
}
