using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Tzar2.Client.Logic;

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
    /// Icons in the info area (multi-selection by type, or the production queue's head) with an optional progress
    /// 0-1000 under them and a count on them: portraits of ICONS.BMP, or with <c>Order</c> an order button of
    /// ORDERS/BUTTONS.BMP (a technology or upgrade in progress).
    /// </summary>
    public IReadOnlyList<StripItem> Strip { get; init; } = [];

    /// <summary>Whether the strip values are health (green/yellow/red) rather than progress.</summary>
    public bool StripIsHealth { get; init; }

    /// <summary>Health share 0-1 for the portrait bar, or −1.</summary>
    public float Health { get; init; } = -1;

    /// <summary>The owner whose colour the portraits wear (0-7), or −1 (Nature, resources).</summary>
    public int Player { get; init; } = -1;
}

/// <summary>An icon of the info area: a portrait or order button, a progress 0-1000 (−1: none) and a count (0: none).</summary>
/// <param name="Icon">The icon index.</param>
/// <param name="Progress">Health or production progress per mille, or −1.</param>
/// <param name="Order">Whether the icon is an order button rather than a portrait.</param>
/// <param name="Count">How many it stands for (selected units of the type, units left to make), shown when above 1.</param>
public readonly record struct StripItem(int Icon, int Progress, bool Order, int Count = 0);

