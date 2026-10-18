using System;
using System.Collections.Generic;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Client.Rendering;
using Tzar2.Content.Assets;
using Tzar2.Content.Compiled;
using Tzar2.Content.Terrain;
using Tzar2.Core.Ids;
using Tzar2.Game.Session;
using RS = Godot.RenderingServer;

namespace Tzar2.Client.Game;

/// <summary>Toggles of the world view.</summary>
public sealed class WorldOptions
{
    /// <summary>Health bars of every object (`` ` `` held or Ctrl+`` ` ``).</summary>
    public bool AllHealthBars { get; set; }

    /// <summary>Translucent trees (<c>\</c>).</summary>
    public bool TranslucentTrees { get; set; }

    /// <summary>Fog of war.</summary>
    public bool Fog { get; set; } = true;

    /// <summary>Day/night (the original's DAYNIGHT setting).</summary>
    public bool DayNight { get; set; } = true;

    /// <summary>Shadows.</summary>
    public bool Shadows { get; set; } = true;
}

/// <summary>Something drawn on screen that the mouse can pick.</summary>
/// <param name="Index">Index into the frame's entities.</param>
/// <param name="Rect">Selection box in world pixels.</param>
/// <param name="FootY">Painter's order (higher = in front).</param>
public readonly record struct Pickable(int Index, Rect2 Rect, int FootY);

/// <summary>
/// Draws the world of a <see cref="GameFrame"/> (TDD §13.3 layers): terrain chunks, ground decals (corpses, fields),
/// y-sorted objects with shadows and player-colour overlays through the ADR 0010 sprite path, selection marks and
/// health bars, the placement ghost, fog of war and night. Animation and direction come from the frame's entity state;
/// positions are interpolated between ticks and snapped to whole pixels (REQ-GFX-01).
/// </summary>
public sealed class WorldRenderer : IDisposable
{
    /// <summary>World units per pixel (TDD §8.2).</summary>
    public const int UnitsPerPixel = 64;

    private const int TilePixels = 32;
    private readonly Ruleset _rules;
    private readonly PresentationCatalog _catalog;
    private readonly SpriteBank _bank;
    private readonly Rid _root;
    private readonly TerrainLayer _terrain;
    private readonly SpriteRenderer _ground;
    private readonly SpriteRenderer _objects;
    private readonly SpriteRenderer _translucent;
    private readonly Rid _marksUnder;
    private readonly Rid _marksOver;
    private readonly Rid _fogItem;
    private readonly ShaderMaterial _fogMaterial;
    private readonly ImageTexture _fogTexture;
    private readonly ImageTexture _lightTexture;
    private readonly Image _fogImage;
    private readonly Image _lightImage;
    private readonly byte[] _fogBytes;
    private readonly byte[] _lightBytes;
    private readonly List<Pickable> _pickables = [];
    private readonly HashSet<uint> _selected = [];
    private long _timeMs;

    /// <summary>Creates the renderer for a match.</summary>
    public WorldRenderer(Node host, Rid canvas, Ruleset rules, PresentationCatalog catalog, SpriteBank bank, TerrainSet set, TerrainGrid grid, Func<string, ImageAsset?> load, Image? lightPattern)
    {
        ArgumentNullException.ThrowIfNull(host);
        _rules = rules;
        _catalog = catalog;
        _bank = bank;
        _root = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(_root, canvas);
        _terrain = new TerrainLayer(_root, 10, set, grid, load);
        _ground = new SpriteRenderer(host, _root, 20);
        _marksUnder = Item(25);
        _objects = new SpriteRenderer(host, _root, 30);
        _translucent = new SpriteRenderer(host, _root, 34);
        _translucent.SetModulate(new Color(1, 1, 1, 0.45f));
        _marksOver = Item(40);
        _ground.SetAtlas(bank.Gpu);
        _objects.SetAtlas(bank.Gpu);
        _translucent.SetAtlas(bank.Gpu);

        MapWidth = grid.Width;
        MapHeight = grid.Height;
        _fogBytes = new byte[MapWidth * MapHeight];
        _lightBytes = new byte[MapWidth * MapHeight];
        _fogImage = Image.CreateFromData(MapWidth, MapHeight, false, Image.Format.L8, _fogBytes);
        _lightImage = Image.CreateFromData(MapWidth, MapHeight, false, Image.Format.L8, _lightBytes);
        _fogTexture = ImageTexture.CreateFromImage(_fogImage);
        _lightTexture = ImageTexture.CreateFromImage(_lightImage);
        _fogMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fog_night.gdshader") };
        _fogMaterial.SetShaderParameter("fog_tex", _fogTexture);
        _fogMaterial.SetShaderParameter("light_tex", _lightTexture);
        _fogMaterial.SetShaderParameter("map_pixels", new Vector2(MapWidth * TilePixels, MapHeight * TilePixels));
        if (lightPattern is not null)
        {
            _fogMaterial.SetShaderParameter("light_pattern", ImageTexture.CreateFromImage(lightPattern));
        }

        _fogItem = Item(50);
        RS.CanvasItemSetMaterial(_fogItem, _fogMaterial.GetRid());
        RS.CanvasItemSetDefaultTextureFilter(_fogItem, RS.CanvasItemTextureFilter.Linear);
        RS.CanvasItemAddTextureRect(_fogItem, new Rect2(0, 0, MapWidth * TilePixels, MapHeight * TilePixels), _fogTexture.GetRid());
    }

