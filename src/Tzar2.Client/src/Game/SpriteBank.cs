using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Tzar2.Client.Rendering;
using Tzar2.Client.Viewer;
using Tzar2.Content.Assets;
using Tzar2.Content.Terrain;

namespace Tzar2.Client.Game;

/// <summary>One drawable IMAGES.INI entry: its sheet in the match atlas, timing, object box and companions.</summary>
public sealed class SheetRef
{
    /// <summary>The entry name.</summary>
    public required string Name { get; init; }

    /// <summary>Index of the sheet in the atlas.</summary>
    public required int SheetIndex { get; init; }

    /// <summary>The decoded sheet.</summary>
    public required SpriteSheet Sheet { get; init; }

    /// <summary>Timing.</summary>
    public required SpriteAnimation Animation { get; init; }

    /// <summary>IMAGES.INI <c>object</c> box (position of the footprint / unit tile inside the cell).</summary>
    public Rect2I Object { get; init; }

    /// <summary>IMAGES.INI <c>selection</c> box inside the cell (hit testing, bars), or the object box.</summary>
    public Rect2I Selection { get; init; }

    /// <summary>The point an effect is drawn at (IMAGES.INI <c>hotspot</c>, else the <c>object</c> box's corner).</summary>
    public Vector2I Anchor { get; init; }

    /// <summary>Offset of a <c>_c</c> overlay relative to the cell (its <c>object</c> x, y).</summary>
    public Vector2I OverlayOffset { get; init; }

    /// <summary>The player-colour overlay (<c>_c</c>), or null.</summary>
    public SheetRef? Overlay { get; set; }

    /// <summary>The shadow sprite (<c>_s</c>), or null.</summary>
    public SheetRef? Shadow { get; set; }
}

/// <summary>
/// The sprites of one match, packed into one atlas (ADR 0010/0013; REQ-GFX-05: only what the match's civilizations use)
/// and uploaded once. Entries are registered by IMAGES.INI name; trees come from the terrain set's TREES strip.
/// </summary>
public sealed class SpriteBank : IDisposable
{
    /// <summary>Cells of a terrain set's tree strip (<c>TREES.BMP</c>, 96×128 each).</summary>
    public const int TreeCells = 31;

    private readonly ContentStore _store;
    private readonly Dictionary<string, SheetRef?> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SpriteSheet> _sheets = [];

    /// <summary>Creates an empty bank.</summary>
    public SpriteBank(ContentStore store) => _store = store;

    /// <summary>The uploaded atlas (after <see cref="Upload"/>).</summary>
    public GpuSpriteAtlas? Gpu { get; private set; }

    /// <summary>The tree strip, or null.</summary>
    public SheetRef? Trees { get; private set; }

    /// <summary>Number of registered sheets.</summary>
    public int SheetCount => _sheets.Count;

    /// <summary>Registers (and decodes) an entry with its overlay and shadow; null when missing.</summary>
    public SheetRef? Add(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (_byName.TryGetValue(name, out SheetRef? known))
        {
            return known;
        }

        SheetRef? result = null;
        SpriteIndexEntry? entry = _store.Index.Find(name);
        if (entry?.Asset is { } asset)
        {
            try
            {
                result = Make(entry, asset);
                if (result is not null)
                {
                    if (_store.Index.OverlayOf(entry) is { Asset: { } overlayAsset } overlayEntry)
                    {
                        result.Overlay = Make(overlayEntry, overlayAsset);
                    }

                    if (_store.Index.ShadowOf(entry) is { Asset: { } shadowAsset } shadowEntry)
                    {
                        result.Shadow = Make(shadowEntry, shadowAsset);
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
            {
                GD.PrintErr($"Sprite {name}: {e.Message}");
            }
        }

        _byName[name] = result;
        return result;
    }

    /// <summary>The entry registered as <paramref name="name"/>, or null.</summary>
    public SheetRef? Get(string? name) => name is not null && _byName.TryGetValue(name, out SheetRef? s) ? s : null;

    /// <summary>Registers the tree strip and its shadows of <paramref name="set"/>.</summary>
    public void AddTrees(TerrainSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Trees is not { } trees || _store.LoadImage(trees) is not { } image)
        {
            return;
        }

        SpriteSheet sheet = ImageSheets.FromImage(image, TreeCells, 1);
        _sheets.Add(sheet);
        Trees = new SheetRef
        {
            Name = "trees",
            SheetIndex = _sheets.Count - 1,
            Sheet = sheet,
            Animation = SpriteAnimation.For(null, 1),
            Object = new Rect2I(32, 96, 32, 32),
            Selection = new Rect2I(24, 8, 48, 112),
        };
        if (set.TreeShadows is { } shadows && _store.LoadImage(shadows) is { } shadowImage)
        {
            SpriteSheet shadowSheet = ImageSheets.FromImage(shadowImage, TreeCells, 1, asShadowMask: true);
            _sheets.Add(shadowSheet);
            Trees.Shadow = new SheetRef { Name = "trees_s", SheetIndex = _sheets.Count - 1, Sheet = shadowSheet, Animation = SpriteAnimation.For(null, 1) };
        }
    }

    /// <summary>The packed atlas (after <see cref="Pack"/>), or null.</summary>
    public SpriteAtlas? Packed { get; private set; }

    /// <summary>Packs everything registered into an atlas (CPU only: may run off the main thread).</summary>
    public void Pack() => Packed = SpriteAtlas.Build(_sheets);

    /// <summary>Uploads the packed atlas (packing first if needed), then drops its CPU pages; main thread, once.</summary>
    public void Upload()
    {
        Gpu?.Dispose();
        if (Packed is null || Packed.PagesReleased)
        {
            Pack();
        }

        Gpu = new GpuSpriteAtlas(Packed!, _store.Palette);
        Packed!.ReleasePages();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Gpu?.Dispose();
        Gpu = null;
    }

    private SheetRef? Make(SpriteIndexEntry entry, string asset)
    {
        SpriteSheet? sheet = _store.LoadSheet(asset, entry);
        if (sheet is null)
        {
            return null;
        }

        _sheets.Add(sheet);
        int[]? box = entry.GetInts("object");
        int[]? sel = entry.GetInts("selection");
        int[]? hot = entry.GetInts("hotspot");
        Rect2I obj = box is { Length: >= 4 } ? new Rect2I(box[0], box[1], box[2], box[3]) : new Rect2I(0, 0, sheet.CellWidth, sheet.CellHeight);
        return new SheetRef
        {
            Name = entry.Name,
            SheetIndex = _sheets.Count - 1,
            Sheet = sheet,
            Animation = SpriteAnimation.For(entry, sheet.FramesPerDirection),
            Object = obj,
            Selection = sel is { Length: >= 4 } && sel[2] > 1 && sel[3] > 1 ? new Rect2I(sel[0], sel[1], sel[2], sel[3]) : obj,
            OverlayOffset = box is { Length: >= 2 } ? new Vector2I(box[0], box[1]) : Vector2I.Zero,
            Anchor = hot is { Length: >= 2 } ? new Vector2I(hot[0], hot[1]) : box is { Length: >= 2 } ? new Vector2I(box[0], box[1]) : new Vector2I(sheet.CellWidth / 2, sheet.CellHeight / 2),
        };
    }
}
