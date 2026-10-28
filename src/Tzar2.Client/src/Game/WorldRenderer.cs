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
using Tzar2.Game.Ui;
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
    private readonly SpriteRenderer _ghostLayer; // the building being placed: its green shadow (CTRL-023)
    private bool? _ghostLegal; // the colour the ghost layer is set to: green when legal, red when not
    private readonly byte[] _wallPieces; // the material of the finished wall or gate on each tile (PRES-010), refilled each frame
    private readonly List<int> _wallTiles = []; // the tiles set in _wallPieces, cleared with the next frame
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
    private HashSet<uint> _fished = [];
    private readonly Dictionary<uint, (int X, int Y)> _shotStart = [];
    private readonly Dictionary<uint, (int X, int Y)> _shotPrevious = [];
    private readonly Dictionary<uint, (int X, int Y, ShotLook? Look)> _shotCurrent = [];
    private readonly HashSet<uint> _shotSeen = [];
    private readonly List<uint> _shotGone = [];
    private readonly List<(SheetRef Sheet, int X, int Y, long StartMs)> _bursts = [];
    private readonly AnimationStarts _starts = new(); // when each unit's picture was set (PRES-008)
    private long _timeMs;
    private GameFrame? _current; // the frame being drawn
    private readonly int _thirdSwing; // the technology that gives a third swing (Kung Fu, PRES-005), or -1
    private static readonly StringName FogEnabledParam = "fog_enabled";
    private static readonly StringName DarknessParam = "darkness";
    private bool? _fogEnabled;
    private float _darkness = float.NaN;
    private (int X, int Y, uint Target, long StartMs, BlinkColour Colour)? _order;
    private bool _lightsValid;

    // The blows each unit has struck, counted when its swing starts again (PRES-005's alternate swings).
    private readonly Dictionary<uint, (int LastSwingMs, uint Blows)> _swings = [];
    private readonly TerrainGrid _grid;

    /// <summary>Where trees and their shadows are drawn and which pictures grow where (PRES-009; set by the game screen from the terrain set's data).</summary>
    public TreeLayout Trees { get; set; } = TreeLayout.Default;
    private ImageTexture? _lightPatternTexture;

    /// <summary>Creates the renderer for a match; the terrain is <paramref name="composed"/> already, or composed here.</summary>
    public WorldRenderer(Node host, Rid canvas, Ruleset rules, PresentationCatalog catalog, SpriteBank bank, TerrainSet set, TerrainGrid grid, Func<string, ImageAsset?> load, Image? lightPattern, TerrainLayer.ComposedTerrain? composed = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _rules = rules;
        _thirdSwing = rules.TechnologyId("te_kungfu"); // ReTzar 0x4ae090: the Asian peasant's image_attack3 under Kung Fu
        _catalog = catalog;
        _bank = bank;
        _root = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(_root, canvas);
        _terrain = composed is null ? new TerrainLayer(_root, 10, set, grid, load) : new TerrainLayer(_root, 10, composed);
        _grid = grid;
        _ground = new SpriteRenderer(host, _root, 20);
        _marksUnder = Item(25);
        _objects = new SpriteRenderer(host, _root, 30);
        _translucent = new SpriteRenderer(host, _root, 34);
        _translucent.SetModulate(new Color(1, 1, 1, 0.45f));
        _glow = new SpriteRenderer(host, _root, 36, additive: true);
        _glow.SetShadowsVisible(false);
        _ghostLayer = new SpriteRenderer(host, _root, 38);
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
        _wallPieces = new byte[MapWidth * MapHeight];
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

    /// <summary>
    /// Confirms an order the player gave: <paramref name="target"/> blinks in <paramref name="colour"/>
    /// (<see cref="OrderBlink"/>, CTRL-017); on the ground (no target) the original's red cross turns and shrinks at
    /// (<paramref name="x"/>, <paramref name="y"/>) (world units). Game time: it waits with the pause.
    /// </summary>
    public void ConfirmOrder(int x, int y, EntityId target, BlinkColour colour = BlinkColour.Green) => _order = (x, y, target.Value, _timeMs, colour);

    /// <summary>Plays IMAGES.INI picture <paramref name="name"/> once at world (<paramref name="x"/>, <paramref name="y"/>), if the package has it.</summary>
    public void Burst(string name, int x, int y)
    {
        if (_bank.Get(name) is { } burst)
        {
            _bursts.Add((burst, x, y, _timeMs));
        }
    }

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
        _current = frame;
        _starts.Sweep(timeMs);
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

        _fished = Tzar2.Game.Ui.WorkMarks.FishedPatches(frame, _rules, localPlayer); // HUD-025

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
        FillWallPieces(entities);
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
            bool damaged = e.MaxHealth > 0 && e.Health < e.MaxHealth;
            if (def.Traits.IsWall
                && _bank.Get(SpriteRules.WallImage(def, SpriteRules.WallMask(_wallPieces, MapWidth, e.TileX, e.TileY, SpriteRules.WallMaterial(def)), e.ConstructionPermille >= 0, damaged)) is { } wall)
            {
                // PRES-010: a wall joins its neighbours, shows its damage, and its site is the wall's own.
                sheet = wall;
                frameIndex = e.ConstructionPermille >= 0 ? SpriteRules.SiteFrame(e.ConstructionPermille, wall.Sheet.FramesPerDirection)
                    : damaged ? SpriteRules.WallDamageFrame(e.Health, e.MaxHealth, wall.Sheet.FramesPerDirection) : 0;
            }
            else if (_bank.Get(SpriteRules.GateImage(def, facing, site: e.ConstructionPermille >= 0)) is { } gate)
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
                    if (SpriteRules.IsMine(def))
                    {
                        // A mine keeps its own look until it is empty, under the fog too (PRES-011; the sixth play-test: it
                        // vanished while mined, and flickered between its looks under the fog).
                        frameIndex = SpriteRules.MineFrame(phase, e.ResourceLeft, sheet.Sheet.FramesPerDirection);
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
            UnitActivity.Murder when visual.Images.ContainsKey(UnitAnim.Murder) => UnitAnim.Murder,
            UnitActivity.Murder => UnitAnim.Attack, // the attack picture for a class without image_murder (ReTzar 0x4e197d)
            _ => UnitAnim.Idle,
        };
        if (anim == UnitAnim.Attack)
        {
            // PRES-005: half the blows swing the other way; a unit with a third swing under its technology (the Asian peasant's
            // Kung Fu) takes each of three a third of the time.
            bool third = visual.Images.ContainsKey(UnitAnim.Attack3) && _thirdSwing >= 0 && _current is { } now && e.Owner < now.Players.Length && now.Players[e.Owner].HasTechnology(_thirdSwing);
            anim = SpriteRules.Swing(visual.Images.ContainsKey(UnitAnim.Attack2), third, NextSwing(e));
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
        else if (anim is UnitAnim.Attack or UnitAnim.Attack2 or UnitAnim.Attack3 && e.SwingMs >= 0)
        {
            frame = SpriteRules.SwingFrame(s.StartRow, e.SwingMs >= s.Animation.CycleMs ? 0 : s.Animation.FrameAt(e.SwingMs), s.Sheet.FramesPerDirection);
        }
        else
        {
            // PRES-008: a picture runs from its start row from when it was set (ReTzar 0x5017f0), not on a shared clock.
            frame = SpriteRules.SwingFrame(s.StartRow, s.Animation.FrameAt(_starts.Elapsed(e.Id.Value, (int)anim, timeMs)), s.Sheet.FramesPerDirection);
        }

        int dir = SpriteRules.Direction(e.Facing, s.Sheet.Directions);
        if (anim is UnitAnim.Attack or UnitAnim.Attack2 or UnitAnim.Attack3)
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

    // Projectiles in flight (GAME-07, PRES-002): the original's picture of each kind and shooter, raised on its arc from
    // where it was first seen to its target and turned along its flight; shells, bolts and rockets leave a hole, a splash
    // or a blast where they vanish, spells their burst.
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
            foreach ((uint id, (int x, int y, ShotLook? look)) in _shotCurrent)
            {
                if (_shotSeen.Contains(id))
                {
                    continue;
                }

                _shotGone.Add(id);
                if (look is not null && look.ImpactOn(WaterAt(x, y), look.Shell && BuildingAt(frame, x, y)) is { } name && _bank.Get(name) is { } burst)
                {
                    _bursts.Add((burst, x, y, _timeMs));
                }
            }

            foreach (uint id in _shotGone)
            {
                _shotStart.Remove(id);
            }

            _shotPrevious.Clear();
            foreach ((uint id, (int x, int y, ShotLook? _)) in _shotCurrent)
            {
                _shotPrevious[id] = (x, y);
            }

            _shotCurrent.Clear();
            foreach (FrameProjectile s in shots)
            {
                ShotLook? look = LookOf(s);
                _shotCurrent[s.Id] = (s.X, s.Y, look);
                if (_shotStart.TryAdd(s.Id, (s.X, s.Y)) && look is { Muzzle: true } && _bank.Get("explosion_small") is { } flash)
                {
                    _bursts.Add((flash, s.X, s.Y - ((24 - ShotLift) * UnitsPerPixel), _timeMs)); // the muzzle flash, 24 px up
                }
            }
        }

        uint top = uint.MaxValue >> 2;
        foreach (FrameProjectile s in shots)
        {
            if (LookOf(s) is not { } look || _bank.Get(look.Body) is not { } sheet)
            {
                continue;
            }

            int a = Math.Clamp(alpha, 0, 1000);
            (int x, int y) = _shotPrevious.TryGetValue(s.Id, out (int X, int Y) from) ? (from.X + (int)((long)(s.X - from.X) * a / 1000), from.Y + (int)((long)(s.Y - from.Y) * a / 1000)) : (s.X, s.Y);
            (int sx, int sy) = _shotStart.TryGetValue(s.Id, out (int X, int Y) start) ? start : (x, y);

            // 0x4335e7: the arc over the Chebyshev distance flown; shots without one fly at a body's height.
            double span = Math.Max(Math.Abs(s.TargetX - sx), Math.Abs(s.TargetY - sy)) / (double)UnitsPerPixel;
            double flown = Math.Max(Math.Abs(x - sx), Math.Abs(y - sy)) / (double)UnitsPerPixel;
            bool arcs = look.Peak != 0 || look.Start != 0;
            int lift = arcs ? EffectImages.ArcHeight(look.Peak, look.Start, span, flown) : ShotLift;
            int px = x / UnitsPerPixel, py = y / UnitsPerPixel;
            if (!cull.HasPoint(new Vector2I(px, py)))
            {
                continue;
            }

            int frameIndex;
            if (look.Turns)
            {
                // The screen direction along the arc: the ground direction, tilted by the arc's slope.
                double dx = (s.TargetX - x) / (double)UnitsPerPixel, dy = (s.TargetY - y) / (double)UnitsPerPixel;
                double d = Math.Max(1, Math.Sqrt((dx * dx) + (dy * dy)));
                const double Step = 4;
                double rise = arcs ? EffectImages.ArcHeight(look.Peak, look.Start, span, flown + Step) - lift : 0;
                frameIndex = EffectImages.TurnFrame(dx / d * Step, (dy / d * Step) - rise, sheet.Sheet.FramesPerDirection);
            }
            else
            {
                frameIndex = sheet.Animation.FrameAt(_timeMs + (s.Id * 37));
            }

            if (look.Shadow is { } shadowName && _bank.Get(shadowName) is { } shadow)
            {
                Add(_objects, shadow, new Vector2I(px, py) - shadow.Anchor, 0, look.Turns ? frameIndex : 0, -1, top, shadowOnly: true);
            }
            else if (sheet.Shadow is { } own)
            {
                Add(_objects, own, new Vector2I(px, py) - sheet.Anchor, 0, frameIndex, -1, top, shadowOnly: true);
            }

            Add(look.Glows ? _glow : _objects, sheet, new Vector2I(px, py - lift) - sheet.Anchor, 0, frameIndex, -1, top | 1, shadowOnly: false);
        }

        // Explosions where explosive shots landed.
        for (int i = _bursts.Count - 1; i >= 0; i--)
        {
            (SheetRef burst, int bx, int by, long startMs) = _bursts[i];
            long age = _timeMs - startMs;
            // A hole stays on the ground for 10 s on its last frame (0x43392a); the rest play once.
            bool hole = burst.Name == EffectImages.Hole;
            if (age < 0 || age >= (hole ? EffectImages.HoleMs : burst.Animation.CycleMs))
            {
                _bursts.RemoveAt(i);
                continue;
            }

            int f = age >= burst.Animation.CycleMs ? burst.Sheet.FramesPerDirection - 1 : burst.Animation.FrameAt(age);
            bool ground = hole || burst.Name == EffectImages.Splash;
            Add(ground ? _ground : _glow, burst, new Vector2I(bx / UnitsPerPixel, (by / UnitsPerPixel) - (ground ? 0 : ShotLift)) - burst.Anchor, 0, f, -1, top | 2, shadowOnly: false);
        }
    }

    // How a shot looks: by its kind and its shooter's original class (PRES-002).
    private ShotLook? LookOf(in FrameProjectile s)
    {
        if (s.Kind >= _rules.Projectiles.Count)
        {
            return null;
        }

        string? shooter = s.ShooterType < _rules.EntityTypes.Count ? _rules.EntityTypes[s.ShooterType].Traits.LegacyClass : null;
        return EffectImages.Look(_rules.Projectiles[s.Kind].Key, shooter);
    }

    // Open water under a point (world units): all four corners of its tile are water.
    private bool WaterAt(int x, int y)
    {
        int tx = Math.Clamp(x / (TilePixels * UnitsPerPixel), 0, _grid.Width - 1), ty = Math.Clamp(y / (TilePixels * UnitsPerPixel), 0, _grid.Height - 1);
        (int tl, int tr, int br, int bl) = _grid.CornersOf(tx, ty);
        return tl == 0 && tr == 0 && br == 0 && bl == 0;
    }

    // A building's footprint covers the point (a shell there blasts).
    private bool BuildingAt(GameFrame frame, int x, int y)
    {
        int tx = x / (TilePixels * UnitsPerPixel), ty = y / (TilePixels * UnitsPerPixel);
        foreach (FrameEntity e in frame.Entities)
        {
            if (e.TileX < 0 || e.TypeId >= _rules.EntityTypes.Count)
            {
                continue;
            }

            EntityTypeDefinition def = _rules.EntityTypes[e.TypeId];
            (int w, int h) = def.FootprintSize(e.Rotated && def.CanTurn);
            if (def.Traits.Category == EntityCategory.Building && tx >= e.TileX && tx < e.TileX + w && ty >= e.TileY && ty < e.TileY + h)
            {
                return true;
            }
        }

        return false;
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
            if (!selected && _fished.Contains(e.Id.Value))
            {
                // Patch 1.05: a blue selection around the fish patch the player's boats are fishing (HUD-025).
                Corners(_marksOver, p.Rect, new Color(0.3f, 0.55f, 1f));
            }

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

    // The confirmation of the last order: the target blinks (OrderBlink, CTRL-017), or the red cross plays where the
    // ground was clicked (the original's anims\cursor).
    private void DrawOrder(GameFrame frame, byte localPlayer)
    {
        if (_order is not { } order)
        {
            return;
        }

        long age = _timeMs - order.StartMs;
        if (order.Target != 0)
        {
            if (age >= OrderBlink.DurationMs)
            {
                _order = null;
                return;
            }

            if (!OrderBlink.On(age))
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
                // A one-pixel frame one pixel inside the selection's rectangle (0x4f2528).
                Color blink = order.Colour switch
                {
                    BlinkColour.Red => new Color(1f, 0f, 0f),
                    BlinkColour.Blue => new Color(0f, 0f, 1f),
                    _ => new Color(0f, 1f, 0f),
                };
                Vector2 corner = r.Position + Vector2.One, size = (r.Size - (Vector2.One * 2)).Max(Vector2.One);
                RS.CanvasItemAddRect(_marksOver, new Rect2(corner, new Vector2(size.X, 1)), blink);
                RS.CanvasItemAddRect(_marksOver, new Rect2(corner.X, corner.Y + size.Y - 1, size.X, 1), blink);
                RS.CanvasItemAddRect(_marksOver, new Rect2(corner, new Vector2(1, size.Y)), blink);
                RS.CanvasItemAddRect(_marksOver, new Rect2(corner.X + size.X - 1, corner.Y, 1, size.Y), blink);
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

    // The finished walls and gates of the frame by tile and material, for the walls to join (PRES-010). Walls join those
    // of any kingdom, as the original's lookup.
    private void FillWallPieces(ReadOnlySpan<FrameEntity> entities)
    {
        foreach (int tile in _wallTiles)
        {
            _wallPieces[tile] = 0;
        }

        _wallTiles.Clear();
        for (int i = 0; i < entities.Length; i++)
        {
            ref readonly FrameEntity e = ref entities[i];
            EntityTypeDefinition def = _rules.EntityTypes[e.TypeId];
            if (e.ConstructionPermille < 0 && SpriteRules.WallMaterial(def) is > 0 and var material)
            {
                (int w, int h) = def.FootprintSize(e.Rotated && def.CanTurn);
                for (int y = e.TileY; y < e.TileY + h; y++)
                {
                    for (int x = e.TileX; x < e.TileX + w; x++)
                    {
                        MarkWallPiece(x, y, material);
                    }
                }
            }
        }
    }

    private void MarkWallPiece(int x, int y, byte material)
    {
        if (x >= 0 && y >= 0 && x < MapWidth && y < MapHeight)
        {
            _wallPieces[(y * MapWidth) + x] = material;
            _wallTiles.Add((y * MapWidth) + x);
        }
    }

    private void DrawGhost(Ghost ghost)
    {
        EntityTypeDefinition def = _rules.EntityTypes[ghost.TypeId];
        int left = ghost.TileX * TilePixels, top = ghost.TileY * TilePixels;
        (int width, int height) = def.FootprintSize(ghost.Rotated && def.CanTurn);
        var rect = new Rect2(left, top, width * TilePixels, height * TilePixels);
        bool green = ghost.Legal || ghost.Row is not null;
        if (_ghostLegal != green)
        {
            // The shadow of the building, green, or red where it cannot stand (CTRL-023; the owner's sixth play-test).
            _ghostLegal = green;
            _ghostLayer.SetSilhouette(green ? new Color(0.3f, 1f, 0.3f, 0.55f) : new Color(1f, 0.25f, 0.2f, 0.55f));
        }

        if (ghost.Row is { } row)
        {
            DrawWallRow(def, row);
            return;
        }

        byte material = SpriteRules.WallMaterial(def);
        SheetRef? image = _bank.Get(SpriteRules.WallImage(def, SpriteRules.WallMask(_wallPieces, MapWidth, ghost.TileX, ghost.TileY, material), site: false, damaged: false))
            ?? _bank.Get(SpriteRules.GateImage(def, (ghost.Rotated ? 2 : 0) + (ghost.Mirrored ? 1 : 0), site: false))
            ?? _bank.Get(SpriteRules.Turned(_catalog.Type(ghost.TypeId).Image(UnitAnim.Base), ghost.Rotated));
        if (image is { } sheet)
        {
            var origin = new Vector2I(left - sheet.Object.Position.X, top - sheet.Object.Position.Y);
            // Its shadow, without the kingdom's colours (the owner, 2026-10-21 and the sixth play-test; CTRL-023).
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

    // A wall being dragged (BLD-011): the shadow of each segment where one can go, joined as the row will stand, and a red
    // tile where none can.
    private void DrawWallRow(EntityTypeDefinition def, IReadOnlyList<GhostCell> row)
    {
        byte material = SpriteRules.WallMaterial(def);
        foreach (GhostCell c in row)
        {
            if (c.Legal)
            {
                MarkWallPiece(c.TileX, c.TileY, material);
            }
        }

        foreach (GhostCell c in row)
        {
            int left = c.TileX * TilePixels, top = c.TileY * TilePixels;
            if (!c.Legal)
            {
                RS.CanvasItemAddRect(_marksOver, new Rect2(left, top, TilePixels, TilePixels), new Color(1f, 0.15f, 0.1f, 0.45f));
                continue;
            }

            int mask = SpriteRules.WallMask(_wallPieces, MapWidth, c.TileX, c.TileY, material);
            if ((_bank.Get(SpriteRules.WallImage(def, mask, site: false, damaged: false)) ?? _bank.Get(_catalog.Type(def.Id).Image(UnitAnim.Base))) is { } sheet)
            {
                var origin = new Vector2I(left - sheet.Object.Position.X, top - sheet.Object.Position.Y);
                AddSheet(_ghostLayer, sheet, origin, 0, 0, -1, (uint)(top + TilePixels + 4096) << 6, shadows: false);
            }
        }
    }

    private void UpdateFogAndLight(GameFrame frame, bool fresh, bool useFog, byte localPlayer, float darkness)
    {
        // Every drawn frame: cached names, and only what changed (the code review: a StringName per call otherwise).
        if (useFog != _fogEnabled)
        {
            _fogEnabled = useFog;
            _fogMaterial.SetShaderParameter(FogEnabledParam, useFog);
        }

        if (darkness != _darkness)
        {
            _darkness = darkness;
            _fogMaterial.SetShaderParameter(DarknessParam, darkness);
        }

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
/// <param name="Row">The tiles of a wall being dragged (BLD-011), each with whether a segment can go there; null otherwise.</param>
public readonly record struct Ghost(ushort TypeId, int TileX, int TileY, bool Legal, IReadOnlyList<(int Dx, int Dy)>? Blocked = null, bool Rotated = false, bool Mirrored = false, IReadOnlyList<GhostCell>? Row = null);

/// <summary>A tile of a wall being dragged.</summary>
/// <param name="TileX">Tile X.</param>
/// <param name="TileY">Tile Y.</param>
/// <param name="Legal">Whether a segment can go there now.</param>
public readonly record struct GhostCell(int TileX, int TileY, bool Legal);