    /// <summary>Map width in tiles.</summary>
    public int MapWidth { get; }

    /// <summary>Map height in tiles.</summary>
    public int MapHeight { get; }

    /// <summary>The terrain (minimap colours).</summary>
    public TerrainLayer Terrain => _terrain;

    /// <summary>What can be picked, in draw order (last = front).</summary>
    public IReadOnlyList<Pickable> Pickables => _pickables;

    /// <summary>Instances drawn in the last frame.</summary>
    public int DrawnSprites => _ground.Count + _objects.Count;

    /// <summary>The sprite direction column of a sim facing (sim: 0 = east, clockwise; sheets: S, SE, E, NE, N, NW, W, SW).</summary>
    public static int Direction(int facing, int directions)
        => directions < 8 ? 0 : ((2 - facing) % 8 + 8) % 8;

    /// <summary>Draws <paramref name="frame"/>.</summary>
    public void Render(
        GameFrame frame,
        FrameInterpolator interpolator,
        int alpha,
        Vector2I pan,
        Vector2I origin,
        Vector2I viewSize,
        long timeMs,
        IReadOnlyList<EntityId> selection,
        byte localPlayer,
        WorldOptions options,
        float darkness,
        Ghost? ghost)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _timeMs = timeMs;
        var view = new Transform2D(new Vector2(1, 0), new Vector2(0, 1), (Vector2)(origin - pan));
        var visible = new Rect2I(pan, viewSize);
        RS.CanvasItemSetTransform(_root, Transform2D.Identity);
        _terrain.Update(view, visible, timeMs);
        Vector2I screen = viewSize + origin + new Vector2I(0, 200);
        _ground.SetView(view, screen);
        _objects.SetView(view, screen);
        _translucent.SetView(view, screen);
        _ground.SetShadowsVisible(options.Shadows);
        _objects.SetShadowsVisible(options.Shadows);
        RS.CanvasItemSetTransform(_marksUnder, view);
        RS.CanvasItemSetTransform(_marksOver, view);
        RS.CanvasItemSetTransform(_fogItem, view);
        RS.CanvasItemClear(_marksUnder);
        RS.CanvasItemClear(_marksOver);
        _ground.Clear();
        _objects.Clear();
        _translucent.Clear();
        _pickables.Clear();
        _selected.Clear();
        foreach (EntityId id in selection)
        {
            _selected.Add(id.Value);
        }

        byte[] fog = frame.Visibility;
        bool useFog = options.Fog && fog.Length == MapWidth * MapHeight;
        var cull = new Rect2I(visible.Position - new Vector2I(256, 256), visible.Size + new Vector2I(512, 512));

        foreach (FrameCorpse corpse in frame.Corpses)
        {
            int px = corpse.X / UnitsPerPixel, py = corpse.Y / UnitsPerPixel;
            if (!cull.HasPoint(new Vector2I(px, py)) || (useFog && Fog(fog, corpse.X, corpse.Y) < 2))
            {
                continue;
            }

            if (_bank.Get(_catalog.Type(corpse.TypeId).Image(UnitAnim.Die)) is { } die)
            {
                int f = corpse.AgeMs >= die.Animation.CycleMs ? die.Sheet.FramesPerDirection - 1 : die.Animation.FrameAt(corpse.AgeMs);
                int dir = (int)((uint)(corpse.X ^ corpse.Y) % (uint)Math.Max(1, die.Sheet.Directions));
                var at = new Vector2I(px - die.Object.Position.X - (die.Object.Size.X / 2), py - die.Object.Position.Y - (die.Object.Size.Y / 2));
                AddSheet(_ground, die, at, dir, f, corpse.Owner < 8 ? corpse.Owner : -1, (uint)(py + 4096) << 6, shadows: true);
            }
        }

