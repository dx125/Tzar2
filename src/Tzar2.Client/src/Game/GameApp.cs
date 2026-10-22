using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Tzar2.Client.Audio;
using Tzar2.Client.Logic;
using Tzar2.Client.Viewer;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Maps;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// The game mode of the client (TDD §25 M3): main menu → skirmish setup → game → results, on a logical screen chosen by
/// the display preset (ADR 0177: Wide fills the window, Authentic is the original's 1024×768 with integer scaling). Also
/// runs the headless game smoke test and the autoplay driver.
/// </summary>
public partial class GameApp : Node
{
    /// <summary>The original's logical screen (LAYOUT1024H; Authentic preset, test drivers).</summary>
    public static readonly Vector2I ClassicScreen = new(1024, 768);

    /// <summary>The logical screen now: the window over the interface scale (Wide) or <see cref="ClassicScreen"/>.</summary>
    public static Vector2I Screen { get; private set; } = ClassicScreen;

    private readonly ContentStore _store;
    private readonly ViewerArgs _args;
    private readonly SkirmishOptions _skirmish = new();
    private readonly GameSettings _settings = new();
    private UiAssets? _ui;
    private Ruleset? _rules;
    private SkirmishAi _ai = SkirmishAi.EngineDefault;
    private PresentationCatalog? _catalog;
    private int _loadings;
    private Vector2I _window;
    private Node? _current;
    private MidiPlayer? _menuMusic;

    /// <summary>Creates the mode over an opened package.</summary>
    public GameApp(ContentStore store, ViewerArgs args)
    {
        _store = store;
        _args = args;
        _skirmish.Players = args.Players;
        _skirmish.TestMapSize = args.MapSize;
        if (Content.MapGen.RandomMapNames.TryParse(args.MapType, out Content.MapGen.RandomMapType type))
        {
            _skirmish.MapType = Array.IndexOf(SkirmishOptions.MapTypes, type);
        }

        if (Content.MapGen.RandomMapNames.TryParse(args.MapSizeName, out Content.MapGen.RandomMapSize size))
        {
            _skirmish.MapSize = Array.IndexOf(SkirmishOptions.MapSizes, size);
        }
        if (args.Seed is { } seed)
        {
            _skirmish.Seed = seed;
        }

        if (Content.MapGen.RandomMapNames.TryParse(args.Terrain, out Content.MapGen.MapTerrainSet set))
        {
            _skirmish.Terrain = Array.IndexOf(SkirmishOptions.TerrainSets, set);
        }
    }

    /// <summary>Raised when the player opens the asset viewer from the main menu.</summary>
    public event Action? ViewerRequested;

    /// <summary>The running game screen, or null.</summary>
    public GameScreen? Game => _current as GameScreen;

    /// <summary>The screen shown now (test drivers; menus are wrapped in a canvas layer).</summary>
    public Node? Current => _current is CanvasLayer layer && layer.GetChildCount() > 0 ? layer.GetChild(0) : _current;

    /// <inheritdoc/>
    public override void _Ready()
    {
        ApplyDisplay(window: !_args.GameSmoke && _args.AutoplayDir is null && !_args.MultiplayerSmoke && _args.ResourceSoak is null);
        _ui = new UiAssets(_store);
        try
        {
            CompiledRuleset compiled = RulesetLoader.Compile(_store.Source);
            _rules = compiled.Ruleset;

            // Computer kingdoms run the original random.ais from the package, else the engine default (ADR 0073).
            _ai = SkirmishAi.Resolve(_store.Source);
            GD.Print(_ai.IsOriginal ? $"AI: the original {_ai.Script} from the package" : "AI: " + _ai.Notice);
            foreach (string warning in compiled.Warnings)
            {
                GD.Print("Ruleset: " + warning);
            }

            _catalog = new PresentationCatalog(_rules, path => _store.Vfs.Exists(path) ? _store.Read(path) : null, _store.Vfs.Enumerate(PresentationCatalog.ObjectsFolder));
        }
        catch (RulesException e)
        {
            GD.PrintErr("The classic ruleset does not compile from this package: " + e.Message);
        }

        if (_args.MultiplayerSmoke)
        {
            ShowMainMenu();
            _ = MultiplayerSmokeAsync();
        }
        else if (_args.GameSmoke || _args.AutoplayDir is not null || _args.Skirmish)
        {
            StartSkirmish(_skirmish);
            if (_args.GameSmoke)
            {
                _ = SmokeAsync();
            }
            else if (_args.AutoplayDir is { } dir)
            {
                _ = new AutoplayDriver(this, dir).RunAsync();
            }
            else if (_args.ResourceSoak is { } soak)
            {
                _ = ResourceSoakAsync(soak);
            }
        }
        else if (_args.Editor)
        {
            ShowEditor(_args.EditorFile);
            if (_args.EditorSmoke)
            {
                _ = EditorSmokeAsync();
            }
        }
        else if (_args.Campaign is { } campaign)
        {
            if (campaign == "play")
            {
                StartMission(DemoCampaign(), null);
            }
            else
            {
                ShowCampaigns();
            }
        }
        else
        {
            ShowMainMenu();
        }
    }

