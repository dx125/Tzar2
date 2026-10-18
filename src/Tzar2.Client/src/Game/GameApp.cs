using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Tzar2.Client.Audio;
using Tzar2.Client.Logic;
using Tzar2.Client.Viewer;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Game.Maps;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// The game mode of the client (TDD §25 M3): main menu → skirmish setup → game → results, at the original's logical
/// 1024×768 with integer scaling (REQ-GFX-07 Authentic). Also runs the headless game smoke test and the autoplay driver.
/// </summary>
public partial class GameApp : Node
{
    /// <summary>Logical screen size (LAYOUT1024H).</summary>
    public static readonly Vector2I Screen = new(1024, 768);

    private readonly ContentStore _store;
    private readonly ViewerArgs _args;
    private readonly SkirmishOptions _skirmish = new();
    private UiAssets? _ui;
    private Ruleset? _rules;
    private PresentationCatalog? _catalog;
    private Node? _current;
    private MidiPlayer? _menuMusic;

    /// <summary>Creates the mode over an opened package.</summary>
    public GameApp(ContentStore store, ViewerArgs args)
    {
        _store = store;
        _args = args;
        _skirmish.Players = args.Players;
        _skirmish.MapSize = args.MapSize;
        if (args.Seed is { } seed)
        {
            _skirmish.Seed = seed;
        }

        if (args.Terrain is { } terrain && Array.IndexOf(SkirmishOptions.TerrainSets, terrain) is var t and >= 0)
        {
            _skirmish.Terrain = t;
        }
    }

    /// <summary>Raised when the player opens the asset viewer from the main menu.</summary>
    public event Action? ViewerRequested;

    /// <summary>The running game screen, or null.</summary>
    public GameScreen? Game => _current as GameScreen;

    /// <inheritdoc/>
    public override void _Ready()
    {
        Window root = GetTree().Root;
        root.ContentScaleSize = Screen;
        root.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        root.ContentScaleAspect = Window.ContentScaleAspectEnum.Keep;
        root.ContentScaleStretch = Window.ContentScaleStretchEnum.Integer;
        _ui = new UiAssets(_store);
        try
        {
            CompiledRuleset compiled = RulesetLoader.Compile(_store.Source);
            _rules = compiled.Ruleset;
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

        if (_args.GameSmoke || _args.AutoplayDir is not null || _args.Skirmish)
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
        }
        else
        {
            ShowMainMenu();
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
        var menu = new MainMenuScreen(_ui!, _catalog, Screen);
        menu.SinglePlayer += ShowSetup;
        menu.MultiplayerPressed += () => GD.Print("Multiplayer is M6.");
        menu.Credits += () => GD.Print("Credits: see text/local/credits.txt of your package.");
        menu.Viewer += () => ViewerRequested?.Invoke();
        menu.Quit += () => GetTree().Quit();
        Replace(menu, music: "music/intro.mid");
    }

    /// <summary>Shows the skirmish setup.</summary>
    public void ShowSetup()
    {
        var setup = new SkirmishSetupScreen(_ui!, _catalog, Screen, _skirmish, Preview);
        setup.Play += StartSkirmish;
        setup.Cancel += ShowMainMenu;
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

        MapData map = setup.Map.Create(_rules, setup);
        int scale = Math.Max(1, 176 / Math.Max(map.Width, map.Height));
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
                "comm.tree" => new Color(0.08f, 0.22f, 0.06f),
                "comm.res_gold" => new Color(0.95f, 0.8f, 0.2f),
                "comm.res_stone" => new Color(0.65f, 0.65f, 0.65f),
                _ when o.Owner < 8 => PlayerColour(o.Owner),
                _ => grass,
            };
            int size = o.TypeKey.EndsWith(".castle", StringComparison.Ordinal) ? 6 : o.TypeKey.StartsWith("comm.res", StringComparison.Ordinal) ? 3 : 1;
            image.FillRect(new Rect2I(o.X / MapData.UnitsPerTile * scale, o.Y / MapData.UnitsPerTile * scale, size * scale, size * scale), c);
        }

        return image;
    }

    /// <summary>Starts a skirmish.</summary>
    public void StartSkirmish(SkirmishOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_rules is null)
        {
            ShowMainMenu();
            return;
        }

        if (Setup(options) is not { } setup)
        {
            GD.PrintErr("The ruleset has no castles.");
            return;
        }

        string terrain = SkirmishOptions.TerrainSets[options.Terrain];
        var session = new GameSession(setup, _rules);
        if (_args.GameSmoke)
        {
            session.SpeedIndex = GameSession.SpeedSteps.Length - 1;
        }

        var screen = new GameScreen(_store, _ui!, session, terrain, _args.SoundFont);
        screen.Finished += (outcome, stats) => ShowResults(outcome, stats);
        screen.QuitRequested += ShowMainMenu;
        screen.RestartRequested += () => StartSkirmish(options);
        Replace(screen, music: null);
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

        var players = new PlayerSetup[options.Players];
        for (int i = 0; i < players.Length; i++)
        {
            players[i] = i == 0
                ? new PlayerSetup(0, own, ControllerKind.Human)
                : new PlayerSetup((byte)i, civs[(i + options.Civilization) % civs.Length], ControllerKind.Computer, options.Difficulty);
        }

        return new MatchSetup
        {
            Map = new SkirmishTestMap(options.MapSize),
            Players = players,
            Seed = options.Seed,
            StartingResources = options.StartingResources >= 3 ? -1 : options.StartingResources,
            PopulationLimit = SkirmishOptions.PopulationLimits[options.PopulationLimit],
        };
    }

    private Color PlayerColour(int player)
    {
        Content.Assets.Rgba32 c = _store.Palette.Ramp(player, 11);
        return Color.Color8(c.R, c.G, c.B);
    }

    private void ShowResults(MatchOutcome outcome, MatchStats stats)
    {
        var results = new ResultsScreen(_ui!, _catalog, Screen, outcome, stats);
        results.Restart += () => StartSkirmish(_skirmish);
        results.Quit += ShowMainMenu;
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
            GameScreen game = Game ?? throw new InvalidOperationException("No game screen.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
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
