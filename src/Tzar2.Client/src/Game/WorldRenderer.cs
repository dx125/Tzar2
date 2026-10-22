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

    /// <summary>The selection's waypoints and the objects it attacks or guards in turn (Shift held; CTRL-025).</summary>
    public bool Waypoints { get; set; }

    /// <summary>A unit's control group digit, 0 for none: drawn in the corner of its selection marks (CTRL-020).</summary>
    public Func<EntityId, int>? GroupOf { get; set; }
}

/// <summary>Something drawn on screen that the mouse can pick.</summary>
/// <param name="Index">Index into the frame's entities.</param>
/// <param name="Rect">Selection box in world pixels (IMAGES.INI <c>selection</c>: marks and health bars).</param>
/// <param name="FootY">Painter's order (higher = in front).</param>
/// <param name="Hit">What a click or a drag box hits, in world pixels: for units the selection box joined with the drawn
/// figure, so a click on the head selects as well as one at the feet.</param>
public readonly record struct Pickable(int Index, Rect2 Rect, int FootY, Rect2 Hit);

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
    private readonly SpriteRenderer _ghostLayer; // the building being placed: green, without the kingdom's colours
    private readonly SpriteRenderer _glow;
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
    private readonly Dictionary<uint, (int X, int Y)> _shotStart = [];
    private readonly Dictionary<uint, (int X, int Y)> _shotPrevious = [];
    private readonly Dictionary<uint, (int X, int Y, byte Kind)> _shotCurrent = [];
    private readonly HashSet<uint> _shotSeen = [];
    private readonly List<uint> _shotGone = [];
    private readonly List<(SheetRef Sheet, int X, int Y, long StartMs)> _bursts = [];
    private long _timeMs;
    private (int X, int Y, uint Target, long StartMs)? _order;
    private bool _lightsValid;

    // The blows each unit has struck, counted when its swing starts again (PRES-005's alternate swings).
    private readonly Dictionary<uint, (int LastSwingMs, uint Blows)> _swings = [];
    private readonly TerrainGrid _grid;

    /// <summary>Where trees and their shadows are drawn and which pictures grow where (PRES-009; set by the game screen from the terrain set's data).</summary>
    public TreeLayout Trees { get; set; } = TreeLayout.Default;
    private ImageTexture? _lightPatternTexture;

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
        _grid = grid;
        _ground = new SpriteRenderer(host, _root, 20);
        _marksUnder = Item(25);
        _objects = new SpriteRenderer(host, _root, 30);
        _translucent = new SpriteRenderer(host, _root, 34);
        _translucent.SetModulate(new Color(1, 1, 1, 0.45f));
        _glow = new SpriteRenderer(host, _root, 36, additive: true);
        _glow.SetShadowsVisible(false);
        _ghostLayer = new SpriteRenderer(host, _root, 38);
        _ghostLayer.SetModulate(new Color(0.35f, 1f, 0.35f, 0.8f));
        _ghostLayer.SetShadowsVisible(false);
        _marksOver = Item(40);
        _ground.SetAtlas(bank.Gpu);
        _objects.SetAtlas(bank.Gpu);
        _translucent.SetAtlas(bank.Gpu);
        _glow.SetAtlas(bank.Gpu);
        _ghostLayer.SetAtlas(bank.Gpu);

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
            _lightPatternTexture = ImageTexture.CreateFromImage(lightPattern);
            _fogMaterial.SetShaderParameter("light_pattern", _lightPatternTexture);
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

    /// <summary>How long the marks of an order's target flash (three blinks).</summary>
    public const int ConfirmMs = 720;

    /// <summary>
    /// Confirms an order the player gave: on <paramref name="target"/> its marks flash; on the ground (no target) the
    /// original's red cross turns and shrinks at (<paramref name="x"/>, <paramref name="y"/>) (world units). Game time:
    /// it waits with the pause.
    /// </summary>
    public void ConfirmOrder(int x, int y, EntityId target) => _order = (x, y, target.Value, _timeMs);

    /// <summary>Draws <paramref name="frame"/>.</summary>
    public void Render(
        GameFrame frame,
        bool fresh,
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
        _glow.SetView(view, screen);
        _ghostLayer.SetView(view, screen);
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
        _glow.Clear();
        _ghostLayer.Clear();
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

            int player = e.SeemsOwner >= 0 ? e.SeemsOwner : e.Owner < 8 ? e.Owner : -1; // a taken-over unit keeps its old colours
            DrawEntity(in e, i, def, px, py, player, timeMs, options);
        }

        DrawProjectiles(frame, fresh, alpha, cull);
        DrawOrder(frame, localPlayer);
        _ground.Commit();
        _translucent.Commit();
        _glow.Commit();
        DrawMarks(frame, interpolator, alpha, localPlayer, options);
        if (options.Waypoints)
        {
            DrawWaypoints(frame);
        }

        if (ghost is { } g)
        {
            DrawGhost(g);
        }

        _objects.Commit();
        _ghostLayer.Commit();

        UpdateFogAndLight(frame, fresh, useFog, localPlayer, options.DayNight ? darkness : 0f);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _terrain.Dispose();
        _ground.Dispose();
        _objects.Dispose();
        _translucent.Dispose();
        _glow.Dispose();
        _ghostLayer.Dispose();
        foreach (Rid rid in new[] { _marksUnder, _marksOver, _fogItem, _root })
        {
            RS.FreeRid(rid);
        }

        _fogMaterial.Dispose();
        _fogTexture.Dispose();
        _lightTexture.Dispose();
        _lightPatternTexture?.Dispose();
        _fogImage.Dispose();
        _lightImage.Dispose();
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
            (int width, int height) = def.FootprintSize(e.Rotated && def.CanTurn);
            int foot = top + (height * TilePixels);
            var footprint = new Rect2(left, top, width * TilePixels, height * TilePixels);
            if (def.Key == "comm.tree" && _bank.Trees is { } trees)
            {
                // PRES-009: a picture of the kinds that grow on the tile's terrain ([treename], [treeterrain]), the tree
                // and its shadow placed by their anchors ([trees] pos_tree, pos_shadow).
                int cell = Trees.Picture(_grid[Math.Clamp(e.TileX, 0, _grid.Width), Math.Clamp(e.TileY, 0, _grid.Height)], phase);
                (int tx, int ty) = Trees.TreeOrigin(e.TileX, e.TileY);
                var at = new Vector2I(tx, ty);
                SpriteRenderer layer = options.TranslucentTrees ? _translucent : _objects;
                Add(layer, trees, at, cell, 0, -1, (uint)(foot + 4096) << 6, shadowOnly: false);
                if (trees.Shadow is { } treeShadow)
                {
                    (int sx, int sy) = Trees.ShadowOrigin(e.TileX, e.TileY);
                    Add(layer, treeShadow, new Vector2I(sx, sy), cell, 0, -1, (uint)(foot + 4096) << 6, shadowOnly: true);
                }

                var treeBox = new Rect2(at.X + 24, at.Y + 8, 48, 112);
                _pickables.Add(new Pickable(index, treeBox, foot, treeBox));
                return;
            }

            SheetRef? sheet;
            int frameIndex = 0, variant = 0;
            // Fields and bridges lie on the ground: units walk over them and are drawn on top (a bridge hid the units
            // crossing it, the owner's fourth play-test).
            bool field = def.Traits.WalkableFootprint;
            bool ground = field || def.Traits.IsBridge;
            int facing = (e.Rotated ? 2 : 0) + (e.Mirrored ? 1 : 0);
            if (_bank.Get(SpriteRules.GateImage(def, facing, site: e.ConstructionPermille >= 0)) is { } gate)
            {
                // Gates draw their drawbridge for their facing: raised when closed, lowered when open (CTRL-024).
                sheet = gate;
                frameIndex = e.ConstructionPermille >= 0 ? SpriteRules.SiteFrame(e.ConstructionPermille, gate.Sheet.FramesPerDirection)
                    : SpriteRules.GateFrame((e.Gate & FrameGate.Open) != 0);
            }
            else if (e.ConstructionPermille >= 0 && !field)
            {
                sheet = _bank.Get(SpriteRules.BuildSiteName(def));
                if (sheet is not null)
                {
                    frameIndex = SpriteRules.SiteFrame(e.ConstructionPermille, sheet.Sheet.FramesPerDirection);
                }
            }
            else
            {
                sheet = (e.ItemLook is { } look ? _bank.Get(look) : null) ?? _bank.Get(SpriteRules.Turned(visual.Image(UnitAnim.Base), e.Rotated)); // an item's own look (ITEM-016)
                if (sheet is not null)
                {
                    variant = (int)(phase % (uint)Math.Max(1, sheet.Sheet.Directions));
                    if (def.Traits.ResourceNode is { Amount: > 0 } node && e.ResourceLeft >= 0 && sheet.Sheet.FramesPerDirection > 1)
                    {
                        // Rocks shrink as they are mined (frames = stages).
                        frameIndex = SpriteRules.DepletionFrame(e.ResourceLeft, node.Amount, sheet.Sheet.FramesPerDirection);
                    }
                    else
                    {
                        frameIndex = sheet.Animation.FrameAt(timeMs + phase);
                    }
                }
            }

            if (sheet is null)
            {
                _pickables.Add(new Pickable(index, footprint, foot, footprint));
                return;
            }

            var origin = new Vector2I(left - sheet.Object.Position.X, top - sheet.Object.Position.Y);
            AddSheet(ground ? _ground : _objects, sheet, origin, variant, frameIndex, player, (uint)((ground ? 0 : foot) + 4096) << 6, shadows: true);
            if (e.Burning)
            {
                DrawFire(in e, def, left, top, foot);
            }

            Rect2 sel = new(origin + sheet.Selection.Position, sheet.Selection.Size);
            Rect2 box = sel.Size.X > 4 ? sel : footprint;
            _pickables.Add(new Pickable(index, box, ground ? 0 : foot, box));
            return;
        }

        UnitAnim anim = e.Activity switch
        {
            UnitActivity.Walk when e.CarryAmount > 0 => e.Carry switch
            {
                FrameCarry.Wood => UnitAnim.CarryWood,
                FrameCarry.Gold => UnitAnim.CarryGold,
                FrameCarry.Stone => UnitAnim.CarryStone,
                FrameCarry.Food => UnitAnim.CarryFood,
                FrameCarry.Milk => UnitAnim.CarryMilk,
                FrameCarry.Meat => UnitAnim.CarryMeat,
                _ => UnitAnim.Base,
            },
            UnitActivity.Walk => UnitAnim.Base,
            UnitActivity.Milk => UnitAnim.Milk,
            UnitActivity.Attack => UnitAnim.Attack,
            UnitActivity.Chop => UnitAnim.Chop,
            UnitActivity.Mine => UnitAnim.Mine,
            UnitActivity.Farm => UnitAnim.Farm,
            UnitActivity.Build => UnitAnim.Build,
            UnitActivity.Capture when visual.Images.ContainsKey(UnitAnim.Capture) => UnitAnim.Capture,
            UnitActivity.Capture => UnitAnim.Farm, // digging, without a picture of its own
            UnitActivity.Meat when visual.Images.ContainsKey(UnitAnim.Meat) => UnitAnim.Meat,
            UnitActivity.Meat => UnitAnim.Farm,
            UnitActivity.Cast when visual.Images.ContainsKey(UnitAnim.Spell) => UnitAnim.Spell,
            UnitActivity.Cast => UnitAnim.Attack,
            _ => UnitAnim.Idle,
        };
        if (anim == UnitAnim.Attack)
        {
            anim = SpriteRules.Swing(visual.Images.ContainsKey(UnitAnim.Attack2), NextSwing(e)); // PRES-005: half the blows swing the other way
        }
        SheetRef? s = _bank.Get(visual.Image(anim)) ?? _bank.Get(visual.Image(UnitAnim.Base));
        if (s is null)
        {
            var stand = new Rect2(px - 8, py - 16, 16, 24);
            _pickables.Add(new Pickable(index, stand, py, stand));
            return;
        }

        int frame;
        if (anim == UnitAnim.Idle && !visual.Images.ContainsKey(UnitAnim.Idle))
        {
            frame = 0;
        }
        else if (anim is UnitAnim.Attack or UnitAnim.Attack2 && e.SwingMs >= 0)
        {
            frame = SpriteRules.SwingFrame(s.StartRow, e.SwingMs >= s.Animation.CycleMs ? 0 : s.Animation.FrameAt(e.SwingMs), s.Sheet.FramesPerDirection);
        }
        else
        {
            frame = s.Animation.FrameAt(timeMs + phase);
        }

        int dir = SpriteRules.Direction(e.Facing, s.Sheet.Directions);
        if (anim is UnitAnim.Attack or UnitAnim.Attack2)
        {
            dir = SpriteRules.TurnedColumn(dir, s.StartCol, s.Sheet.Directions); // PRES-008: the knight's and ships' attack sheets
        }

        var cellOrigin = new Vector2I(px - s.Object.Position.X - (s.Object.Size.X / 2), py - s.Object.Position.Y - (s.Object.Size.Y / 2));
        AddSheet(_objects, s, cellOrigin, dir, frame, player, ((uint)(py + 4096) << 6) | 2, shadows: true);
        var selection = new Rect2(cellOrigin + s.Selection.Position, s.Selection.Size);
        if (e.CarriesBook)
        {
            DrawBookStars(selection, py, phase);
        }
        _pickables.Add(new Pickable(index, selection, py, CellBounds(s, cellOrigin, dir, frame) is { } figure ? selection.Merge(figure) : selection));
    }

    // A unit carrying a spell book has yellow stars floating above its head (1.09, ITEM-012): three of the original's
    // magic stars, each on its own beat, rising and falling a little.
    private void DrawBookStars(Rect2 selection, int footY, uint phase)
    {
        if (_bank.Get(EffectImages.BookStars) is not { } star)
        {
            return;
        }

        for (int k = 0; k < 3; k++)
        {
            long t = _timeMs + phase + (k * 330L);
            int bob = (int)(2.5 * Math.Sin(t / 260.0));
            var at = new Vector2I((int)(selection.Position.X + (selection.Size.X / 2)) - 9 + (k * 9), (int)selection.Position.Y - 10 + bob) - star.Anchor;
            Add(_glow, star, at, 0, star.Animation.FrameAt(t), -1, ((uint)(footY + 4096) << 6) | 3, shadowOnly: false);
        }
    }

    // The drawn (trimmed) pixels of one cell of a sheet, in world pixels, or null for an empty cell.
    private Rect2? CellBounds(SheetRef sheet, Vector2I origin, int direction, int frame)
    {
        SpriteSheet s = sheet.Sheet;
        int d = Math.Clamp(direction, 0, s.Directions - 1);
        int f = ((frame % s.FramesPerDirection) + s.FramesPerDirection) % s.FramesPerDirection;
        AtlasCell cell = _bank.Gpu!.Atlas.GetCell(sheet.SheetIndex, (d * s.FramesPerDirection) + f);
        return cell.IsEmpty ? null : new Rect2(origin.X + cell.OffsetX, origin.Y + cell.OffsetY, cell.Width, cell.Height);
    }

    // The number of the blow a unit is striking: one more each time its swing starts over.
    private uint NextSwing(in FrameEntity e)
    {
        uint key = e.Id.Value;
        (int last, uint blows) = _swings.TryGetValue(key, out (int, uint) seen) ? seen : (-1, key * 2654435761u);
        if (e.SwingMs >= 0 && last >= 0 && e.SwingMs < last)
        {
            blows++;
        }

        if (_swings.Count > 4096)
        {
            _swings.Clear(); // dead units; a fresh count only shifts the alternation
        }

        _swings[key] = (e.SwingMs, blows);
        return blows;
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

    private const int ShotLift = 14; // shots leave and hit at a body's height (pixels)

    // Projectiles in flight (GAME-07): the original's picture of each kind, raised on an arc from where it was first seen
    // to its target and turned along its flight; explosive ones leave an explosion where they vanish.
    private void DrawProjectiles(GameFrame frame, bool fresh, int alpha, Rect2I cull)
    {
        ReadOnlySpan<FrameProjectile> shots = frame.Projectiles;
        if (fresh)
        {
            _shotSeen.Clear();
            foreach (FrameProjectile s in shots)
            {
                _shotSeen.Add(s.Id);
            }

            _shotGone.Clear();
            foreach ((uint id, (int x, int y, byte kind)) in _shotCurrent)
            {
                if (_shotSeen.Contains(id))
                {
                    continue;
                }

                _shotGone.Add(id);
                if (kind < _rules.Projectiles.Count && EffectImages.Impact(_rules.Projectiles[kind].Key) is { } name && _bank.Get(name) is { } burst)
                {
                    _bursts.Add((burst, x, y, _timeMs));
                }
            }

            foreach (uint id in _shotGone)
            {
                _shotStart.Remove(id);
            }

            _shotPrevious.Clear();
            foreach ((uint id, (int x, int y, byte _)) in _shotCurrent)
            {
                _shotPrevious[id] = (x, y);
            }

            _shotCurrent.Clear();
            foreach (FrameProjectile s in shots)
            {
                _shotCurrent[s.Id] = (s.X, s.Y, s.Kind);
                _shotStart.TryAdd(s.Id, (s.X, s.Y));
            }
        }

        uint top = uint.MaxValue >> 2;
        foreach (FrameProjectile s in shots)
        {
            if (s.Kind >= _rules.Projectiles.Count)
            {
                continue;
            }

            string key = _rules.Projectiles[s.Kind].Key;
            if (EffectImages.Projectile(key) is not { } image || _bank.Get(image) is not { } sheet)
            {
                continue;
            }

            int a = Math.Clamp(alpha, 0, 1000);
            (int x, int y) = _shotPrevious.TryGetValue(s.Id, out (int X, int Y) from) ? (from.X + (int)((long)(s.X - from.X) * a / 1000), from.Y + (int)((long)(s.Y - from.Y) * a / 1000)) : (s.X, s.Y);
            (int sx, int sy) = _shotStart.TryGetValue(s.Id, out (int X, int Y) start) ? start : (x, y);
            double length = Math.Sqrt(((double)(s.TargetX - sx) * (s.TargetX - sx)) + ((double)(s.TargetY - sy) * (s.TargetY - sy))) / UnitsPerPixel;
            double left = Math.Sqrt(((double)(s.TargetX - x) * (s.TargetX - x)) + ((double)(s.TargetY - y) * (s.TargetY - y))) / UnitsPerPixel;
            double progress = length > 0 ? 1 - (left / length) : 1;
            int lift = ShotLift + EffectImages.ArcHeight(key, length, progress);
            int px = x / UnitsPerPixel, py = y / UnitsPerPixel;
            if (!cull.HasPoint(new Vector2I(px, py)))
            {
                continue;
            }

            int frameIndex;
            if (EffectImages.Turns(key))
            {
                // The screen direction along the arc: the ground direction, tilted by the arc's slope.
                double dx = (s.TargetX - x) / (double)UnitsPerPixel, dy = (s.TargetY - y) / (double)UnitsPerPixel;
                double d = Math.Max(1, Math.Sqrt((dx * dx) + (dy * dy)));
                double step = Math.Max(0.01, 4 / Math.Max(1, length));
                double rise = EffectImages.ArcHeight(key, length, Math.Min(1, progress + step)) - EffectImages.ArcHeight(key, length, progress);
                frameIndex = EffectImages.TurnFrame(dx / d * step * length, (dy / d * step * length) - rise, sheet.Sheet.FramesPerDirection);
            }
            else
            {
                frameIndex = sheet.Animation.FrameAt(_timeMs + (s.Id * 37));
            }

            if (sheet.Shadow is { } shadow)
            {
                Add(_objects, shadow, new Vector2I(px, py) - sheet.Anchor, 0, frameIndex, -1, top, shadowOnly: true);
            }

            Add(EffectImages.Glows(key) ? _glow : _objects, sheet, new Vector2I(px, py - lift) - sheet.Anchor, 0, frameIndex, -1, top | 1, shadowOnly: false);
        }

        // Explosions where explosive shots landed.
        for (int i = _bursts.Count - 1; i >= 0; i--)
        {
            (SheetRef burst, int bx, int by, long startMs) = _bursts[i];
            long age = _timeMs - startMs;
            if (age < 0 || age >= burst.Animation.CycleMs)
            {
                _bursts.RemoveAt(i);
                continue;
            }

            Add(_glow, burst, new Vector2I(bx / UnitsPerPixel, (by / UnitsPerPixel) - ShotLift) - burst.Anchor, 0, burst.Animation.FrameAt(age), -1, top | 2, shadowOnly: false);
        }
    }

    // Flames on a burning building (GAME-03): the original's fire strips at a few spots of the footprint, more on larger
    // buildings (⚠ the original's flame positions are not measured).
    private void DrawFire(in FrameEntity e, EntityTypeDefinition def, int left, int top, int foot)
    {
        int area = Math.Max(1, def.FootprintWidth * def.FootprintHeight);
        if (_bank.Get(area >= 9 ? EffectImages.FireBig : EffectImages.FireSmall) is not { } fire)
        {
            return;
        }

        int flames = Math.Clamp(area / 6, 1, 4);
        int w = def.FootprintWidth * TilePixels, h = def.FootprintHeight * TilePixels;
        uint seed = (uint)e.Id.Index * 2654435761u;
        for (int k = 0; k < flames; k++)
        {
            uint r = seed ^ ((uint)k * 40503u);
            int fx = left + (int)((w / 5) + (r % (uint)Math.Max(1, w * 3 / 5)));
            int fy = top + (int)((h / 8) + ((r >> 8) % (uint)Math.Max(1, h / 3))); // the upper part: roofs burn
            int f = fire.Animation.FrameAt(_timeMs + (r % 1000u));
            Add(_glow, fire, new Vector2I(fx, fy) - fire.Anchor, 0, f, -1, ((uint)(foot + 4096) << 6) | 3, shadowOnly: false);
        }
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
                    (int w, int h) = def.FootprintSize(e.Rotated && def.CanTurn);
                    var r = new Rect2(e.TileX * TilePixels, e.TileY * TilePixels, Math.Max(1, w) * TilePixels, Math.Max(1, h) * TilePixels);
                    Corners(_marksOver, r, mark);
                }
                else
                {
                    // Units: the square of the original's selection box (IMAGES.INI selection), as on structures.
                    Corners(_marksOver, p.Rect, mark);
                }

                if (options.GroupOf?.Invoke(e.Id) is > 0 and var group)
                {
                    // The control group's digit in the corner of the selection (CTRL-020).
                    var at = new Vector2(p.Rect.End.X - 4, p.Rect.End.Y + 2);
                    ThemeDB.FallbackFont.DrawString(_marksOver, at + Vector2.One, group.ToString(System.Globalization.CultureInfo.InvariantCulture), HorizontalAlignment.Left, -1, 11, Colors.Black);
                    ThemeDB.FallbackFont.DrawString(_marksOver, at, group.ToString(System.Globalization.CultureInfo.InvariantCulture), HorizontalAlignment.Left, -1, 11, mark);
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

    // Shift held: the way of each selected unit, a flag on each point it walks to (the original's wp_flag) and marks on the
    // objects it attacks (red) or guards (green) in turn (CTRL-025).
    private void DrawWaypoints(GameFrame frame)
    {
        ReadOnlySpan<FrameWaypoint> points = frame.Waypoints;
        if (points.Length == 0 || _selected.Count == 0)
        {
            return;
        }

        var where = new Dictionary<uint, int>();
        ReadOnlySpan<FrameEntity> entities = frame.Entities;
        for (int i = 0; i < entities.Length; i++)
        {
            where[entities[i].Id.Value] = i;
        }

        var boxes = new Dictionary<uint, Rect2>();
        foreach (Pickable p in _pickables)
        {
            boxes[entities[p.Index].Id.Value] = p.Rect;
        }

        SheetRef? flag = _bank.Get(EffectImages.Waypoint);
        var line = new Color(1f, 1f, 1f, 0.45f);
        uint unit = 0;
        Vector2 from = default;
        foreach (FrameWaypoint w in points)
        {
            if (!_selected.Contains(w.Unit) || !where.TryGetValue(w.Unit, out int u))
            {
                continue;
            }

            if (w.Unit != unit)
            {
                unit = w.Unit;
                from = new Vector2(entities[u].X / UnitsPerPixel, entities[u].Y / UnitsPerPixel);
            }

            var to = new Vector2(w.X / UnitsPerPixel, w.Y / UnitsPerPixel);
            if (w.Target != 0 && where.TryGetValue(w.Target, out int t))
            {
                to = new Vector2(entities[t].X / UnitsPerPixel, entities[t].Y / UnitsPerPixel);
            }

            RS.CanvasItemAddLine(_marksOver, from, to, line, 1f);
            if (w.Kind is FrameOrderKind.Attack or FrameOrderKind.Guard)
            {
                Rect2 box = boxes.TryGetValue(w.Target, out Rect2 b) ? b : new Rect2(to - new Vector2(10, 10), new Vector2(20, 20));
                Corners(_marksOver, box.Grow(3), w.Kind == FrameOrderKind.Attack ? new Color(1f, 0.3f, 0.25f) : new Color(0.25f, 1f, 0.25f));
            }
            else if (flag is not null)
            {
                var at = new Vector2I((int)to.X, (int)to.Y) - flag.Anchor;
                Add(_objects, flag, at, 0, flag.Animation.FrameAt(_timeMs), -1, ((uint)((int)to.Y + 4096) << 6) | 2, shadowOnly: false);
            }

            from = to;
        }
    }

    // The confirmation of the last order: the target's marks blink three times, or the red cross plays where the ground
    // was clicked (the original's anims\cursor; ⚠ the blink of a target is ours: the original's is not captured).
    private void DrawOrder(GameFrame frame, byte localPlayer)
    {
        if (_order is not { } order)
        {
            return;
        }

        long age = _timeMs - order.StartMs;
        if (order.Target != 0)
        {
            if (age >= ConfirmMs)
            {
                _order = null;
                return;
            }

            if ((age / (ConfirmMs / 6)) % 2 != 0)
            {
                return;
            }

            ReadOnlySpan<FrameEntity> entities = frame.Entities;
            foreach (Pickable p in _pickables)
            {
                ref readonly FrameEntity e = ref entities[p.Index];
                if (e.Id.Value != order.Target)
                {
                    continue;
                }

                EntityTypeDefinition def = _rules.EntityTypes[e.TypeId];
                (int fw, int fh) = def.FootprintSize(e.Rotated && def.CanTurn);
                Rect2 r = def.HasFootprint && e.TileX >= 0
                    ? new Rect2(e.TileX * TilePixels, e.TileY * TilePixels, Math.Max(1, fw) * TilePixels, Math.Max(1, fh) * TilePixels)
                    : p.Rect;
                Color mark = e.Owner == localPlayer ? new Color(0.25f, 1f, 0.25f) : e.Owner >= 8 ? new Color(1f, 1f, 0.4f) : new Color(1f, 0.3f, 0.25f);
                Corners(_marksOver, r.Grow(2), mark);
                return;
            }

            return;
        }

        if (_bank.Get(EffectImages.OrderCross) is not { } cross || age >= cross.Animation.CycleMs)
        {
            _order = null;
            return;
        }

        var at = new Vector2I(order.X / UnitsPerPixel, order.Y / UnitsPerPixel) - cross.Anchor;
        Add(_ground, cross, at, 0, cross.Animation.FrameAt(Math.Max(0, age)), -1, (uint)(order.Y / UnitsPerPixel + 4096) << 6, shadowOnly: false);
    }

    private void DrawGhost(Ghost ghost)
    {
        EntityTypeDefinition def = _rules.EntityTypes[ghost.TypeId];
        int left = ghost.TileX * TilePixels, top = ghost.TileY * TilePixels;
        (int width, int height) = def.FootprintSize(ghost.Rotated && def.CanTurn);
        var rect = new Rect2(left, top, width * TilePixels, height * TilePixels);
        SheetRef? image = _bank.Get(SpriteRules.GateImage(def, (ghost.Rotated ? 2 : 0) + (ghost.Mirrored ? 1 : 0), site: false))
            ?? _bank.Get(SpriteRules.Turned(_catalog.Type(ghost.TypeId).Image(UnitAnim.Base), ghost.Rotated));
        if (image is { } sheet)
        {
            var origin = new Vector2I(left - sheet.Object.Position.X, top - sheet.Object.Position.Y);
            // Green and without the kingdom's colours, as in the original (the owner, 2026-10-21; CTRL-023).
            AddSheet(_ghostLayer, sheet, origin, 0, 0, -1, uint.MaxValue >> 1, shadows: false);
        }

        // The green building shows where it goes (no fill over a legal site); only the cells that block it turn red.
        if (!ghost.Legal)
        {
            var red = new Color(1f, 0.15f, 0.1f, 0.45f);
            foreach ((int dx, int dy) in ghost.Blocked ?? [])
            {
                RS.CanvasItemAddRect(_marksOver, new Rect2(left + (dx * TilePixels), top + (dy * TilePixels), TilePixels, TilePixels), red);
            }
        }

        Corners(_marksOver, rect, ghost.Legal ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.25f, 0.2f));
    }

    private void UpdateFogAndLight(GameFrame frame, bool fresh, bool useFog, byte localPlayer, float darkness)
    {
        _fogMaterial.SetShaderParameter("fog_enabled", useFog);
        _fogMaterial.SetShaderParameter("darkness", darkness);

        // The layers change with the sim (fog every 4 ticks, lights as units move): upload on new frames only.
        bool lightsNeeded = darkness > 0f;
        if (!fresh && !(lightsNeeded && !_lightsValid))
        {
            return;
        }

        _lightsValid = lightsNeeded;
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
/// <param name="Blocked">Footprint cells (offsets from the top-left) that block it.</param>
/// <param name="Rotated">Turned by 90° (gates and bridges; CTRL-024, BLD-019).</param>
/// <param name="Mirrored">A gate facing the other way along its axis (CTRL-024).</param>
public readonly record struct Ghost(ushort TypeId, int TileX, int TileY, bool Legal, IReadOnlyList<(int Dx, int Dy)>? Blocked = null, bool Rotated = false, bool Mirrored = false);
