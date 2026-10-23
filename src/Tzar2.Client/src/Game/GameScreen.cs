using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Godot;
using Tzar2.Client.Audio;
using Tzar2.Client.Logic;
using Tzar2.Client.Viewer;
using Tzar2.Content.Assets;
using Tzar2.Content.Compiled;
using Tzar2.Content.Terrain;
using Tzar2.Core.Ids;
using Tzar2.Game.Input;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;

namespace Tzar2.Client.Game;

/// <summary>Statistics of a finished (or running) match for the results screen.</summary>
public sealed class MatchStats
{
    /// <summary>Units trained by the local player.</summary>
    public int Trained { get; set; }

    /// <summary>Buildings completed by the local player.</summary>
    public int Built { get; set; }

    /// <summary>Resources delivered by the local player.</summary>
    public int Gathered { get; set; }

    /// <summary>Enemies killed by the local player.</summary>
    public int Kills { get; set; }

    /// <summary>Own units and buildings lost.</summary>
    public int Losses { get; set; }

    /// <summary>Game time in seconds.</summary>
    public int Seconds { get; set; }
}

/// <summary>
/// The in-game screen of a skirmish: runs the <see cref="GameSession"/> (sim thread), draws its frames with the
/// <see cref="WorldRenderer"/>, turns mouse and keys into commands (selection rules, contextual orders, command card,
/// placement ghost, hotkeys of <see cref="HotkeyTable"/>), drives the <see cref="Hud"/>, the sound mixer and the music.
/// </summary>
public partial class GameScreen : Node
{
    /// <summary>Edge-scroll speed in pixels per second (⚠ the original's scroll speeds are not measured).</summary>
    public const int ScrollSpeed = 720;

    /// <summary>Pointer travel (pixels) after which a held right or middle button drags the map instead of clicking.</summary>
    public const float GrabThreshold = 6f;

    private readonly ContentStore _store;
    private readonly UiAssets _ui;
    private readonly GameSession _session;
    private Tzar2.Game.Diagnostics.MatchTracer? _tracer;
    private double _slowFrameLogged = double.MinValue;
    private readonly Ruleset _rules;
    private readonly PresentationCatalog _catalog;
    private readonly CommandCard _card;
    private readonly HotkeyTable _hotkeys = new();
    private readonly SelectionModel _selection;
    private readonly FrameInterpolator _interpolator = new();
    private readonly WorldOptions _options = new();
    private readonly List<SimEvent> _events = [];
    private readonly Dictionary<uint, int> _indexById = [];
    private readonly Dictionary<int, (int X, int Y, ushort Type, byte Owner)> _lastByIndex = [];
    private readonly List<(string Text, double Until)> _messages = [];
    private readonly List<(int X, int Y, EntityId Id)> _eventHistory = [];
    private readonly List<(int X, int Y)> _locations = [];
    private readonly string _terrainSetId;
    private SpriteBank? _bank;
    private WorldRenderer? _world;
    private Hud? _hud;
    private SoundMixer? _mixer;
    private MidiPlayer? _music;
    private string? _soundFont;
    private GameFrame? _frame;
    private double _now;
    private double _frameArrived;
    private Vector2I _pan;
    private Vector2? _dragStart;
    private Vector2 _mouse;
    private OrderMode _mode;
    private int _modeArgument; // the spell id of OrderMode.Spell
    private EntityId _picked; // the unit picked in the list of those inside the selected building (ORD-015)
    private readonly List<EntityId> _insideShown = []; // the units of that list, in the info area's order
    private CardDialog? _dialog;
    private EntityId _dialogFor;
    private (int Goods, int Load)? _route; // a trade route waiting for the far market's click
    private ushort _placing = ushort.MaxValue;
    private int _facing; // the placement ghost's facing: 0, 2 turned by 90° (gates and bridges); 1, 3 a gate facing the other way (CTRL-024, BLD-019)
    private int _eventCursor = -1;
    private int _locationCursor = -1;
    private int _buildingCursor;
    private double _lastAlert = -100;
    private double _minimapDue;
    private bool _barsToggled;
    private bool _barsHeld;
    private bool _outcomeShown;
    private double _outcomeAt = -1;
    private ImageTexture? _minimapTexture;
    private byte[] _minimapBytes = [];
    private Image? _minimapImage;

    // HUD-030: the minimap's tile colours and the trees' colour from TERRAIN.INI [minidatargb] (null: the tiles' pictures).
    private Color[]? _minimapTiles;
    private Color? _minimapTree;
    private int _workSoundBudget;
    private Rid _boxItem;
    private bool _faultShown;
    private System.Threading.Tasks.Task? _prepare;
    private TerrainSet? _set;
    private TerrainGrid? _grid;
    private TerrainLayer.ComposedTerrain? _composed;
    private CanvasLayer? _hudLayer;
    private CanvasLayer? _weatherLayer;
    private double _animMs;
    private Vector2? _grabStart;
    private Vector2I _grabPan;
    private bool _grabbing;
    private MouseButton _grabButton;

    /// <summary>Creates the screen for a session that has not started yet.</summary>
    public GameScreen(ContentStore store, UiAssets ui, GameSession session, string terrainSet, string? soundFont, GameSettings? settings = null)
    {
        Settings = settings ?? new GameSettings();
        _store = store;
        _ui = ui;
        _session = session;
        _rules = session.Ruleset;
        _terrainSetId = terrainSet;
        _soundFont = soundFont;
        _selection = new SelectionModel(session.LocalPlayer);
        _catalog = new PresentationCatalog(_rules, path => store.Vfs.Exists(path) ? store.Read(path) : null, store.Vfs.Enumerate(PresentationCatalog.ObjectsFolder));
        _card = new CommandCard(_rules, _catalog, session.LocalPlayer);
        Stats = new MatchStats();
    }

    /// <summary>Raised when the match ends (results screen) or the player quits (null).</summary>
    public event Action<MatchOutcome, MatchStats>? Finished;

    /// <summary>Raised when the player chooses QUIT in the game menu.</summary>
    public event Action? QuitRequested;

    /// <summary>Raised when the player chooses RESTART.</summary>
    public event Action? RestartRequested;

    /// <summary>The session.</summary>
    public GameSession Session => _session;

    /// <summary>The presentation catalog.</summary>
    public PresentationCatalog Catalog => _catalog;

    /// <summary>The selection.</summary>
    public SelectionModel Selection => _selection;

    /// <summary>The world renderer (after <see cref="_Ready"/>).</summary>
    public WorldRenderer? World => _world;

    /// <summary>The HUD.</summary>
    public Hud? Hud => _hud;

    /// <summary>Statistics so far.</summary>
    public MatchStats Stats { get; }

    /// <summary>The newest frame.</summary>
    public GameFrame? Frame => _frame;

    /// <summary>The sound mixer.</summary>
    public SoundMixer? Mixer => _mixer;

    /// <summary>World options (fog, day/night...).</summary>
    public WorldOptions Options => _options;

    /// <summary>Camera position (world pixel at the view's top-left).</summary>
    public Vector2I Pan => _pan;

    /// <summary>The pending command-card order.</summary>
    public OrderMode Mode => _mode;

    /// <summary>The type being placed, or <see cref="ushort.MaxValue"/>.</summary>
    public ushort Placing => _placing;

    /// <summary>Scroll when the mouse touches the screen edge (off for the autoplay driver).</summary>
    public bool EdgeScroll { get; set; } = true;

    /// <summary>Only the test driver gives input (the real mouse and keyboard are ignored).</summary>
    public bool Scripted { get; set; }

    /// <summary>Forces rain on (test driver); otherwise rain comes and goes when weather effects are on.</summary>
    public bool ForceRain { get; set; }

    /// <summary>Whether it rains now.</summary>
    public bool Rain { get; private set; }

    /// <summary>Keeps the session's game speed instead of the settings' (smoke test, test driver).</summary>
    public bool KeepSpeed { get; set; }

    /// <summary>The player's preferences.</summary>
    public GameSettings Settings { get; }

    /// <summary>
    /// Starts the match's CPU work on a worker thread (code review 2026-10, item 16): decoding and packing the sprites,
    /// laying out and composing the terrain. A huge map took 2-5 s of it and a real-world map 12 s, all on the main thread
    /// behind a frozen loading screen. The caller shows the screen once the task is done; <see cref="_Ready"/> only
    /// uploads. Idempotent.
    /// </summary>
    public System.Threading.Tasks.Task Prepare() => _prepare ??= System.Threading.Tasks.Task.Run(PrepareCpu);

    /// <summary>Whether <see cref="Prepare"/> has finished (successfully or not).</summary>
    public bool IsPrepared => _prepare is { IsCompleted: true };

    // The CPU part of the load (no Godot objects): sprite sheets decoded and packed, the terrain grid and its composition.
    private void PrepareCpu()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string setId = _session.Map.TerrainSet ?? _terrainSetId;
        TerrainSet set = _store.TerrainSets.FirstOrDefault(s => s.Id == setId) ?? _store.TerrainSets.FirstOrDefault()
            ?? throw new InvalidOperationException("The package has no terrain sets.");

        // Sprites of the civilizations in the match (REQ-GFX-05): the kingdoms' and those of every object on the map (the
        // original maps give a kingdom buildings and units of other civilizations).
        _bank = new SpriteBank(_store, [.. Enumerable.Range(0, 8).Select(ColourOf)]);
        var civs = new HashSet<string>(_session.Setup.Players.Select(p => p.Civilization)) { _rules.CommonCivilization, string.Empty };
        foreach (MapObject o in _session.Map.Objects)
        {
            if (_rules.EntityTypeId(o.TypeKey) is >= 0 and var placed)
            {
                civs.Add(_catalog.Type((ushort)placed).Civilization);
            }
        }

        foreach (EntityTypeDefinition type in _rules.EntityTypes)
        {
            TypeVisual v = _catalog.Type(type.Id);
            if (!civs.Contains(v.Civilization))
            {
                continue;
            }

            foreach (string image in v.Images.Values)
            {
                _bank.Add(image);
                if (Placement.CanTurn(type) && SpriteRules.Turned(image, rotated: true) is { } turned && turned != image)
                {
                    _bank.Add(turned); // a turned bridge (BLD-019)
                }
            }

            for (int facing = 0; facing < 4 && type.Traits.Gate is not null; facing++)
            {
                _bank.Add(SpriteRules.GateImage(type, facing, site: false)); // the drawbridges (CTRL-024)
                _bank.Add(SpriteRules.GateImage(type, facing, site: true));
            }

            if (type.HasFootprint && type.Traits.Category == EntityCategory.Building)
            {
                _bank.Add(SpriteRules.BuildSiteName(type));
            }
        }

        foreach (string effect in EffectImages.All)
        {
            _bank.Add(effect);
        }