    /// <summary>
    /// Applies the display settings (ADR 0177): with <paramref name="window"/>, the window mode and size first (test
    /// drivers keep their window); then the logical screen and the root's content scale.
    /// </summary>
    public void ApplyDisplay(bool window = true)
    {
        Window root = GetTree().Root;
        root.MinSize = new Vector2I(DisplayMath.MinWidth, DisplayMath.MinHeight);
        if (window)
        {
            Window.ModeEnum mode = _settings.WindowMode == 1 ? Window.ModeEnum.Fullscreen : Window.ModeEnum.Windowed;
            if (root.Mode != mode)
            {
                root.Mode = mode;
            }

            if (mode == Window.ModeEnum.Windowed && _settings.WindowWidth > 0 && _settings.WindowHeight > 0)
            {
                root.Size = new Vector2I(_settings.WindowWidth, _settings.WindowHeight);
            }
        }

        Relayout();
    }

    // Called every frame (_Process in GameApp.Multiplayer): the window was resized or maximised (the root's size signal
    // does not cover every way it changes).
    private void WatchWindow()
    {
        if (DisplayServer.WindowGetSize() != _window)
        {
            Relayout();
        }
    }

    // The logical screen for the window now; screens follow (menus re-centre, the game rebuilds its HUD).
    private void Relayout()
    {
        Window root = GetTree().Root;
        Vector2I window = DisplayServer.WindowGetSize();
        _window = window;
        Vector2I logical = ClassicScreen;
        if (_settings.DisplayPreset == 0)
        {
            int k = DisplayMath.Scale(window.X, window.Y, _settings.InterfaceScale);
            (int w, int h) = DisplayMath.Logical(window.X, window.Y, k);
            logical = new Vector2I(Math.Max(w, DisplayMath.MinWidth), Math.Max(h, DisplayMath.MinHeight));
        }

        root.ContentScaleSize = logical;
        root.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        root.ContentScaleAspect = Window.ContentScaleAspectEnum.Keep;
        root.ContentScaleStretch = Window.ContentScaleStretchEnum.Integer;
        if (logical == Screen)
        {
            return;
        }

        Screen = logical;
        ClientLog.Info($"display: window {window.X}x{window.Y}, logical {logical.X}x{logical.Y}, {(_settings.DisplayPreset == 0 ? "Wide" : "Authentic")}");
        Node? shown = _current is CanvasLayer layer && layer.GetChildCount() > 0 ? layer.GetChild(0) : _current;
        if (shown is ArtScreen art)
        {
            art.Resize(logical);
        }
        else if (shown is GameScreen game)
        {
            game.Relayout();
        }
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        CursorCache.Reset();
        Window root = GetTree().Root;
        root.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
        _ui?.Dispose();
    }

    /// <summary>Shows the main menu.</summary>
    public void ShowMainMenu()
    {
        var menu = new MainMenuScreen(_ui!, _catalog, Screen, _ai.Notice);
        menu.SinglePlayer += ShowSinglePlayer;
        menu.MultiplayerPressed += ShowMultiplayer;
        menu.Credits += () => GD.Print("Credits: see text/local/credits.txt of your package.");
        menu.Viewer += () => ViewerRequested?.Invoke();
        menu.Editor += () => ShowEditor();
        menu.Settings += ShowSettings;
        menu.Quit += () => GetTree().Quit();
        Replace(menu, music: "music/intro.mid");
    }