/// <summary>A command-card slot.</summary>
/// <param name="Button">The MENU.INI button.</param>
/// <param name="Enabled">Whether it can be used now.</param>
/// <param name="Active">Whether its order is pending (highlighted).</param>
/// <param name="Detail">What the tooltip adds: the cost, and why the button is greyed out.</param>
public readonly record struct CardSlot(CardButton Button, bool Enabled, bool Active, string Detail = "");

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
    private readonly TextureRect[] _stats = new TextureRect[5];
    private readonly TextureRect[] _statValues = new TextureRect[5];
    private readonly List<TextureRect> _strip = [];
    private readonly List<ColorRect> _stripBars = [];
    private readonly List<ColorRect> _stripBacks = [];
    private readonly List<TextureRect> _stripCounts = [];
    private readonly TextureRect[] _messages = new TextureRect[6];
    private readonly TextureRect _portrait;
    private readonly ColorRect _portraitBar;
    private readonly TextureRect _name;
    private readonly TextureRect _detail;
    private readonly TextureRect _clock;
    private readonly TextureRect _minimap;
    private readonly MinimapFrame _minimapFrame;
    private readonly TextureRect _tooltip;
    private readonly TextureRect _status;
    private CardSlot?[] _card = new CardSlot?[HudLayout.CardSlots];
    private int _clockFrame = -1;
    private int _hovered = -1;

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

        // Blocks clicks on the panels from reaching the world (top bar and bottom panel).
        AddChild(new Control { Position = Vector2.Zero, Size = new Vector2(layout.Size.X, layout.View.Position.Y), MouseFilter = MouseFilterEnum.Stop });
        AddChild(new Control { Position = new Vector2(0, layout.PanelTop), Size = new Vector2(layout.Size.X, HudLayout.PanelHeight), MouseFilter = MouseFilterEnum.Stop });

        TopButton(layout.MenuButton, layout.MenuImage, () => MenuPressed?.Invoke());
        TopButton(layout.DiplomacyButton, layout.DiplomacyImage, () => DiplomacyPressed?.Invoke());
        TopButton(layout.ChatButton, layout.ChatImage, () => ChatPressed?.Invoke());
        Label(layout.MenuButton, catalog.Text("game_scr", "menu", "MENU"));
        for (int i = 0; i < _resources.Length; i++)
        {
            _resources[i] = Add(new TextureRect { Position = layout.Resources[i].Position + new Vector2I(4, 3) });
        }

        _minimap = Add(new TextureRect { Position = layout.Minimap.Position, Size = layout.Minimap.Size, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Stop });
        _minimap.GuiInput += e => MinimapInput(e);
        _minimapFrame = Add(new MinimapFrame { Position = layout.Minimap.Position, Size = layout.Minimap.Size, MouseFilter = MouseFilterEnum.Ignore });
        _clock = Add(new TextureRect { Position = layout.Clock.Position, Size = layout.Clock.Size });

        _portrait = Add(new TextureRect { Position = layout.Portrait.Position, Size = layout.Portrait.Size });
        _portraitBar = Add(new ColorRect { Position = layout.Portrait.Position + new Vector2I(0, 50), Size = new Vector2(48, 3), Color = Colors.Green, Visible = false });
        _name = Add(new TextureRect { Position = new Vector2(layout.Portrait.Position.X - 2, layout.Portrait.Position.Y + 57) });
        _detail = Add(new TextureRect { Position = new Vector2(layout.Portrait.Position.X - 2, layout.Portrait.Position.Y + 73) });
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
            _cardIcons[i].GuiInput += e => CardInput(slot, e);
            _cardIcons[i].MouseEntered += () =>
            {
                _hovered = slot;
                ShowTooltip(slot);
            };
            _cardIcons[i].MouseExited += () =>
            {
                _hovered = -1;
                _tooltip!.Texture = null;
            };
        }

        for (int i = 0; i < _messages.Length; i++)
        {
            _messages[i] = Add(new TextureRect { Position = new Vector2(layout.View.Position.X + 8, layout.View.Position.Y + 8 + (i * 15)), MouseFilter = MouseFilterEnum.Ignore });
        }

        _tooltip = Add(new TextureRect { Position = new Vector2(layout.CardSlot(0).Position.X, layout.PanelTop - 16), MouseFilter = MouseFilterEnum.Ignore });
        _status = Add(new TextureRect { Position = new Vector2(layout.View.End.X - 220, layout.View.Position.Y + 8), MouseFilter = MouseFilterEnum.Ignore });
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

    /// <summary>DIPLOMACY button.</summary>
    public event Action? DiplomacyPressed;

    /// <summary>CHAT button.</summary>
    public event Action? ChatPressed;

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

        _name.Texture = info is null ? null : _ui.Text(info.Name, UiAssets.BoldFont, new Color(1f, 0.92f, 0.65f));
        _detail.Texture = info is null ? null : _ui.Text(info.Detail, UiAssets.SmallFont, Colors.White);
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
            _strip.Add(Add(new TextureRect { Position = at, Size = new Vector2(48, 48), MouseFilter = MouseFilterEnum.Ignore }));
            _stripBacks.Add(Add(new ColorRect { Position = at + new Vector2(0, 50), Size = new Vector2(48, 5), Color = new Color(0, 0, 0, 0.85f), MouseFilter = MouseFilterEnum.Ignore }));
            _stripBars.Add(Add(new ColorRect { Position = at + new Vector2(1, 51), Size = new Vector2(46, 3), MouseFilter = MouseFilterEnum.Ignore }));
            _stripCounts.Add(Add(new TextureRect { Position = at, Size = new Vector2(48, 48), MouseFilter = MouseFilterEnum.Ignore }));
        }

        for (int i = 0; i < _strip.Count; i++)
        {
            bool has = i < strip.Count;
            _strip[i].Texture = !has ? null : strip[i].Order ? _ui.Cell(Buttons, strip[i].Icon) : _ui.PlayerCell(Icons, strip[i].Icon, info!.Player);
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
                ? _ui.Cell(Buttons, s.Button.ImageIndex) ?? _ui.Text(s.Button.Action == CardAction.Page ? s.Button.Argument : s.Button.Hotkey.ToString(), UiAssets.BoldFont)
                : null;
            _cardShade[i].Visible = slot is { Enabled: false };
            _cardIcons[i].Modulate = slot is { Enabled: false } ? new Color(0.5f, 0.5f, 0.5f) : Colors.White;
            _cardFrames[i].Visible = slot is { Active: true };
        }

        // The tooltip of the slot under the pointer follows the card (resources change while it is shown).
        if (_hovered >= 0 && !Equals(before[_hovered], _card[_hovered]))
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

    private void ShowTooltip(int slot)
    {
        _tooltip.Texture = null;
        if (_card[slot] is { } s)
        {
            string key = s.Button.Hotkey != '\0' ? $" ({s.Button.Hotkey})" : string.Empty;
            string detail = s.Detail.Length > 0 ? ": " + s.Detail : s.Enabled ? string.Empty : " - not available yet";
            _tooltip.Texture = _ui.Text(s.Button.Tooltip + key + detail, UiAssets.BoldFont, s.Enabled ? new Color(1f, 0.95f, 0.7f) : new Color(1f, 0.7f, 0.55f));
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
    private void AddOverlay(UiAssets ui, HudLayout layout)
    {
        if (!layout.Wide)
        {
            AddChild(new TextureRect { Texture = ui.Texture(layout.Image), MouseFilter = MouseFilterEnum.Ignore });
            return;
        }

        const int TopLeft = 805, TopRight = 219, TopFillX = 850, TopFill = 150;
        const int PanelLeft = 660, PanelRight = 164, PanelFill = 200;
        Vector2I size = layout.Size, bitmap = layout.Base;
        int top = layout.View.Position.Y;
        int panel = HudLayout.PanelHeight;
        int from = bitmap.Y - panel, to = size.Y - panel;
        Piece(new Rect2I(0, 0, TopLeft, top), Vector2I.Zero);
        Tile(new Rect2I(TopFillX, 0, TopFill, top), new Rect2I(TopLeft, 0, size.X - TopLeft - TopRight, top));
        Piece(new Rect2I(bitmap.X - TopRight, 0, TopRight, top), new Vector2I(size.X - TopRight, 0));
        Piece(new Rect2I(0, from, PanelLeft, panel), new Vector2I(0, to));
        Tile(new Rect2I(PanelLeft, from, PanelFill, panel), new Rect2I(PanelLeft, to, size.X - PanelLeft - PanelRight, panel));
        Piece(new Rect2I(bitmap.X - PanelRight, from, PanelRight, panel), new Vector2I(size.X - PanelRight, to));
        Piece(layout.GlobeOrnamentSource, layout.GlobeOrnament.Position);

        void Piece(Rect2I source, Vector2I at)
        {
            if (ui.Region(layout.Image, source) is { } t)
            {
                AddChild(new TextureRect { Texture = t, Position = at, MouseFilter = MouseFilterEnum.Ignore });
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
            var r = new Rect2(Camera.Position * Size, Camera.Size * Size);
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