        ReadOnlySpan<FrameEntity> entities = frame.Entities;
        for (int i = 0; i < entities.Length; i++)
        {
            ref readonly FrameEntity e = ref entities[i];
            (int x, int y) = interpolator.Position(e, alpha);
            int px = x / UnitsPerPixel, py = y / UnitsPerPixel;
            if (!cull.HasPoint(new Vector2I(px, py)))
            {
                continue;
            }

            EntityTypeDefinition def = _rules.EntityTypes[e.TypeId];
            bool own = e.Owner == localPlayer;
            if (useFog && !own)
            {
                int seen = Fog(fog, x, y);
                if (seen == 0 || (seen == 1 && def.IsMobile))
                {
                    continue;
                }
            }

            int player = e.Owner < 8 ? e.Owner : -1;
            DrawEntity(in e, i, def, px, py, player, timeMs, options);
        }

        _ground.Commit();
        _translucent.Commit();
        DrawMarks(frame, interpolator, alpha, localPlayer, options);
        if (ghost is { } g)
        {
            DrawGhost(g, localPlayer);
        }

        _objects.Commit();

        UpdateFogAndLight(frame, useFog, localPlayer, options.DayNight ? darkness : 0f);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _terrain.Dispose();
        _ground.Dispose();
        _objects.Dispose();
        _translucent.Dispose();
        foreach (Rid rid in new[] { _marksUnder, _marksOver, _fogItem, _root })
        {
            RS.FreeRid(rid);
        }

