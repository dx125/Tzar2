using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using Tzar2.Client.Rendering;
using Tzar2.Content.Assets;

namespace Tzar2.Client.Viewer;

/// <summary>How the sprite page arranges cells.</summary>
public enum SpriteLayout
{
    /// <summary>Every direction animated side by side, one row per shown player.</summary>
    AnimateDirections,

    /// <summary>The whole sheet: directions across, frames down (selected player).</summary>
    AllFrames,
}

/// <summary>
/// Browses every <c>IMAGES.INI</c> entry (and any sprite/image file no entry references): all directions and frames,
/// recoloured for the eight players by the sprite shader, with the shadow sprite (<c>_s</c>/<c>_shadow</c>, mask
/// pass), the baked-shadow hints of unit sheets, and the <c>_c</c> player-colour overlays of buildings.
/// </summary>
public sealed class SpritePage : IViewerPage
{
    /// <summary>Player names in ramp order (pal.md; COLOR BARS order).</summary>
    public static readonly string[] PlayerNames = ["Blue", "Red", "Green", "Purple", "Yellow", "Cyan", "Orange", "Grey"];

    private static readonly string[] Compass8 = ["S", "SE", "E", "NE", "N", "NW", "W", "SW"];
    private const int Gap = 8;
    private const int Wrap = 8;

    private readonly AssetViewer _viewer;
    private readonly VBoxContainer _panel;
    private readonly LineEdit _filter;
    private readonly ItemList _list;
    private readonly OptionButton _player;
    private readonly OptionButton _rows;
    private readonly OptionButton _layout;
    private readonly CheckBox _shadows;
    private readonly CheckBox _baked;
    private readonly CheckBox _overlay;
    private readonly RichTextLabel _info;
    private readonly SpriteRenderer _renderer;
    private readonly List<(string Label, SpriteIndexEntry? Entry, string Asset)> _items = [];
    private readonly List<int> _visible = [];
    private GpuSpriteAtlas? _gpu;
    private Selection? _selection;
    private int[] _playerRows = [0];

    private sealed record Part(SpriteIndexEntry? Entry, SpriteSheet Sheet, int SheetIndex, SpriteAnimation Animation, Vector2I Offset);

    private sealed record Selection(string Label, Part Main, Part? Overlay, Part? Shadow, int FootY);

