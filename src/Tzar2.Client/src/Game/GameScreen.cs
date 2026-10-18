using System;
using System.Collections.Generic;
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

    private readonly ContentStore _store;
    private readonly UiAssets _ui;
    private readonly GameSession _session;
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
        HudLayout layout = HudLayout.Load(_store.Vfs.Exists("tables/data/layout.ini.json") ? IniTable.Parse(_store.Read("tables/data/layout.ini.json")) : IniTable.Empty);
        string setId = _session.Map.TerrainSet ?? _terrainSetId;
        TerrainSet set = _store.TerrainSets.FirstOrDefault(s => s.Id == setId) ?? _store.TerrainSets.FirstOrDefault()
            ?? throw new InvalidOperationException("The package has no terrain sets.");

        // Sprites of the civilizations in the match (REQ-GFX-05).
        _bank = new SpriteBank(_store);
        var civs = new HashSet<string>(_session.Setup.Players.Select(p => p.Civilization)) { "comm", string.Empty };
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

        _bank.AddTrees(set);
        _bank.Upload();

        TerrainGrid grid = TerrainPresentation.Build(_session.Map, _rules, set);
        Image? light = set.Light is { } l ? _ui.Image(l) : null;
        _world = new WorldRenderer(this, GetViewport().FindWorld2D().Canvas, _rules, _catalog, _bank, set, grid, _store.LoadImage, light);
        _boxItem = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(_boxItem, GetViewport().FindWorld2D().Canvas);
        RenderingServer.CanvasItemSetZIndex(_boxItem, 100);

        var weather = new CanvasLayer { Layer = 1 };
        AddChild(weather);
        weather.AddChild(new WeatherLayer(this, _ui, layout.View));

        var hudLayer = new CanvasLayer { Layer = 2 };
        AddChild(hudLayer);
        _hud = new Hud(_ui, layout, _catalog);
        hudLayer.AddChild(_hud);
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
        _selection.Changed += () =>
        {
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
        if (!_session.IsRunning)
        {
            _session.Start();
        }

        RefreshCard();
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
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
        int alpha = frame.Paused ? 1000 : FrameInterpolator.Alpha((_now - _frameArrived) * 1000.0, interval * _interpolator.TickSpan);
        long timeMs = (long)(_now * 1000);
        float phase = DayPhase(frame);
        _world.Render(frame, fresh, _interpolator, alpha, _pan, layout.View.Position, layout.View.Size, timeMs, _selection.Selected, _session.LocalPlayer, Barred(), Darkness(phase), CurrentGhost(frame));
        DrawBox();
        UpdateHud(frame, phase);
        UpdateCursor();
        _workSoundBudget = Settings.NatureSounds ? 2 : 0;

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
            def.IsMobile);
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
            if (view.Intersects(p.Rect))
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
            if (p.Rect.HasPoint(world) && (best is null || p.FootY >= best.Value.FootY))
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

                if (_mode != OrderMode.None)
                {
                    (int x, int y) = ToWorld(e.Position);
                    Send(OrderResolver.WithMode(_mode, SelectedInfos(), PickAt(e.Position), x, y, mods, _session.LocalPlayer));
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
                        if (box.Intersects(p.Rect))
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
        else if (e.ButtonIndex == MouseButton.Right && e.Pressed)
        {
            if (_placing != ushort.MaxValue || _mode != OrderMode.None)
            {
                _placing = ushort.MaxValue;
                _mode = OrderMode.None;
                RefreshCard();
                return;
            }

            if (!InView(e.Position))
            {
                return;
            }

            (int x, int y) = ToWorld(e.Position);
            IReadOnlyList<PlannedCommand> orders = OrderResolver.RightClick(SelectedInfos(), PickAt(e.Position), x, y, mods, _session.LocalPlayer);
            Send(orders);
        }
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
            for (int i = 0; i < Card.Count; i++)
            {
                if (Card[i] is { } slot && slot.Button.Hotkey == letter)
                {
                    OnCard(i, false);
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
                if (_placing != ushort.MaxValue || _mode != OrderMode.None)
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
        if (EdgeScroll && focused && Input.MouseMode == Input.MouseModeEnum.Visible && _dragStart is null && m.X >= 0 && m.Y >= 0 && m.X < size.X && m.Y < size.Y)
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
            Send(OrderResolver.RightClick(SelectedInfos(), null, x, y, mods, _session.LocalPlayer));
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
        bool legal = Placement.IsLegal(frame.Buildable, _world.MapWidth, _world.MapHeight, type, tx, ty);
        if (legal && frame.Visibility.Length > 0)
        {
            // Not on unexplored ground.
            for (int dy = 0; dy < type.FootprintHeight && legal; dy++)
            {
                for (int dx = 0; dx < type.FootprintWidth && legal; dx++)
                {
                    legal = frame.Visibility[((ty + dy) * _world.MapWidth) + tx + dx] != 0;
                }
            }
        }

        return new Ghost(_placing, tx, ty, legal);
    }

    private void OnCard(int slot, bool right)
    {
        if (slot < 0 || slot >= Card.Count || Card[slot] is not { } s || _frame is null)
        {
            return;
        }

        if (!s.Enabled)
        {
            Message(s.Button.Tooltip + ": not available yet");
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
            case CardAction.Stop:
                Send([new PlannedCommand(CommandKind.Stop, CommandFlags.None, EntityId.None, 0, 0, 0, 0, selected.Where(e => e.IsMobile && e.Owner == _session.LocalPlayer).Select(e => e.Id).ToList())]);
                break;
            case CardAction.Submenu:
                _menuStack.Push(s.Button.Argument);
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
                        batch.Add(CommandKind.Cancel, producers.ToArray(), arg0: -1);
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
        }

        RefreshCard();
    }

    private void RefreshCard()
    {
        if (_hud is null || _frame is null)
        {
            return;
        }

        var card = new List<CardSlot?>();
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
                var buttons = _catalog.Card(menu).ToList();
                if (buttons.Count > HudLayout.CardSlots)
                {
                    buttons = buttons.Where(b => b.Action != CardAction.Unsupported).Concat(buttons.Where(b => b.Action == CardAction.Unsupported)).Take(HudLayout.CardSlots).ToList();
                }

                foreach (CardButton b in buttons)
                {
                    card.Add(new CardSlot(b, Enabled(b, v.Civilization), Active(b)));
                }
            }
        }

        Card = card;
        _hud.SetCard(card);
    }

    private bool Active(CardButton b) => b.Action switch
    {
        CardAction.Move => _mode == OrderMode.Move,
        CardAction.Attack => _mode == OrderMode.Attack,
        CardAction.Harvest => _mode == OrderMode.Harvest,
        CardAction.Patrol => _mode == OrderMode.Patrol,
        CardAction.Guard => _mode == OrderMode.Guard,
        _ => false,
    };

    private bool Enabled(CardButton b, string civ)
    {
        switch (b.Action)
        {
            case CardAction.Unsupported or CardAction.Repair:
                return false;
            case CardAction.Train or CardAction.Build:
                int type = _catalog.TypeOfClass(civ, b.Argument);
                return type >= 0 && Affordable(type) && RequirementsMet(type);
            case CardAction.Submenu:
                foreach (CardButton child in _catalog.Card(b.Argument))
                {
                    if (child.Action is CardAction.Build or CardAction.Train or CardAction.Submenu && _catalog.TypeOfClass(civ, child.Argument) >= 0)
                    {
                        return true;
                    }

                    if (child.Action == CardAction.Submenu && Enabled(child, civ))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return true;
        }
    }

    private bool Affordable(int type)
    {
        if (_frame is null || _frame.Players.Length <= _session.LocalPlayer)
        {
            return false;
        }

        FramePlayer p = _frame.Players[_session.LocalPlayer];
        var cost = _rules.EntityTypes[type].Traits.Cost;
        for (int r = 0; r < cost.Length && r < 8; r++)
        {
            if (p.Resources[r] < cost[r])
            {
                return false;
            }
        }

        return true;
    }

    private bool RequirementsMet(int type)
    {
        EntityTraits t = _rules.EntityTypes[type].Traits;
        if (t.RequiredTechnology.Length > 0)
        {
            return false;
        }

        foreach (ushort required in t.RequiredTypes)
        {
            bool found = false;
            foreach (FrameEntity e in _frame!.Entities)
            {
                if (e.Owner == _session.LocalPlayer && e.TypeId == required && e.ConstructionPermille < 0)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private void HandleEvents(GameFrame frame)
    {
        byte me = _session.LocalPlayer;
        int attackSounds = 0;
        foreach (SimEvent e in _events)
        {
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
        _hud.SetStatus(frame.Paused ? _catalog.Text("viewmsg", "pause", "PAUSED") : frame.SpeedPercent != 100 ? $"Speed {frame.SpeedPercent} %" : string.Empty);
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
        var strip = new List<(int, int)>();
        if (_selection.Selected.Count > 1)
        {
            // Multi-selection shows health only (REQ-UI-01) and the selected units' icons.
            foreach (EntityId id in _selection.Selected)
            {
                if (_indexById.TryGetValue(id.Value, out int i))
                {
                    FrameEntity m = frame.Entities[i];
                    strip.Add((_catalog.Type(m.TypeId).IconIndex, m.MaxHealth > 0 ? m.Health * 1000 / m.MaxHealth : -1));
                }
            }

            return new SelectionInfo
            {
                Name = $"{_catalog.Text("selview", "units", "Units")}: {_selection.Selected.Count}",
                Detail = $"{e.Health}/{e.MaxHealth}",
                Icon = v.IconIndex,
                Health = e.MaxHealth > 0 ? e.Health / (float)e.MaxHealth : -1,
                Strip = strip,
                StripIsHealth = true,
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
            stats.Add(("art/icons/time.t2img", $"{e.QueuePermille / 10} %"));
            for (int q = 0; q < e.QueueCount && q < 1; q++)
            {
                strip.Add((_catalog.Type(e.QueueHead).IconIndex, e.QueuePermille));
            }

            if (e.QueueCount > 1)
            {
                stats[^1] = (stats[^1].Item1, $"{e.QueuePermille / 10} % (+{e.QueueCount - 1})");
            }
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
        string name = "s_hand";
        if (_placing == ushort.MaxValue && InView(_mouse))
        {
            if (_mode != OrderMode.None)
            {
                name = _mode switch { OrderMode.Attack => "s_attack", OrderMode.Harvest => "s_harvest", OrderMode.Patrol => "s_patrol", OrderMode.Guard => "s_guard", _ => "s_move" };
            }
            else if (_selection.IsOwn && PickAt(_mouse) is { } hover)
            {
                name = hover.Owner != _session.LocalPlayer && hover.Owner != 0xFF ? "s_attack" : hover.IsResourceNode ? "s_harvest" : "s_hand";
            }
        }

        CursorCache.Apply(_ui, name);
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