        _fogMaterial.Dispose();
        _fogTexture.Dispose();
        _lightTexture.Dispose();
    }

    /// <summary>The construction-site sheet name for a footprint (<c>build1x1</c> … <c>build6x6</c>).</summary>
    public static string BuildSiteName(EntityTypeDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        int n = Math.Clamp(Math.Max(def.FootprintWidth, def.FootprintHeight), 1, 8);
        return $"build{n}x{n}";
    }

    private int Fog(byte[] fog, int x, int y)
    {
        int tx = Math.Clamp(x / (TilePixels * UnitsPerPixel), 0, MapWidth - 1), ty = Math.Clamp(y / (TilePixels * UnitsPerPixel), 0, MapHeight - 1);
        return fog[(ty * MapWidth) + tx];
    }

    private void DrawEntity(in FrameEntity e, int index, EntityTypeDefinition def, int px, int py, int player, long timeMs, WorldOptions options)
    {
        TypeVisual visual = _catalog.Type(e.TypeId);
        uint phase = (uint)e.Id.Index * 7919u;
        if (def.HasFootprint)
        {
            int left = e.TileX * TilePixels, top = e.TileY * TilePixels;
            int foot = top + (def.FootprintHeight * TilePixels);
            var footprint = new Rect2(left, top, def.FootprintWidth * TilePixels, def.FootprintHeight * TilePixels);
            if (def.Key == "comm.tree" && _bank.Trees is { } trees)
            {
                // Living trees are cells 8..26 of the strip (stumps and bushes first, dead trees last); ⚠ placement
                // within the tile (trunk at the cell's bottom centre) is assumed, TREES.INI pos_tree is not decoded.
                int cell = 8 + (int)(phase % 19u);
                var at = new Vector2I(left + (TilePixels / 2) - 48, top + TilePixels - 124);
                AddSheet(options.TranslucentTrees ? _translucent : _objects, trees, at, cell, 0, -1, (uint)(foot + 4096) << 6, shadows: true);
                _pickables.Add(new Pickable(index, new Rect2(at.X + 24, at.Y + 8, 48, 112), foot));
                return;
            }

            SheetRef? sheet;
            int frameIndex = 0, variant = 0;
            bool ground = visual.Class == "field";
            if (e.ConstructionPermille >= 0 && !ground)
            {
                sheet = _bank.Get(BuildSiteName(def));
                if (sheet is not null)
                {
                    frameIndex = Math.Min(sheet.Sheet.FramesPerDirection - 1, e.ConstructionPermille * sheet.Sheet.FramesPerDirection / 1000);
                }
            }
            else
            {
                sheet = _bank.Get(visual.Image(UnitAnim.Base));
                if (sheet is not null)
                {
                    variant = (int)(phase % (uint)Math.Max(1, sheet.Sheet.Directions));
                    if (def.Traits.ResourceNode is { Amount: > 0 } node && e.ResourceLeft >= 0 && sheet.Sheet.FramesPerDirection > 1)
                    {
                        // Rocks shrink as they are mined (frames = stages; ⚠ the original's thresholds are not measured).
                        int left4 = (int)Math.Clamp(e.ResourceLeft * (long)sheet.Sheet.FramesPerDirection / node.Amount, 0, sheet.Sheet.FramesPerDirection - 1);
                        frameIndex = sheet.Sheet.FramesPerDirection - 1 - left4;
                    }
                    else
                    {
                        frameIndex = sheet.Animation.FrameAt(timeMs + phase);
                    }
                }
            }

            if (sheet is null)
            {
                _pickables.Add(new Pickable(index, footprint, foot));
                return;
            }

            var origin = new Vector2I(left - sheet.Object.Position.X, top - sheet.Object.Position.Y);
            AddSheet(ground ? _ground : _objects, sheet, origin, variant, frameIndex, player, (uint)((ground ? 0 : foot) + 4096) << 6, shadows: true);
            Rect2 sel = new(origin + sheet.Selection.Position, sheet.Selection.Size);
            _pickables.Add(new Pickable(index, sel.Size.X > 4 ? sel : footprint, ground ? 0 : foot));
            return;
        }

        UnitAnim anim = e.Activity switch
        {
            UnitActivity.Walk => UnitAnim.Base,
            UnitActivity.Attack => UnitAnim.Attack,
            UnitActivity.Chop => UnitAnim.Chop,
            UnitActivity.Mine => UnitAnim.Mine,
            UnitActivity.Farm => UnitAnim.Farm,
            UnitActivity.Build => UnitAnim.Build,
            _ => UnitAnim.Idle,
        };
        SheetRef? s = _bank.Get(visual.Image(anim)) ?? _bank.Get(visual.Image(UnitAnim.Base));
        if (s is null)
        {
            _pickables.Add(new Pickable(index, new Rect2(px - 8, py - 16, 16, 24), py));
            return;
        }

        int frame;
        if (anim == UnitAnim.Idle && !visual.Images.ContainsKey(UnitAnim.Idle))
        {
            frame = 0;
        }
        else if (anim == UnitAnim.Attack && e.SwingMs >= 0)
        {
            frame = e.SwingMs >= s.Animation.CycleMs ? 0 : s.Animation.FrameAt(e.SwingMs);
        }
        else
        {
            frame = s.Animation.FrameAt(timeMs + phase);
        }

        int dir = Direction(e.Facing, s.Sheet.Directions);
        var cellOrigin = new Vector2I(px - s.Object.Position.X - (s.Object.Size.X / 2), py - s.Object.Position.Y - (s.Object.Size.Y / 2));
        AddSheet(_objects, s, cellOrigin, dir, frame, player, ((uint)(py + 4096) << 6) | 2, shadows: true);
        _pickables.Add(new Pickable(index, new Rect2(cellOrigin + s.Selection.Position, s.Selection.Size), py));
    }

    private void AddSheet(SpriteRenderer renderer, SheetRef sheet, Vector2I origin, int direction, int frame, int player, uint key, bool shadows)
    {
        Add(renderer, sheet, origin, direction, frame, player, key, shadowOnly: false);
        if (sheet.Overlay is { } overlay)
        {
            int f = overlay.Animation.FrameAt(_timeMs);
            Add(renderer, overlay, origin + overlay.OverlayOffset, direction, f, player, key | 1, shadowOnly: false);
        }

        if (shadows && sheet.Shadow is { } shadow)
        {
            Add(renderer, shadow, origin, direction, frame, player, key, shadowOnly: true);
        }
    }

    private void Add(SpriteRenderer renderer, SheetRef sheet, Vector2I origin, int direction, int frame, int player, uint key, bool shadowOnly)
    {
        SpriteSheet s = sheet.Sheet;
        int d = Math.Clamp(direction, 0, s.Directions - 1);
        int f = ((frame % s.FramesPerDirection) + s.FramesPerDirection) % s.FramesPerDirection;
        int index = (d * s.FramesPerDirection) + f;
        GpuSpriteAtlas gpu = _bank.Gpu!;
        AtlasCell cell = gpu.Atlas.GetCell(sheet.SheetIndex, index);
        if (cell.IsEmpty)
        {
            return;
        }

        renderer.Add(new SpriteInstance(
            origin.X + cell.OffsetX, origin.Y + cell.OffsetY, cell.Width, cell.Height,
            gpu.GlobalCell(sheet.SheetIndex, index), cell.PaletteRow, player, key, shadowOnly));
    }

    private void DrawMarks(GameFrame frame, FrameInterpolator interpolator, int alpha, byte localPlayer, WorldOptions options)
    {
        ReadOnlySpan<FrameEntity> entities = frame.Entities;
        foreach (Pickable p in _pickables)
        {
            ref readonly FrameEntity e = ref entities[p.Index];
            bool selected = _selected.Contains(e.Id.Value);
            EntityTypeDefinition def = _rules.EntityTypes[e.TypeId];
            if (!selected && !(options.AllHealthBars && e.MaxHealth > 0 && def.Traits.Category is EntityCategory.Unit or EntityCategory.Building))
            {
                continue;
            }

            Color mark = e.Owner == localPlayer ? new Color(0.25f, 1f, 0.25f) : e.Owner >= 8 ? new Color(1f, 1f, 0.4f) : new Color(1f, 0.3f, 0.25f);
            if (selected)
            {
                if (def.HasFootprint)
                {
                    // Structures: the footprint outline (⚠ the original's selection look is not measured yet).
                    var r = new Rect2(e.TileX * TilePixels, e.TileY * TilePixels, Math.Max(1, def.FootprintWidth) * TilePixels, Math.Max(1, def.FootprintHeight) * TilePixels);
                    Corners(_marksUnder, r, mark);
                }
                else
                {
                    (int x, int y) = interpolator.Position(e, alpha);
                    Ellipse(_marksUnder, new Vector2(x / UnitsPerPixel, (y / UnitsPerPixel) + 3), Math.Max(8f, p.Rect.Size.X * 0.45f), mark);
                }
            }

            if (e.MaxHealth > 0 && def.Traits.Category != EntityCategory.Resource)
            {
                float w = Math.Clamp(p.Rect.Size.X, 16f, 64f);
                var bar = new Rect2(p.Rect.Position.X + ((p.Rect.Size.X - w) / 2), p.Rect.Position.Y - 5, w, 3);
                float share = Math.Clamp(e.Health / (float)e.MaxHealth, 0f, 1f);
                Color c = share > 0.5f ? new Color(0.1f, 0.85f, 0.1f) : share > 0.25f ? new Color(0.95f, 0.85f, 0.1f) : new Color(0.9f, 0.1f, 0.1f);
                RS.CanvasItemAddRect(_marksOver, bar.Grow(1), Colors.Black);
                RS.CanvasItemAddRect(_marksOver, new Rect2(bar.Position, new Vector2(MathF.Round(w * share), 3)), c);
                if (e.ConstructionPermille >= 0)
                {
                    var progress = new Rect2(bar.Position + new Vector2(0, 5), new Vector2(w, 2));
                    RS.CanvasItemAddRect(_marksOver, progress.Grow(1), Colors.Black);
                    RS.CanvasItemAddRect(_marksOver, new Rect2(progress.Position, new Vector2(MathF.Round(w * e.ConstructionPermille / 1000f), 2)), new Color(0.9f, 0.75f, 0.3f));
                }
            }
        }
    }

    private void DrawGhost(Ghost ghost, byte localPlayer)
    {
        EntityTypeDefinition def = _rules.EntityTypes[ghost.TypeId];
        int left = ghost.TileX * TilePixels, top = ghost.TileY * TilePixels;
        var rect = new Rect2(left, top, def.FootprintWidth * TilePixels, def.FootprintHeight * TilePixels);
        if (_bank.Get(_catalog.Type(ghost.TypeId).Image(UnitAnim.Base)) is { } sheet)
        {
            var origin = new Vector2I(left - sheet.Object.Position.X, top - sheet.Object.Position.Y);
            Add(_objects, sheet, origin, 0, 0, localPlayer, uint.MaxValue >> 1, shadowOnly: false);
        }

        Color tint = ghost.Legal ? new Color(0.2f, 1f, 0.2f, 0.35f) : new Color(1f, 0.15f, 0.1f, 0.45f);
        RS.CanvasItemAddRect(_marksOver, rect, tint);
        Corners(_marksOver, rect, ghost.Legal ? Colors.Green : Colors.Red);
    }

    private void UpdateFogAndLight(GameFrame frame, bool useFog, byte localPlayer, float darkness)
    {
        _fogMaterial.SetShaderParameter("fog_enabled", useFog);
        _fogMaterial.SetShaderParameter("darkness", darkness);
        if (useFog)
        {
            byte[] fog = frame.Visibility;
            for (int i = 0; i < _fogBytes.Length; i++)
            {
                _fogBytes[i] = fog[i] == 0 ? (byte)0 : fog[i] == 1 ? (byte)128 : (byte)255;
            }

            _fogImage.SetData(MapWidth, MapHeight, false, Image.Format.L8, _fogBytes);
            _fogTexture.Update(_fogImage);
        }

        if (darkness > 0f)
        {
            // ⚠ UNK-32 placeholder: light discs of 3 tiles around own units and 5 around own buildings.
            Array.Clear(_lightBytes);
            foreach (ref readonly FrameEntity e in frame.Entities)
            {
                if (e.Owner != localPlayer)
                {
                    continue;
                }

                int r = e.TileX >= 0 ? 5 : 3;
                int tx = e.X / (TilePixels * UnitsPerPixel), ty = e.Y / (TilePixels * UnitsPerPixel);
                for (int y = Math.Max(0, ty - r); y <= Math.Min(MapHeight - 1, ty + r); y++)
                {
                    for (int x = Math.Max(0, tx - r); x <= Math.Min(MapWidth - 1, tx + r); x++)
                    {
                        int d2 = ((x - tx) * (x - tx)) + ((y - ty) * (y - ty));
                        if (d2 <= r * r)
                        {
                            int v = 255 - (d2 * 200 / (r * r));
                            int o = (y * MapWidth) + x;
                            _lightBytes[o] = (byte)Math.Max(_lightBytes[o], v);
                        }
                    }
                }
            }

            _lightImage.SetData(MapWidth, MapHeight, false, Image.Format.L8, _lightBytes);
            _lightTexture.Update(_lightImage);
        }
    }

    private Rid Item(int drawIndex)
    {
        Rid item = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(item, _root);
        RS.CanvasItemSetDrawIndex(item, drawIndex);
        RS.CanvasItemSetDefaultTextureFilter(item, RS.CanvasItemTextureFilter.Nearest);
        return item;
    }

    private static void Ellipse(Rid item, Vector2 centre, float rx, Color colour)
    {
        const int n = 20;
        var points = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float a = i * MathF.Tau / n;
            points[i] = centre + new Vector2(MathF.Round(MathF.Cos(a) * rx), MathF.Round(MathF.Sin(a) * rx * 0.5f));
        }

        RS.CanvasItemAddPolyline(item, points, [colour], 1f);
    }

    private static void Corners(Rid item, Rect2 r, Color colour)
    {
        float l = Math.Min(10f, r.Size.X / 3);
        Vector2 a = r.Position, b = r.Position + new Vector2(r.Size.X, 0), c = r.End, d = r.Position + new Vector2(0, r.Size.Y);
        foreach ((Vector2 p, Vector2 dx, Vector2 dy) in new[] { (a, Vector2.Right, Vector2.Down), (b, Vector2.Left, Vector2.Down), (c, Vector2.Left, Vector2.Up), (d, Vector2.Right, Vector2.Up) })
        {
            RS.CanvasItemAddLine(item, p, p + (dx * l), colour);
            RS.CanvasItemAddLine(item, p, p + (dy * l), colour);
        }
    }
}

/// <summary>A build-placement ghost.</summary>
/// <param name="TypeId">Building type.</param>
/// <param name="TileX">Footprint top-left tile X.</param>
/// <param name="TileY">Footprint top-left tile Y.</param>
/// <param name="Legal">Whether the sim would accept it now.</param>
public readonly record struct Ghost(ushort TypeId, int TileX, int TileY, bool Legal);
