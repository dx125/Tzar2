using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// Base of the dialogs and menu screens re-created from the imported <c>IMAGES/SCREENS</c> art (REQ-UI-02): a centred
/// background bitmap, bitmap buttons with their pressed state, and text in the original fonts. Positions of the button
/// slots are measured on the user's bitmaps (the ui/layouts YAML of REQ-UI-02 comes with the full dialog set).
/// </summary>
public abstract partial class ArtScreen : Control
{
    /// <summary>Creates a screen of <paramref name="screen"/> size with <paramref name="background"/> centred.</summary>
    protected ArtScreen(UiAssets ui, Vector2I screen, string background, bool dim = false)
    {
        Ui = ui;
        Size = screen;
        MouseFilter = MouseFilterEnum.Stop;
        _backdrop = new ColorRect { Size = screen, Color = dim ? new Color(0, 0, 0, 0.45f) : Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_backdrop);
        ImageTexture? back = ui.Texture(background);
        Vector2I size = back is null ? new Vector2I(500, 320) : new Vector2I(back.GetWidth(), back.GetHeight());
        _artSize = size;
        Origin = (screen - size) / 2;
        if (back is not null)
        {
            AddChild(new TextureRect { Texture = back, Position = Origin, MouseFilter = MouseFilterEnum.Ignore });
        }
        else
        {
            AddChild(new ColorRect { Position = Origin, Size = size, Color = new Color(0.12f, 0.13f, 0.1f), MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    private readonly ColorRect _backdrop;
    private readonly Vector2I _artSize;

    /// <summary>The size of the background art (500 × 320 when it is missing).</summary>
    protected Vector2I ArtSize => _artSize;

    /// <summary>The UI assets.</summary>
    protected UiAssets Ui { get; }

    /// <summary>Top-left of the background on screen.</summary>
    protected Vector2I Origin { get; private set; }

    /// <summary>The logical screen changed (the window was resized, ADR 0177): the backdrop covers it, the art stays centred.</summary>
    public void Resize(Vector2I screen)
    {
        Vector2I origin = (screen - _artSize) / 2;
        Vector2I delta = origin - Origin;
        Size = screen;
        foreach (Node child in GetChildren())
        {
            if (child == _backdrop)
            {
                _backdrop.Size = screen;
            }
            else if (child is Control control)
            {
                control.Position += delta;
            }
        }

        Origin = origin;
    }

    /// <summary>Text at a position relative to the background (centred in <paramref name="width"/> when given).</summary>
    protected TextureRect Text(Vector2I at, string text, string font = UiAssets.BoldFont, Color? colour = null, int width = 0)
    {
        ImageTexture? t = Ui.Text(text, font, colour ?? new Color(1f, 0.92f, 0.65f));
        int dx = width > 0 && t is not null ? (width - t.GetWidth()) / 2 : 0;
        var rect = new TextureRect { Texture = t, Position = Origin + at + new Vector2I(dx, 0), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(rect);
        return rect;
    }

    /// <summary>
    /// A button from a strip of states side by side (<paramref name="image"/>; state 0 normal, 1 pressed) at a position
    /// relative to the background, with a centred label.
    /// </summary>
    protected TextureButton Button(Vector2I at, Vector2I size, string image, string label, Action pressed)
    {
        Texture2D? normal = Ui.Region(image, new Rect2I(0, 0, size.X, size.Y));
        Texture2D? down = Ui.Region(image, new Rect2I(size.X, 0, size.X, size.Y));
        var button = new TextureButton { Position = Origin + at, Size = size, TextureNormal = normal, TexturePressed = down ?? normal, IgnoreTextureSize = true, StretchMode = TextureButton.StretchModeEnum.Keep };
        if (normal is null)
        {
            button.AddChild(new ColorRect { Size = size, Color = new Color(0.25f, 0.25f, 0.2f), MouseFilter = MouseFilterEnum.Ignore });
        }

        button.Pressed += pressed;
        AddChild(button);
        if (label.Length > 0 && Ui.Text(label, UiAssets.BoldFont, new Color(1f, 0.92f, 0.65f)) is { } t)
        {
            button.AddChild(new TextureRect { Texture = t, Position = new Vector2((size.X - t.GetWidth()) / 2, (size.Y - t.GetHeight()) / 2), MouseFilter = MouseFilterEnum.Ignore });
        }

        return button;
    }

    /// <summary>
    /// A warning line (e.g. the AI fell back to the engine default), wrapped to <paramref name="width"/> (default: to the
    /// screen's right edge), so it stays on its panel (UI-L5).
    /// </summary>
    protected void Notice(string? text, Vector2I at, int width = 0)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int max = width > 0 ? width : Math.Max(200, (int)Size.X - Origin.X - at.X - 40);
        var line = new System.Text.StringBuilder();
        int y = 0;
        foreach (string word in text.Split(' '))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Ui.Measure(next) > max)
            {
                Text(at + new Vector2I(0, y), line.ToString(), UiAssets.SmallFont, new Color(1f, 0.6f, 0.4f));
                y += 14;
                line.Clear().Append(word);
            }
            else
            {
                line.Clear().Append(next);
            }
        }

        Text(at + new Vector2I(0, y), line.ToString(), UiAssets.SmallFont, new Color(1f, 0.6f, 0.4f));
    }

    /// <summary>The text cut to the width (with "..."), so nothing spills over the panel frames.</summary>
    protected string Fit(string text, int width, string font)
    {
        if (Ui.Measure(text, font) <= width)
        {
            return text;
        }

        string cut = text;
        while (cut.Length > 1 && Ui.Measure(cut + "...", font) > width)
        {
            cut = cut[..^1];
        }

        return cut + "...";
    }

    /// <summary>The text broken into lines no wider than <paramref name="width"/> (paragraphs at line breaks).</summary>
    protected List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        foreach (string paragraph in (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = new System.Text.StringBuilder();
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Ui.Measure(next) > width)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(next);
                }
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    /// <summary>An invisible click area relative to the background (labels baked into the art, UNK-15).</summary>
    protected Button Area(Rect2I rect, Action pressed)
    {
        var button = new Button { Position = Origin + rect.Position, Size = rect.Size, Flat = true, Modulate = new Color(1, 1, 1, 0.02f) };
        button.Pressed += pressed;
        AddChild(button);
        return button;
    }
}

/// <summary>
/// The main menu (<c>S_MAIN</c>; SHELL-003): one column of equal buttons, the original's single player, multiplayer,
/// preferences (SETTINGS) and credits, then the editor and the asset viewer, then quit. Campaigns are in single player
/// (the owner, 2026-10-22).
/// </summary>
public partial class MainMenuScreen : ArtScreen
{
    /// <summary>Creates the menu.</summary>
    public MainMenuScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, string? notice = null)
        : base(ui, screen, "art/screens/s_main/back.t2img")
    {
        Notice(notice, new Vector2I(8, 566));
        string Label(string key, string fallback) => catalog?.Text("mainscr", key, fallback) ?? fallback;
        var size = new Vector2I(235, 25);
        const string Image = "art/screens/s_main/btn.t2img";
        (string Label, Action Pressed)[] items =
        [
            (Label("single", "SINGLE PLAYER"), () => SinglePlayer?.Invoke()),
            (Label("multi", "MULTIPLAYER"), () => MultiplayerPressed?.Invoke()),
            (ShellTexts.Preferences.In(catalog is null ? null : catalog.Text), () => Settings?.Invoke()),
            (Label("credits", "CREDITS"), () => Credits?.Invoke()),
            ("EDITOR", () => Editor?.Invoke()),
            ("ASSET VIEWER", () => Viewer?.Invoke()),
            (Label("quit", "QUIT"), () => Quit?.Invoke()),
        ];
        for (int i = 0; i < items.Length; i++)
        {
            Button(new Vector2I(270, 215 + (i * 48)), size, Image, items[i].Label, items[i].Pressed);
        }
        Text(new Vector2I(8, 584), $"{Tzar2.Game.GameInfo.Codename} {Tzar2.Game.GameInfo.Version}", UiAssets.SmallFont, new Color(0.7f, 0.7f, 0.6f));
    }

    /// <summary>SINGLE PLAYER.</summary>
    public event Action? SinglePlayer;

    /// <summary>MULTIPLAYER.</summary>
    public event Action? MultiplayerPressed;

    /// <summary>The asset viewer (dev tool, DEC-11).</summary>
    public event Action? Viewer;

    /// <summary>The scenario and campaign editor (TDD §18).</summary>
    public event Action? Editor;

    /// <summary>CREDITS.</summary>
    public event Action? Credits;

    /// <summary>SETTINGS (ADR 0177).</summary>
    public event Action? Settings;

    /// <summary>QUIT.</summary>
    public event Action? Quit;
}

/// <summary>Skirmish settings chosen on the setup screen.</summary>
public sealed class SkirmishOptions
{
    /// <summary>
    /// The civilizations a player may choose: the ruleset's playable ones in its order (ADR 0211; classic: European,
    /// Arabian, Asian, the original's race numbers 1-3), or those three without a ruleset. A fork adds one in its YAML.
    /// </summary>
    public static string[] CivilizationsOf(Tzar2.Content.Compiled.Ruleset? rules)
    {
        string[] playable = rules is null ? [] : [.. System.Linq.Enumerable.Where(rules.Civilizations, rules.IsPlayableCivilization)];
        return playable.Length > 0 ? playable : ["euro", "arab", "asia"];
    }

