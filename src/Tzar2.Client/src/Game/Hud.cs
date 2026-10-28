using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Game.Ui;

namespace Tzar2.Client.Game;

/// <summary>What the selection view shows.</summary>
public sealed class SelectionInfo
{
    /// <summary>Name line.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Second line (owner, health).</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Portrait icon index (icons.bmp), or −1.</summary>
    public int Icon { get; init; } = -1;

    /// <summary>Stat rows: icon asset and value.</summary>
    public IReadOnlyList<(string Icon, string Value)> Stats { get; init; } = [];

    /// <summary>
    /// Icons in the info area (a multi-selection by type; a queue shows as counts on the card's buttons, ADR 0180) with
    /// an optional progress 0-1000 under them and a count on them: portraits of ICONS.BMP, or with <c>Order</c> an order
    /// button of ORDERS/BUTTONS.BMP.
    /// </summary>
    public IReadOnlyList<StripItem> Strip { get; init; } = [];

    /// <summary>Whether the strip values are health (green/yellow/red) rather than progress.</summary>
    public bool StripIsHealth { get; init; }

    /// <summary>Health share 0-1 for the portrait bar, or −1.</summary>
    public float Health { get; init; } = -1;

    /// <summary>The owner whose colour the portraits wear (0-7), or −1 (Nature, resources).</summary>
    public int Player { get; init; } = -1;

    /// <summary>The health share 0-1 of the units inside a holder, a bar under its own (HUD-021), or −1.</summary>
    public float InsideHealth { get; init; } = -1;

}

/// <summary>An icon of the info area: a portrait or order button, a progress 0-1000 (−1: none) and a count (0: none).</summary>
/// <param name="Icon">The icon index.</param>
/// <param name="Progress">Health or production progress per mille, or −1.</param>
/// <param name="Order">Whether the icon is an order button rather than a portrait.</param>
/// <param name="Count">How many it stands for (selected units of the type, units left to make), shown when above 1.</param>
/// <param name="Picked">Framed: the unit picked in a holder's list (ORD-015).</param>
public readonly record struct StripItem(int Icon, int Progress, bool Order, int Count = 0, bool Picked = false);

/// <summary>
/// The authentic desktop HUD (REQ-UI-01) on a <c>CanvasLayer</c>: the <c>S_VIEW</c> screen bitmap of the
/// <see cref="HudLayout"/>, resource numbers, menu/diplomacy/chat buttons, the minimap, the clock globe, the selection
/// view, the command card (<c>ORDERS/BUTTONS.BMP</c> icons) and the message lines. Every bitmap and font comes from the
/// imported package.
/// </summary>
public partial class Hud : Control
{
    private const string Icons = "art/icons/icons.t2img";
    private const string Buttons = "art/orders/buttons.t2img";
    private readonly UiAssets _ui;
    private readonly HudLayout _layout;
    private readonly TextureRect[] _resources = new TextureRect[5];
    private readonly string[] _resourceText = new string[5];
    private readonly TextureRect[] _cardIcons = new TextureRect[HudLayout.CardSlots];
    private readonly ColorRect[] _cardShade = new ColorRect[HudLayout.CardSlots];
    private readonly TextureRect[] _cardFrames = new TextureRect[HudLayout.CardSlots];
    private readonly TextureRect[] _cardCounts = new TextureRect[HudLayout.CardSlots];
    private readonly TextureRect[] _stats = new TextureRect[5];
    private readonly TextureRect[] _statValues = new TextureRect[5];
    private readonly List<TextureRect> _strip = [];
    private readonly List<ColorRect> _stripBars = [];
    private readonly List<ColorRect> _stripBacks = [];
    private readonly List<TextureRect> _stripCounts = [];
    private readonly List<TextureRect> _stripFrames = [];
    private readonly ColorRect _insideBar;
    private int _stripShown;
    private readonly TextureRect[] _messages = new TextureRect[6];
    private readonly TextureRect[] _side = new TextureRect[3];
    private readonly string[] _sideArt = new string[3];
    private readonly int[] _sideFrame = new int[3];
    private int _sideHover = -1;
    private readonly TextureRect _portrait;
    private readonly ColorRect _portraitBar;
    private readonly Control _nameBox;
    private string? _nameShown;
    private readonly TextureRect _clock;
    private readonly TextureRect _minimap;
    private readonly MinimapFrame _minimapFrame;
    private readonly Control _tooltip;
    private readonly TextureRect _status;
    private readonly Control _scores;
    private CardSlot?[] _card = new CardSlot?[HudLayout.CardSlots];
    private CardDialog? _dialog;
    private int _clockFrame = -1;
    private int _hovered = -1;
    private double _hoveredSince;
    private double _tooltipGone = double.NegativeInfinity;
    private double _seconds;

