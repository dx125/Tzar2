using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using Tzar2.Client.Rendering;
using Tzar2.Content.Assets;
using Tzar2.Content.Terrain;

namespace Tzar2.Client.Viewer;

/// <summary>
/// Terrain sets: base textures, the 14-shape transitions on a demonstration corner grid (every shape occurs), animated
/// water and shores, and the raw bitmaps with their colour keys. Composition is <see cref="TerrainComposer"/>
/// (lossless RGB555); everything that rests on an unresolved rule (UNK-10) is labelled as such in the panel.
/// </summary>
public sealed class TerrainPage : IViewerPage
{
    /// <summary>Water/shore frame rate: <c>WATERFPS</c> of the user's <c>tzar.ini</c>, default 7 (bmp.md §3.3). Not imported (user settings).</summary>
    public const int WaterFps = 7;

    private const string Unresolved =
        "[color=#ffd080]⚠ UNK-10 (unresolved, shown as assumed):[/color]\n"
        + "• terrain stored per corner (vertex grid) — assumed\n"
        + "• base tile = (x mod cols, y mod rows) of the seamless atlas — assumed\n"
        + "• transition row = 4-corner mask − 1 (TL 1, TR 2, BR 4, BL 8 = lower terrain) — derived from key colours\n"
        + "• magenta key = lower terrain A, green key = B — hypothesis\n"
        + "• variant column = fixed hash of the tile — placeholder\n"
        + "• water 5 frames / shore 4 frames synchronisation — open (cycle = lcm)\n"
        + "• tiles with 3+ terrains, cliff pseudo-terrains 8/9 (18/28/38/48.BMP) — not composed\n";

    private readonly AssetViewer _viewer;
    private readonly VBoxContainer _panel;
    private readonly OptionButton _set;
    private readonly ItemList _list;
    private readonly CheckBox _raw;
    private readonly RichTextLabel _info;
    private readonly Rgb555Surface _composed;
    private readonly Rgb555Surface _bitmap;
    private readonly List<TerrainFile> _files = [];
    private bool _active;
    private bool _hasComposed;
    private bool _hasBitmap;
    private int _frames = 1;
    private int _pinnedFrame = -1;