    /// <summary>Shows SETTINGS (ADR 0177): changes are saved and applied at once.</summary>
    public void ShowSettings()
    {
        var screen = new SettingsScreen(_ui!, _catalog, Screen, _settings);
        screen.Changed += display =>
        {
            if (display)
            {
                ApplyDisplay();
            }

            SoundMixer.SetVolume(SoundMixer.SfxBus, _settings.SoundVolume);
            SoundMixer.SetVolume(SoundMixer.MusicBus, _settings.MusicVolume);
        };
        screen.Closed += ShowMainMenu;
        Replace(screen, music: null);
    }

    /// <summary>Shows the skirmish setup.</summary>
    public void ShowSetup()
    {
        var setup = new SkirmishSetupScreen(_ui!, _catalog, Screen, _skirmish, Preview, _ai.Notice);
        setup.Play += StartSkirmish;
        setup.Cancel += ShowSinglePlayer;
        Replace(setup, music: null);
    }

    /// <summary>A small picture of the map the options generate (setup dialog), or null.</summary>
    public Image? Preview(SkirmishOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_rules is null || Setup(options) is not { } setup)
        {
            return null;
        }

        MapData map;
        try
        {
            map = setup.Map.Create(_rules, setup);
        }
        catch (Exception e) when (e is Content.MapGen.MapGenException or ArgumentException)
        {
            GD.PrintErr("Map preview: " + e.Message);
            return null;
        }