    /// <summary>Terrain sets offered (random-map terrain sets).</summary>
    public static readonly Content.MapGen.MapTerrainSet[] TerrainSets =
        [Content.MapGen.MapTerrainSet.Grass, Content.MapGen.MapTerrainSet.Summer, Content.MapGen.MapTerrainSet.Desert, Content.MapGen.MapTerrainSet.Winter];

    /// <summary>Random map types offered (Random first).</summary>
    public static readonly Content.MapGen.RandomMapType[] MapTypes = Enum.GetValues<Content.MapGen.RandomMapType>();

    /// <summary>Random map sizes offered.</summary>
    public static readonly Content.MapGen.RandomMapSize[] MapSizes = Enum.GetValues<Content.MapGen.RandomMapSize>();

    /// <summary>Population limits offered.</summary>
    public static readonly int[] PopulationLimits = [50, 100, 150, 200, 300, 500, 1000];

    /// <summary>Own civilization index.</summary>
    public int Civilization { get; set; }

    /// <summary>Computer difficulty.</summary>
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;

    /// <summary>Starting resources (0 low, 1 normal, 2 high, 3 ruleset default).</summary>
    public int StartingResources { get; set; } = 3;

    /// <summary>Index into <see cref="PopulationLimits"/>.</summary>
    public int PopulationLimit { get; set; } = 3;