    /// <summary>Creates the HUD for <paramref name="layout"/>.</summary>
    public Hud(UiAssets ui, HudLayout layout, PresentationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(layout);
        _ui = ui;
        _layout = layout;
        Catalog = catalog;
        Size = layout.Size;
        MouseFilter = MouseFilterEnum.Ignore;

        AddOverlay(ui, layout);

        // Blocks clicks on the panels from reaching the world (the top bar, the minimap corner, the bottom panel).
        AddChild(new Control { Position = Vector2.Zero, Size = new Vector2(layout.Size.X, layout.View.Position.Y), MouseFilter = MouseFilterEnum.Stop });
        AddChild(new Control { Position = layout.Corner.Position, Size = layout.Corner.Size, MouseFilter = MouseFilterEnum.Stop });
        AddChild(new Control { Position = layout.Panel.Position, Size = layout.Panel.Size, MouseFilter = MouseFilterEnum.Stop });

        TopButton(layout.MenuButton, layout.MenuImage, () => MenuPressed?.Invoke());
        TopButton(layout.DiplomacyButton, layout.DiplomacyImage, () => DiplomacyPressed?.Invoke());
        TopButton(layout.ChatButton, layout.ChatImage, () => ChatPressed?.Invoke());
        Label(layout.MenuButton, catalog.Text("game_scr", "menu", "MENU"));
        for (int i = 0; i < _resources.Length; i++)
        {
            _resources[i] = Add(new TextureRect { Position = layout.Resources[i].Position + new Vector2I(4, 3) });
        }

        // IgnoreSize: the texture has a pixel per tile, so a map larger than the box would grow the control over the
        // selection view (the owner's play-test, 2026-10-22).
        _minimap = Add(new TextureRect { Position = layout.Minimap.Position, Size = layout.Minimap.Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Stop });
        _minimap.GuiInput += e => MinimapInput(e);
        _minimapFrame = Add(new MinimapFrame { Position = layout.Minimap.Position, Size = layout.Minimap.Size, MouseFilter = MouseFilterEnum.Ignore });
        _clock = Add(new TextureRect { Position = layout.Clock.Position, Size = layout.Clock.Size });

        _portrait = Add(new TextureRect { Position = layout.Portrait.Position, Size = layout.Portrait.Size });
        _portraitBar = Add(new ColorRect { Position = layout.Portrait.Position + new Vector2I(0, 50), Size = new Vector2(48, 3), Color = Colors.Green, Visible = false });
        _insideBar = Add(new ColorRect { Position = layout.Portrait.Position + new Vector2I(0, 54), Size = new Vector2(48, 2), Color = Colors.Green, Visible = false });
        _nameBox = Add(new Control { Position = new Vector2(layout.Portrait.Position.X - 2, layout.Portrait.Position.Y + 57), MouseFilter = MouseFilterEnum.Ignore });
        for (int i = 0; i < _stats.Length; i++)
        {
            Rect2I r = layout.StatRow(i);
            _stats[i] = Add(new TextureRect { Position = r.Position, Size = r.Size });
            _statValues[i] = Add(new TextureRect { Position = r.Position + new Vector2I(24, 4) });
        }

        for (int i = 0; i < HudLayout.CardSlots; i++)
        {
            int slot = i;
            Rect2I r = layout.CardSlot(i);
            _cardIcons[i] = Add(new TextureRect { Position = r.Position, Size = r.Size, MouseFilter = MouseFilterEnum.Stop });
            _cardShade[i] = Add(new ColorRect { Position = r.Position, Size = r.Size, Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore, Visible = false });
            _cardFrames[i] = Add(new TextureRect { Position = r.Position - new Vector2I(3, 3), Texture = ui.Texture("art/orders/frame.t2img"), MouseFilter = MouseFilterEnum.Ignore, Visible = false });
            _cardCounts[i] = Add(new TextureRect { Position = r.Position, MouseFilter = MouseFilterEnum.Ignore });
            _cardIcons[i].GuiInput += e => CardInput(slot, e);
        }

        // The arrows that turn the card's pages and the eye that opens the inventory (HUD-017, HUD-019).
        string[] sides = ["art/orders/left.t2img", "art/orders/holder.t2img", "art/orders/right.t2img"];
        for (int i = 0; i < _side.Length; i++)
        {
            int box = i;
            Rect2I r = layout.SideBox(i);
            _side[i] = Add(new TextureRect { Position = r.Position, Size = r.Size, MouseFilter = MouseFilterEnum.Stop });
            _sideArt[i] = sides[i];
            _side[i].MouseEntered += () => HoverSide(box);
            _side[i].MouseExited += () => HoverSide(-1);
            _side[i].GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                {
                    if (_sideFrame[box] == 3)
                    {
                        // Grey: nothing that way (the arrows) or nothing to show (the eye).
                    }
                    else if (box == 1)
                    {
                        EyePressed?.Invoke();
                    }
                    else
                    {
                        PagePressed?.Invoke(box == 0 ? -1 : 1);
                    }

                    _side[box].AcceptEvent();
                }
            };
        }

        SetSide(false, 0, false);

        for (int i = 0; i < _messages.Length; i++)
        {
            _messages[i] = Add(new TextureRect { Position = layout.MessagesAt + new Vector2I(0, i * 15), MouseFilter = MouseFilterEnum.Ignore });
        }