        _bank.AddTrees(set);
        long decoded = watch.ElapsedMilliseconds;
        _bank.Pack();
        long packed = watch.ElapsedMilliseconds;
        _grid = TerrainPresentation.Build(_session.Map, _rules, set);
        long laid = watch.ElapsedMilliseconds;
        _composed = TerrainLayer.Compose(set, _grid, _store.LoadImage);
        _set = set;
        ClientLog.Info($"match load (worker thread): sprites decoded {decoded} ms ({_bank.SheetCount} sheets), packed {packed - decoded} ms, terrain laid out {laid - packed} ms, composed {watch.ElapsedMilliseconds - laid} ms ({_composed.FrameTotal} chunk frames, {_composed.Atlas.Count} distinct tiles on {_composed.Atlas.Pages.Count} atlas pages)");
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        HudLayout layout = LoadLayout();
        Prepare().GetAwaiter().GetResult(); // shown before it finished (tests, the editor's playtest): wait here
        long waited = watch.ElapsedMilliseconds;
        TerrainSet set = _set!;
        _bank!.Upload();
        long uploaded = watch.ElapsedMilliseconds;
        Image? light = set.Light is { } l ? _ui.Image(l) : null;
        _world = new WorldRenderer(this, GetViewport().FindWorld2D().Canvas, _rules, _catalog, _bank, set, _grid!, _store.LoadImage, light, _composed);
        _composed = null;
        MinimapFromData(set, _grid!);
        ClientLog.Info($"match load (main thread): waited {waited} ms for the worker, sprites uploaded {uploaded - waited} ms, world renderer {watch.ElapsedMilliseconds - uploaded} ms (terrain uploaded {_world.Terrain.UploadMs} ms)");
        _boxItem = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(_boxItem, GetViewport().FindWorld2D().Canvas);
        RenderingServer.CanvasItemSetZIndex(_boxItem, 100);

        _weatherLayer = new CanvasLayer { Layer = 1 };
        AddChild(_weatherLayer);
        _hudLayer = new CanvasLayer { Layer = 2 };
        AddChild(_hudLayer);
        BuildHud(layout);
        _selection.Changed += () =>
        {
            _card.Reset();
            _picked = EntityId.None;
            _mode = OrderMode.None;
            RefreshCard();
        };

        _mixer = new SoundMixer();
        AddChild(_mixer);
        _mixer.Bind(_store, _catalog);
        _music = new MidiPlayer();
        AddChild(_music);
        StartMusic();
        ApplySettings();

        _minimapImage = Image.CreateEmpty(_world.MapWidth, _world.MapHeight, false, Image.Format.Rgba8);
        _minimapTexture = ImageTexture.CreateFromImage(_minimapImage);

        // Start on the local castle.
        GameFrame? first = _session.AcquireFrame(out _);
        if (first is not null)
        {
            Accept(first);
            foreach (FrameEntity e in first.Entities)
            {
                if (e.Owner == _session.LocalPlayer && _rules.EntityTypes[e.TypeId].Traits.IsSeat)
                {
                    CenterOn(e.X, e.Y);
                    break;
                }
            }
        }

        Message(_catalog.Text("sp_setupmap", "vcond_0", "Standard") + ": destroy every enemy unit and building.");
        GD.Print($"Skirmish: {_session.Map.Name}, {_session.Setup.Players.Count} kingdoms, terrain {set.Id}, {_bank.SheetCount} sheets, {_world.Terrain.Issues} terrain tiles without a rule (UNK-10)");
        // Diagnostics (ADR 0176): the match's commands, events and summaries go to the run's log; a crash saves its replay.
        if (ClientLog.Sink is { } sink)
        {
            _tracer = Tzar2.Game.Diagnostics.MatchTracer.Attach(_session, sink, _session.IsMultiplayer ? "multiplayer match" : "match");
        }

        ClientLog.TrackSession(_session);
        if (!_session.IsRunning)
        {
            _session.Start();
        }

