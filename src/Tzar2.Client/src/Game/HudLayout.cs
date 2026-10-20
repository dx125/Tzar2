using System;
using System.Globalization;
using Godot;
using Tzar2.Client.Logic;

namespace Tzar2.Client.Game;

/// <summary>
/// One <c>LAYOUT.INI</c> layout (REQ-UI-01: geometry from the imported table, art from <c>IMAGES/SCREENS/S_VIEW</c>):
/// screen size, the world view, minimap, resource boxes, top buttons and the clock. The bottom panel's inner slots
/// (selection view, command card) are not in the INI; their positions are measured on the panel bitmap (same at 800 and
/// 1024, anchored to the bottom edge) and listed here as constants.
/// </summary>
public sealed class HudLayout
{
    /// <summary>Height of the bottom panel art (from the screen bottom).</summary>
    public const int PanelHeight = 154;

    /// <summary>Command card: left of the first slot, pitch, top of the first row relative to the screen bottom.</summary>
    public const int CardLeft = 404;

    /// <summary>Horizontal pitch of card slots.</summary>
    public const int CardPitchX = 62;

    /// <summary>Top of card row 0 as an offset from the bottom of the screen.</summary>
    public const int CardRow0FromBottom = 132;

    /// <summary>Top of card row 1 as an offset from the bottom of the screen.</summary>
    public const int CardRow1FromBottom = 65;

    /// <summary>Card slots.</summary>
    public const int CardSlots = 8;

    /// <summary>Button cell size of <c>ORDERS/BUTTONS.BMP</c>.</summary>
    public const int Button = 48;

    private HudLayout(string name, Vector2I size)
    {
        Name = name;
        Size = size;
    }

    /// <summary>Section name (<c>LAYOUT1024H</c>).</summary>
    public string Name { get; }

    /// <summary>Logical screen size.</summary>
    public Vector2I Size { get; }

    /// <summary>The overlay bitmap.</summary>
    public string Image { get; private init; } = "art/screens/s_view/screen 1024.t2img";

    /// <summary>World view rectangle.</summary>
    public Rect2I View { get; private init; }

    /// <summary>Minimap rectangle.</summary>
    public Rect2I Minimap { get; private init; }

    /// <summary>Resource number boxes: gold, wood, food, stone, population.</summary>
    public Rect2I[] Resources { get; private init; } = [];

    /// <summary>Menu button.</summary>
    public Rect2I MenuButton { get; private init; }

    /// <summary>Menu button image.</summary>
    public string MenuImage { get; private init; } = string.Empty;

    /// <summary>Diplomacy button.</summary>
    public Rect2I DiplomacyButton { get; private init; }

    /// <summary>Diplomacy button image.</summary>
    public string DiplomacyImage { get; private init; } = string.Empty;

    /// <summary>Chat button.</summary>
    public Rect2I ChatButton { get; private init; }

    /// <summary>Chat button image.</summary>
    public string ChatImage { get; private init; } = string.Empty;

    /// <summary>Clock globe.</summary>
    public Rect2I Clock { get; private init; }

    /// <summary>Top of the bottom panel.</summary>
    public int PanelTop => Size.Y - PanelHeight;

    /// <summary>Card slot rectangle.</summary>
    public Rect2I CardSlot(int slot)
        => new(CardLeft + ((slot % 4) * CardPitchX), Size.Y - (slot < 4 ? CardRow0FromBottom : CardRow1FromBottom), Button, Button);

    /// <summary>Portrait slot of the selection view.</summary>
    public Rect2I Portrait => new(201, Size.Y - 132, 48, 48);

    /// <summary>Stat rows of the selection view (20×20 icon boxes).</summary>
    public Rect2I StatRow(int row) => new(262, Size.Y - 131 + (row * 22), 20, 20);

    /// <summary>Area right of the command card (multi-selection icons, production queue); wider on a wide screen.</summary>
    public Rect2I InfoArea => new(648, Size.Y - 140, 236 + Math.Max(0, Size.X - Base.X), 130);

    /// <summary>The bitmap's own size (1024 × 768 or 800 × 600); <see cref="Size"/> is larger or smaller on a Wide screen.</summary>
    public Vector2I Base { get; private init; } = new(1024, 768);