        _tooltip = Add(new Control { MouseFilter = MouseFilterEnum.Ignore, Visible = false });
        _status = Add(new TextureRect { Position = new Vector2(layout.View.End.X - 220, layout.View.Position.Y + 8), MouseFilter = MouseFilterEnum.Ignore });
        _scores = Add(new Control { Position = layout.View.Position, Size = layout.View.Size, MouseFilter = MouseFilterEnum.Ignore });
    }

    /// <summary>The presentation catalog.</summary>
    public PresentationCatalog Catalog { get; }

    /// <summary>The layout.</summary>
    public HudLayout Layout => _layout;

    /// <summary>Raised for card clicks (slot, right button).</summary>
    public event Action<int, bool>? CardPressed;

    /// <summary>Raised when the minimap is clicked or dragged (map share 0-1, right button).</summary>
    public event Action<Vector2, bool, Tzar2.Game.Input.KeyMods>? MinimapPressed;

    /// <summary>MENU button.</summary>
    public event Action? MenuPressed;

    /// <summary>An arrow by the orders bar: −1 the previous page, +1 the next (HUD-019).</summary>
    public event Action<int>? PagePressed;

    /// <summary>The inventory's eye (HUD-017).</summary>
    public event Action? EyePressed;

    /// <summary>
    /// The arrows and the eye, each from its art's four frames (ReTzar's reading of the original): an arrow is lit when
    /// there is a page that way (frame 3, grey, otherwise); the eye's frame is the card's (<c>CommandCard.EyeFrame</c>).
    /// A lit box shows frame 1 under the pointer.
    /// </summary>
    public void SetSide(bool previous, int eye, bool next)
    {
        _sideFrame[0] = previous ? 0 : 3;
        _sideFrame[1] = Math.Clamp(eye, 0, 3);
        _sideFrame[2] = next ? 0 : 3;
        DrawSide();
    }

    private void HoverSide(int box)
    {
        _sideHover = box;
        DrawSide();
    }

    private void DrawSide()
    {
        for (int i = 0; i < _side.Length; i++)
        {
            int frame = _sideFrame[i] == 0 && _sideHover == i ? 1 : _sideFrame[i];
            _side[i].Texture = _ui.Region(_sideArt[i], new Rect2I(frame * 25, 0, 25, 25));
        }
    }

    /// <summary>DIPLOMACY button.</summary>
    public event Action? DiplomacyPressed;

    /// <summary>CHAT button.</summary>
    public event Action? ChatPressed;

    /// <summary>Raised when an icon of the info area is clicked (its index; a unit of a holder's list, ORD-015).</summary>
    public event Action<int>? StripPressed;

    /// <summary>Sets the five resource boxes (gold, wood, food, stone, population "used/room").</summary>
    public void SetResources(string gold, string wood, string food, string stone, string population)
    {
        string[] values = [gold, wood, food, stone, population];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] != _resourceText[i])
            {
                _resourceText[i] = values[i];
                _resources[i].Texture = _ui.Text(values[i], UiAssets.SmallFont, new Color(1f, 0.92f, 0.65f));
            }
        }
    }

    /// <summary>Shows clock globe frame for a day phase 0-1 (<c>DAYNIGHT.BMP</c>: 24 frames of 36×36).</summary>
    public void SetClock(float phase)
    {
        int frame = Math.Clamp((int)(phase * 24), 0, 23);
        if (frame != _clockFrame)
        {
            _clockFrame = frame;
            _clock.Texture = _ui.Region("art/screens/s_view/daynight.t2img", new Rect2I(0, frame * 36, 36, 36));
        }
    }

    /// <summary>Replaces the minimap image.</summary>
    public void SetMinimap(Texture2D texture, Rect2 cameraShare)
    {
        _minimap.Texture = texture;
        _minimapFrame.Camera = cameraShare;
        _minimapFrame.QueueRedraw();
    }

    /// <summary>Fills the selection view (null = nothing selected).</summary>
    public void SetSelection(SelectionInfo? info)
    {
        _portrait.Texture = info is null ? null : _ui.PlayerCell(Icons, info.Icon, info.Player);
        _portraitBar.Visible = info is { Health: >= 0 };
        if (info is { Health: >= 0 } h)
        {
            _portraitBar.Size = new Vector2(Math.Max(1, MathF.Round(48 * h.Health)), 3);
            _portraitBar.Color = HealthColour(h.Health);
        }

        // The health of the units inside a castle or outpost, under its own (guide: Important tips; HUD-021).
        _insideBar.Visible = info is { InsideHealth: >= 0 };
        if (info is { InsideHealth: >= 0 } inside)
        {
            _insideBar.Size = new Vector2(Math.Max(1, MathF.Round(48 * inside.InsideHealth)), 2);
            _insideBar.Color = HealthColour(inside.InsideHealth);
        }

        SetName(info);
        for (int i = 0; i < _stats.Length; i++)
        {
            bool has = info is not null && i < info.Stats.Count;
            _stats[i].Texture = has ? _ui.Texture(info!.Stats[i].Icon) : null;
            _statValues[i].Texture = has ? _ui.Text(info!.Stats[i].Value, UiAssets.SmallFont, Colors.White) : null;
        }

        IReadOnlyList<StripItem> strip = info?.Strip ?? [];
        Rect2I area = _layout.InfoArea;
        int perRow = area.Size.X / 49;
        while (_strip.Count < Math.Min(strip.Count, perRow * 2))
        {
            int n = _strip.Count;
            var at = new Vector2(area.Position.X + ((n % perRow) * 49), area.Position.Y + ((n / perRow) * 62));
            TextureRect icon = Add(new TextureRect { Position = at, Size = new Vector2(48, 48), MouseFilter = MouseFilterEnum.Stop });
            icon.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && n < _stripShown)
                {
                    StripPressed?.Invoke(n);
                }
            };
            _strip.Add(icon);
            _stripBacks.Add(Add(new ColorRect { Position = at + new Vector2(0, 50), Size = new Vector2(48, 5), Color = new Color(0, 0, 0, 0.85f), MouseFilter = MouseFilterEnum.Ignore }));
            _stripBars.Add(Add(new ColorRect { Position = at + new Vector2(1, 51), Size = new Vector2(46, 3), MouseFilter = MouseFilterEnum.Ignore }));
            _stripCounts.Add(Add(new TextureRect { Position = at, Size = new Vector2(48, 48), MouseFilter = MouseFilterEnum.Ignore }));
            _stripFrames.Add(Add(new TextureRect { Position = at - new Vector2(3, 3), Texture = _ui.Texture("art/orders/frame.t2img"), MouseFilter = MouseFilterEnum.Ignore, Visible = false }));
        }

        _stripShown = strip.Count;

        for (int i = 0; i < _strip.Count; i++)
        {
            bool has = i < strip.Count;
            _strip[i].Texture = !has ? null : strip[i].Order ? _ui.Cell(Buttons, strip[i].Icon) : _ui.PlayerCell(Icons, strip[i].Icon, info!.Player);
            _stripFrames[i].Visible = has && strip[i].Picked;
            bool bar = has && strip[i].Progress >= 0;
            _stripBars[i].Visible = bar;
            _stripBacks[i].Visible = bar;
            if (bar)
            {
                _stripBars[i].Size = new Vector2(Math.Max(1, 46 * strip[i].Progress / 1000), 3);
                _stripBars[i].Color = info!.StripIsHealth ? HealthColour(strip[i].Progress / 1000f) : new Color(0.9f, 0.75f, 0.3f);
            }

            // The count in the icon's bottom-right corner, white on a dark outline.
            ImageTexture? count = has && strip[i].Count > 1 ? _ui.Text(strip[i].Count.ToString(System.Globalization.CultureInfo.InvariantCulture), UiAssets.BoldFont, Colors.White) : null;
            _stripCounts[i].Texture = count;
            if (count is not null)
            {
                Vector2 icon = _strip[i].Position;
                _stripCounts[i].Position = icon + new Vector2(46 - count.GetWidth(), 46 - count.GetHeight());
                _stripCounts[i].Size = count.GetSize();
            }
        }
    }

    /// <summary>The health bar colour of a share 0-1 (⚠ thresholds not measured).</summary>
    public static Color HealthColour(float share)
        => share > 0.5f ? new Color(0.1f, 0.85f, 0.1f) : share > 0.25f ? new Color(0.95f, 0.85f, 0.1f) : new Color(0.9f, 0.1f, 0.1f);

    // A button without art: the page number, its letter, or (an entry of a kingdom list) its name's first letter.
    private static string ButtonText(CardButton b)
        => b.Action == CardAction.Page ? b.Argument : b.Hotkey != '\0' ? b.Hotkey.ToString() : b.Tooltip.Length > 0 ? b.Tooltip[..1] : "?";

    /// <summary>Shows <paramref name="dialog"/> in the command card's place (null: the card again).</summary>
    public void ShowDialog(CardDialog? dialog)
    {
        _dialog?.QueueFree();
        _dialog = dialog;
        bool card = dialog is null;
        for (int i = 0; i < HudLayout.CardSlots; i++)
        {
            _cardIcons[i].Visible = card;
            _cardShade[i].Visible = card && _card[i] is { Enabled: false };
            _cardFrames[i].Visible = card && _card[i] is { Active: true };
            _cardCounts[i].Visible = card;
        }

        if (dialog is not null)
        {
            // Centred on the card's slots, frames included: the panel's own cells are closer together than the card's, so
            // aligning their first cells left the last column of the card showing (the sixth play-test).
            Rect2I first = _layout.CardSlot(0), last = _layout.CardSlot(HudLayout.CardSlots - 1);
            var slots = new Rect2I(first.Position - new Vector2I(3, 3), last.End - first.Position + new Vector2I(6, 6));
            dialog.Position = slots.Position + ((slots.Size - CardDialog.PanelSize) / 2);
            AddChild(dialog);
            HideTooltip();
        }
    }

    /// <summary>Fills the command card.</summary>
    public void SetCard(IReadOnlyList<CardSlot?> card)
    {
        ArgumentNullException.ThrowIfNull(card);
        CardSlot?[] before = _card;
        _card = new CardSlot?[HudLayout.CardSlots];
        for (int i = 0; i < HudLayout.CardSlots; i++)
        {
            CardSlot? slot = i < card.Count ? card[i] : null;
            _card[i] = slot;
            _cardIcons[i].Texture = slot is { } s
                ? (s.Button.OwnIcon is { } own ? _ui.OwnIcon(own)
                    : s.Button.Action == CardAction.InventoryItem ? _ui.Cell(Icons, s.Button.ImageIndex) : _ui.Cell(Buttons, s.Button.ImageIndex)) ?? _ui.Text(ButtonText(s.Button), UiAssets.BoldFont)
                : null;
            _cardShade[i].Visible = _dialog is null && slot is { Enabled: false };
            _cardIcons[i].Modulate = slot is { Enabled: false } ? new Color(0.5f, 0.5f, 0.5f) : Colors.White;
            _cardFrames[i].Visible = _dialog is null && slot is { Active: true };

            // How many of the button's item are queued, in its bottom-right corner (as the original's "8" on the
            // castle's peasant; ADR 0180).
            int count = slot?.Count ?? 0;
            if (count != (before[i]?.Count ?? 0) || (count > 0 && _cardCounts[i].Texture is null))
            {
                ImageTexture? text = count > 0 ? _ui.Text(count.ToString(CultureInfo.InvariantCulture), UiAssets.BoldFont, Colors.White) : null;
                _cardCounts[i].Texture = text;
                if (text is not null)
                {
                    Rect2I r = _layout.CardSlot(i);
                    _cardCounts[i].Position = r.Position + new Vector2(r.Size.X - text.GetWidth() - 2, r.Size.Y - text.GetHeight() - 1);
                    _cardCounts[i].Size = text.GetSize();
                }
            }

            _cardCounts[i].Visible = _dialog is null;
        }

        // The tooltip of the slot under the pointer follows the card (resources change while it is shown); a slot that
        // emptied takes it away.
        if (_hovered >= 0 && _tooltip.Visible && !Equals(before[_hovered], _card[_hovered]))
        {
            ShowTooltip(_hovered);
        }
    }

    /// <summary>Shows the newest messages (oldest first).</summary>
    public void SetMessages(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        for (int i = 0; i < _messages.Length; i++)
        {
            _messages[i].Texture = i < lines.Count ? _ui.Text(lines[i], UiAssets.BoldFont, new Color(1f, 0.95f, 0.7f)) : null;
        }
    }

    /// <summary>
    /// F4's clock and scoreboard over the view (DIP-008; ReTzar 0x4f2903): the clock at its top right, the lines above
    /// its bottom right corner, each ending 12 pixels left of the kingdom's colour square, a defeated one struck through,
    /// in the small font with a shadow. Null clock: nothing.
    /// </summary>
    public void SetScores(string? clock, IReadOnlyList<(string Text, Color Colour, bool Struck)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (Node child in _scores.GetChildren())
        {
            child.QueueFree();
        }

        if (clock is null)
        {
            return;
        }

        Vector2 size = _scores.Size;
        var text = new Color(1f, 0.95f, 0.7f);
        void Shadowed(string s, Vector2 at)
        {
            if (_ui.Text(s, UiAssets.SmallFont, Colors.Black) is { } shadow && _ui.Text(s, UiAssets.SmallFont, text) is { } t)
            {
                var front = new TextureRect { Texture = t, Position = at, MouseFilter = MouseFilterEnum.Ignore };
                front.AddChild(new TextureRect { Texture = shadow, Position = Vector2.One, ShowBehindParent = true, MouseFilter = MouseFilterEnum.Ignore });
                _scores.AddChild(front);
            }
        }

        Shadowed(clock, new Vector2(size.X - 80, 10));
        float top = size.Y - 10 - (20 * lines.Count);
        for (int k = 0; k < lines.Count; k++)
        {
            (string line, Color colour, bool struck) = lines[k];
            float y = top + (20 * k);
            int w = _ui.Measure(line, UiAssets.SmallFont);
            float x = size.X - 22 - w;
            Shadowed(line, new Vector2(x, y));
            if (struck)
            {
                _scores.AddChild(new ColorRect { Position = new Vector2(x, y + 6), Size = new Vector2(w + 1, 1), Color = text, MouseFilter = MouseFilterEnum.Ignore });
            }

            _scores.AddChild(new ColorRect { Position = new Vector2(size.X - 20, y + 4), Size = new Vector2(4, 4), Color = colour, MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    /// <summary>A status line at the top right of the view (game speed, pause).</summary>
    public void SetStatus(string text) => _status.Texture = text.Length == 0 ? null : _ui.Text(text, UiAssets.BoldFont, new Color(1f, 0.95f, 0.7f));

    /// <summary>The card slot under a screen point, or −1.</summary>
    public int CardSlotAt(Vector2 point)
    {
        for (int i = 0; i < HudLayout.CardSlots; i++)
        {
            if (new Rect2(_layout.CardSlot(i).Position, _layout.CardSlot(i).Size).HasPoint(point))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Time the pointer rests on a button before its tooltip shows (a sweep across the card shows none).</summary>
    public const double TooltipDelay = 0.15;

    /// <summary>For this long after a tooltip went away, the next button's tooltip shows at once.</summary>
    public const double TooltipWarm = 0.4;

    /// <summary>Widest a tooltip's text runs before it wraps.</summary>
    public const int TooltipTextWidth = 300;

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _seconds += delta;
        UpdateTooltip();
    }

    // The tooltip follows the control the pointer is really over, every frame (third play-test: it must never stay
    // behind): the card button under the pointer, no dialog over the card, the HUD shown. Leaving the button, a new
    // selection, an emptied slot or a dialog takes it away at once.
    private void UpdateTooltip()
    {
        int slot = -1;
        if (IsVisibleInTree() && _dialog is null && GetViewport().GuiGetHoveredControl() is TextureRect hovered)
        {
            slot = Array.IndexOf(_cardIcons, hovered);
            if (slot >= 0 && _card[slot] is null)
            {
                slot = -1;
            }
        }

        if (slot != _hovered)
        {
            if (_tooltip.Visible)
            {
                _tooltipGone = _seconds;
            }

            HideTooltip();
            _hovered = slot;
            _hoveredSince = _seconds;
        }

        if (_hovered >= 0 && !_tooltip.Visible && (_seconds - _hoveredSince >= TooltipDelay || _seconds - _tooltipGone <= TooltipWarm))
        {
            ShowTooltip(_hovered);
        }
    }

    // The tooltip of a card button: the name with the hotkey on a key cap, the price with the original's resource icons
    // (red where the kingdom has too little) and the time, what the order does, and why it is greyed out ("Requires: ...",
    // the original's own words). It sits above the card (beside it on a side panel), clear of the buttons, inside the
    // screen.
    private void ShowTooltip(int slot)
    {
        HideTooltip();
        if (_dialog is not null || _card[slot] is not { } s)
        {
            return;
        }

        const int Pad = 6;
        var lines = new List<Control>();
        var title = new Control { MouseFilter = MouseFilterEnum.Ignore };
        float titleWidth = 0, titleHeight = 0;
        if (_ui.Text(s.Button.Tooltip, UiAssets.BoldFont, new Color(1f, 0.92f, 0.65f)) is { } name)
        {
            title.AddChild(new TextureRect { Texture = name, Size = name.GetSize(), MouseFilter = MouseFilterEnum.Ignore });
            titleWidth = name.GetWidth();
            titleHeight = name.GetHeight();
        }

        if (s.Button.Hotkey != '\0' && _ui.Text(s.Button.Hotkey.ToString(), UiAssets.BoldFont, Colors.White) is { } letter)
        {
            // The hotkey on a key cap after the name.
            var cap = new Vector2(Math.Max(letter.GetWidth() + 8, 16), letter.GetHeight() + 2);
            float x = titleWidth + 8;
            title.AddChild(new ColorRect { Position = new Vector2(x, 0), Size = cap, Color = new Color(0.22f, 0.18f, 0.1f), MouseFilter = MouseFilterEnum.Ignore });
            title.AddChild(new ReferenceRect { Position = new Vector2(x, 0), Size = cap, BorderColor = new Color(0.85f, 0.7f, 0.35f), BorderWidth = 1, EditorOnly = false, MouseFilter = MouseFilterEnum.Ignore });
            title.AddChild(new TextureRect { Texture = letter, Position = new Vector2(x + ((cap.X - letter.GetWidth()) / 2), 1), Size = letter.GetSize(), MouseFilter = MouseFilterEnum.Ignore });
            titleWidth = x + cap.X;
            titleHeight = Math.Max(titleHeight, cap.Y);
        }

        title.Size = new Vector2(titleWidth, titleHeight);
        lines.Add(title);

        if (s.Price is { Count: > 0 } price)
        {
            var row = new Control { MouseFilter = MouseFilterEnum.Ignore };
            float x = 0, height = 0;
            foreach (PriceItem item in price)
            {
                if (_ui.Texture(item.Icon) is { } picture)
                {
                    row.AddChild(new TextureRect { Texture = picture, Position = new Vector2(x, 0), Size = picture.GetSize(), MouseFilter = MouseFilterEnum.Ignore });
                    x += picture.GetWidth() + 2;
                    height = Math.Max(height, picture.GetHeight());
                }

                Color colour = item.Short ? new Color(1f, 0.45f, 0.35f) : Colors.White;
                if (_ui.Text(item.Value, UiAssets.SmallFont, colour) is { } number)
                {
                    row.AddChild(new TextureRect { Texture = number, Position = new Vector2(x, 4), Size = number.GetSize(), MouseFilter = MouseFilterEnum.Ignore });
                    x += number.GetWidth() + 10;
                    height = Math.Max(height, number.GetHeight() + 4);
                }
            }

            row.Size = new Vector2(x - 10, height);
            lines.Add(row);
        }

        if (s.Description.Length > 0)
        {
            Wrapped(lines, s.Description, new Color(0.9f, 0.9f, 0.85f));
        }

        string note = s.Detail.Length > 0 ? s.Detail : s.Enabled ? string.Empty : "not available yet";
        if (note.Length > 0)
        {
            Wrapped(lines, note, s.Enabled ? new Color(0.85f, 0.95f, 0.8f) : new Color(1f, 0.55f, 0.45f));
        }

        float width = 0, y = Pad;
        foreach (Control line in lines)
        {
            line.Position = new Vector2(Pad, y);
            y += line.Size.Y + 3;
            width = Math.Max(width, line.Size.X);
        }

        var size = new Vector2(width + (2 * Pad), y + Pad - 3);
        _tooltip.AddChild(new ColorRect { Size = size, Color = new Color(0.05f, 0.04f, 0.02f, 0.88f), MouseFilter = MouseFilterEnum.Ignore });
        _tooltip.AddChild(new ReferenceRect { Size = size, BorderColor = new Color(0.75f, 0.6f, 0.3f), BorderWidth = 1, EditorOnly = false, MouseFilter = MouseFilterEnum.Ignore });
        foreach (Control line in lines)
        {
            _tooltip.AddChild(line);
        }

        _tooltip.Size = size;
        _tooltip.Position = TooltipPlace(_layout.CardSlot(slot), size);
        _tooltip.Visible = true;
    }

    // Where a tooltip of size goes for a button: above the card when the card is at the bottom of the screen (the
    // horizontal panel), else beside the button (a side panel), always inside the screen and never over the card.
    private Vector2 TooltipPlace(Rect2I button, Vector2 size)
    {
        const int Gap = 4;
        Vector2 screen = Size;
        Vector2 at;
        if (button.Position.Y > screen.Y / 2)
        {
            int cardTop = Math.Min(_layout.PanelTop, _layout.CardSlot(0).Position.Y - 3);
            at = new Vector2(button.Position.X - 3, cardTop - size.Y - Gap);
        }
        else
        {
            bool left = button.Position.X > screen.X / 2;
            at = new Vector2(left ? button.Position.X - size.X - Gap : button.End.X + Gap, button.Position.Y);
        }

        return new Vector2(Math.Clamp(at.X, Gap, Math.Max(Gap, screen.X - size.X - Gap)), Math.Clamp(at.Y, Gap, Math.Max(Gap, screen.Y - size.Y - Gap)));
    }

    // Adds text in lines of at most TooltipTextWidth pixels, broken between words.
    private void Wrapped(List<Control> lines, string text, Color colour)
    {
        foreach (string line in WrapWords(text, UiAssets.SmallFont, TooltipTextWidth))
        {
            if (_ui.Text(line, UiAssets.SmallFont, colour) is { } t)
            {
                lines.Add(new TextureRect { Texture = t, Size = t.GetSize(), MouseFilter = MouseFilterEnum.Ignore });
            }
        }
    }

    private List<string> WrapWords(string text, string font, int width)
    {
        var result = new List<string>();
        string current = string.Empty;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string tried = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && _ui.Measure(tried, font) > width)
            {
                result.Add(current);
                current = word;
            }
            else
            {
                current = tried;
            }
        }

        if (current.Length > 0)
        {
            result.Add(current);
        }

        return result;
    }

    private void HideTooltip()
    {
        foreach (Node child in _tooltip.GetChildren())
        {
            child.QueueFree();
        }

        _tooltip.Visible = false;
    }

    // The name and the health/owner line under the portrait, beside the stat column: the name in the bold font when it
    // fits, else broken between words in the small one, and the detail likewise (third play-test: "Watch Tower" ran under
    // the stats). Rebuilt only when the text changes.
    private void SetName(SelectionInfo? info)
    {
        string shown = info is null ? string.Empty : info.Name + "\n" + info.Detail;
        if (shown == _nameShown)
        {
            return;
        }

        _nameShown = shown;
        foreach (Node child in _nameBox.GetChildren())
        {
            child.QueueFree();
        }

        if (info is null)
        {
            return;
        }

        int room = _layout.StatRow(0).Position.X - (int)_nameBox.Position.X - 3;
        var parts = new List<(string Text, string Font, Color Colour)>();
        var gold = new Color(1f, 0.92f, 0.65f);
        if (_ui.Measure(info.Name, UiAssets.BoldFont) <= room)
        {
            parts.Add((info.Name, UiAssets.BoldFont, gold));
        }
        else
        {
            parts.AddRange(WrapWords(info.Name, UiAssets.SmallFont, room).ConvertAll(l => (l, UiAssets.SmallFont, gold)));
        }

        parts.AddRange(WrapWords(info.Detail, UiAssets.SmallFont, room).ConvertAll(l => (l, UiAssets.SmallFont, Colors.White)));
        float y = 0;
        foreach ((string text, string font, Color colour) in parts)
        {
            if (_ui.Text(text, font, colour) is { } t)
            {
                _nameBox.AddChild(new TextureRect { Texture = t, Position = new Vector2(0, y), Size = t.GetSize(), MouseFilter = MouseFilterEnum.Ignore });
                y += font == UiAssets.BoldFont ? 16 : 13;
            }
        }
    }

    private void CardInput(int slot, InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } mb && (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right))
        {
            CardPressed?.Invoke(slot, mb.ButtonIndex == MouseButton.Right);
            AcceptEvent();
        }
    }

    private void MinimapInput(InputEvent e)
    {
        Vector2 local;
        bool right;
        Tzar2.Game.Input.KeyMods mods = e is InputEventWithModifiers m ? KeyMap.Mods(m) : Tzar2.Game.Input.KeyMods.None;
        if (e is InputEventMouseButton { Pressed: true } mb)
        {
            local = mb.Position;
            right = mb.ButtonIndex == MouseButton.Right;
        }
        else if (e is InputEventMouseMotion { ButtonMask: MouseButtonMask.Left } mm)
        {
            local = mm.Position;
            right = false;
        }
        else
        {
            return;
        }

        MinimapPressed?.Invoke(new Vector2(local.X / _layout.Minimap.Size.X, local.Y / _layout.Minimap.Size.Y), right, mods);
        AcceptEvent();
    }

    private void TopButton(Rect2I rect, string image, Action pressed)
    {
        Texture2D? normal = _ui.Region(image, new Rect2I(0, 0, rect.Size.X, rect.Size.Y));
        Texture2D? down = _ui.Region(image, new Rect2I(rect.Size.X, 0, rect.Size.X, rect.Size.Y));
        var button = new TextureButton { Position = rect.Position, Size = rect.Size, TextureNormal = normal, TexturePressed = down ?? normal, MouseFilter = MouseFilterEnum.Stop };
        button.Pressed += pressed;
        AddChild(button);
    }

    private void Label(Rect2I rect, string text)
    {
        ImageTexture? t = _ui.Text(text, UiAssets.BoldFont, new Color(1f, 0.92f, 0.65f));
        if (t is not null)
        {
            Add(new TextureRect
            {
                Texture = t,
                Position = rect.Position + new Vector2I((rect.Size.X - t.GetWidth()) / 2, (rect.Size.Y - t.GetHeight()) / 2),
                MouseFilter = MouseFilterEnum.Ignore,
            });
        }
    }

    // The S_VIEW bitmap: whole on its own screen size; on a Wide screen (ADR 0177) cut into anchored pieces with a plain
    // slice repeated between them. The cuts are measured on SCREEN 1024.BMP: the top bar's left part ends after the
    // population box, its vine ornament is the last 219 px; the panel's left part ends where the info area's marble
    // starts plain, its letters and ornament are the last 164 px.
    // The hybrid HUD's art (HudLayout): the top bar (its ends kept, the middle tiled on a wider screen), the left layout's
    // corner with the minimap and the day globe, and the bottom panel's middle.
    private void AddOverlay(UiAssets ui, HudLayout layout)
    {
        const int TopLeft = 805, TopRight = 219, TopFillX = 850, TopFill = 150;
        Vector2I size = layout.Size, bitmap = layout.Base;
        int top = layout.View.Position.Y;
        if (size.X == bitmap.X)
        {
            Piece(layout.Image, new Rect2I(0, 0, bitmap.X, top), Vector2I.Zero);
        }
        else
        {
            Piece(layout.Image, new Rect2I(0, 0, Math.Min(TopLeft, size.X), top), Vector2I.Zero);
            Tile(new Rect2I(TopFillX, 0, TopFill, top), new Rect2I(TopLeft, 0, size.X - TopLeft - TopRight, top));
            Piece(layout.Image, new Rect2I(bitmap.X - TopRight, 0, TopRight, top), new Vector2I(size.X - TopRight, 0));
        }

        Piece(layout.CornerImage, HudLayout.CornerSource, layout.Corner.Position);
        Piece(layout.Image, HudLayout.PanelSource, layout.Panel.Position);
        Piece(layout.Image, HudLayout.PanelBorderSource, new Vector2I(layout.Panel.End.X - HudLayout.PanelBorderSource.Size.X, layout.Panel.Position.Y), mirrored: true);

        void Piece(string image, Rect2I source, Vector2I at, bool mirrored = false)
        {
            if (ui.Region(image, source) is { } t)
            {
                AddChild(new TextureRect { Texture = t, Position = at, FlipH = mirrored, MouseFilter = MouseFilterEnum.Ignore });
            }
        }

        void Tile(Rect2I source, Rect2I target)
        {
            if (target.Size.X > 0 && ui.Slice(layout.Image, source) is { } t)
            {
                AddChild(new TextureRect { Texture = t, Position = target.Position, Size = target.Size, StretchMode = TextureRect.StretchModeEnum.Tile, MouseFilter = MouseFilterEnum.Ignore });
            }
        }
    }

    private T Add<T>(T node)
        where T : Control
    {
        AddChild(node);
        return node;
    }

    /// <summary>Draws the camera rectangle on the minimap.</summary>
    private sealed partial class MinimapFrame : Control
    {
        public Rect2 Camera { get; set; }

        public override void _Draw()
        {
            // Kept inside the minimap: the view can be larger than a small map.
            Rect2 r = new Rect2(Camera.Position * Size, Camera.Size * Size).Intersection(new Rect2(Vector2.Zero, Size));
            DrawRect(r, Colors.White, filled: false, width: 1f);
        }
    }
}

/// <summary>Formats HUD numbers (invariant culture).</summary>
public static class HudText
{
    /// <summary>An integer as text.</summary>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
