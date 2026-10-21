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
    private readonly Stack<string> _menuStack = new();
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
    private CardDialog? _dialog;
    private EntityId _dialogFor;
    private (int Goods, int Load)? _route; // a trade route waiting for the far market's click
    private ushort _placing = ushort.MaxValue;
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
    private int _workSoundBudget;
    private Rid _boxItem;
    private bool _faultShown;
    private readonly List<CardSlot> _wholeCard = [];
    private CanvasLayer? _hudLayer;
    private CanvasLayer? _weatherLayer;
    private int _cardPage;
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

    /// <inheritdoc/>
    public override void _Ready()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        HudLayout layout = LoadLayout();
        string setId = _session.Map.TerrainSet ?? _terrainSetId;
        TerrainSet set = _store.TerrainSets.FirstOrDefault(s => s.Id == setId) ?? _store.TerrainSets.FirstOrDefault()
            ?? throw new InvalidOperationException("The package has no terrain sets.");

        // Sprites of the civilizations in the match (REQ-GFX-05): the kingdoms' and those of every object on the map (the
        // original maps give a kingdom buildings and units of other civilizations).
        _bank = new SpriteBank(_store);
        var civs = new HashSet<string>(_session.Setup.Players.Select(p => p.Civilization)) { "comm", string.Empty };
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
        _bank.Upload();
        long uploaded = watch.ElapsedMilliseconds;

        TerrainGrid grid = TerrainPresentation.Build(_session.Map, _rules, set);
        ClientLog.Info($"match load: sprites decoded {decoded} ms ({_bank.SheetCount} sheets), packed {packed - decoded} ms, uploaded {uploaded - packed} ms, terrain {watch.ElapsedMilliseconds - uploaded} ms");
        Image? light = set.Light is { } l ? _ui.Image(l) : null;
        long before = watch.ElapsedMilliseconds;
        _world = new WorldRenderer(this, GetViewport().FindWorld2D().Canvas, _rules, _catalog, _bank, set, grid, _store.LoadImage, light);
        ClientLog.Info($"match load: world renderer {watch.ElapsedMilliseconds - before} ms (terrain composed {_world.Terrain.ComposeMs} ms, {_world.Terrain.FrameTotal} chunk frames, uploaded {_world.Terrain.UploadMs} ms)");
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
            _cardPage = 0;
            _menuStack.Clear();
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
                if (e.Owner == _session.LocalPlayer && _catalog.Type(e.TypeId).Class == "castle")
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
        _hud.MinimapPressed += (share, right, mods) => OnMinimap(share, right, mods);
        _hud.MenuPressed += () => ShowMenu();
        _hud.DiplomacyPressed += () => Message(_catalog.Text("game_scr", "diplomacy", "DIPLOMACY") + ": not available yet");
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

    private EntityInfo InfoOf(in FrameEntity e)
    {
        EntityTypeDefinition def = _rules.EntityTypes[e.TypeId];
        EntityTraits t = def.Traits;
        return new EntityInfo(
            e.Id,
            e.TypeId,
            e.Owner,
            t.Category,
            t.Armament is not null && t.Harvester is null,
            (int)e.Activity,
            e.Health,
            e.MaxHealth,
            t.Harvester is not null,
            t.IsBuilder,
            t.Armament is not null,
            t.Producer is not null,
            t.ResourceNode is not null,
            e.ConstructionPermille >= 0,
            def.IsMobile,
            t.IsRepairable,
            t.IsCapturer,
            t.Capturable is { } capture && (capture.RequiredTechnology < 0 || Researched(capture.RequiredTechnology)),
            t.InventorySlots > 0,
            t.Item is not null);
    }

    private bool Researched(int technology)
    {
        byte me = _session.LocalPlayer;
        return _frame is { } f && me < f.Players.Length && f.Players[me].HasTechnology(technology);
    }

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
                    SendAndConfirm(OrderResolver.WithMode(_mode, SelectedInfos(), PickAt(e.Position), x, y, mods, _session.LocalPlayer, _modeArgument), x, y);
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
        SendAndConfirm(OrderResolver.RightClick(SelectedInfos(), PickAt(position), x, y, mods, _session.LocalPlayer, _rules), x, y);
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
            foreach (CardSlot slot in _wholeCard)
            {
                if (slot.Button.Hotkey == letter)
                {
                    OnButton(slot, false);
                    return;
                }
            }
        }

        if (_hotkeys.Find(k, mods) is { } binding)
        {
            Act(binding, mods);
        }
    }

    private void Act(HotkeyBinding binding, KeyMods mods)
    {
        if ((binding.Action == GameAction.SaveDialog && QuickSave()) || (binding.Action == GameAction.LoadDialog && QuickLoad()))
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
                else if (_menuStack.Count > 0)
                {
                    _menuStack.Pop();
                }
                else
                {
                    _selection.Clear();
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
            string cls = _catalog.Type(e.TypeId).Class;
            if (e.Owner == _session.LocalPlayer && (important || cls != "castle"))
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
        batch.Add(CommandKind.Build, builders.ToArray(), ghost.TileX * 2048, ghost.TileY * 2048, arg0: ghost.TypeId, flags: (mods & KeyMods.Shift) != 0 ? CommandFlags.Queue : CommandFlags.None);
        _session.Submit(batch);
        _mixer?.PlaySet("build", SoundCategory.Effects, _catalog.SoundPriority("build"));
        if ((mods & KeyMods.Shift) == 0)
        {
            _placing = ushort.MaxValue;
            _menuStack.Clear();
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
        (int x, int y) = ToWorld(_mouse);
        (int tx, int ty) = Placement.TopLeftFor(type, x, y);
        List<(int Dx, int Dy)> blocked = Placement.BlockedCells(frame.Buildable, _world.MapWidth, _world.MapHeight, type, tx, ty);
        if (frame.Visibility.Length == _world.MapWidth * _world.MapHeight)
        {
            // Not on unexplored ground.
            for (int dy = 0; dy < type.FootprintHeight; dy++)
            {
                for (int dx = 0; dx < type.FootprintWidth; dx++)
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

        bool legal = blocked.Count == 0 && Placement.IsLegal(frame.Buildable, _world.MapWidth, _world.MapHeight, type, tx, ty);
        return new Ghost(_placing, tx, ty, legal, blocked);
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

        if (s.Button.Action == CardAction.Page)
        {
            _cardPage++;
            RefreshCard();
            return;
        }

        if (!s.Enabled)
        {
            Message(s.Detail.Length > 0 ? $"{s.Button.Tooltip}: {s.Detail}" : s.Button.Tooltip + ": not available yet");
            return;
        }

        List<EntityInfo> selected = SelectedInfos();
        string civ = Lead() is { } lead ? _catalog.Type(_frame.Entities[lead].TypeId).Civilization : string.Empty;
        switch (s.Button.Action)
        {
            case CardAction.Move:
                _mode = OrderMode.Move;
                break;
            case CardAction.Attack:
                _mode = OrderMode.Attack;
                break;
            case CardAction.Harvest:
                _mode = OrderMode.Harvest;
                break;
            case CardAction.Patrol:
                _mode = OrderMode.Patrol;
                break;
            case CardAction.Guard:
                _mode = OrderMode.Guard;
                break;
            case CardAction.Repair:
                _mode = OrderMode.Repair;
                break;
            case CardAction.Stop:
                Send([new PlannedCommand(CommandKind.Stop, CommandFlags.None, EntityId.None, 0, 0, 0, 0, selected.Where(e => e.IsMobile && e.Owner == _session.LocalPlayer).Select(e => e.Id).ToList())]);
                break;
            case CardAction.Submenu:
                _menuStack.Push(s.Button.Argument);
                _cardPage = 0;
                break;
            case CardAction.Build:
                int building = _catalog.TypeOfClass(civ, s.Button.Argument);
                if (building >= 0)
                {
                    _placing = (ushort)building;
                }

                break;
            case CardAction.Train:
                int unit = _catalog.TypeOfClass(civ, s.Button.Argument);
                var producers = selected.Where(e => e.IsProducer && e.Owner == _session.LocalPlayer && !e.UnderConstruction).Select(e => e.Id).ToList();
                if (unit >= 0 && producers.Count > 0)
                {
                    var batch = new CommandList();
                    if (right)
                    {
                        batch.Add(CommandKind.Cancel, producers.ToArray(), arg0: -1, arg1: unit + 1); // one of this unit (ADR 0180)
                    }
                    else
                    {
                        batch.Add(CommandKind.Train, producers.ToArray(), arg0: unit, arg1: Input.IsKeyPressed(Key.Shift) ? 5 : 1);
                    }

                    _session.Submit(batch);
                }

                break;
            case CardAction.Cancel:
                var sites = selected.Where(e => e.UnderConstruction && e.Owner == _session.LocalPlayer).Select(e => e.Id).ToArray();
                if (sites.Length > 0)
                {
                    var batch = new CommandList();
                    batch.Add(CommandKind.Cancel, sites);
                    _session.Submit(batch);
                }

                break;
            case CardAction.Research when _rules.TechnologyId(s.Button.Argument) is >= 0 and var tech:
                Submit(CommandKind.Research, OwnIds(selected, e => e.IsProducer && !e.UnderConstruction), arg0: tech);
                break;
            case CardAction.Upgrade when _rules.UpgradeId(s.Button.Argument) is >= 0 and var upgrade:
                Submit(CommandKind.Upgrade, OwnIds(selected, e => !e.UnderConstruction), arg0: upgrade);
                break;
            case CardAction.Capture:
                _mode = OrderMode.Capture;
                break;
            case CardAction.MoveAdvance:
                _mode = OrderMode.MoveAdvance;
                break;
            case CardAction.MoveSneak:
                _mode = OrderMode.MoveSneak;
                break;
            case CardAction.AttackGround:
                _mode = OrderMode.AttackGround;
                break;
            case CardAction.Stance when int.TryParse(s.Button.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stance):
                Submit(CommandKind.SetStance, OwnIds(selected, e => e.IsArmed), arg0: stance);
                break;
            case CardAction.Gate:
                foreach (EntityInfo gate in selected.Where(e => e.Owner == _session.LocalPlayer))
                {
                    FrameGate state = _indexById.TryGetValue(gate.Id.Value, out int gi) ? _frame.Entities[gi].Gate : FrameGate.None;
                    int mode = s.Button.Argument switch { "open" => 1, "close" => 0, "auto" => 2, _ => (state & FrameGate.Open) != 0 ? 1 : 0 };
                    if (state != FrameGate.None)
                    {
                        Submit(CommandKind.SetGate, [gate.Id], arg0: mode);
                    }
                }

                break;
            case CardAction.Explore:
                Submit(CommandKind.Explore, OwnIds(selected, e => e.IsMobile));
                break;
            case CardAction.Delete:
                DeleteSelected(important: false);
                break;
            case CardAction.Unload:
                foreach (EntityInfo holder in selected.Where(e => e.Owner == _session.LocalPlayer))
                {
                    if (_indexById.TryGetValue(holder.Id.Value, out int hi))
                    {
                        Submit(CommandKind.Unload, [holder.Id], _frame.Entities[hi].X, _frame.Entities[hi].Y);
                    }
                }

                break;
            case CardAction.Become when _catalog.TypeOfClass(civ, s.Button.Argument) is >= 0 and var into:
                Submit(CommandKind.ConvertUnit, OwnIds(selected, e => e.IsMobile), arg0: into);
                break;
            case CardAction.NextIdle:
                SelectNextIdlePeasant();
                break;
            case CardAction.Quests when _session.Scenario is not null:
                ShowQuests();
                break;
            case CardAction.Quests:
                Message(_catalog.Text("sp_setupmap", "vcond_0", "Standard") + ": destroy every enemy unit and building.");
                break;
            case CardAction.Spell when SpellOf(s.Button) is >= 0 and var spell:
                if (_rules.Spells[spell].Target == SpellTarget.Self)
                {
                    Submit(CommandKind.CastSpell, OwnIds(selected, e => CanCast(e, spell)), arg0: spell);
                }
                else
                {
                    _mode = OrderMode.Spell;
                    _modeArgument = spell;
                }

                break;
            case CardAction.Loan when s.Button.Argument == "return":
                Submit(CommandKind.Loan, OwnIds(selected, e => !e.IsMobile && !e.UnderConstruction), arg1: 1);
                break;
            case CardAction.Loan when _rules.ResourceId(s.Button.Argument) is >= 0 and var resource:
                Submit(CommandKind.Loan, OwnIds(selected, e => !e.IsMobile && !e.UnderConstruction), arg0: resource);
                break;
            case CardAction.Bribe:
                _mode = OrderMode.Bribe;
                break;
            case CardAction.Crusade:
                _mode = OrderMode.Crusade;
                break;
            case CardAction.PickItem:
                _mode = OrderMode.PickItem;
                break;
            case CardAction.UseItem when s.Button.Argument == "self":
                foreach (EntityInfo user in selected.Where(e => e.Owner == _session.LocalPlayer && e.IsMobile))
                {
                    Submit(CommandKind.UseItem, [user.Id], arg0: -1, target: user.Id);
                }

                break;
            case CardAction.UseItem:
                _mode = OrderMode.UseItem;
                break;
            case CardAction.DropItem:
                Submit(CommandKind.DropItem, OwnIds(selected, e => e.IsMobile), arg0: 0);
                break;
            case CardAction.Hire when int.TryParse(s.Button.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int offer):
                Submit(CommandKind.Hire, Taverns(), arg0: offer);
                break;
            case CardAction.Gamble:
                var gamble = new GambleDialog(_ui, _rules.World.MaxGambleStake, _rules.World.GambleOdds.Length);
                gamble.Bet += (resource, odds, stake) => Submit(CommandKind.Gamble, Taverns(), arg0: _rules.ResourceId(resource) | (odds << 8), arg1: stake);
                OpenDialog(gamble);
                break;
            case CardAction.Exchange:
                var exchange = new ExchangeDialog(_ui, _rules.World.ConvertRatio);
                exchange.Exchange += (give, get, amount) => Submit(CommandKind.Convert, Taverns(), arg0: _rules.ResourceId(give) | (_rules.ResourceId(get) << 8), arg1: amount);
                OpenDialog(exchange);
                break;
            case CardAction.Trade:
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

    private EntityId[] Taverns() => OwnIds(SelectedInfos(), e => !e.IsMobile && !e.UnderConstruction && _rules.EntityTypes[e.TypeId].Traits.World.IsTavern);

    private EntityId[] Traders() => OwnIds(SelectedInfos(), e => e.IsMobile && _rules.EntityTypes[e.TypeId].Traits.World.Trader is not null);

    // The far market of a trade route: a market or harbor under the pointer. The near one is the own market (of the
    // traders' kind: land or water) nearest to the first trader.
    private void PickMarket(Vector2 screen, (int Goods, int Load) route)
    {
        EntityId[] traders = Traders();
        if (traders.Length == 0 || _frame is null || !_indexById.TryGetValue(traders[0].Value, out int first))
        {
            _route = null;
            return;
        }

        Content.Compiled.DropOffAccess access = _rules.EntityTypes[_frame.Entities[first].TypeId].Traits.World.Trader!.Access;
        if (PickAt(screen) is not { } far || (_rules.EntityTypes[far.TypeId].Traits.World.TradeDock & access) == 0 || far.UnderConstruction)
        {
            Message("Trade: that is not a market of this trader's kind.");
            return;
        }

        FrameEntity t = _frame.Entities[first];
        EntityId near = EntityId.None;
        long best = long.MaxValue;
        foreach (FrameEntity m in _frame.Entities)
        {
            if (m.Owner != _session.LocalPlayer || m.Id == far.Id || m.ConstructionPermille >= 0 || (_rules.EntityTypes[m.TypeId].Traits.World.TradeDock & access) == 0)
            {
                continue;
            }

            long d = ((long)(m.X - t.X) * (m.X - t.X)) + ((long)(m.Y - t.Y) * (m.Y - t.Y));
            if (d < best)
            {
                best = d;
                near = m.Id;
            }
        }

        _route = null;
        if (near.IsNone)
        {
            Message("Trade: no own market to load at.");
            return;
        }

        (int x, int y) = _indexById.TryGetValue(far.Id.Value, out int fi) ? (_frame.Entities[fi].X, _frame.Entities[fi].Y) : ToWorld(screen);
        Submit(CommandKind.Trade, traders, x, y, arg0: route.Goods, arg1: route.Load, target: near);
    }

    // The spell of a card button: its key, or (the ninjas' cloak and murder, raise dead) its order's name.
    private int SpellOf(CardButton b) => _rules.SpellId(b.Argument) is >= 0 and var id ? id : _rules.SpellId(b.Order);

    private bool CanCast(EntityInfo e, int spell) => spell is >= 0 and <= ushort.MaxValue && _rules.EntityTypes[e.TypeId].Traits.CanCast((ushort)spell);

    private EntityId[] OwnIds(List<EntityInfo> selected, Func<EntityInfo, bool> which)
        => selected.Where(e => e.Owner == _session.LocalPlayer && which(e)).Select(e => e.Id).ToArray();

    private void Submit(CommandKind kind, EntityId[] entities, int x = 0, int y = 0, int arg0 = 0, int arg1 = 0, EntityId target = default)
    {
        if (entities.Length == 0)
        {
            return;
        }

        var batch = new CommandList();
        batch.Add(kind, entities, x, y, target: target, arg0: arg0, arg1: arg1);
        _session.Submit(batch);
    }

    // The idle peasant after the current selection (the original's "next idle peasant" button and key).
    private void SelectNextIdlePeasant()
    {
        if (_frame is null)
        {
            return;
        }

        ReadOnlySpan<FrameEntity> all = _frame.Entities;
        int start = Lead() is { } lead ? lead + 1 : 0;
        for (int k = 0; k < all.Length; k++)
        {
            FrameEntity e = all[(start + k) % all.Length];
            if (e.Owner == _session.LocalPlayer && e.Activity == UnitActivity.Idle && _rules.EntityTypes[e.TypeId].Traits.Harvester is not null)
            {
                _selection.SelectOwn([e.Id]);
                CenterOn(e.X, e.Y);
                return;
            }
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

        var card = new List<CardSlot?>();
        _wholeCard.Clear();
        if (_placing == ushort.MaxValue && _selection.IsOwn && Lead() is { } lead)
        {
            FrameEntity e = _frame.Entities[lead];
            TypeVisual v = _catalog.Type(e.TypeId);
            if (e.ConstructionPermille >= 0)
            {
                card.Add(new CardSlot(new CardButton("cancel", 'C', CardAction.Cancel, string.Empty, 109, "Cancel construction"), true, false));
            }
            else
            {
                string? menu = _menuStack.Count > 0 ? _menuStack.Peek() : v.Menu;
                var buttons = (TavernMenu(menu) is { } heroes ? HireButtons(heroes) : _catalog.Card(menu)).Where(b => !Hidden(b, e)).ToList();
                if (buttons.Count > HudLayout.CardSlots)
                {
                    // What this build cannot do yet goes to the last page.
                    buttons = buttons.Where(b => b.Action != CardAction.Unsupported).Concat(buttons.Where(b => b.Action == CardAction.Unsupported)).ToList();
                }

                foreach (CardButton b in buttons)
                {
                    (bool enabled, string detail) = Availability(b, v.Civilization);
                    _wholeCard.Add(new CardSlot(b, enabled, Active(b), detail));
                }

                // More orders than slots (a peasant has nine): pages of seven and a page button; hotkeys reach every page.
                if (_wholeCard.Count <= HudLayout.CardSlots)
                {
                    card.AddRange(_wholeCard.Select(c => (CardSlot?)c));
                }
                else
                {
                    const int perPage = HudLayout.CardSlots - 1;
                    int pages = (_wholeCard.Count + perPage - 1) / perPage;
                    int page = _cardPage % pages;
                    card.AddRange(_wholeCard.Skip(page * perPage).Take(perPage).Select(c => (CardSlot?)c));
                    while (card.Count < perPage)
                    {
                        card.Add(null);
                    }

                    var more = new CardButton("page", '\0', CardAction.Page, $"{page + 1}/{pages}", -1, "More orders");
                    card.Add(new CardSlot(more, true, false, $"page {page + 1} of {pages}"));
                }
            }
        }

        Card = card;
        _hud.SetCard(card);
    }

    // The original's tavern menus are empty in MENU.INI: the game lists the period's offers there. True: the heroes' menu.
    private static bool? TavernMenu(string? menu) => menu switch
    {
        _ when string.Equals(menu, "tavern_merc", StringComparison.OrdinalIgnoreCase) => false,
        _ when string.Equals(menu, "tavern_hero", StringComparison.OrdinalIgnoreCase) => true,
        _ => null,
    };

    private List<CardButton> HireButtons(bool heroes)
    {
        var buttons = new List<CardButton>();
        if (_frame is null)
        {
            return buttons;
        }

        foreach (FrameTavernOffer o in _frame.TavernOffers)
        {
            if (o.IsHero != heroes)
            {
                continue;
            }

            TypeVisual v = _catalog.Type(o.TypeId);
            string what = o.IsHero ? $"Hire a hero: {v.Name} (level {o.Level})" : $"Hire {o.Count} x {v.Name} (level {o.Level})";
            char key = (char)('1' + buttons.Count);
            buttons.Add(new CardButton(o.IsHero ? "hire_hero" : "hire_merc", key, CardAction.Hire, o.Slot.ToString(CultureInfo.InvariantCulture), _catalog.TrainImage(v.Class), what));
        }

        return buttons;
    }

    private bool Active(CardButton b) => b.Action switch
    {
        CardAction.Move => _mode == OrderMode.Move,
        CardAction.Attack => _mode == OrderMode.Attack,
        CardAction.Harvest => _mode == OrderMode.Harvest,
        CardAction.Patrol => _mode == OrderMode.Patrol,
        CardAction.Guard => _mode == OrderMode.Guard,
        CardAction.Repair => _mode == OrderMode.Repair,
        CardAction.Capture => _mode == OrderMode.Capture,
        CardAction.MoveAdvance => _mode == OrderMode.MoveAdvance,
        CardAction.MoveSneak => _mode == OrderMode.MoveSneak,
        CardAction.AttackGround => _mode == OrderMode.AttackGround,
        CardAction.Spell => _mode == OrderMode.Spell && _modeArgument == SpellOf(b),
        CardAction.Bribe => _mode == OrderMode.Bribe,
        CardAction.Crusade => _mode == OrderMode.Crusade,
        CardAction.PickItem => _mode == OrderMode.PickItem,
        CardAction.UseItem => _mode == OrderMode.UseItem,
        CardAction.Stance => Lead() is { } l && _frame is not null && b.Argument == _frame.Entities[l].Stance.ToString(CultureInfo.InvariantCulture),
        CardAction.Gate => Lead() is { } g && _frame is not null && GateButtonOn(b.Argument, _frame.Entities[g].Gate),
        _ => false,
    };

    private static bool GateButtonOn(string argument, FrameGate gate) => argument switch
    {
        "open" => (gate & (FrameGate.Open | FrameGate.Automatic)) == FrameGate.Open,
        "close" => gate != FrameGate.None && (gate & (FrameGate.Open | FrameGate.Automatic)) == 0,
        "auto" => (gate & FrameGate.Automatic) != 0,
        _ => gate != FrameGate.None && (gate & FrameGate.Automatic) == 0,
    };

    // Researched technologies and upgrades the entity has (or does not take) leave the card, as in the original, and so
    // do orders a technology unlocks (TECHTREE [orders]: bribe, spells, loans...) until it is researched.
    private bool Hidden(CardButton b, in FrameEntity lead)
    {
        if (_frame is null)
        {
            return false;
        }

        int gate = _rules.OrderTechnology(b.Order, _catalog.Type(lead.TypeId).Civilization);
        if (gate >= 0 && !Me().HasTechnology(gate))
        {
            return true;
        }

        switch (b.Action)
        {
            case CardAction.Research:
                int tech = _rules.TechnologyId(b.Argument);
                return tech >= 0 && _frame.Players.Length > _session.LocalPlayer && _frame.Players[_session.LocalPlayer].HasTechnology(tech);
            case CardAction.Upgrade:
                int up = _rules.UpgradeId(b.Argument);
                if (up < 0)
                {
                    return false;
                }

                UpgradeDefinition u = _rules.Upgrades[up];
                return (lead.Upgrades & (1u << up)) != 0 || (u.IsConversion && u.ConvertedType(lead.TypeId) < 0);
            default:
                return false;
        }
    }

    // Whether a card button works now and what its tooltip adds: the cost of what it makes, and why it is greyed out
    // (missing building or technology, too few resources).
    private (bool Enabled, string Detail) Availability(CardButton b, string civ)
    {
        List<EntityInfo> selected = SelectedInfos();
        byte me = _session.LocalPlayer;
        switch (b.Action)
        {
            case CardAction.Unsupported:
                return (false, "not in this build yet");
            case CardAction.Spell:
                int spellId = SpellOf(b);
                if (spellId < 0)
                {
                    return (false, "not in this build yet");
                }

                int manaCost = _rules.Spells[spellId].ManaCost;
                string mana = manaCost > 0 ? string.Create(CultureInfo.InvariantCulture, $"mana {manaCost}") : string.Empty;
                return selected.Any(e => e.Owner == me && CanCast(e, spellId)) ? (true, mana) : (false, "needs a caster");
            case CardAction.Loan or CardAction.Crusade:
                return selected.Any(e => e.Owner == me && !e.IsMobile && !e.UnderConstruction) ? (true, string.Empty) : (false, "needs the building");
            case CardAction.Bribe:
                return selected.Any(e => e.Owner == me && e.CanBuild) ? (true, string.Empty) : (false, "needs a peasant");
            case CardAction.PickItem or CardAction.UseItem or CardAction.DropItem:
                return selected.Any(e => e.Owner == me && e.IsMobile) ? (true, string.Empty) : (false, "needs a unit");
            case CardAction.Gamble or CardAction.Exchange:
                return selected.Any(e => e.Owner == me && !e.UnderConstruction && _rules.EntityTypes[e.TypeId].Traits.World.IsTavern) ? (true, string.Empty) : (false, "needs a tavern");
            case CardAction.Hire:
                if (_frame is null || !int.TryParse(b.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
                {
                    return (false, string.Empty);
                }

                for (int i = 0; i < _frame.TavernOfferCount; i++)
                {
                    FrameTavernOffer o = _frame.TavernOffers[i];
                    if (o.Slot != slot)
                    {
                        continue;
                    }

                    int stride = _frame.TavernCostStride;
                    ImmutableArray<int> price = ImmutableArray.Create(_frame.TavernCosts, i * stride, stride);
                    string priceText = CostText(price);
                    if (o.Taken)
                    {
                        return (false, "hired this period");
                    }

                    string hireTech = o.IsHero ? "te_heroesquest" : "te_mercenaries";
                    if (_rules.TechnologyId(hireTech) is >= 0 and var hireGate && !Me().HasTechnology(hireGate))
                    {
                        return (false, o.IsHero ? "needs Heroes' Quest" : "needs Mercenaries");
                    }

                    string? shortOf = Shortfall(price);
                    return shortOf is null ? (true, priceText) : (false, $"{priceText}; not enough {shortOf}");
                }

                return (false, "no offer now");
            case CardAction.Trade:
                return selected.Any(e => e.Owner == me && _rules.EntityTypes[e.TypeId].Traits.World.Trader is not null) ? (true, string.Empty) : (false, "needs a trader");
            case CardAction.Repair:
                return selected.Any(e => e.Owner == me && e.CanBuild) ? (true, string.Empty) : (false, "needs a builder");
            case CardAction.Capture:
                return selected.Any(e => e.Owner == me && _rules.EntityTypes[e.TypeId].Traits.IsCapturer) ? (true, string.Empty) : (false, "needs a peasant");
            case CardAction.Train or CardAction.Build or CardAction.Become:
                int type = _catalog.TypeOfClass(civ, b.Argument);
                if (type < 0)
                {
                    return (false, "not available to this kingdom");
                }

                ImmutableArray<int> typeCost = _rules.EntityTypes[type].Traits.Cost;
                string cost = CostText(typeCost);
                if (MissingRequirement(type, civ) is { } missing)
                {
                    return (false, Join(cost, "needs " + missing));
                }

                return Shortfall(typeCost) is { } shortfall ? (false, Join(cost, "not enough " + shortfall)) : (true, cost);
            case CardAction.Research:
                int tech = _rules.TechnologyId(b.Argument);
                if (tech < 0)
                {
                    return (false, "not in this ruleset");
                }

                TechnologyDefinition t = _rules.Technologies[tech];
                string researchCost = Join(CostText(t.Cost), $"{t.TimeMs / 1000} s");
                if (Me().IsResearching(tech))
                {
                    return (false, Join(researchCost, "being researched"));
                }

                if (_frame!.Researchable.Length > tech && _frame.Researchable[tech] == 0)
                {
                    return (false, Join(researchCost, "needs " + (MissingIn(t.Requirements, civ) ?? "requirements")));
                }

                return Shortfall(t.Cost) is { } lacking ? (false, Join(researchCost, "not enough " + lacking)) : (true, researchCost);
            case CardAction.Upgrade:
                int up = _rules.UpgradeId(b.Argument);
                if (up < 0 || Lead() is not { } lead)
                {
                    return (false, "not in this ruleset");
                }

                UpgradeDefinition u = _rules.Upgrades[up];
                FrameEntity entity = _frame!.Entities[lead];
                string upgradeCost = Join(CostText(u.Cost), $"{u.TimeMs / 1000} s");
                foreach (ushort before in u.After)
                {
                    if ((entity.Upgrades & (1u << before)) == 0)
                    {
                        return (false, Join(upgradeCost, "needs " + _rules.Upgrades[before].Name));
                    }
                }

                if (MissingIn(u.Requirements, _catalog.Type(entity.TypeId).Civilization) is { } prerequisite)
                {
                    return (false, Join(upgradeCost, "needs " + prerequisite));
                }

                return Shortfall(u.Cost) is { } low ? (false, Join(upgradeCost, "not enough " + low)) : (true, upgradeCost);
            case CardAction.Submenu when TavernMenu(b.Argument) is { } heroes:
                // The tavern's offers show (with their prices) once the technology is there, affordable or not.
                string offersTech = heroes ? "te_heroesquest" : "te_mercenaries";
                if (_rules.TechnologyId(offersTech) is >= 0 and var offersGate && !Me().HasTechnology(offersGate))
                {
                    return (false, heroes ? "needs Heroes' Quest" : "needs Mercenaries");
                }

                return HireButtons(heroes).Count > 0 ? (true, string.Empty) : (false, "no offer now");
            case CardAction.Submenu:
                IEnumerable<CardButton> children = _catalog.Card(b.Argument);
                foreach (CardButton child in children)
                {
                    if (child.Action is CardAction.Build or CardAction.Train or CardAction.Submenu && _catalog.TypeOfClass(civ, child.Argument) >= 0)
                    {
                        return (true, string.Empty);
                    }

                    // Any other order that works now (research, loans, hiring...) opens the menu too.
                    if (child.Action is not (CardAction.Build or CardAction.Train or CardAction.Back or CardAction.Unsupported) && Availability(child, civ).Enabled)
                    {
                        return (true, string.Empty);
                    }
                }

                return (false, "nothing to build there yet");
            default:
                return (true, string.Empty);
        }

        static string Join(string first, string second) => first.Length == 0 ? second : second.Length == 0 ? first : $"{first} - {second}";
    }

    private FramePlayer Me() => _frame is not null && _frame.Players.Length > _session.LocalPlayer ? _frame.Players[_session.LocalPlayer] : default;

    // "200 food, 200 stone" (the base cost; technologies that make things cheaper are not shown yet).
    private string CostText(ImmutableArray<int> cost)
    {
        var parts = new List<string>();
        for (int r = 0; r < cost.Length && r < _rules.ResourceTypes.Count; r++)
        {
            if (cost[r] > 0)
            {
                parts.Add($"{cost[r]} {_rules.ResourceTypes[r]}");
            }
        }

        return string.Join(", ", parts);
    }

    // The first resource the player lacks, as "food (150 of 200)", or null when affordable.
    private string? Shortfall(ImmutableArray<int> cost)
    {
        if (_frame is null || _frame.Players.Length <= _session.LocalPlayer)
        {
            return "resources";
        }

        FramePlayer p = _frame.Players[_session.LocalPlayer];
        for (int r = 0; r < cost.Length && r < 8; r++)
        {
            if (p.Resources[r] < cost[r])
            {
                return $"{(r < _rules.ResourceTypes.Count ? _rules.ResourceTypes[r] : "resources")} ({p.Resources[r]} of {cost[r]})";
            }
        }

        return null;
    }

    // What the type still needs (a building or a technology), or null when its requirements are met. The sim's verdict
    // comes with the frame (GameFrame.Makeable); the names come from the type's requirement sets for the civilization.
    private string? MissingRequirement(int type, string civ)
    {
        if (_frame is null)
        {
            return "a game";
        }

        if (_frame.Makeable.Length > type && _frame.Makeable[type] != 0)
        {
            return null;
        }

        EntityTraits t = _rules.EntityTypes[type].Traits;
        if (MissingIn(t.Requirements, civ) is { } missing)
        {
            return missing;
        }

        foreach (ushort building in t.RequiredTypes)
        {
            if (!HasCompleted(building))
            {
                return _catalog.Type(building).Name;
            }
        }

        // Without the sim's verdict (an old frame) the names above are all there is.
        return _frame.Makeable.Length > type ? "requirements" : null;
    }

    // The first unmet prerequisite of the sets for the civilization: a technology or a building, by name.
    private string? MissingIn(ImmutableArray<RequirementSet> sets, string civ)
    {
        FramePlayer me = Me();
        foreach (RequirementSet set in sets)
        {
            if (!set.AppliesTo(civ))
            {
                continue;
            }

            if (set.Unsatisfiable)
            {
                return "something this kingdom cannot have";
            }

            foreach (ushort tech in set.Technologies)
            {
                if (!me.HasTechnology(tech))
                {
                    return "the technology " + _rules.Technologies[tech].Name;
                }
            }

            foreach (ushort building in set.Types)
            {
                if (!HasCompleted(building))
                {
                    return _catalog.Type(building).Name;
                }
            }
        }

        return null;
    }

    private bool HasCompleted(ushort type)
    {
        foreach (FrameEntity e in _frame!.Entities)
        {
            if (e.Owner == _session.LocalPlayer && e.TypeId == type && e.ConstructionPermille < 0)
            {
                return true;
            }
        }

        return false;
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
        string owner = e.Owner < 8 ? SpritePage.PlayerNames[e.Owner] : string.Empty;
        var strip = new List<StripItem>();
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

        if (e.QueueCount > 0)
        {
            // As in the original: what is being made, its progress under it and how many are left on it.
            strip.Add(e.QueueKind switch
            {
                FrameQueueKind.Research when e.QueueHead < _rules.Technologies.Count => new StripItem(_catalog.ButtonImage(_rules.Technologies[e.QueueHead].Key), e.QueuePermille, true),
                FrameQueueKind.Upgrade when e.QueueHead < _rules.Upgrades.Count => new StripItem(_catalog.ButtonImage(_rules.Upgrades[e.QueueHead].Key), e.QueuePermille, true),
                _ => new StripItem(_catalog.Type(e.QueueHead).IconIndex, e.QueuePermille, false, e.QueueCount),
            });
        }

        if (e.ConstructionPermille >= 0)
        {
            stats.Add(("art/icons/time.t2img", $"{e.ConstructionPermille / 10} %"));
        }

        return new SelectionInfo
        {
            Name = v.Name,
            Detail = e.MaxHealth > 0 ? $"{e.Health}/{e.MaxHealth} {owner}" : owner,
            Icon = v.IconIndex,
            Health = e.MaxHealth > 0 && t.Category != EntityCategory.Resource ? e.Health / (float)e.MaxHealth : -1,
            Stats = stats,
            Strip = strip,
            Player = e.Owner < 8 ? e.Owner : -1,
        };
    }

    private void UpdateMinimap(GameFrame frame)
    {
        if (_world is null || _minimapImage is null || _minimapTexture is null || _hud is null)
        {
            return;
        }

        int w = _world.MapWidth, h = _world.MapHeight;
        Color[] terrain = _world.Terrain.TileColours;
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
                Rgba32 r = palette.Ramp(e.Owner, 11);
                c = Color.Color8(r.R, r.G, r.B);
            }
            else if (def.Traits.ResourceNode is { } node)
            {
                c = _rules.ResourceTypes[node.Resource] switch { "wood" => new Color(0.05f, 0.3f, 0.05f), "gold" => new Color(0.95f, 0.8f, 0.2f), _ => new Color(0.6f, 0.6f, 0.6f) };
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
            if (_mode != OrderMode.None)
            {
                name = _mode switch
                {
                    OrderMode.Attack => "s_attack",
                    OrderMode.Harvest => "s_harvest",
                    OrderMode.Patrol => "s_patrol",
                    OrderMode.Guard => "s_guard",
                    OrderMode.Repair => "s_repair",
                    OrderMode.AttackGround or OrderMode.MoveAdvance => "s_attack",
                    OrderMode.Capture or OrderMode.Bribe or OrderMode.PickItem => "s_hand",
                    OrderMode.Spell => "s_spell",
                    OrderMode.UseItem => "s_potion",
                    _ => "s_move",
                };
            }
            else if (_selection.IsOwn && PickAt(_mouse) is { } hover)
            {
                name = HoverCursor(hover, SelectedInfos(), name);
            }
        }

        CursorCache.Apply(_ui, name);
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
        string path = ProjectSettings.GlobalizePath($"user://screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
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
            }
        };
        var layer = new CanvasLayer { Layer = 3, Name = "GameMenu" };
        layer.AddChild(menu);
        AddChild(layer);
        menu.Closed += _ => layer.QueueFree();
    }
}