    /// <summary>Whether the screen differs from the bitmap and the HUD is drawn in anchored pieces (ADR 0177).</summary>
    public bool Wide => Size != Base;

    /// <summary>The clock globe's ornament over the panel's left end (LAYOUT.INI <c>mscr1</c>), on screen.</summary>
    public Rect2I GlobeOrnament { get; private init; }

    /// <summary>Where the globe ornament is in the bitmap.</summary>
    public Rect2I GlobeOrnamentSource { get; private init; }

    /// <summary>
    /// Reads <paramref name="section"/> of LAYOUT.INI for a logical screen of <paramref name="screen"/> (default: the
    /// bitmap's own size); falls back to the measured 1024 layout when missing. On a Wide screen (ADR 0177) the top bar
    /// keeps its place, everything of the lower half is anchored to the bottom, and the world view and the info area take
    /// the extra room.
    /// </summary>
    public static HudLayout Load(IniTable layout, string section = "LAYOUT1024H", Vector2I? screen = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        IniSection? s = layout.Section(section);
        bool small = section.Contains("800", StringComparison.Ordinal);
        Vector2I bitmap = small ? new Vector2I(800, 600) : new Vector2I(1024, 768);
        Vector2I size = screen ?? bitmap;
        Vector2I grow = size - bitmap;
        Rect2I Low(Rect2I r) => r.Position.Y >= bitmap.Y / 2 ? new Rect2I(r.Position + new Vector2I(0, grow.Y), r.Size) : r;
        string image = s?.Get("image") is { Length: > 0 } img ? "art/screens/s_view/" + img.ToLowerInvariant().Replace(".bmp", ".t2img", StringComparison.Ordinal) : $"art/screens/s_view/screen {bitmap.X}.t2img";
        Rect2I view = Rect(s, "view", new Rect2I(0, 30, bitmap.X, bitmap.Y - 184));
        Rect2I globe = Rect(s, "mscr1", new Rect2I(0, bitmap.Y - 231, 126, 77));
        return new HudLayout(section, size)
        {
            Base = bitmap,
            Image = image,
            View = new Rect2I(view.Position, view.Size + grow),
            GlobeOrnamentSource = globe,
            GlobeOrnament = Low(globe),
            Minimap = Low(Rect(s, "minimap", new Rect2I(28, bitmap.Y - 146, 128, 128))),
            Resources =
            [
                Rect(s, "resgold", new Rect2I(360, 0, 48, 18)),
                Rect(s, "reswood", new Rect2I(463, 0, 39, 18)),
                Rect(s, "resfood", new Rect2I(549, 0, 46, 18)),
                Rect(s, "resstone", new Rect2I(644, 0, 46, 18)),
                Rect(s, "poplimit", new Rect2I(740, 0, 46, 18)),
            ],
            MenuButton = Rect(s, "menubutton", new Rect2I(4, 0, 114, 25)),
            MenuImage = Art(s?.Get("menuimage"), "btn menu top"),
            DiplomacyButton = Rect(s, "diplomacybutton", new Rect2I(152, 0, 61, 25)),
            DiplomacyImage = Art(s?.Get("diplomacyimage"), "btn diplomacy left"),
            ChatButton = Rect(s, "chatbutton", new Rect2I(219, 0, 61, 25)),
            ChatImage = Art(s?.Get("chatimage"), "btn chat left"),
            Clock = Low(Rect(s, "clock", new Rect2I(8, bitmap.Y - 193, 36, 36))),
        };
    }

    private static string Art(string? file, string fallback)
        => "art/screens/s_view/" + (file is { Length: > 0 } f ? f.ToLowerInvariant().Replace(".bmp", string.Empty, StringComparison.Ordinal) : fallback) + ".t2img";

    private static Rect2I Rect(IniSection? s, string key, Rect2I fallback)
    {
        if (s?.Get(key) is not { } value)
        {
            return fallback;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return fallback;
        }

        int[] n = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i]))
            {
                return fallback;
            }
        }

        return new Rect2I(n[0], n[1], n[2], n[3]);
    }
}