        return MapPreview(map);
    }

    /// <summary>A small picture of <paramref name="map"/>: land and water, trees, mines and the kingdoms' objects.</summary>
    public Image? MapPreview(MapData map, int size = 176)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (_rules is null || map.Width <= 0 || map.Height <= 0)
        {
            return null;
        }

        int scale = Math.Max(1, size / Math.Max(map.Width, map.Height));
        var image = Image.CreateEmpty(map.Width * scale, map.Height * scale, false, Image.Format.Rgba8);
        var water = new Color(0.15f, 0.25f, 0.55f);
        var grass = new Color(0.3f, 0.45f, 0.18f);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                bool wet = _rules.TerrainTypes[map.TerrainAt(x + 1, y + 1)].IsWater && _rules.TerrainTypes[map.TerrainAt(x, y)].IsWater;
                image.FillRect(new Rect2I(x * scale, y * scale, scale, scale), wet ? water : grass);
            }
        }

        foreach (MapObject o in map.Objects)
        {
            Color c = o.TypeKey switch
            {
                _ when o.TypeKey.StartsWith("comm.tree", StringComparison.Ordinal) => new Color(0.08f, 0.22f, 0.06f),
                "comm.res_gold" => new Color(0.95f, 0.8f, 0.2f),
                "comm.res_stone" => new Color(0.65f, 0.65f, 0.65f),
                _ when o.Owner < 8 => PlayerColour(o.Owner),
                _ => grass,
            };
            int dot = o.TypeKey.EndsWith(".castle", StringComparison.Ordinal) ? 6 : o.TypeKey.StartsWith("comm.res", StringComparison.Ordinal) ? 3 : 1;
            image.FillRect(new Rect2I(o.X / MapData.UnitsPerTile * scale, o.Y / MapData.UnitsPerTile * scale, dot * scale, dot * scale), c);
        }

        return image;
    }

    /// <summary>Starts a skirmish: the loading screen at once, the match a frame later.</summary>
    public void StartSkirmish(SkirmishOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ShowLoading(options.TestMapSize > 0 ? "the test map" : Content.MapGen.RandomMapNames.Name(SkirmishOptions.MapSizes[options.MapSize]) + " map");
        _ = AfterFrames(2, () => BuildSkirmish(options, null));
    }

    // A skirmish started with its options, or continued from a save (its description rebuilds the map).
    private void BuildSkirmish(SkirmishOptions? options, SaveGame? resume)
    {
        if (_rules is null)
        {
            ShowMainMenu();
            return;
        }

        GameSession session;
        string terrain;
        if (resume is not null)
        {
            try
            {
                session = resume.ResumeSkirmish(_rules, _ai);
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or Content.MapGen.MapGenException)
            {
                GD.PrintErr("The save cannot be continued: " + e.Message);
                ShowSinglePlayer();
                return;
            }

            terrain = session.Setup.Map is RandomMapSource random ? Content.MapGen.RandomMapNames.SetId(random.Set) : "grass";
        }
        else if (options is null || Setup(options) is not { } setup)
        {
            GD.PrintErr("The ruleset has no castles.");
            return;
        }
        else
        {
            terrain = Content.MapGen.RandomMapNames.SetId(SkirmishOptions.TerrainSets[options.Terrain]);
            try
            {
                session = new GameSession(setup, _rules);
            }
            catch (Exception e) when (e is Content.MapGen.MapGenException or ArgumentException)
            {
                // A random map the rules cannot host: fall back to the skirmish test map.
                GD.PrintErr($"Random map failed ({e.Message}); using the skirmish test map.");
                session = new GameSession(setup with { Map = new SkirmishTestMap(96) }, _rules);
            }
        }

        if (_args.GameSmoke)
        {
            session.SpeedIndex = GameSession.SpeedSteps.Length - 1;
        }

        var screen = new GameScreen(_store, _ui!, session, terrain, _args.SoundFont, _settings) { KeepSpeed = _args.GameSmoke || _args.AutoplayDir is not null || _args.ResourceSoak is not null };
        screen.Finished += (outcome, stats) =>
        {
            SaveReplay(session, "skirmish");
            ShowResults(outcome, stats);
        };
        screen.QuitRequested += () =>
        {
            SaveReplay(session, "skirmish");
            ShowMainMenu();
        };
        screen.RestartRequested += () =>
        {
            if (options is not null)
            {
                StartSkirmish(options);
            }
            else
            {
                LoadSave(resume!);
            }
        };
        screen.Saves = Saves;
        screen.CaptureSave = label => SaveGame.CaptureMatch(session, label);
        screen.LoadRequested += save =>
        {
            session.Stop();
            LoadSave(save);
        };
        Replace(screen, music: null);
    }

    /// <summary>The loading screen (a match is being built).</summary>
    public void ShowLoading(string what) => Replace(new LoadingScreen(_ui!, _catalog, Screen, _loadings++, what), music: null);

    // Runs `then` once `frames` frames have passed, so that the loading screen is on screen before the heavy work starts.
    // Process frames, not drawn ones: the headless dummy renderer never signals a drawn frame (the smoke tests).
    private async System.Threading.Tasks.Task AfterFrames(int frames, Action then)
    {
        for (int i = 0; i <= frames; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        then();
    }

    /// <summary>Shows the results screen (test driver).</summary>
    public void ShowResultsForTest(MatchOutcome outcome, MatchStats stats) => ShowResults(outcome, stats);

    private MatchSetup? Setup(SkirmishOptions options)
    {
        if (_rules is null)
        {
            return null;
        }

        string own = SkirmishOptions.Civilizations[options.Civilization];
        string[] civs = SkirmishOptions.Civilizations.Where(c => _rules.EntityTypeId(c + ".castle") >= 0).ToArray();
        if (civs.Length == 0)
        {
            return null;
        }

        if (Array.IndexOf(civs, own) < 0)
        {
            own = civs[0];
        }

        int count = options.TestMapSize > 0 ? options.Players : Math.Min(options.Players, Content.MapGen.MapGenerator.MaxPlayersFor(SkirmishOptions.MapSizes[options.MapSize]));
        var players = new PlayerSetup[count];
        for (int i = 0; i < players.Length; i++)
        {
            players[i] = i == 0
                ? new PlayerSetup(0, own, ControllerKind.Human)
                : new PlayerSetup((byte)i, civs[(i + options.Civilization) % civs.Length], ControllerKind.Computer, options.Difficulty);
        }

        return new MatchSetup
        {
            Map = MapSource(options),
            Players = players,
            Seed = options.Seed,
            StartingResources = options.StartingResources >= 3 ? -1 : options.StartingResources,
            PopulationLimit = SkirmishOptions.PopulationLimits[options.PopulationLimit],
            NoFog = options.NoFog,
            Explored = options.Explore,
            Ai = _ai,
            AiAssist = true, // the kingdom's card offers AI assistance (ADR 0183; the original only in its lobby)
        };
    }

    private static IMapSource MapSource(SkirmishOptions options)
    {
        if (options.TestMapSize > 0)
        {
            return new SkirmishTestMap(options.TestMapSize);
        }

        Content.MapGen.RandomMapSize size = SkirmishOptions.MapSizes[options.MapSize];
        Content.MapGen.MapResourceLevel resources = options.StartingResources switch
        {
            0 => Content.MapGen.MapResourceLevel.Low,
            2 => Content.MapGen.MapResourceLevel.High,
            _ => Content.MapGen.MapResourceLevel.Normal,
        };
        return new RandomMapSource(SkirmishOptions.MapTypes[options.MapType], SkirmishOptions.TerrainSets[options.Terrain], size, resources);
    }

    private Color PlayerColour(int player)
    {
        Content.Assets.Rgba32 c = _store.Palette.Ramp(player, 11);
        return Color.Color8(c.R, c.G, c.B);
    }

    private void ShowResults(MatchOutcome outcome, MatchStats stats, Action? restart = null, Action? quit = null)
    {
        var results = new ResultsScreen(_ui!, _catalog, Screen, outcome, stats);
        results.Restart += restart ?? (() => StartSkirmish(_skirmish));
        results.Quit += quit ?? ShowMainMenu;
        Replace(results, music: outcome == MatchOutcome.Victory ? "music/win1.mid" : "music/outro.mid");
    }

    private void Replace(Node next, string? music)
    {
        if (_current is not null)
        {
            RemoveChild(_current);
            _current.QueueFree();
        }

        CursorCache.Reset();
        ClientLog.Info("screen: " + next.GetType().Name);
        _current = next;
        if (next is Control control)
        {
            var layer = new CanvasLayer { Layer = 4 };
            layer.AddChild(control);
            _current = layer;
            AddChild(layer);
        }
        else
        {
            AddChild(next);
        }

        PlayMenuMusic(music);
    }

    private void PlayMenuMusic(string? track)
    {
        _menuMusic?.Stop();
        if (track is null || !_store.Vfs.Exists(track) || _args.GameSmoke)
        {
            return;
        }

        string? sf = SoundFontLocator.Choose(System.IO.File.Exists, _args.SoundFont, System.Environment.GetEnvironmentVariable(SoundFontLocator.EnvironmentVariable));
        if (sf is null)
        {
            return;
        }

        if (_menuMusic is null)
        {
            _menuMusic = new MidiPlayer();
            AddChild(_menuMusic);
        }

        try
        {
            if (!_menuMusic.HasSoundFont)
            {
                _menuMusic.LoadSoundFont(sf);
            }

            _menuMusic.Play(_store.Read(track));
        }
        catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or InvalidOperationException)
        {
            GD.PrintErr("Music: " + e.Message);
        }
    }

    private async Task SmokeAsync()
    {
        int exit = 1;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (Game is null && watch.Elapsed.TotalSeconds < 60)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); // the loading screen comes first
            }

            GameScreen game = Game ?? throw new InvalidOperationException("No game screen.");
            watch.Restart();
            while (game.Session.TicksRun < _args.Ticks && watch.Elapsed.TotalSeconds < 120 && game.Session.Fault is null)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (game.Session.Fault is { } fault)
            {
                GD.PrintErr("GAME SMOKE FAILED: " + fault);
            }
            else if (game.Session.TicksRun < _args.Ticks)
            {
                GD.PrintErr($"GAME SMOKE FAILED: only {game.Session.TicksRun} ticks in 120 s");
            }
            else
            {
                GameFrame frame = game.Frame!;
                GD.Print($"GAME SMOKE OK ticks={game.Session.TicksRun} entities={frame.EntityCount} sprites={game.World?.DrawnSprites} card={game.Card.Count}");
                exit = 0;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException)
        {
            GD.PrintErr("GAME SMOKE FAILED: " + e);
        }

        if (_current is not null)
        {
            RemoveChild(_current);
            _current.QueueFree();
            _current = null;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetTree().Quit(exit);
    }
}