    /// <summary>Creates the page.</summary>
    public TerrainPage(AssetViewer viewer)
    {
        _viewer = viewer;
        _panel = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _set = new OptionButton();
        foreach (TerrainSet set in viewer.Store.TerrainSets)
        {
            _set.AddItem($"{set.Name} ({set.Id})");
        }

        _set.ItemSelected += _ => FillList();
        _panel.AddChild(_set);
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 200) };
        _list.ItemSelected += i => Show((int)i);
        _panel.AddChild(_list);
        _raw = new CheckBox { Text = "Show the raw bitmap (keys visible)", ButtonPressed = true };
        _raw.Toggled += _ => ApplyVisibility();
        _panel.AddChild(_raw);
        _info = new RichTextLabel { BbcodeEnabled = true, SelectionEnabled = true, CustomMinimumSize = new Vector2(0, 260), ScrollActive = true };
        _panel.AddChild(_info);

        _composed = new Rgb555Surface(viewer.World.Root, WorldView.DrawSurfaces);
        _bitmap = new Rgb555Surface(viewer.World.Root, WorldView.DrawSurfaces + 1);
        ApplyVisibility();
        FillList();
    }

    /// <inheritdoc/>
    public string Title => "Terrain";

    /// <inheritdoc/>
    public Control Panel => _panel;

    /// <summary>Frames in the current animation cycle.</summary>
    public int FrameCount => _frames;

    /// <summary>Selects set <paramref name="setId"/> and pair <paramref name="pair"/> (e.g. <c>"12"</c>); false when not found.</summary>
    public bool Select(string setId, string pair)
    {
        IReadOnlyList<TerrainSet> sets = _viewer.Store.TerrainSets;
        for (int s = 0; s < sets.Count; s++)
        {
            if (string.Equals(sets[s].Id, setId, StringComparison.OrdinalIgnoreCase))
            {
                _set.Selected = s;
                FillList();
                int index = _files.FindIndex(f => f.Pair == pair);
                if (index < 0)
                {
                    return false;
                }

                _list.Select(index);
                Show(index);
                return true;
            }
        }

        return false;
    }

    /// <summary>Pins the animation to <paramref name="frame"/> (−1 follows the clock).</summary>
    public void PinFrame(int frame) => _pinnedFrame = frame;

    /// <inheritdoc/>
    public void SetActive(bool active)
    {
        _active = active;
        ApplyVisibility();
    }

    /// <inheritdoc/>
    public void Update()
    {
        WorldView world = _viewer.World;
        _composed.SetView(world.Transform);
        _bitmap.SetView(world.Transform);
        int frame = _pinnedFrame >= 0 ? _pinnedFrame : (int)(_viewer.TimeMs * WaterFps / 1000);
        _composed.SetFrame(frame);
        world.ClearLabels();
        if (_hasComposed)
        {
            world.Label(world.ToScreen(Vector2I.Zero) + new Vector2(0, -6), string.Create(CultureInfo.InvariantCulture, $"composed (frame {frame % _frames + 1}/{_frames} @ {WaterFps} fps)"), Colors.White);
        }

        if (_hasBitmap && _raw.ButtonPressed)
        {
            world.Label(world.ToScreen(BitmapOrigin) + new Vector2(0, -6), "raw bitmap", Colors.White);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _composed.Dispose();
        _bitmap.Dispose();
    }

    private Vector2I BitmapOrigin => new(_hasComposed ? _composed.Width + 32 : 0, 0);

    private void ApplyVisibility()
    {
        _composed.SetVisible(_active && _hasComposed);
        _bitmap.SetVisible(_active && _hasBitmap && _raw.ButtonPressed);
    }

    private TerrainSet? CurrentSet
        => _set.Selected >= 0 && _set.Selected < _viewer.Store.TerrainSets.Count ? _viewer.Store.TerrainSets[_set.Selected] : null;

    private void FillList()
    {
        _list.Clear();
        _files.Clear();
        if (CurrentSet is not { } set)
        {
            return;
        }

        foreach (TerrainFile file in set.Files)
        {
            _files.Add(file);
            string what = file.IsBase
                ? $"{file.Pair}  {set.TerrainName(file.A)}"
                : $"{file.Pair}  {set.TerrainName(file.A)} / {set.TerrainName(file.B)}";
            _list.AddItem(file.IsUnresolved ? what + "  ⚠ UNK-10 (raw)" : what);
        }
    }

    private void Show(int index)
    {
        TerrainSet set = CurrentSet!;
        TerrainFile file = _files[index];
        ContentStore store = _viewer.Store;
        var text = new StringBuilder();
        CultureInfo c = CultureInfo.InvariantCulture;
        text.Append(c, $"[b]{set.Name}[/b] pair {file.Pair}: {file.Asset ?? "(missing file)"}\n");
        text.Append(c, $"size {(file.Size is { } z ? $"{z.Columns},{z.Rows},{z.Frames}" : "not declared")}{(file.SizeInherited ? " (inherited from the pair sharing the file)" : string.Empty)}\n");
        _hasComposed = false;
        _hasBitmap = false;
        _frames = 1;
        if (!file.IsUnresolved)
        {
            TerrainGrid grid = file.IsBase ? new TerrainGrid(20, 12, file.A) : TerrainGrid.Demo(file.A, file.B);
            var composer = new TerrainComposer(set, store.LoadImage);
            _frames = composer.FrameCount(grid);
            var frames = new List<byte[]>();
            TerrainImage first = composer.Compose(grid, 0);
            frames.Add(first.Pixels);
            for (int f = 1; f < _frames; f++)
            {
                frames.Add(composer.Compose(grid, f).Pixels);
            }

            IReadOnlyList<string> issues = first.Issues;
            _composed.Set(first.Width, first.Height, frames, Vector2I.Zero, showKeys: false);
            _hasComposed = true;
            text.Append(c, $"demo grid {grid.Width}×{grid.Height} tiles, {_frames} frame(s)\n");
            if (issues.Count > 0)
            {
                text.Append(c, $"[color=#ff8080]{issues.Count} tile(s) not composed by rule[/color], e.g. {issues[0]}\n");
            }
        }
        else
        {
            text.Append("[color=#ffd080]Placement rules unknown (UNK-10): raw bitmap only.[/color]\n");
        }

        if (file.Asset is not null && store.LoadImage(file.Asset) is { Format: Tzar2.Content.Assets.PixelFormat.Rgb5A1 } bitmap)
        {
            _bitmap.Set(bitmap.Width, bitmap.Height, [bitmap.Pixels], BitmapOrigin, showKeys: true);
            _hasBitmap = true;
            text.Append(c, $"bitmap {bitmap.Width}×{bitmap.Height}");
            if (file.Size is { } size)
            {
                int expectedW = file.IsBase ? size.Columns * TerrainComposer.TileSize : size.Columns * Math.Max(1, size.Frames) * TerrainComposer.TileSize;
                if (bitmap.Width != expectedW)
                {
                    text.Append(c, $" [color=#ffd080](declared width {expectedW}: ⚠ only the declared part is used, bmp.md §3.1)[/color]");
                }
            }

            text.Append('\n');
        }

        text.Append(Unresolved);
        _info.Text = text.ToString();
        _viewer.World.ResetPan();
        ApplyVisibility();
    }
}