    /// <summary>Kingdoms (2-8).</summary>
    public int Players { get; set; } = 2;

    /// <summary>Index into <see cref="TerrainSets"/>.</summary>
    public int Terrain { get; set; }

    /// <summary>Test-map side in tiles, or 0 for a random map.</summary>
    public int TestMapSize { get; set; }

    /// <summary>Index into <see cref="MapTypes"/>.</summary>
    public int MapType { get; set; } = 3;

    /// <summary>Index into <see cref="MapSizes"/>.</summary>
    public int MapSize { get; set; } = 1;

    /// <summary>No Fog (GAME-12).</summary>
    public bool NoFog { get; set; }

    /// <summary>Explore: the map starts explored (GAME-12).</summary>
    public bool Explore { get; set; }

    /// <summary>Match seed.</summary>
    public ulong Seed { get; set; } = 0x7A42_0003UL;

    /// <summary>
    /// The match chosen on the random map screen (<see cref="Tzar2.Game.Ui.RandomMapScreenModel.Describe"/>); when set
    /// it is played instead of the options above (those stay for the command line and the test drivers).
    /// </summary>
    public MatchDescriptor? Match { get; set; }
}

/// <summary>
/// The random map screen (<c>S_SINGLERND</c>; SHELL-010, SHELL-011, SHELL-012, AI-001) drawing
/// <see cref="Tzar2.Game.Ui.RandomMapScreenModel"/>: one <c>ROW BACK</c> per kingdom (colour, name, the computer's AI
/// button, civilization, team), the six options of the left panel, the victory limit, No Fog and Explore below them, the
/// map's size, type, terrain and resources and its five options on the right, PLAY and BACK. Positions are the original's
/// as ReTzar reads its constructor; the captions' places are ours (UNVERIFIED until a screenshot of the original).
/// What the screen set is kept in <c>user://random-map.cfg</c>.
/// </summary>
public partial class SkirmishSetupScreen : ArtScreen
{
    private const string Art = "art/screens/s_singlernd/";
    private const string Common = "art/screens/common/";
    private const string SavePath = "user://random-map.cfg";
    private static readonly Color Caption = new(0.85f, 0.85f, 0.75f);