        RefreshCard();
    }

    /// <summary>
    /// The logical screen changed (the window was resized or the display preset changed, ADR 0177): the HUD and the
    /// weather overlay are built again for the new size, and the camera stays on the map.
    /// </summary>
    public void Relayout()
    {
        if (_hud is null || _hudLayer is null)
        {
            return;
        }

        _hudLayer.RemoveChild(_hud);
        _hud.QueueFree();
        foreach (Node old in _weatherLayer!.GetChildren())
        {
            _weatherLayer.RemoveChild(old);
            old.QueueFree();
        }

        BuildHud(LoadLayout());
        ClampPan();
        RefreshCard();
    }

    // LAYOUT1024H for the logical screen: the bitmap's own size in the Authentic preset, the window's in Wide.
    private HudLayout LoadLayout()
    {
        IniTable ini = _store.Vfs.Exists("tables/data/layout.ini.json") ? IniTable.Parse(_store.Read("tables/data/layout.ini.json")) : IniTable.Empty;
        Vector2 visible = GetViewport().GetVisibleRect().Size;
        return HudLayout.Load(ini, "LAYOUT1024H", new Vector2I(Math.Max(1, (int)visible.X), Math.Max(1, (int)visible.Y)));
    }

    private void BuildHud(HudLayout layout)
    {
        _weatherLayer!.AddChild(new WeatherLayer(this, _ui, layout.View));
        _hud = new Hud(_ui, layout, _catalog);
        _hudLayer!.AddChild(_hud);
        _hud.CardPressed += OnCard;
        _hud.StripPressed += i =>
        {
            // A unit of the list of those inside: picked for Emerge, or let go with a second click (manual: Emerge).
            if (i < _insideShown.Count)
            {
                _picked = _picked == _insideShown[i] ? EntityId.None : _insideShown[i];
                RefreshCard();
            }
        };
        _hud.MinimapPressed += (share, right, mods) => OnMinimap(share, right, mods);
        _hud.MenuPressed += () => ShowMenu();
        _hud.DiplomacyPressed += ShowDiplomacy;
        _hud.PagePressed += delta =>
        {
            _card.TurnPage(delta);
            RefreshCard();
        };
        _hud.EyePressed += ToggleInventory;
        _hud.ChatPressed += () =>
        {
            if (!_session.IsMultiplayer)
            {
                Message(_catalog.Text("game_scr", "chat", "CHAT") + ": needs multiplayer");
            }
        };
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        _session.Stop(); // the sim thread first: the tracer's last summary reads the world
        _tracer?.Dispose();
        ClientLog.TrackSession(null);
        _session.Dispose();
        _world?.Dispose();
        _bank?.Dispose();
        if (_boxItem.IsValid)
        {
            RenderingServer.FreeRid(_boxItem);
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (delta > 0.25 && _now - _slowFrameLogged > 5)
        {
            _slowFrameLogged = _now;
            ClientLog.Warn(string.Create(CultureInfo.InvariantCulture, $"slow frame: {delta * 1000:0} ms (at most one report per 5 s)"));
        }

        _now += delta;
        GameFrame? frame = _session.AcquireFrame(out bool fresh);
        if (frame is null || _world is null || _hud is null)
        {
            return;
        }

        if (fresh)
        {
            Accept(frame);
        }

        if (_session.Fault is { } fault && !_faultShown)
        {
            _faultShown = true;
            Message("The simulation stopped: " + fault.Message);
            GD.PrintErr("Simulation fault: " + fault);
        }

        _session.DrainEvents(_events);
        HandleEvents(frame);
        _events.Clear();
        UpdateScenario(frame);
        Scroll(delta);
        _selection.Prune(Selectable);

        HudLayout layout = _hud.Layout;
        double interval = frame.TickMs * 100.0 / Math.Max(1, frame.SpeedPercent);
        // The session publishes frames only after ticks, so a pause shows in the session before any frame says so.
        bool paused = frame.Paused || _session.Paused;
        int alpha = paused ? 1000 : FrameInterpolator.Alpha((_now - _frameArrived) * 1000.0, interval * _interpolator.TickSpan);
        // The world's animations run on game time: they stop with the pause and follow the game speed.
        if (!paused)
        {
            _animMs += delta * 1000.0 * frame.SpeedPercent / 100.0;
        }

        long timeMs = (long)_animMs;
        float phase = DayPhase(frame);
        _world.Render(frame, fresh, _interpolator, alpha, _pan, layout.View.Position, layout.View.Size, timeMs, _selection.Selected, _session.LocalPlayer, Barred(), Darkness(phase), CurrentGhost(frame));
        DrawBox();
        UpdateHud(frame, phase);
        UpdateCursor();
        // The pause holds the sounds that are playing and starts no work sounds (play-test of 2026-10-20).
        _mixer?.SetPaused(paused);
        _workSoundBudget = Settings.NatureSounds && !paused ? 2 : 0;

        // The sim's weather (GAME-16, ADR 0105); the setting only hides the overlay.
        Rain = ForceRain || (Settings.Weather && frame.Weather == Tzar2.Content.Compiled.WeatherKind.Rain);

        if (frame.Outcome != MatchOutcome.None && !_outcomeShown)
        {
            if (_outcomeAt < 0)
            {
                _outcomeAt = _now + 3;
                Message(frame.Outcome == MatchOutcome.Victory ? "Victory!" : "Defeat!");
            }
            else if (_now >= _outcomeAt)
            {
                _outcomeShown = true;
                _session.Paused = true;
                Stats.Seconds = frame.Tick.Value * frame.TickMs / 1000;
                Finished?.Invoke(frame.Outcome, Stats);
            }
        }
    }

    /// <inheritdoc/>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (_hud is null || _frame is null || Scripted)
        {
            return;
        }

        switch (@event)
        {
            case InputEventMouseMotion motion:
                _mouse = motion.Position;
                GrabScroll(motion);
                break;
            case InputEventMouseButton button:
                _mouse = button.Position;
                MouseButtonEvent(button);
                break;
            case InputEventKey { Pressed: true, Echo: false } turn when TurnGhost(turn.Keycode):
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                KeyDown(key);
                break;
            case InputEventKey { Pressed: false } key when key.Keycode == Key.Quoteleft:
                _barsHeld = false;
                break;
        }
    }

    /// <summary>Centres the view on a world point (world units).</summary>
    public void CenterOn(int x, int y)
    {
        if (_hud is null || _world is null)
        {
            return;
        }

        Vector2I view = _hud.Layout.View.Size;
        _pan = new Vector2I((x / WorldRenderer.UnitsPerPixel) - (view.X / 2), (y / WorldRenderer.UnitsPerPixel) - (view.Y / 2));
        ClampPan();
    }

    /// <summary>The screen position of a world point (world units).</summary>
    public Vector2 ToScreen(int x, int y)
        => new Vector2(x / WorldRenderer.UnitsPerPixel, y / WorldRenderer.UnitsPerPixel) - _pan + (_hud?.Layout.View.Position ?? Vector2I.Zero);

    /// <summary>The world point (world units) under a screen position.</summary>
    public (int X, int Y) ToWorld(Vector2 screen)
    {
        Vector2 p = screen - (_hud?.Layout.View.Position ?? Vector2I.Zero) + _pan;
        return ((int)(p.X * WorldRenderer.UnitsPerPixel), (int)(p.Y * WorldRenderer.UnitsPerPixel));
    }

    /// <summary>Simulated input for the test driver: a mouse button at a screen point.</summary>
    public void Click(Vector2 screen, MouseButton button, KeyMods mods = KeyMods.None, bool doubleClick = false)
    {
        _mouse = screen;
        var press = new InputEventMouseButton { Position = screen, ButtonIndex = button, Pressed = true, DoubleClick = doubleClick, ShiftPressed = (mods & KeyMods.Shift) != 0, CtrlPressed = (mods & KeyMods.Ctrl) != 0, AltPressed = (mods & KeyMods.Alt) != 0 };
        MouseButtonEvent(press);
        var release = (InputEventMouseButton)press.Duplicate();
        release.Pressed = false;
        release.DoubleClick = false;
        MouseButtonEvent(release);
    }

    /// <summary>Simulated input for the test driver: a minimap click at a map share (0-1).</summary>
    public void MinimapClick(Vector2 share, bool right, KeyMods mods = KeyMods.None) => OnMinimap(share, right, mods);

    /// <summary>Simulated input for the test driver: the mouse moves to a screen point.</summary>
    public void Hover(Vector2 screen) => _mouse = screen;

    /// <summary>Simulated input for the test driver: a drag box.</summary>
    public void Drag(Vector2 from, Vector2 to)
    {
        MouseButtonEvent(new InputEventMouseButton { Position = from, ButtonIndex = MouseButton.Left, Pressed = true });
        _mouse = to;
        MouseButtonEvent(new InputEventMouseButton { Position = to, ButtonIndex = MouseButton.Left, Pressed = false });
    }

    /// <summary>Simulated input for the test driver: a key press.</summary>
    public void Press(Key key, KeyMods mods = KeyMods.None)
        => KeyDown(new InputEventKey { Keycode = key, Pressed = true, ShiftPressed = (mods & KeyMods.Shift) != 0, CtrlPressed = (mods & KeyMods.Ctrl) != 0, AltPressed = (mods & KeyMods.Alt) != 0 });

    /// <summary>Simulated input for the test driver: presses command-card slot <paramref name="slot"/>.</summary>
    public void PressCard(int slot, bool right = false) => OnCard(slot, right);

    /// <summary>The current command card.</summary>
    public IReadOnlyList<CardSlot?> Card { get; private set; } = [];

    /// <summary>Information about an entity of the newest frame, or null.</summary>
    public EntityInfo? Info(EntityId id)
        => _frame is not null && _indexById.TryGetValue(id.Value, out int i) ? InfoOf(_frame.Entities[i]) : null;

    /// <summary>A message line in the view.</summary>
    public void Message(string text)
    {
        _messages.Add((text, _now + 10));
        if (_messages.Count > 6)
        {
            _messages.RemoveAt(0);
        }
    }

    /// <summary>
    /// Night darkness for a time of day (0 = midnight, 0.5 = noon; the clock globe's 24 frames follow the same hours).
    /// ⚠ UNK-32 placeholder curve: night until 05:00, dawn to 07:00, day, dusk from 18:00 to 20:30.
    /// </summary>
    public static float Darkness(float phase)
    {
        const float night = 0.55f;
        return phase switch
        {
            < 0.21f => night,
            < 0.29f => (0.29f - phase) / 0.08f * night,
            < 0.75f => 0f,
            < 0.855f => (phase - 0.75f) / 0.105f * night,
            _ => night,
        };
    }

    /// <summary>Time of day of a frame (0 = midnight): the sim's day clock (GAME-16, ADR 0105).</summary>
    public static float DayPhase(GameFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.DayMinute / 1440f;
    }

    // F4 (GAME-17): every kingdom's score and statistics from the sim.
    private void ShowScores()
    {
        if (_frame is null)
        {
            return;
        }

        for (int p = 0; p < _frame.Players.Length; p++)
        {
            KingdomStatistics k = _frame.Players[p].Statistics;
            Message($"Kingdom {p + 1}: score {_frame.Players[p].Score}, trained {k.UnitsTrained}, killed {k.UnitsKilled}, lost {k.UnitsLost}, "
                + $"built {k.BuildingsBuilt}, destroyed {k.BuildingsDestroyed}, gathered {k.GatheredTotal}");
        }
    }

    private WorldOptions Barred()
    {
        _options.AllHealthBars = _barsToggled || _barsHeld;
        _options.Waypoints = Input.IsKeyPressed(Key.Shift);
        _options.GroupOf ??= _selection.GroupOf;
        return _options;
    }

    private void Accept(GameFrame frame)
    {
        _frame = frame;
        _frameArrived = _now;
        _interpolator.Push(frame);
        _indexById.Clear();
        ReadOnlySpan<FrameEntity> entities = frame.Entities;
        for (int i = 0; i < entities.Length; i++)
        {
            _indexById[entities[i].Id.Value] = i;
            _lastByIndex[entities[i].Id.Index] = (entities[i].X, entities[i].Y, entities[i].TypeId, entities[i].Owner);
        }

        RefreshCard();
    }

    // Own entities stay selected while they exist; others only while they are visible (they vanish into the fog).
    private bool Selectable(EntityId id)
    {
        if (_frame is null || _world is null || !_indexById.TryGetValue(id.Value, out int i))
        {
            return false;
        }

        FrameEntity e = _frame.Entities[i];
        byte[] fog = _frame.Visibility;
        if (e.Owner == _session.LocalPlayer || !_options.Fog || fog.Length != _world.MapWidth * _world.MapHeight)
        {
            return true;
        }

        int tx = Math.Clamp(e.X / 2048, 0, _world.MapWidth - 1), ty = Math.Clamp(e.Y / 2048, 0, _world.MapHeight - 1);
        return fog[(ty * _world.MapWidth) + tx] == 2;
    }

    private EntityInfo InfoOf(in FrameEntity e) => CommandCard.Info(_rules, _frame!, e, _session.LocalPlayer);

    private List<EntityInfo> SelectedInfos()
    {
        var list = new List<EntityInfo>();
        if (_frame is null)
        {
            return list;
        }

        foreach (EntityId id in _selection.Selected)
        {
            if (_indexById.TryGetValue(id.Value, out int i))
            {
                list.Add(InfoOf(_frame.Entities[i]));
            }
        }

        return list;
    }

    private List<EntityInfo> OnScreen()
    {
        var list = new List<EntityInfo>();
        if (_frame is null || _world is null || _hud is null)
        {
            return list;
        }

        var view = new Rect2(_pan, _hud.Layout.View.Size);
        foreach (Pickable p in _world.Pickables)
        {
            if (view.Intersects(p.Hit))
            {
                list.Add(InfoOf(_frame.Entities[p.Index]));
            }
        }

        return list;
    }

    private EntityInfo? PickAt(Vector2 screen)
    {
        if (_frame is null || _world is null || _hud is null)
        {
            return null;
        }

        Vector2 world = screen - _hud.Layout.View.Position + _pan;
        Pickable? best = null;
        foreach (Pickable p in _world.Pickables)
        {
            if (p.Hit.HasPoint(world) && (best is null || p.FootY >= best.Value.FootY))
            {
                best = p;
            }
        }

        return best is { } b ? InfoOf(_frame.Entities[b.Index]) : null;
    }

    private bool InView(Vector2 screen) => _hud is not null && new Rect2(_hud.Layout.View.Position, _hud.Layout.View.Size).HasPoint(screen);

    private void MouseButtonEvent(InputEventMouseButton e)
    {
        if (_frame is null)
        {
            return;
        }

        KeyMods mods = KeyMap.Mods(e);
        if (e.ButtonIndex == MouseButton.Left)
        {
            if (e.Pressed)
            {
                if (!InView(e.Position))
                {
                    return;
                }

                if (_placing != ushort.MaxValue)
                {
                    PlaceBuilding(mods);
                    return;
                }

                if (_route is { } route)
                {
                    PickMarket(e.Position, route);
                    return;
                }

                if (_mode != OrderMode.None)
                {
                    (int x, int y) = ToWorld(e.Position);
                    SendAndConfirm(OrderResolver.WithMode(_mode, ModeSelection(), PickAt(e.Position), x, y, mods, _session.LocalPlayer, _modeArgument, OnScreen()), x, y);
                    if ((mods & KeyMods.Shift) == 0)
                    {
                        _mode = OrderMode.None;
                        RefreshCard();
                    }

                    return;
                }

                if (e.DoubleClick && PickAt(e.Position) is { } hit)
                {
                    _selection.DoubleClick(hit, OnScreen(), (mods & KeyMods.Ctrl) != 0, (mods & KeyMods.Alt) != 0);
                    _dragStart = null;
                    SelectionSound();
                    return;
                }

                _dragStart = e.Position;
            }
            else if (_dragStart is { } start)
            {
                _dragStart = null;
                if ((e.Position - start).Length() > 4 && _world is not null && _hud is not null)
                {
                    Vector2 a = start - _hud.Layout.View.Position + _pan, b = e.Position - _hud.Layout.View.Position + _pan;
                    var box = new Rect2(new Vector2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), (a - b).Abs());
                    var inBox = new List<EntityInfo>();
                    foreach (Pickable p in _world.Pickables)
                    {
                        if (box.Intersects(p.Hit))
                        {
                            inBox.Add(InfoOf(_frame.Entities[p.Index]));
                        }
                    }

                    _selection.Box(inBox, (mods & KeyMods.Shift) != 0);
                }
                else
                {
                    _selection.Click(PickAt(e.Position), (mods & KeyMods.Shift) != 0);
                }

                SelectionSound();
            }
        }
        else if (e.ButtonIndex is MouseButton.Right or MouseButton.Middle)
        {
            if (e.Pressed)
            {
                // A held right (or middle) button drags the map; a right click without a drag gives the order on release.
                if (InView(e.Position) && (e.ButtonIndex == MouseButton.Middle || Settings.DragScroll))
                {
                    _grabStart = e.Position;
                    _grabPan = _pan;
                    _grabbing = false;
                    _grabButton = e.ButtonIndex;
                    return;
                }

                if (e.ButtonIndex == MouseButton.Right)
                {
                    RightClick(e.Position, mods);
                }

                return;
            }

            if (_grabStart is null || e.ButtonIndex != _grabButton)
            {
                return;
            }

            bool dragged = _grabbing;
            _grabStart = null;
            _grabbing = false;
            if (!dragged && e.ButtonIndex == MouseButton.Right)
            {
                RightClick(e.Position, mods);
            }
        }
    }

    private void RightClick(Vector2 position, KeyMods mods)
    {
        if (_mode is OrderMode.Unload or OrderMode.Patrol or OrderMode.Guard or OrderMode.Attack && _placing == ushort.MaxValue && InView(position))
        {
            // Emerge and unload, patrol, guard and attack: a right click gives the place or the object too, and with Shift
            // the order waits for more (the manual ch. 5 §2.3, the owner 2026-10-21; ORD-015, ORD-006, CTRL-026, CTRL-027).
            (int ux, int uy) = ToWorld(position);
            SendAndConfirm(OrderResolver.WithMode(_mode, ModeSelection(), PickAt(position), ux, uy, mods, _session.LocalPlayer, _modeArgument, OnScreen()), ux, uy);
            if ((mods & KeyMods.Shift) == 0 || _mode == OrderMode.Unload)
            {
                _mode = OrderMode.None;
                RefreshCard();
            }

            return;
        }

        if (_placing != ushort.MaxValue || _mode != OrderMode.None)
        {
            _placing = ushort.MaxValue;
            _mode = OrderMode.None;
            RefreshCard();
            return;
        }

        if (!InView(position))
        {
            return;
        }

        (int x, int y) = ToWorld(position);
        SendAndConfirm(OrderResolver.RightClick(SelectedInfos(), PickAt(position), x, y, mods, _session.LocalPlayer, _rules, (mods & KeyMods.Alt) != 0 ? OnScreen() : null), x, y);
    }

    // Alt or Ctrl turns a gate to be built through its four facings, Alt a bridge across or down (CTRL-024, BLD-019; the
    // manual: Alt + Ctrl for gates).
    private bool TurnGhost(Key key)
    {
        if (_placing == ushort.MaxValue || key is not (Key.Alt or Key.Ctrl) || !Placement.CanTurn(_rules.EntityTypes[_placing])
            || (key == Key.Ctrl && _rules.EntityTypes[_placing].Traits.IsBridge))
        {
            return false;
        }

        _facing = _rules.EntityTypes[_placing].Traits.Gate is not null ? (_facing + 1) % 4 : (_facing == 0 ? 2 : 0);
        return true;
    }

    // Drag scrolling: the map follows the pointer while the right (or middle) button is held, after a few pixels of
    // travel so that a click stays a click.
    private void GrabScroll(InputEventMouseMotion motion)
    {
        if (_grabStart is not { } start)
        {
            return;
        }

        MouseButtonMask held = _grabButton == MouseButton.Middle ? MouseButtonMask.Middle : MouseButtonMask.Right;
        if ((motion.ButtonMask & held) == 0)
        {
            // The release happened outside the window: drop the drag without an order.
            _grabStart = null;
            _grabbing = false;
            return;
        }

        Vector2 moved = motion.Position - start;
        if (!_grabbing && moved.Length() > GrabThreshold)
        {
            _grabbing = true;
        }

        if (_grabbing)
        {
            _pan = _grabPan - (Vector2I)moved.Round();
            ClampPan();
        }
    }

    // Sends the orders of a click in the world and confirms them where they go: the target's marks flash, or the
    // original's red cross plays on the clicked ground (play-test of 2026-10-20).
    private void SendAndConfirm(IReadOnlyList<PlannedCommand> orders, int x, int y)
    {
        if (orders.Count == 0)
        {
            return;
        }

        Send(orders);
        EntityId target = EntityId.None;
        foreach (PlannedCommand c in orders)
        {
            if (c.Target != EntityId.None)
            {
                target = c.Target;
                break;
            }
        }

        _world?.ConfirmOrder(x, y, target);
    }

    private void Send(IReadOnlyList<PlannedCommand> orders)
    {
        if (orders.Count == 0)
        {
            return;
        }

        var batch = new CommandList();
        foreach (PlannedCommand c in orders)
        {
            c.AddTo(batch);
        }

        _session.Submit(batch);
        if (Lead() is { } lead && _frame is not null)
        {
            TypeVisual v = _catalog.Type(_frame.Entities[lead].TypeId);
            _mixer?.PlaySet(v.SoundCommand, SoundCategory.Speech, _catalog.SoundPriority("obj_order"));
        }
    }

    private int? Lead()
    {
        if (_selection.Selected.Count == 0)
        {
            return null;
        }

        EntityId id = _selection.Selected[Math.Min(_selection.Focus, _selection.Selected.Count - 1)];
        return _indexById.TryGetValue(id.Value, out int i) ? i : null;
    }

    private void SelectionSound()
    {
        if (Lead() is { } lead && _frame is not null)
        {
            FrameEntity e = _frame.Entities[lead];
            TypeVisual v = _catalog.Type(e.TypeId);
            if (e.Owner == _session.LocalPlayer)
            {
                _mixer?.PlaySet(v.SoundSelect ?? v.SoundCommand, v.SoundSelect is not null ? SoundCategory.Buildings : SoundCategory.Speech, _catalog.SoundPriority("obj_select"));
            }
        }
    }

    private void KeyDown(InputEventKey key)
    {
        GameKey k = KeyMap.From(key.Keycode);
        KeyMods mods = KeyMap.Mods(key);
        if (k == GameKey.None)
        {
            return;
        }

        // Command-card letters (MENU.INI) come first, without Ctrl/Alt.
        if (k >= GameKey.A && k <= GameKey.Z && (mods & (KeyMods.Ctrl | KeyMods.Alt)) == 0)
        {
            char letter = (char)('A' + (k - GameKey.A));
            if (_frame is not null && _card.SlotOfKey(CardNow(), letter) is { } slot)
            {
                OnButton(slot, false);
                return;
            }
        }

        if (_hotkeys.Find(k, mods) is { } binding)
        {
            Act(binding, mods);
        }
    }

    private void Act(HotkeyBinding binding, KeyMods mods)
    {
        if ((binding.Action == GameAction.SaveDialog && ShowSaveDialog()) || (binding.Action == GameAction.LoadDialog && ShowLoadDialog()))
        {
            return;
        }

        if (binding.Status == ParityStatus.Blocked || binding.Action is GameAction.SaveDialog or GameAction.LoadDialog)
        {
            Message($"{binding.Action}: {binding.Note}");
            return;
        }

        switch (binding.Action)
        {
            case GameAction.MenuScreen:
                ShowMenu();
                break;
            case GameAction.Scores:
                ShowScores();
                break;
            case GameAction.Objectives when _session.Scenario is not null:
                ShowQuests();
                break;
            case GameAction.Objectives:
                Message(_catalog.Text("sp_setupmap", "vcond_0", "Standard") + ": destroy every enemy unit and building.");
                break;
            case GameAction.Options:
                ShowOptions();
                break;
            case GameAction.Diplomacy:
                ShowDiplomacy();
                break;
            case GameAction.ShowInventory:
                ToggleInventory();
                break;
            case GameAction.InventoryPrevious or GameAction.InventoryNext:
                _card.TurnPage(binding.Action == GameAction.InventoryNext ? 1 : -1);
                RefreshCard();
                break;
            case GameAction.Tribute:
                ShowTribute(back: false);
                break;
            case GameAction.Screenshot:
                Screenshot();
                break;
            case GameAction.CenterSelection:
                if (Lead() is { } lead && _frame is not null)
                {
                    CenterOn(_frame.Entities[lead].X, _frame.Entities[lead].Y);
                }

                break;
            case GameAction.DeselectOrBack:
                if (_dialog is not null || _route is not null)
                {
                    CloseDialog();
                    _route = null;
                }
                else if (_placing != ushort.MaxValue || _mode != OrderMode.None)
                {
                    _placing = ushort.MaxValue;
                    _mode = OrderMode.None;
                }
                else if (_card.OpenMenu is not null)
                {
                    _card.Back();
                }
                else if (_selection.Selected.Count > 0)
                {
                    _selection.Clear();
                }
                else
                {
                    ShowMenu(); // nothing left to cancel: Esc opens the game menu (the owner's fourth play-test)
                    return;
                }

                RefreshCard();
                break;
            case GameAction.LastEvent or GameAction.LastEventAndSelect:
                _eventCursor = _eventHistory.Count - 1;
                GoToEvent(binding.Action == GameAction.LastEventAndSelect);
                break;
            case GameAction.NextEvent:
                _eventCursor = Math.Min(_eventCursor + 1, _eventHistory.Count - 1);
                GoToEvent(false);
                break;
            case GameAction.PreviousEvent:
                _eventCursor = Math.Max(_eventCursor - 1, 0);
                GoToEvent(false);
                break;
            case GameAction.LastLocation or GameAction.NextLocation or GameAction.PreviousLocation:
                if (_locations.Count > 0)
                {
                    _locationCursor = binding.Action switch
                    {
                        GameAction.LastLocation => _locations.Count - 1,
                        GameAction.NextLocation => Math.Min(_locationCursor + 1, _locations.Count - 1),
                        _ => Math.Max(_locationCursor - 1, 0),
                    };
                    CenterOn(_locations[_locationCursor].X, _locations[_locationCursor].Y);
                }

                break;
            case GameAction.ShowRallyPoint:
                if (Lead() is { } l && _frame is not null && _frame.Entities[l].HasRally)
                {
                    CenterOn(_frame.Entities[l].RallyX, _frame.Entities[l].RallyY);
                }

                break;
            case GameAction.DeleteSelected:
                DeleteSelected(binding.Argument == 1);
                break;
            case GameAction.SpeedUp:
                _session.SpeedIndex++;
                Message($"Game speed {_session.SpeedPercent} %");
                break;
            case GameAction.SpeedDown:
                _session.SpeedIndex--;
                Message($"Game speed {_session.SpeedPercent} %");
                break;
            case GameAction.TranslucentTrees:
                _options.TranslucentTrees = !_options.TranslucentTrees;
                break;
            case GameAction.CycleSelection:
                _selection.CycleFocus();
                if (Lead() is { } focus && _frame is not null)
                {
                    CenterOn(_frame.Entities[focus].X, _frame.Entities[focus].Y);
                }

                break;
            case GameAction.HealthBarsHold:
                _barsHeld = true;
                break;
            case GameAction.HealthBarsToggle:
                _barsToggled = !_barsToggled;
                break;
            case GameAction.AssignGroup:
                _selection.AssignGroup(binding.Argument);
                Message($"Group {binding.Argument}");
                break;
            case GameAction.SelectGroup:
                if (_selection.SelectGroup(binding.Argument, id => _indexById.ContainsKey(id.Value)) && Lead() is { } g && _frame is not null)
                {
                    CenterOn(_frame.Entities[g].X, _frame.Entities[g].Y);
                }

                break;
            case GameAction.ClearGroup:
                _selection.ClearGroup();
                break;
            case GameAction.SelectBuildingClass:
                SelectBuildingClass((char)('A' + binding.Argument));
                break;
            case GameAction.SelectAboveAverageExperience or GameAction.SelectBelowAverageExperience:
                _selection.SelectByExperience(OnScreen(), binding.Action == GameAction.SelectAboveAverageExperience);
                break;
            case GameAction.Chat when !_session.IsMultiplayer:
                Message("Chat: single player has no chat line yet."); // multiplayer: the overlay's chat line takes Enter
                break;
            case GameAction.UseDefaultItem when _frame is not null:
                Submit(_card.UseDefaultItem(CardNow()));
                break;
            case GameAction.SelectAboveAverageHealth or GameAction.SelectBelowAverageHealth:
                _selection.SelectByHealth(OnScreen(), binding.Action == GameAction.SelectAboveAverageHealth);
                break;
            case GameAction.SelectUngrouped:
                _selection.SelectUngrouped(OnScreen());
                break;
            case GameAction.Pause:
                _session.Paused = !_session.Paused;
                break;
            case GameAction.ScrollLeft or GameAction.ScrollRight or GameAction.ScrollUp or GameAction.ScrollDown:
                // Held keys scroll in Scroll().
                break;
        }
    }

    private void GoToEvent(bool select)
    {
        if (_eventCursor < 0 || _eventCursor >= _eventHistory.Count)
        {
            return;
        }

        (int x, int y, EntityId id) = _eventHistory[_eventCursor];
        CenterOn(x, y);
        if (select && _indexById.ContainsKey(id.Value))
        {
            _selection.SelectOwn([id]);
        }
    }

    private void DeleteSelected(bool important)
    {
        var ids = new List<EntityId>();
        foreach (EntityInfo e in SelectedInfos())
        {
            // A protected type (classic: the castle, GAME-04) goes only with Shift + Del.
            if (e.Owner == _session.LocalPlayer && (important || !_rules.EntityTypes[e.TypeId].Traits.ProtectedDelete))
            {
                ids.Add(e.Id);
            }
        }

        if (ids.Count > 0)
        {
            var batch = new CommandList();
            batch.Add(CommandKind.Delete, ids.ToArray(), flags: important ? CommandFlags.Queue : CommandFlags.None); // Shift + Del (sim: castle and guilds)
            _session.Submit(batch);
        }
    }

    private void SelectBuildingClass(char letter)
    {
        if (_frame is null)
        {
            return;
        }

        IReadOnlyList<string> classes = _catalog.ClassesOfKey(letter);
        var matches = new List<FrameEntity>();
        foreach (FrameEntity e in _frame.Entities)
        {
            if (e.Owner == _session.LocalPlayer && classes.Contains(_catalog.Type(e.TypeId).Class))
            {
                matches.Add(e);
            }
        }

        if (matches.Count == 0)
        {
            return;
        }

        FrameEntity pick = matches[_buildingCursor++ % matches.Count];
        _selection.SelectOwn([pick.Id]);
        CenterOn(pick.X, pick.Y);
    }

    private void Scroll(double delta)
    {
        if (_hud is null || ViewLocked)
        {
            return;
        }

        Vector2 size = GetViewport().GetVisibleRect().Size;
        Vector2 dir = Vector2.Zero;
        bool focused = DisplayServer.WindowIsFocused();
        Vector2 m = GetViewport().GetMousePosition();
        if (EdgeScroll && Settings.EdgeScroll && focused && Input.MouseMode == Input.MouseModeEnum.Visible && _dragStart is null && _grabStart is null && m.X >= 0 && m.Y >= 0 && m.X < size.X && m.Y < size.Y)
        {
            if (m.X <= 2) { dir.X -= 1; }
            if (m.X >= size.X - 3) { dir.X += 1; }
            if (m.Y <= 2) { dir.Y -= 1; }
            if (m.Y >= size.Y - 3) { dir.Y += 1; }
        }

        if (Input.IsKeyPressed(Key.Left)) { dir.X -= 1; }
        if (Input.IsKeyPressed(Key.Right)) { dir.X += 1; }
        if (Input.IsKeyPressed(Key.Up)) { dir.Y -= 1; }
        if (Input.IsKeyPressed(Key.Down)) { dir.Y += 1; }
        if (dir != Vector2.Zero)
        {
            _pan += (Vector2I)(dir * (float)(ScrollSpeed * Settings.ScrollSpeed / 5.0 * delta)).Round();
            ClampPan();
        }
    }

    private void ClampPan()
    {
        if (_world is null || _hud is null)
        {
            return;
        }

        Vector2I view = _hud.Layout.View.Size;
        _pan = new Vector2I(
            Math.Clamp(_pan.X, 0, Math.Max(0, (_world.MapWidth * 32) - view.X)),
            Math.Clamp(_pan.Y, 0, Math.Max(0, (_world.MapHeight * 32) - view.Y)));
    }

    private void OnMinimap(Vector2 share, bool right, KeyMods mods)
    {
        if (_world is null)
        {
            return;
        }

        int x = (int)(share.X * _world.MapWidth * 32 * WorldRenderer.UnitsPerPixel), y = (int)(share.Y * _world.MapHeight * 32 * WorldRenderer.UnitsPerPixel);
        if (right)
        {
            SendAndConfirm(OrderResolver.RightClick(SelectedInfos(), null, x, y, mods, _session.LocalPlayer, _rules), x, y);
        }
        else
        {
            CenterOn(x, y);
        }
    }

    private void PlaceBuilding(KeyMods mods)
    {
        if (_frame is null || CurrentGhost(_frame) is not { } ghost)
        {
            return;
        }

        if (!ghost.Legal)
        {
            Message("Cannot build there.");
            return;
        }

        var builders = new List<EntityId>();
        foreach (EntityInfo e in SelectedInfos())
        {
            if (e.Owner == _session.LocalPlayer && e.CanBuild)
            {
                builders.Add(e.Id);
            }
        }

        if (builders.Count == 0)
        {
            _placing = ushort.MaxValue;
            return;
        }

        var batch = new CommandList();
        batch.Add(CommandKind.Build, builders.ToArray(), ghost.TileX * 2048, ghost.TileY * 2048, arg0: ghost.TypeId, arg1: (ghost.Rotated ? 1 : 0) | (ghost.Mirrored ? 2 : 0), flags: (mods & KeyMods.Shift) != 0 ? CommandFlags.Queue : CommandFlags.None);
        _session.Submit(batch);
        _mixer?.PlaySet("build", SoundCategory.Effects, _catalog.SoundPriority("build"));
        if ((mods & KeyMods.Shift) == 0)
        {
            _placing = ushort.MaxValue;
            _card.Reset();
            RefreshCard();
        }
    }

    private Ghost? CurrentGhost(GameFrame frame)
    {
        if (_placing == ushort.MaxValue || _world is null || !InView(_mouse))
        {
            return null;
        }

        EntityTypeDefinition type = _rules.EntityTypes[_placing];
        bool turned = _facing >= 2 && Placement.CanTurn(type);
        bool mirrored = (_facing & 1) != 0 && type.Traits.Gate is not null;
        (int x, int y) = ToWorld(_mouse);
        (int tx, int ty) = Placement.TopLeftFor(type, x, y, turned);
        List<(int Dx, int Dy)> blocked = Placement.BlockedCells(frame.Buildable, frame.Terrain, _world.MapWidth, _world.MapHeight, type, tx, ty, turned);
        (int width, int height) = Placement.Size(type, turned);
        if (frame.Visibility.Length == _world.MapWidth * _world.MapHeight)
        {
            // Not on unexplored ground.
            for (int dy = 0; dy < height; dy++)
            {
                for (int dx = 0; dx < width; dx++)
                {
                    int cx = tx + dx, cy = ty + dy;
                    bool inside = cx >= 0 && cy >= 0 && cx < _world.MapWidth && cy < _world.MapHeight;
                    if (inside && frame.Visibility[(cy * _world.MapWidth) + cx] == 0 && !blocked.Contains((dx, dy)))
                    {
                        blocked.Add((dx, dy));
                    }
                }
            }
        }

        bool legal = blocked.Count == 0 && Placement.IsLegal(frame.Buildable, frame.Terrain, _world.MapWidth, _world.MapHeight, type, tx, ty, turned);
        return new Ghost(_placing, tx, ty, legal, blocked, turned, mirrored);
    }

    private void OnCard(int slot, bool right)
    {
        if (slot >= 0 && slot < Card.Count && Card[slot] is { } s)
        {
            OnButton(s, right);
        }
    }

    private void OnButton(CardSlot s, bool right)
    {
        if (_frame is null)
        {
            return;
        }

        CardPress press = _card.Press(CardNow(), s, right, Input.IsKeyPressed(Key.Shift), Input.IsKeyPressed(Key.Alt), Input.IsKeyPressed(Key.Ctrl));
        if (press.Commands is { } commands)
        {
            _session.Submit(commands);
        }

        if (press.Mode is { } mode)
        {
            _mode = mode;
            _modeArgument = press.ModeArgument;
        }

        if (press.Placing is { } type)
        {
            _placing = type;
            _facing = 0;
        }

        if (press.Message is { } text)
        {
            Message(text);
        }

        if (press.NextIdlePeasant)
        {
            SelectNextIdlePeasant();
        }

        if (press.Select is { } chosen && _frame is not null && _indexById.TryGetValue(chosen.Value, out int at))
        {
            // An entry of a kingdom list (heroes, spies, items: HUD-033).
            _selection.SelectOwn([chosen]);
            CenterOn(_frame.Entities[at].X, _frame.Entities[at].Y);
        }

        if (press.ShowQuests)
        {
            ShowQuests();
        }

        if (press.DeleteSelected)
        {
            DeleteSelected(important: false);
        }

        switch (press.Dialog)
        {
            case CardDialogKind.Gamble:
                var gamble = new GambleDialog(_ui, _rules.World.MaxGambleStake, _rules.World.GambleOdds.Length);
                gamble.Bet += (resource, odds, stake) => Submit(_card.Gamble(CardNow(), resource, odds, stake));
                OpenDialog(gamble);
                break;
            case CardDialogKind.Exchange:
                var exchange = new ExchangeDialog(_ui, _rules.World.ConvertRatio);
                exchange.Exchange += (give, get, amount) => Submit(_card.Exchange(CardNow(), give, get, amount));
                OpenDialog(exchange);
                break;
            case CardDialogKind.Craft:
                var items = _card.CraftOptions(CardNow()).Select(CraftChoiceOf).ToList();
                var craft = new CraftDialog(_ui, items, _card.CraftPricePerPower, _card.CraftMaxPower);
                craft.Craft += (type, variant, gold) =>
                {
                    Submit(_card.Craft(CardNow(), type, gold, variant));
                    CloseDialog();
                };
                OpenDialog(craft);
                break;
            case CardDialogKind.Trade:
                var trade = new TradeDialog(_ui);
                trade.Route += (here, there, load) =>
                {
                    _route = (_rules.ResourceId(here) | (_rules.ResourceId(there) << 8), load);
                    CloseDialog();
                    Message("Trade: click the far market or harbor.");
                };
                OpenDialog(trade);
                break;
        }

        RefreshCard();
    }

    // An item of the magic workshop's dialog: its kind (books, potions, weapons and shields) and picture. A book shows its
    // spell's order picture (the owner's screenshot: Fire Rain's burning buildings), the others D_BUILD's items.bmp (bow,
    // book, shield, sword, potion; which weapon shows the bow and which the sword is ours, ⚠ UNVERIFIED).
    private CraftChoice CraftChoiceOf(CraftOption option)
    {
        ItemDefinition? item = _rules.EntityTypes[option.Item].Traits.Item;
        Texture2D? picture = null;
        string name = _catalog.Type(option.Item).Name;
        if (option.Kind == CraftKind.Book && option.Spell >= 0 && option.Spell < _rules.Spells.Count)
        {
            string spell = _rules.Spells[option.Spell].Key;
            picture = _ui.Cell("art/orders/buttons.t2img", _catalog.OrderImage(spell));
            name = _catalog.Text("spell_names", spell, spell);
        }
        else if (option.Look is { } look)
        {
            name = _catalog.Text("images", look, look);
        }

        // items.bmp: bow, book, shield, sword, potion; a power item by its look, else by what it adds.
        int cell = option.Kind switch
        {
            CraftKind.Book => 1,
            CraftKind.Potion => 4,
            _ when option.Look?.StartsWith("item_bow", StringComparison.Ordinal) == true => 0,
            _ when option.Effect == ItemEffect.Defence || item?.Kind == ItemKind.Armor => 2,
            _ => 3,
        };
        if (option.Kind == CraftKind.Potion && option.Effect != ItemEffect.Default)
        {
            name += " (" + option.Effect.ToString().ToLowerInvariant() + ")";
        }

        picture ??= _ui.Region("art/screens/d_build/items.t2img", new Rect2I(0, cell * 48, 48, 48));
        return new CraftChoice(option.Item, option.Variant, (int)option.Kind, picture, name);
    }

    // What the command card is built for now (Tzar2.Game.Ui.CommandCard, ADR 0182).
    private CardContext CardNow()
        => new(_frame!, _selection.Selected, _selection.Focus, _selection.IsOwn, _placing != ushort.MaxValue, _mode, _modeArgument, _session.Scenario is not null, _picked, _session.Setup.AiAssist);

    private void Submit(CommandList? commands)
    {
        if (commands is not null)
        {
            _session.Submit(commands);
        }
    }

    // A dialog in the card's place, for the building or units selected now (it closes when the selection changes).
    private void OpenDialog(CardDialog dialog)
    {
        CloseDialog();
        _dialog = dialog;
        _dialogFor = _selection.Selected.Count > 0 ? _selection.Selected[0] : EntityId.None;
        dialog.Closed += CloseDialog;
        _hud?.ShowDialog(dialog);
    }

    private void CloseDialog()
    {
        if (_dialog is null)
        {
            return;
        }

        _dialog = null;
        _hud?.ShowDialog(null);
    }

    // The far market of a trade route: a market or harbor under the pointer.
    private void PickMarket(Vector2 screen, (int Goods, int Load) route)
    {
        if (_frame is null)
        {
            _route = null;
            return;
        }

        if (PickAt(screen) is not { } far)
        {
            Message("Trade: that is not a market of this trader's kind.");
            return;
        }

        (CommandList? commands, string? message) = _card.Trade(CardNow(), far.Id, route.Goods, route.Load);
        if (message is not null)
        {
            Message(message);
        }

        if (commands is not null || message == "Trade: no own market to load at.")
        {
            _route = null;
        }

        Submit(commands);
    }

    // The idle peasant after the current selection (the original's "next idle peasant" button and key).
    private void SelectNextIdlePeasant()
    {
        if (_frame is null)
        {
            return;
        }

        if (_card.NextIdlePeasant(CardNow()) is { } peasant && _indexById.TryGetValue(peasant.Value, out int i))
        {
            _selection.SelectOwn([peasant]);
            CenterOn(_frame.Entities[i].X, _frame.Entities[i].Y);
            return;
        }

        Message("No idle peasants.");
    }

    private void RefreshCard()
    {
        if (_hud is null || _frame is null)
        {
            return;
        }

        if (_dialog is not null && (_selection.Selected.Count == 0 || _selection.Selected[0] != _dialogFor))
        {
            CloseDialog(); // the selection changed
        }

        Card = _card.Build(CardNow());
        _hud.SetCard(Card);
        int eye = _card.InventoryOpen ? 2 : _card.Carrier(CardNow()).IsNone ? 0 : 1;
        _hud.SetSide(_card.Page > 0, eye, _card.Page < _card.PageCount - 1);
    }

    // The eye and the slash key (HUD-017): the selected unit's items on the card, or back to its orders.
    private void ToggleInventory()
    {
        if (_frame is null)
        {
            return;
        }

        if (!_card.ToggleInventory(CardNow()) && _card.Carrier(CardNow()).IsNone)
        {
            Message("Inventory: the selection carries no items.");
        }

        RefreshCard();
    }

    // An item of the inventory waiting for its target is used by its carrier alone (the mode's argument is slot + 1).
    private List<EntityInfo> ModeSelection()
    {
        List<EntityInfo> all = SelectedInfos();
        return _mode == OrderMode.UseItem && _modeArgument > 0 && _card.InventoryOpen ? [.. all.Where(e => e.Id == _card.InventoryCarrier)] : all;
    }

    private void HandleEvents(GameFrame frame)
    {
        byte me = _session.LocalPlayer;
        int attackSounds = 0;
        foreach (SimEvent e in _events)
        {
            if (e.Kind == SimEventKind.Gambled && e.Player == me && _dialog is GambleDialog dice)
            {
                dice.ShowResult(e.Y == 1, e.Arg);
            }

            switch (e.Kind)
            {
                case SimEventKind.UnitTrained when e.Player == me:
                    Stats.Trained++;
                    {
                        TypeVisual v = _catalog.Type((ushort)e.Arg);
                        Position(e.Entity, out int x, out int y);
                        _eventHistory.Add((x, y, e.Entity));
                        Message($"{v.Name} ready");
                        _mixer?.PlaySet(v.SoundCommand, SoundCategory.Speech, _catalog.SoundPriority("obj_unit_ready"), Distance(x, y));
                    }

                    break;
                case SimEventKind.ConstructionCompleted when e.Player == me:
                    Stats.Built++;
                    {
                        TypeVisual v = _catalog.Type((ushort)e.Arg);
                        Position(e.Entity, out int x, out int y);
                        _eventHistory.Add((x, y, e.Entity));
                        Message($"{v.Name} completed");
                        _mixer?.PlaySet(v.SoundSelect, SoundCategory.Buildings, _catalog.SoundPriority("obj_building_ready"), Distance(x, y));
                    }

                    break;
                case SimEventKind.ResourceDelivered when e.Player == me:
                    Stats.Gathered += e.Arg;
                    break;
                case SimEventKind.Damaged:
                    if (_lastByIndex.TryGetValue(e.X, out var attacker) && attackSounds < 4)
                    {
                        attackSounds++;
                        _mixer?.PlaySet(_catalog.Type(attacker.Type).SoundAttack, SoundCategory.Effects, _catalog.SoundPriority("attack_close"), Distance(attacker.X, attacker.Y));
                    }

                    if (_lastByIndex.TryGetValue(e.Entity.Index, out var victim) && victim.Owner == me && _now - _lastAlert > 10)
                    {
                        _lastAlert = _now;
                        _locations.Add((victim.X, victim.Y));
                        Message($"{_catalog.Type(victim.Type).Name} {_catalog.Text("notify", "under_attack", "under attack")} (Shift+Home)");
                    }

                    break;
                case SimEventKind.Killed:
                    // The event carries the victim's owner (Player), the killer's owner (Y) and the victim's type (Arg);
                    // index-based lookups can already see a newer entity in the slot, because the frame is accepted
                    // before the events are drained.
                    if (e.Player == me)
                    {
                        Stats.Losses++;
                    }
                    else if (e.Y == me)
                    {
                        Stats.Kills++;
                    }

                    if (_lastByIndex.TryGetValue(e.Entity.Index, out var dead) && dead.Type == (ushort)e.Arg)
                    {
                        _mixer?.PlaySet(_catalog.Type((ushort)e.Arg).SoundDie, SoundCategory.Effects, _catalog.SoundPriority("death"), Distance(dead.X, dead.Y));
                    }

                    break;
                case SimEventKind.CommandRejected when e.Player == me:
                    Message(RejectionText((CommandRejection)e.Arg));
                    break;
                case SimEventKind.ProductionStalled when e.Player == me && e.Arg == 1:
                    if (_now - _lastAlert > 5)
                    {
                        _lastAlert = _now;
                        Message(_catalog.Text("viewmsg", "poplimit2", "Not enough houses"));
                    }

                    break;
            }
        }

        // Work sounds of a few visible workers (chop, mine, build; ⚠ cadence not measured).
        foreach (FrameEntity w in frame.Entities)
        {
            if (_workSoundBudget <= 0)
            {
                break;
            }

            if (w.Activity is UnitActivity.Chop or UnitActivity.Mine or UnitActivity.Build or UnitActivity.Farm && GD.Randf() < 0.004f)
            {
                float d = Distance(w.X, w.Y);
                if (d < 0.6f)
                {
                    string set = w.Activity switch { UnitActivity.Chop => "chop", UnitActivity.Mine => "mine", UnitActivity.Farm => "dig", _ => "build" };
                    _mixer?.PlaySet(set, SoundCategory.Effects, _catalog.SoundPriority(set), d);
                    _workSoundBudget--;
                }
            }
        }
    }

    private string RejectionText(CommandRejection rejection) => rejection switch
    {
        CommandRejection.NotAffordable => "Not enough resources",
        CommandRejection.IllegalPlacement => "Cannot build there",
        CommandRejection.RequirementNotMet => "Requirements are not met",
        CommandRejection.QueueFull => "The queue is full",
        _ => string.Empty,
    };

    private void Position(EntityId id, out int x, out int y)
    {
        if (_lastByIndex.TryGetValue(id.Index, out var p))
        {
            (x, y) = (p.X, p.Y);
        }
        else
        {
            Vector2 c = _pan + ((_hud?.Layout.View.Size ?? Vector2I.Zero) / 2);
            (x, y) = ((int)c.X * WorldRenderer.UnitsPerPixel, (int)c.Y * WorldRenderer.UnitsPerPixel);
        }
    }

    private float Distance(int x, int y)
    {
        if (_hud is null)
        {
            return 0;
        }

        Vector2 view = _hud.Layout.View.Size;
        Vector2 centre = _pan + (view / 2);
        Vector2 d = (new Vector2(x, y) / WorldRenderer.UnitsPerPixel) - centre;
        return Math.Max(Math.Abs(d.X) / view.X, Math.Abs(d.Y) / view.Y);
    }

    private void UpdateHud(GameFrame frame, float phase)
    {
        if (_hud is null || _world is null)
        {
            return;
        }

        if (frame.Players.Length > _session.LocalPlayer)
        {
            FramePlayer p = frame.Players[_session.LocalPlayer];
            int R(string key) => _rules.ResourceId(key) is var r and >= 0 and < 8 ? p.Resources[r] : 0;
            _hud.SetResources(HudText.Number(R("gold")), HudText.Number(R("wood")), HudText.Number(R("food")), HudText.Number(R("stone")), $"{p.Population}/{p.PopulationCap}");
        }

        _hud.SetClock(phase);
        _messages.RemoveAll(m => m.Until < _now);
        _hud.SetMessages(_messages.ConvertAll(m => m.Text));
        _hud.SetStatus(frame.Paused || _session.Paused ? _catalog.Text("viewmsg", "pause", "PAUSED") : frame.SpeedPercent != 100 ? $"Speed {frame.SpeedPercent} %" : string.Empty);
        _hud.SetSelection(Describe(frame));
        if (_now >= _minimapDue)
        {
            _minimapDue = _now + 0.25;
            UpdateMinimap(frame);
        }
        else
        {
            Vector2 map = new(_world.MapWidth * 32, _world.MapHeight * 32);
            _hud.SetMinimap(_minimapTexture!, new Rect2(_pan / map, _hud.Layout.View.Size / map));
        }
    }

    private SelectionInfo? Describe(GameFrame frame)
    {
        if (Lead() is not { } lead)
        {
            return null;
        }

        FrameEntity e = frame.Entities[lead];
        TypeVisual v = _catalog.Type(e.TypeId);
        EntityTypeDefinition def = _rules.EntityTypes[e.TypeId];
        string owner = e.Owner < 8 ? SpritePage.PlayerNames[ColourOf(e.Owner)] : string.Empty;
        var strip = new List<StripItem>();
        _insideShown.Clear();
        if (_selection.Selected.Count > 1)
        {
            // Multi-selection shows health only (REQ-UI-01) and one icon per type with how many there are and their
            // average health (owner play-test 2026-10-20: not every single unit).
            var byType = new List<(ushort Type, int Count, long Health)>();
            foreach (EntityId id in _selection.Selected)
            {
                if (_indexById.TryGetValue(id.Value, out int i))
                {
                    FrameEntity m = frame.Entities[i];
                    int share = m.MaxHealth > 0 ? m.Health * 1000 / m.MaxHealth : 1000;
                    int k = byType.FindIndex(g => g.Type == m.TypeId);
                    if (k < 0)
                    {
                        byType.Add((m.TypeId, 1, share));
                    }
                    else
                    {
                        byType[k] = (m.TypeId, byType[k].Count + 1, byType[k].Health + share);
                    }
                }
            }

            foreach ((ushort type, int count, long health) in byType)
            {
                strip.Add(new StripItem(_catalog.Type(type).IconIndex, (int)(health / count), false, count));
            }

            return new SelectionInfo
            {
                Name = $"{_catalog.Text("selview", "units", "Units")}: {_selection.Selected.Count}",
                Detail = $"{e.Health}/{e.MaxHealth}",
                Icon = v.IconIndex,
                Health = e.MaxHealth > 0 ? e.Health / (float)e.MaxHealth : -1,
                Strip = strip,
                StripIsHealth = true,
                Player = e.Owner < 8 ? e.Owner : -1,
            };
        }

        var stats = new List<(string, string)>();
        EntityTraits t = def.Traits;
        if (t.Armament is { } arm)
        {
            stats.Add((arm.RangeUnits > 0 ? "art/icons/attack bow.t2img" : "art/icons/attack sword.t2img", HudText.Number(arm.Attack)));
            if (arm.RangeUnits > 0)
            {
                stats.Add(("art/icons/range.t2img", HudText.Number(arm.RangeUnits / 2048)));
            }
        }

        if (t.Category == EntityCategory.Unit)
        {
            stats.Add(("art/icons/defence.t2img", HudText.Number(t.Defence)));
        }

        if (t.GainsExperience && e.Owner == _session.LocalPlayer)
        {
            stats.Add(("art/icons/level.t2img", HudText.Number(e.Level))); // LEVEL (manual: The Selection View; HUD-016)
        }

        if (t.Category == EntityCategory.Building && t.Resistance > 0)
        {
            stats.Add(("art/icons/resistance.t2img", HudText.Number(t.Resistance)));
        }

        if (e.MaxMana > 0)
        {
            stats.Add(("art/icons/manna.t2img", string.Create(CultureInfo.InvariantCulture, $"{e.Mana}/{e.MaxMana}")));
        }

        if (e.CarryAmount > 0 && e.CarryResource < _rules.ResourceTypes.Count)
        {
            string res = _rules.ResourceTypes[e.CarryResource];
            stats.Add(($"art/icons/res{res}.t2img", HudText.Number(e.CarryAmount)));
        }

        if (e.ResourceLeft >= 0 && t.ResourceNode is { } node && node.Resource < _rules.ResourceTypes.Count)
        {
            stats.Add(($"art/icons/res{_rules.ResourceTypes[node.Resource]}.t2img", HudText.Number(e.ResourceLeft)));
        }

        // As in the original (third play-test): the hourglass TIME stat says how far the work in progress is; what is
        // queued shows as counts on the order buttons.
        if (e.ConstructionPermille >= 0)
        {
            stats.Add(("art/icons/time.t2img", $"{e.ConstructionPermille / 10} %"));
        }
        else if (e.QueueCount > 0)
        {
            stats.Add(("art/icons/time.t2img", $"{e.QueuePermille / 10} %"));
        }
        else if (e.CraftPermille >= 0)
        {
            stats.Add(("art/icons/time.t2img", $"{e.CraftPermille / 10} %")); // an item being made in a magic workshop
        }

        // A holder of ours: INSIDE, how many units it holds of how many (manual: The Selection View), their portraits in
        // the info area, where a click picks one for Emerge (ORD-015), and a bar of their health (HUD-021).
        float insideHealth = -1;
        if (e.InsideMax > 0)
        {
            stats.Add(("art/icons/inside.t2img", string.Create(CultureInfo.InvariantCulture, $"{e.Inside}/{e.InsideMax}")));
            IReadOnlyList<FrameHeld> inside = _card.Inside(CardNow());
            long share = 0;
            foreach (FrameHeld h in inside)
            {
                int permille = h.MaxHealth > 0 ? h.Health * 1000 / h.MaxHealth : 1000;
                share += permille;
                _insideShown.Add(h.Unit);
                strip.Add(new StripItem(_catalog.Type(h.TypeId).IconIndex, permille, false, 0, h.Unit == _picked));
            }

            insideHealth = inside.Count > 0 ? share / (float)inside.Count / 1000f : -1;
        }

        if (!_picked.IsNone && !_insideShown.Contains(_picked))
        {
            _picked = EntityId.None; // it came out, or died
        }

        // A spy's disguise meter follows its name, as in the original: "Spy (100)" in its own look (MAG-021, batch J). A
        // working peasant is named by its job: Woodcutter, Farmer, Miner, Builder... (STRINGS.INI [peons], HUD-032).
        string name = t.LegacyClass == "peon" && FrameJobs.StringKey(e.Job) is { } job ? _catalog.Text("peons", job, v.Name) : v.Name;
        return new SelectionInfo
        {
            Name = e.DisguiseMastery >= 0 ? string.Create(CultureInfo.InvariantCulture, $"{(e.SpyType >= 0 ? _catalog.Type((ushort)e.SpyType).Name : v.Name)} ({e.DisguiseMastery})") : name,
            Detail = e.MaxHealth > 0 ? $"{e.Health}/{e.MaxHealth} {owner}" : owner,
            Icon = v.IconIndex,
            Health = e.MaxHealth > 0 && t.Category != EntityCategory.Resource ? e.Health / (float)e.MaxHealth : -1,
            Stats = stats,
            Strip = strip,
            StripIsHealth = true,
            Player = e.SeemsOwner >= 0 ? e.SeemsOwner : e.Owner < 8 ? e.Owner : -1,
            InsideHealth = insideHealth,
        };
    }

    // HUD-030: the terrain set's [minidatargb] colours each tile by its terrain, and cliffs by their own colour.
    private void MinimapFromData(TerrainSet set, TerrainGrid grid)
    {
        if (set.Table is not { } table || !_store.Vfs.Exists(table)
            || MinimapColours.FromSection(IniTable.Parse(_store.Read(table)).Section("minidatargb")) is not { } colours)
        {
            return;
        }

        static Color Of((byte R, byte G, byte B) c) => Color.Color8(c.R, c.G, c.B);
        ReadOnlySpan<byte> cliffs = _session.Map.CliffTiles;
        Color[] fallback = _world!.Terrain.TileColours;
        var tiles = new Color[grid.Width * grid.Height];
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                int i = (y * grid.Width) + x;
                (int tl, int tr, int br, int bl) = grid.CornersOf(x, y);
                (byte R, byte G, byte B)? c = i < cliffs.Length && cliffs[i] != 0 && colours.Cliff is { } cliff
                    ? cliff
                    : colours.Terrain(MinimapColours.TileTerrain(tl, tr, br, bl));
                tiles[i] = c is { } rgb ? Of(rgb) : fallback[i];
            }
        }

        _minimapTiles = tiles;
        _minimapTree = colours.Tree is { } tree ? Of(tree) : null;
    }

    private void UpdateMinimap(GameFrame frame)
    {
        if (_world is null || _minimapImage is null || _minimapTexture is null || _hud is null)
        {
            return;
        }

        int w = _world.MapWidth, h = _world.MapHeight;
        Color[] terrain = _minimapTiles ?? _world.Terrain.TileColours;
        byte[] fog = _options.Fog ? frame.Visibility : [];
        if (_minimapBytes.Length != w * h * 4)
        {
            _minimapBytes = new byte[w * h * 4];
        }

        // One buffer and one upload (no per-pixel engine calls).
        void Put(int x, int y, Color c)
        {
            int o = ((y * w) + x) * 4;
            _minimapBytes[o] = (byte)(c.R * 255);
            _minimapBytes[o + 1] = (byte)(c.G * 255);
            _minimapBytes[o + 2] = (byte)(c.B * 255);
            _minimapBytes[o + 3] = 255;
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color c = terrain[(y * w) + x];
                if (fog.Length == w * h)
                {
                    byte v = fog[(y * w) + x];
                    c = v == 0 ? Colors.Black : v == 1 ? c.Darkened(0.5f) : c;
                }

                Put(x, y, c);
            }
        }

        PlayerPalette palette = _store.Palette;
        foreach (FrameEntity e in frame.Entities)
        {
            int tx = Math.Clamp(e.X / 2048, 0, w - 1), ty = Math.Clamp(e.Y / 2048, 0, h - 1);
            if (fog.Length == w * h && e.Owner != _session.LocalPlayer && fog[(ty * w) + tx] != 2)
            {
                continue;
            }

            EntityTypeDefinition def = _rules.EntityTypes[e.TypeId];
            Color c;
            if (e.Owner < 8)
            {
                Rgba32 r = palette.Ramp(ColourOf(e.Owner), 11);
                c = Color.Color8(r.R, r.G, r.B);
            }
            else if (def.Traits.ResourceNode is { } node)
            {
                c = _rules.ResourceTypes[node.Resource] switch { "wood" => _minimapTree ?? new Color(0.05f, 0.3f, 0.05f), "gold" => new Color(0.95f, 0.8f, 0.2f), _ => new Color(0.6f, 0.6f, 0.6f) };
            }
            else
            {
                continue;
            }

            int size = Math.Max(1, Math.Max(def.FootprintWidth, def.FootprintHeight));
            int left = e.TileX >= 0 ? e.TileX : tx, top = e.TileY >= 0 ? e.TileY : ty;
            for (int dy = 0; dy < size && top + dy < h; dy++)
            {
                for (int dx = 0; dx < size && left + dx < w; dx++)
                {
                    Put(left + dx, top + dy, c);
                }
            }
        }

        _minimapImage.SetData(w, h, false, Image.Format.Rgba8, _minimapBytes);
        _minimapTexture.Update(_minimapImage);
        Vector2 map = new(w * 32, h * 32);
        _hud.SetMinimap(_minimapTexture, new Rect2(_pan / map, _hud.Layout.View.Size / map));
    }

    private void DrawBox()
    {
        RenderingServer.CanvasItemClear(_boxItem);
        if (_dragStart is { } start && (_mouse - start).Length() > 4)
        {
            var r = new Rect2(new Vector2(Math.Min(start.X, _mouse.X), Math.Min(start.Y, _mouse.Y)), (start - _mouse).Abs());
            Vector2[] points = [r.Position, r.Position + new Vector2(r.Size.X, 0), r.End, r.Position + new Vector2(0, r.Size.Y), r.Position];
            RenderingServer.CanvasItemAddPolyline(_boxItem, points, [new Color(0.3f, 1f, 0.3f)], 1f);
        }
    }

    private void UpdateCursor()
    {
        // Nothing pending: the original's default pointer (s_def, a golden spearhead; owner's screenshot of the original)
        // or the system's; the hand is for taking things.
        string name = Settings.SystemCursor ? CursorCache.System : CursorCache.Default;
        if (_placing == ushort.MaxValue && !_grabbing && InView(_mouse))
        {
            bool shift = Input.IsKeyPressed(Key.Shift), alt = Input.IsKeyPressed(Key.Alt);
            if (_mode != OrderMode.None)
            {
                name = _mode switch
                {
                    OrderMode.Attack when alt => "cs_attacktype", // CTRL-026: every object of the type
                    OrderMode.Attack when shift => "cs_attackset", // CTRL-026: a set attacked in turn
                    OrderMode.Attack => "s_attack",
                    OrderMode.Harvest => "s_harvest",
                    OrderMode.Patrol when shift => "s_patrol2", // ORD-006: a patrol path
                    OrderMode.Patrol => "s_patrol",
                    OrderMode.Guard => "s_guard",
                    OrderMode.Repair => "s_repair",
                    OrderMode.AttackGround or OrderMode.MoveAdvance => "s_attack",
                    OrderMode.Capture or OrderMode.Bribe or OrderMode.PickItem or OrderMode.Impersonate => "s_hand",
                    OrderMode.Spell => "s_spell",
                    OrderMode.UseItem => "s_potion",
                    OrderMode.Unload when !LandUnderPointer() => "s_none", // ORD-014: no landing there
                    _ => "s_move",
                };
            }
            else if (_selection.IsOwn && PickAt(_mouse) is { } hover)
            {
                name = HoverCursor(hover, SelectedInfos(), name);
                if (name == "s_attack")
                {
                    name = alt ? "cs_attacktype" : shift ? "cs_attackset" : name;
                }
            }
        }

        CursorCache.Apply(_ui, name);
    }

    // Whether units can be let out on the tile under the pointer (ORD-014: the pointer shows it): walkable land.
    private bool LandUnderPointer()
    {
        if (_frame is null || _world is null || _frame.Terrain.Length != _world.MapWidth * _world.MapHeight)
        {
            return true;
        }

        (int x, int y) = ToWorld(_mouse);
        int tx = x / MapData.UnitsPerTile, ty = y / MapData.UnitsPerTile;
        return tx >= 0 && ty >= 0 && tx < _world.MapWidth && ty < _world.MapHeight && (_frame.Terrain[(ty * _world.MapWidth) + tx] & Placement.Land) != 0;
    }

    // The cursor of the right click's default action on what the pointer is over (OrderResolver.Hover, the same rules the
    // click follows): the sword over enemies, the hand over buildings to capture and items to pick up, the resource's
    // tool for harvesters (saw, pickaxe, the original's harvest cursor over fields, cows and fish), the hammer over own
    // sites and damaged objects for builders, the move cursor over holders to board and units to follow; elsewhere the
    // pointer (play-test of 2026-10-20: the hand showed over everything).
    private string HoverCursor(EntityInfo hover, List<EntityInfo> selection, string pointer)
    {
        switch (OrderResolver.Hover(selection, hover, _session.LocalPlayer, _rules))
        {
            case DefaultAction.Attack:
                return "s_attack";
            case DefaultAction.Capture or DefaultAction.PickItem:
                return "s_hand";
            case DefaultAction.Build or DefaultAction.Repair:
                return "s_repair";
            case DefaultAction.Board or DefaultAction.Follow:
                return "s_move";
            case DefaultAction.Harvest:
                string resource = _rules.EntityTypes[hover.TypeId].Traits.ResourceNode is { } node && node.Resource < _rules.ResourceTypes.Count ? _rules.ResourceTypes[node.Resource] : string.Empty;
                return resource switch
                {
                    "wood" => OwnCursors.Saw,
                    "gold" or "stone" or "iron" => OwnCursors.Mine,
                    _ => "s_harvest",
                };
            default:
                return pointer;
        }
    }

    private void Screenshot()
    {
        Image image = GetViewport().GetTexture().GetImage();
        string path = ProjectSettings.GlobalizePath(string.Create(CultureInfo.InvariantCulture, $"user://screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png"));
        image.SavePng(path);
        Message("Screenshot " + path);
    }

    private void StartMusic()
    {
        if (_music is null)
        {
            return;
        }

        string? sf = SoundFontLocator.Choose(System.IO.File.Exists, _soundFont, System.Environment.GetEnvironmentVariable(SoundFontLocator.EnvironmentVariable), ProjectSettings.GlobalizePath("res://../../soundfonts/GeneralUser-GS.sf2"));
        if (sf is null)
        {
            GD.Print("Music: no SoundFont (set TZAR2_SOUNDFONT or --soundfont).");
            return;
        }

        try
        {
            _music.LoadSoundFont(sf);
            string track = $"music/{1 + (int)(_session.Setup.Seed % 10)}.mid";
            if (_store.Vfs.Exists(track))
            {
                _music.Play(_store.Read(track));
            }
        }
        catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or InvalidOperationException)
        {
            GD.PrintErr("Music: " + e.Message);
        }
    }

    /// <summary>Applies <see cref="Settings"/> (volumes, categories, speed, day/night).</summary>
    public void ApplySettings()
    {
        SoundMixer.SetVolume(SoundMixer.SfxBus, Settings.SoundVolume);
        SoundMixer.SetVolume(SoundMixer.MusicBus, Settings.MusicVolume);
        _mixer?.SetEnabled(SoundCategory.Speech, Settings.UnitSpeech);
        _mixer?.SetEnabled(SoundCategory.Buildings, Settings.BuildingSounds);
        if (!KeepSpeed)
        {
            _session.SpeedIndex = Settings.GameSpeed;
        }

        _options.DayNight = Settings.DayNight;
    }

    private void ShowOptions()
    {
        if (GetNodeOrNull("Options") is not null || _hud is null)
        {
            return;
        }

        bool paused = _session.Paused;
        _session.Paused = true;
        var options = new OptionsScreen(_ui, _catalog, _hud.Layout.Size, Settings);
        var layer = new CanvasLayer { Layer = 3, Name = "Options" };
        layer.AddChild(options);
        AddChild(layer);
        options.Changed += ApplySettings;
        options.Closed += () =>
        {
            layer.QueueFree();
            _session.Paused = paused;
        };
    }

    // DIPLOMACY and F5 (HUD-012): the stances, the sharing and shared victory towards every other kingdom.
    private void ShowDiplomacy()
    {
        if (GetNodeOrNull("Diplomacy") is not null || GetNodeOrNull("Tribute") is not null || _hud is null || _frame is null)
        {
            return;
        }

        var dialog = new DiplomacyDialog(_ui, _hud.Layout.Size, new DiplomacyScreen(_frame, _session.LocalPlayer), _catalog.Text, KingdomName, KingdomColour);
        var layer = new CanvasLayer { Layer = 3, Name = "Diplomacy" };
        layer.AddChild(dialog);
        AddChild(layer);
        dialog.Closed += layer.QueueFree;
        dialog.Accepted += () =>
        {
            Submit(dialog.Model.Accept());
            layer.QueueFree();
        };
        dialog.TributePressed += () =>
        {
            layer.QueueFree();
            ShowTribute(back: true);
        };
    }

    // TRIBUTE and F6 (HUD-013): gold, food, wood and stone for the other kingdoms; SEND and CANCEL go back to the
    // diplomacy screen when it opened this one.
    private void ShowTribute(bool back)
    {
        if (GetNodeOrNull("Tribute") is not null || _hud is null || _frame is null)
        {
            return;
        }

        int[] columns = [.. CardDialog.Resources.Select(r => _rules.ResourceId(r))];
        var dialog = new TributeDialog(_ui, _hud.Layout.Size, new TributeScreen(_frame, _session.LocalPlayer, columns), _catalog.Text, KingdomName, KingdomColour);
        var layer = new CanvasLayer { Layer = 3, Name = "Tribute" };
        layer.AddChild(dialog);
        AddChild(layer);
        void Done()
        {
            layer.QueueFree();
            if (back)
            {
                Callable.From(ShowDiplomacy).CallDeferred();
            }
        }

        dialog.Closed += Done;
        dialog.Sent += () =>
        {
            Submit(dialog.Model.Send());
            Done();
        };
    }

    // The kingdom's name in the match setup, else its colour's.
    private string KingdomName(int slot)
    {
        _kingdomNames ??= MatchDescriptor.Of(_session.Setup)?.Players.ToDictionary(p => (int)p.Slot, p => p.Name) ?? [];
        return _kingdomNames.TryGetValue(slot, out string? name) && name.Length > 0 ? name : slot < 8 ? SpritePage.PlayerNames[ColourOf(slot)] : $"Kingdom {slot + 1}";
    }

    private Dictionary<int, string>? _kingdomNames;

    private Color KingdomColour(int slot)
    {
        if (slot >= 8)
        {
            return Colors.Gray;
        }

        Rgba32 r = _store.Palette.Ramp(ColourOf(slot), 11);
        return Color.Color8(r.R, r.G, r.B);
    }

    // The colour a kingdom plays with (SHELL-016: chosen in the multiplayer setup, else its slot's).
    private int ColourOf(int kingdom) => kingdom < _session.Setup.Players.Count ? _session.Setup.Players[kingdom].ColourIndex : kingdom;

    private void ShowMenu()
    {
        if (GetNodeOrNull("GameMenu") is not null || _hud is null)
        {
            return;
        }

        _session.Paused = true;
        var menu = new GameMenu(_ui, _hud.Layout.Size) { Name = "GameMenu" };
        menu.Closed += choice =>
        {
            menu.QueueFree();
            _session.Paused = false;
            switch (choice)
            {
                case GameMenuChoice.Quit:
                    QuitRequested?.Invoke();
                    break;
                case GameMenuChoice.Restart:
                    RestartRequested?.Invoke();
                    break;
                case GameMenuChoice.Options:
                    ShowOptions();
                    break;
                case GameMenuChoice.Save:
                    if (!ShowSaveDialog())
                    {
                        Message("This game cannot be saved.");
                    }

                    break;
                case GameMenuChoice.Load:
                    if (!ShowLoadDialog())
                    {
                        Message("Saves cannot be loaded here.");
                    }

                    break;
            }
        };
        var layer = new CanvasLayer { Layer = 3, Name = "GameMenu" };
        layer.AddChild(menu);
        AddChild(layer);
        menu.Closed += _ => layer.QueueFree();
    }
}