    /// <summary>Creates the page.</summary>
    public SpritePage(AssetViewer viewer)
    {
        _viewer = viewer;
        _panel = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _filter = new LineEdit { PlaceholderText = "Filter (name or file)…", ClearButtonEnabled = true };
        _filter.TextChanged += _ => Refilter();
        _panel.AddChild(_filter);
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 240) };
        _list.ItemSelected += i => Select(_visible[(int)i]);
        _panel.AddChild(_list);

        _player = Options(["None (reference grey ramp)", .. PlayerNames.Select((n, i) => $"{i + 1} {n}")], 1);
        _rows = Options(["Selected player", "All 8 players"], 0);
        _layout = Options(["Animate all directions", "All frames of the sheet"], 0);
        _panel.AddChild(Row("Player", _player));
        _panel.AddChild(Row("Rows", _rows));
        _panel.AddChild(Row("Layout", _layout));
        _shadows = Check("Shadows (mask pass, 50 % ⚠ UNK-03)", true);
        _baked = Check("Baked shadow hints as shadow", true);
        _overlay = Check("Player-colour overlay (_c)", true);
        _shadows.Toggled += v => _renderer!.SetShadowsVisible(v);
        _baked.Toggled += v => _renderer!.SetBakedShadowAsShadow(v);
        _info = new RichTextLabel { BbcodeEnabled = true, SelectionEnabled = true, CustomMinimumSize = new Vector2(0, 260), ScrollActive = true };
        _panel.AddChild(_info);

        _renderer = new SpriteRenderer(viewer, viewer.World.Root, WorldView.DrawSprites);
        _renderer.SetVisible(false);
        BuildItems();
        Refilter();

        OptionButton Options(string[] names, int selected)
        {
            var button = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            foreach (string name in names)
            {
                button.AddItem(name);
            }

            button.Selected = selected;
            button.ItemSelected += _ => UpdateRows();
            return button;
        }

        CheckBox Check(string text, bool on)
        {
            var box = new CheckBox { Text = text, ButtonPressed = on };
            _panel.AddChild(box);
            return box;
        }

        static HBoxContainer Row(string label, Control control)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(64, 0) });
            row.AddChild(control);
            return row;
        }
    }

    /// <inheritdoc/>
    public string Title => "Sprites";

    /// <inheritdoc/>
    public Control Panel => _panel;

    /// <summary>The selected entry's label, or null.</summary>
    public string? Selected => _selection?.Label;

    /// <summary>Selects the entry or file named <paramref name="name"/>; false when not found.</summary>
    public bool Select(string name)
    {
        int index = _items.FindIndex(i => string.Equals(i.Entry?.Name ?? i.Asset, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        _filter.Text = string.Empty;
        Refilter();
        Select(index);
        int row = _visible.IndexOf(index);
        _list.Select(row);
        _list.EnsureCurrentIsVisible();
        return true;
    }

    /// <summary>Sets the shown players: one row per entry (−1 = reference ramp); also sets the player option to the first.</summary>
    public void SetPlayers(params int[] players)
    {
        _playerRows = players.Length == 0 ? [0] : players;
        _player.Selected = _playerRows[0] + 1;
    }

    /// <summary>Sets the layout.</summary>
    public void SetLayout(SpriteLayout layout) => _layout.Selected = (int)layout;

    /// <summary>The entry labels in list order (all entries, unfiltered).</summary>
    public IEnumerable<string> EntryNames => _items.Select(i => i.Entry?.Name ?? i.Asset);

    /// <inheritdoc/>
    public void SetActive(bool active)
    {
        _renderer.SetVisible(active);
        if (!active)
        {
            _viewer.World.ClearLabels();
        }
    }

    /// <inheritdoc/>
    public void Update()
    {
        WorldView world = _viewer.World;
        _renderer.SetView(world.Transform, (Vector2I)_viewer.GetViewport().GetVisibleRect().Size);
        _renderer.Clear();
        world.ClearLabels();
        if (_selection is not { } sel)
        {
            _renderer.Commit();
            return;
        }

        SpriteSheet sheet = sel.Main.Sheet;
        int cw = sheet.CellWidth, ch = sheet.CellHeight;
        long t = _viewer.TimeMs;
        var labelColour = Color.Color8(255, 255, 255);
        if ((SpriteLayout)_layout.Selected == SpriteLayout.AllFrames)
        {
            int player = _playerRows[0];
            for (int d = 0; d < sheet.Directions; d++)
            {
                world.Label(world.ToScreen(new Vector2I(d * (cw + Gap), 0)) + new Vector2(0, -6), DirectionName(d, sheet.Directions), labelColour);
                for (int f = 0; f < sheet.FramesPerDirection; f++)
                {
                    var cell = new Vector2I(d * (cw + Gap), f * (ch + Gap));
                    AddCell(sel, cell, d, f, f, player, f);
                }
            }

            for (int f = 0; f < sheet.FramesPerDirection; f++)
            {
                world.Label(world.ToScreen(new Vector2I(0, f * (ch + Gap))) + new Vector2(-24, 14), f.ToString(CultureInfo.InvariantCulture), labelColour);
            }
        }
        else
        {
            int mainFrame = sel.Main.Animation.FrameAt(t);
            int overlayFrame = sel.Overlay?.Animation.FrameAt(t) ?? 0;
            int dirRows = (sheet.Directions + Wrap - 1) / Wrap;
            bool across = sheet.Directions == 1;
            for (int r = 0; r < _playerRows.Length; r++)
            {
                int player = _playerRows[r];
                for (int d = 0; d < sheet.Directions; d++)
                {
                    int col = across ? r : d % Wrap;
                    int row = across ? 0 : (r * dirRows) + (d / Wrap);
                    var cell = new Vector2I(col * (cw + Gap), row * (ch + Gap));
                    AddCell(sel, cell, d, mainFrame, overlayFrame, player, row);
                    if (r == 0 || across)
                    {
                        string head = across ? PlayerName(player) : DirectionName(d, sheet.Directions);
                        world.Label(world.ToScreen(cell) + new Vector2(0, -6), head, labelColour);
                    }
                }

                if (!across)
                {
                    world.Label(world.ToScreen(new Vector2I(0, r * dirRows * (ch + Gap))) + new Vector2(-24, 14), (player + 1).ToString(CultureInfo.InvariantCulture), labelColour);
                }
            }
        }

        _renderer.Commit();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _renderer.Dispose();
        _gpu?.Dispose();
        _gpu = null;
    }

    private static string DirectionName(int d, int directions)
        => directions == 8 ? $"{d} {Compass8[d]}" : d.ToString(CultureInfo.InvariantCulture);

    private static string PlayerName(int player) => player < 0 ? "reference" : PlayerNames[player];

    private void AddCell(Selection sel, Vector2I cellOrigin, int direction, int frame, int overlayFrame, int player, int row)
    {
        uint key = (uint)((((row * 4096) + sel.FootY + 16384) << 4) & 0x7FFFFFFF);
        AddPart(sel.Main, cellOrigin, direction, frame, player, key, false);
        if (sel.Overlay is { } overlay && _overlay.ButtonPressed)
        {
            AddPart(overlay, cellOrigin + overlay.Offset, direction, overlayFrame, player, key | 1, false);
        }

        if (sel.Shadow is { } shadow)
        {
            AddPart(shadow, cellOrigin, direction, frame, player, key, true);
        }
    }

    private void AddPart(Part part, Vector2I origin, int direction, int frame, int player, uint key, bool shadowOnly)
    {
        SpriteSheet sheet = part.Sheet;
        int d = Math.Min(direction, sheet.Directions - 1);
        int f = frame % sheet.FramesPerDirection;
        int index = (d * sheet.FramesPerDirection) + f;
        AtlasCell cell = _gpu!.Atlas.GetCell(part.SheetIndex, index);
        if (cell.IsEmpty)
        {
            return;
        }

        _renderer.Add(new SpriteInstance(
            origin.X + cell.OffsetX, origin.Y + cell.OffsetY, cell.Width, cell.Height,
            _gpu.GlobalCell(part.SheetIndex, index), cell.PaletteRow, player, key, shadowOnly));
    }

    private void UpdateRows()
    {
        int player = _player.Selected - 1;
        _playerRows = _rows.Selected == 1 ? [0, 1, 2, 3, 4, 5, 6, 7] : [player];
    }

    private void BuildItems()
    {
        SpriteIndex index = _viewer.Store.Index;
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (SpriteIndexEntry entry in index.Entries)
        {
            if (entry.Asset is not null)
            {
                referenced.Add(entry.Asset);
            }

            if (entry.Asset is not null && !index.IsCompanion(entry))
            {
                _items.Add((entry.Name, entry, entry.Asset));
            }
        }

        foreach (string prefix in (string[])["art", "art-extra"])
        {
            foreach (string path in _viewer.Store.Vfs.Enumerate(prefix))
            {
                if ((path.EndsWith(SpriteSheet.Extension, StringComparison.Ordinal) || path.EndsWith(ImageAsset.Extension, StringComparison.Ordinal))
                    && !referenced.Contains(path))
                {
                    _items.Add(("file: " + path, null, path));
                }
            }
        }
    }

    private void Refilter()
    {
        string filter = _filter.Text.Trim();
        _list.Clear();
        _visible.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            if (filter.Length == 0 || _items[i].Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || _items[i].Asset.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                _visible.Add(i);
                _list.AddItem(_items[i].Label);
            }
        }
    }

    private void Select(int item)
    {
        (string label, SpriteIndexEntry? entry, string asset) = _items[item];
        ContentStore store = _viewer.Store;
        var text = new StringBuilder();
        try
        {
            SpriteSheet main = store.LoadSheet(asset, entry) ?? throw new FileNotFoundException(asset);
            SpriteIndexEntry? overlayEntry = entry is null ? null : store.Index.OverlayOf(entry);
            SpriteIndexEntry? shadowEntry = entry is null ? null : store.Index.ShadowOf(entry);
            SpriteSheet? overlay = overlayEntry is null ? null : store.LoadSheet(overlayEntry.Asset!, overlayEntry);
            SpriteSheet? shadow = shadowEntry is null ? null : store.LoadSheet(shadowEntry.Asset!, shadowEntry);
            var sheets = new List<SpriteSheet> { main };
            Part Make(SpriteIndexEntry? e, SpriteSheet s, Vector2I offset)
            {
                sheets.Add(s);
                return new Part(e, s, sheets.Count - 1, SpriteAnimation.For(e, s.FramesPerDirection), offset);
            }

            var mainPart = new Part(entry, main, 0, SpriteAnimation.For(entry, main.FramesPerDirection), Vector2I.Zero);
            int[]? at = overlayEntry?.GetInts("object");
            Part? overlayPart = overlay is null ? null : Make(overlayEntry, overlay, at is { Length: >= 2 } ? new Vector2I(at[0], at[1]) : Vector2I.Zero);
            Part? shadowPart = shadow is null ? null : Make(shadowEntry, shadow, Vector2I.Zero);
            SpriteAtlas atlas = SpriteAtlas.Build(sheets);
            _gpu?.Dispose();
            _gpu = new GpuSpriteAtlas(atlas, store.Palette);
            _renderer.SetAtlas(_gpu);
            int[]? box = entry?.GetInts("object");
            int foot = box is { Length: 4 } ? box[1] + box[3] : main.CellHeight;
            _selection = new Selection(label, mainPart, overlayPart, shadowPart, foot);

            Describe(text, "Entry", entry, asset, main, mainPart.Animation);
            if (overlayPart is not null)
            {
                Describe(text, "Overlay", overlayEntry, overlayEntry!.Asset!, overlayPart.Sheet, overlayPart.Animation);
            }

            if (shadowPart is not null)
            {
                Describe(text, "Shadow", shadowEntry, shadowEntry!.Asset!, shadowPart.Sheet, shadowPart.Animation);
            }

            text.Append(CultureInfo.InvariantCulture, $"Atlas: {atlas.Pages(Tzar2.Content.Assets.PixelFormat.Indexed8).Count} R8 + {atlas.Pages(Tzar2.Content.Assets.PixelFormat.Rgb5A1).Count} RG8 + {atlas.Pages(Tzar2.Content.Assets.PixelFormat.R8).Count} mask layers of {atlas.PageSize}²\n");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            _selection = null;
            text.Clear();
            text.Append(CultureInfo.InvariantCulture, $"[color=#ff8080]{label}: {e.Message}[/color]\n");
        }

        _info.Text = text.ToString();
        _viewer.World.ResetPan();
    }

    private static void Describe(StringBuilder text, string role, SpriteIndexEntry? entry, string asset, SpriteSheet sheet, SpriteAnimation animation)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        text.Append(c, $"[b]{role}[/b] {entry?.Name ?? "(file)"}\n{asset}\n");
        text.Append(c, $"{PixelFormats.Name(sheet.Format)}, kind {sheet.SourceKind}, {sheet.Directions} dir × {sheet.FramesPerDirection} frames, cell {sheet.CellWidth}×{sheet.CellHeight}, {sheet.TrimmedPixelCount} px\n");
        if (sheet.PlayerFirst >= 0)
        {
            text.Append(c, $"player range {sheet.PlayerFirst}–{sheet.PlayerFirst + sheet.PlayerCount - 1}\n");
        }

        if (sheet.ShadowIndices.Count > 0 || sheet.ShadowColour >= 0)
        {
            string hints = sheet.ShadowColour >= 0 ? $"RGB555 0x{sheet.ShadowColour:X4}" : "indices " + string.Join(",", sheet.ShadowIndices);
            text.Append(c, $"baked-shadow hint: {hints} (⚠ UNK-03)\n");
        }

        text.Append(c, $"timing: {animation.Source}, cycle {animation.CycleMs} ms\n");
        if (entry is not null)
        {
            foreach (KeyValuePair<string, string[]> p in entry.Properties)
            {
                if (p.Key != "image")
                {
                    text.Append(c, $"  {p.Key} = {string.Join(" | ", p.Value)}\n");
                }
            }
        }
    }
}