    private readonly Tzar2.Game.Ui.RandomMapScreenModel _model;
    private readonly SkirmishOptions _options;
    private readonly PresentationCatalog? _catalog;
    private readonly Control _layer;

    /// <summary>Creates the screen.</summary>
    public SkirmishSetupScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, SkirmishOptions options, System.Collections.Generic.IReadOnlyList<AiScriptOption> scripts, string? notice = null)
        : base(ui, screen, Art + "back.t2img")
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _catalog = catalog;
        _model = new Tzar2.Game.Ui.RandomMapScreenModel(SkirmishOptions.CivilizationsOf(catalog?.Ruleset), scripts, Load());
        _layer = new Control { MouseFilter = MouseFilterEnum.Ignore, Position = Origin }; // moves with the art (Resize)
        AddChild(_layer);
        Notice(notice, new Vector2I(66, 304), 470); // below the kingdoms' rows
        const string Btn = Art + "btn.t2img";
        Button(new Vector2I(556, 499), new Vector2I(210, 27), Btn, T("play", "PLAY"), () =>
        {
            Save();
            _options.Match = _model.Describe(_options.Seed);
            Play?.Invoke(_options);
        });
        Button(new Vector2I(556, 554), new Vector2I(210, 27), Btn, T("back", "BACK"), () =>
        {
            Save();
            Cancel?.Invoke();
        });
        Refresh();
    }

    /// <summary>PLAY, with <see cref="SkirmishOptions.Match"/> set to the chosen match.</summary>
    public event Action<SkirmishOptions>? Play;

    /// <summary>BACK.</summary>
    public event Action? Cancel;

    /// <summary>The model (test drivers).</summary>
    public Tzar2.Game.Ui.RandomMapScreenModel Model => _model;

    private Func<string, string, string, string>? Lookup => _catalog is null ? null : _catalog.Text;

    private string T(string key, string fallback) => _catalog?.Text("sp_setuprnd", key, fallback) ?? fallback;

    private static byte[] Load()
    {
        var file = new ConfigFile();
        if (file.Load(SavePath) != Error.Ok)
        {
            return [];
        }

        try
        {
            return Convert.FromBase64String((string)file.GetValue("random", "screen", string.Empty));
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private void Save()
    {
        var file = new ConfigFile();
        file.SetValue("random", "screen", Convert.ToBase64String(_model.ToBytes()));
        if (file.Save(SavePath) != Error.Ok)
        {
            GD.PrintErr("The random map settings could not be kept.");
        }
    }

    private void Refresh()
    {
        foreach (Node child in _layer.GetChildren())
        {
            child.QueueFree();
        }

        Rows();
        Options();
    }

    private void Rows()
    {
        var model = _model;
        for (int i = 0; i < model.Rows.Count; i++)
        {
            Tzar2.Game.Ui.RandomMapRow row = model.Rows[i];
            int y = 35 * i, index = i;
            Picture(new Vector2I(62, 20 + y), Ui.Texture(Art + "row back.t2img"));
            Picture(new Vector2I(73, 31 + y), Ui.Region(Common + "color bars.t2img", new Rect2I(0, row.Colour * 13, 13, 13)));
            Click(new Rect2I(73, 31 + y, 13, 13), () => model.ChangeRow(index, Tzar2.Game.Ui.RandomMapRowField.Colour, +1));
            string name = i == 0 ? "Player" : row.Script > 0 ? model.Scripts[row.Script].Name : $"Computer {i}";
            Label(new Vector2I(110, 30 + y), Fit(name, 160, UiAssets.SmallFont), Colors.White);
            if (i > 0)
            {
                // The computer's AI button (COMMON/AI: dim, lit once it has a script of its own); each press the next script.
                Picture(new Vector2I(273, 26 + y), Ui.Region(Common + "ai.t2img", new Rect2I(0, row.Script > 0 ? 20 : 0, 20, 20)));
                Click(new Rect2I(273, 26 + y, 20, 20), () => model.ChangeRow(index, Tzar2.Game.Ui.RandomMapRowField.Script, +1));
            }

            string civ = model.CivilizationOf(row) is { } key ? T($"race_{row.Civilization}", key) : T("race_0", "RANDOM");
            Spin(new Vector2I(311, 23 + y), civ, step => model.ChangeRow(index, Tzar2.Game.Ui.RandomMapRowField.Civilization, step), arrows: true);
            Picture(new Vector2I(511, 31 + y), Ui.Region(Common + "teams.t2img", new Rect2I(0, ((row.Team + 4) % 5) * 13, 13, 13)));
            Click(new Rect2I(511, 31 + y, 13, 13), () => model.ChangeRow(index, Tzar2.Game.Ui.RandomMapRowField.Team, +1));
        }
    }

    private void Options()
    {
        var model = _model;
        Tzar2.Game.Session.MatchSettings s = model.Settings;
        string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        string[] victories = ["Standard", "Destroy Castles", "Time Limit", "Score Limit"];
        string victory = T($"vcond_{model.VictoryChoice}", victories[model.VictoryChoice]);
        Option(new Vector2I(66, 369), T("t_num_players", "Number of Players"), N(model.Players), Tzar2.Game.Ui.RandomMapOption.Players);
        Option(new Vector2I(66, 427), T("t_game_type", "Game Type"), model.IsDeathmatch ? "Deathmatch" : T("g_type_0", "Standard"), Tzar2.Game.Ui.RandomMapOption.GameType);
        Option(new Vector2I(66, 487), T("t_vict_cond", "Victory Condition"), victory, Tzar2.Game.Ui.RandomMapOption.Victory);
        Option(new Vector2I(270, 369), T("t_poplimit", "Population Limit"), N(s.PopulationLimit), Tzar2.Game.Ui.RandomMapOption.PopulationLimit);
        Option(new Vector2I(270, 427), T("t_diff_lvl", "Difficulty Level"), T($"difflvl_{(int)model.Difficulty}", model.Difficulty.ToString()), Tzar2.Game.Ui.RandomMapOption.Difficulty);
        Option(new Vector2I(270, 487), T("t_st_res", "Starting Resources"), T($"resurcs_{s.StartingResources}", N(s.StartingResources)), Tzar2.Game.Ui.RandomMapOption.StartingResources);
        if (model.HasVictoryLimit)
        {
            // The limit's selector (COMMON/SCROLL CTRL) under the victory condition.
            Picture(new Vector2I(66, 538), Ui.Texture(Common + "scroll ctrl.t2img"));
            string caption = model.IsScoreLimit ? T("t_score_lim", "Score Limit") : T("t_time_lim", "Time Limit (min.)");
            Label(new Vector2I(66, 524), caption, Caption);
            Spin(new Vector2I(66, 541), N(model.VictoryLimit), step => model.Change(Tzar2.Game.Ui.RandomMapOption.VictoryLimit, step), arrows: false, width: 191);
        }

        // The AI resign flag (AIRESIGN.BMP, 2 x 2 frames of 32: the white flag on top, the swords below; the right
        // column is the lit one, not used here).
        Picture(new Vector2I(471, 427), Ui.Region("art/airesign.t2img", new Rect2I(0, model.IsOn(Tzar2.Game.Ui.RandomMapOption.AiResign) ? 0 : 32, 32, 32)));
        Click(new Rect2I(471, 427, 32, 32), () => model.Change(Tzar2.Game.Ui.RandomMapOption.AiResign, +1));
        Check(new Vector2I(268, 539), T("t_fog", "No Fog"), Tzar2.Game.Ui.RandomMapOption.NoFog);
        Check(new Vector2I(268, 569), T("t_expolre", "Explore"), Tzar2.Game.Ui.RandomMapOption.Explore);

        Option(new Vector2I(565, 56), T("t_map_size", "Map Size"), ShellTexts.MapSize(s.MapSize).In(Lookup), Tzar2.Game.Ui.RandomMapOption.MapSize);
        Option(new Vector2I(565, 111), T("t_map_type", "Map Type"), ShellTexts.MapType(s.MapType).In(Lookup), Tzar2.Game.Ui.RandomMapOption.MapType);
        Option(new Vector2I(565, 169), T("t_terrain", "Terrain"), ShellTexts.Terrain(s.Terrain).In(Lookup), Tzar2.Game.Ui.RandomMapOption.Terrain);
        Option(new Vector2I(565, 228), T("t_map_res", "Map Resources"), T($"m_res_{(int)s.Resources}", s.Resources.ToString()), Tzar2.Game.Ui.RandomMapOption.MapResources);
        Check(new Vector2I(564, 267), T("t_gslocations", "Guarded Town"), Tzar2.Game.Ui.RandomMapOption.GuardedTown);
        Check(new Vector2I(564, 303), T("t_animals", "Animals"), Tzar2.Game.Ui.RandomMapOption.Animals);
        Check(new Vector2I(564, 339), T("t_mitems", "Magic Items"), Tzar2.Game.Ui.RandomMapOption.MagicItems);
        Check(new Vector2I(564, 376), T("t_cliffs", "Cliffs"), Tzar2.Game.Ui.RandomMapOption.Cliffs);
        Check(new Vector2I(564, 411), T("t_gateways", "Gateways"), Tzar2.Game.Ui.RandomMapOption.Gateways);
    }

    // A baked 180 x 27 spin of the art with its caption above it.
    private void Option(Vector2I at, string caption, string value, Tzar2.Game.Ui.RandomMapOption option)
    {
        Label(at - new Vector2I(0, 17), caption, Caption);
        Spin(at, value, step => _model.Change(option, step), arrows: false);
    }

    // A 30 x 30 check box (COMMON/CHK: the green light on top, the red one below) with its caption to the right.
    private void Check(Vector2I at, string caption, Tzar2.Game.Ui.RandomMapOption option)
    {
        Picture(at, Ui.Region(Common + "chk.t2img", new Rect2I(0, _model.IsOn(option) ? 0 : 31, 31, 31)));
        Click(new Rect2I(at, new Vector2I(30, 30)), () => _model.Change(option, +1)); // the original's 30 x 30 boxes, 30 apart
        Label(at + new Vector2I(36, 8), caption, Caption);
    }

    // A spin: arrows at both ends (drawn from COMMON/BTN LEFT and RIGHT where the art has none), the value between them.
    private void Spin(Vector2I at, string value, Action<int> change, bool arrows, int width = 180)
    {
        const int Arrow = 27;
        if (arrows)
        {
            Picture(at, Ui.Region(Common + "btn left.t2img", new Rect2I(0, 0, Arrow, Arrow)));
            Picture(at + new Vector2I(width - Arrow, 0), Ui.Region(Common + "btn right.t2img", new Rect2I(0, 0, Arrow, Arrow)));
        }

        Click(new Rect2I(at, new Vector2I(Arrow, Arrow)), () => change(-1));
        Click(new Rect2I(at + new Vector2I(width - Arrow, 0), new Vector2I(Arrow, Arrow)), () => change(+1));
        Click(new Rect2I(at + new Vector2I(Arrow, 0), new Vector2I(width - (2 * Arrow), Arrow)), () => change(+1));
        string shown = Fit(value, width - (2 * Arrow) - 6, UiAssets.SmallFont);
        if (Ui.Text(shown, UiAssets.SmallFont, Colors.White) is { } t)
        {
            Picture(at + new Vector2I((width - t.GetWidth()) / 2, (Arrow - t.GetHeight()) / 2), t);
        }
    }

    private void Label(Vector2I at, string text, Color colour)
    {
        if (Ui.Text(text, UiAssets.SmallFont, colour) is { } t)
        {
            Picture(at, t);
        }
    }

    private void Picture(Vector2I at, Texture2D? texture)
    {
        if (texture is not null)
        {
            _layer.AddChild(new TextureRect { Texture = texture, Position = at, MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    private void Click(Rect2I rect, Action pressed)
    {
        var button = new Button { Position = rect.Position, Size = rect.Size, Flat = true, Modulate = new Color(1, 1, 1, 0.02f) };
        button.Pressed += () =>
        {
            pressed();
            Callable.From(Refresh).CallDeferred();
        };
        _layer.AddChild(button);
    }
}

/// <summary>
/// The end-of-game dialog (<c>D_ENDGAME</c>, <c>WIN.BMP</c>/<c>LOSE.BMP</c>) with the match statistics. Its four slots hold
/// the original's <c>endgamedlg</c> choices (UI review UI-A4): ACHIEVEMENTS (<c>stat</c>; greyed until the score screen
/// exists), RESTART, QUIT and LOAD GAME (<c>load</c>). Which slot the original gives each is ours until the owner's
/// screenshot (question 4).
/// </summary>
public partial class ResultsScreen : ArtScreen
{
    /// <summary>Creates the dialog.</summary>
    public ResultsScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, MatchOutcome outcome, MatchStats stats)
        : base(ui, screen, "art/screens/d_endgame/back2.t2img")
    {
        ArgumentNullException.ThrowIfNull(stats);
        bool won = outcome == MatchOutcome.Victory;
        if (ui.Texture(won ? "art/screens/win.t2img" : "art/screens/lose.t2img") is { } picture)
        {
            AddChild(new TextureRect { Texture = picture, Position = Origin + new Vector2I(42, 45), MouseFilter = MouseFilterEnum.Ignore });
        }

        string T(string key, string fallback) => catalog?.Text("endgamedlg", key, fallback) ?? fallback;
        Text(new Vector2I(234, 40), T(won ? "win1" : "lose1", won ? "YOU ARE" : "YOU WERE"), UiAssets.TitleFont, null, 210);
        Text(new Vector2I(234, 64), T(won ? "win2" : "lose2", won ? "VICTORIOUS" : "DEFEATED"), UiAssets.TitleFont, null, 210);
        const string Btn = "art/screens/d_endgame/btn.t2img";
        var size = new Vector2I(210, 27);
        Vector2I Slot(int i) => new(DialogBoxes.EndgameSlot(i).X, DialogBoxes.EndgameSlot(i).Y);
        TextureButton achievements = Button(Slot(0), size, Btn, string.Empty, () => { });
        achievements.Disabled = true;
        Text(Slot(0) + new Vector2I(0, 5), T("stat", "ACHIEVEMENTS"), UiAssets.BoldFont, new Color(0.55f, 0.52f, 0.42f), 210);
        Button(Slot(1), size, Btn, T("restart", "RESTART"), () => Restart?.Invoke());
        Button(Slot(2), size, Btn, T("quit", "QUIT"), () => Quit?.Invoke());
        Button(Slot(3), size, Btn, T("load", "LOAD GAME"), () => LoadGame?.Invoke());
        var lines = new List<string>
        {
            $"Time {stats.Seconds / 60}:{stats.Seconds % 60:00}    Units trained {stats.Trained}    Buildings {stats.Built}",
            $"Resources gathered {stats.Gathered}    Enemies killed {stats.Kills}    Losses {stats.Losses}",
        };
        for (int i = 0; i < lines.Count; i++)
        {
            Text(new Vector2I(52, 318 + (i * 20)), lines[i], UiAssets.SmallFont, Colors.White);
        }
    }

    /// <summary>RESTART.</summary>
    public event Action? Restart;

    /// <summary>QUIT (to the main menu).</summary>
    public event Action? Quit;

    /// <summary>LOAD GAME.</summary>
    public event Action? LoadGame;
}

/// <summary>Choices of the in-game menu.</summary>
public enum GameMenuChoice
{
    /// <summary>Back to the game.</summary>
    Back,

    /// <summary>Options.</summary>
    Options,

    /// <summary>Restart the match.</summary>
    Restart,

    /// <summary>Quit to the main menu.</summary>
    Quit,

    /// <summary>SAVE GAME.</summary>
    Save,

    /// <summary>LOAD GAME.</summary>
    Load,
}

/// <summary>The in-game menu (<c>D_MENU</c>; its labels are baked into the bitmap, UNK-15): load, save, options, restart, quit, back.</summary>
public partial class GameMenu : ArtScreen
{
    /// <summary>Creates the menu.</summary>
    public GameMenu(UiAssets ui, Vector2I screen)
        : base(ui, screen, "art/screens/d_menu/back.t2img", dim: true)
    {
        var size = new Vector2I(150, 25);
        Area(new Rect2I(new Vector2I(63, 86), size), () => Closed?.Invoke(GameMenuChoice.Load));
        Area(new Rect2I(new Vector2I(63, 126), size), () => Closed?.Invoke(GameMenuChoice.Save));
        Area(new Rect2I(new Vector2I(63, 166), size), () => Closed?.Invoke(GameMenuChoice.Options));
        Area(new Rect2I(new Vector2I(63, 206), size), () => Closed?.Invoke(GameMenuChoice.Restart));
        Area(new Rect2I(new Vector2I(63, 246), size), () => Closed?.Invoke(GameMenuChoice.Quit));
        Area(new Rect2I(new Vector2I(63, 326), size), () => Closed?.Invoke(GameMenuChoice.Back));
    }

    /// <summary>Raised with the choice.</summary>
    public event Action<GameMenuChoice>? Closed;

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Closed?.Invoke(GameMenuChoice.Back);
        }
    }
}
