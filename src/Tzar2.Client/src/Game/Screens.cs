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
}

/// <summary>
/// The random-map setup (<c>S_SINGLERND</c>): six option sliders (kingdom, difficulty, starting resources, population
/// limit, No Fog, Explore), the map rows of the right panel (type, size, terrain, kingdoms) above a preview of the map,
/// PLAY and BACK.
/// </summary>
public partial class SkirmishSetupScreen : ArtScreen
{
    private const int Slots = 10;
    private readonly string[] _civilizations;
    private readonly SkirmishOptions _options;
    private readonly PresentationCatalog? _catalog;
    private readonly TextureRect[] _values = new TextureRect[Slots];
    private readonly Func<SkirmishOptions, Image?>? _preview;
    private readonly TextureRect _map;

    /// <summary>Creates the dialog.</summary>
    public SkirmishSetupScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, SkirmishOptions options, Func<SkirmishOptions, Image?>? preview = null, string? notice = null)
        : base(ui, screen, "art/screens/s_singlernd/back.t2img")
    {
        _civilizations = SkirmishOptions.CivilizationsOf(catalog?.Ruleset);
        Notice(notice, new Vector2I(66, 88), 470); // the left panel, x 66-536 of the art
        _options = options;
        _catalog = catalog;
        _preview = preview;
        _map = new TextureRect { Position = Origin + new Vector2I(566, 272), Size = new Vector2(180, 172), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_map);
        Text(new Vector2I(66, 40), catalog?.Text("singlescr", "random", "RANDOM MAP") ?? "RANDOM MAP", UiAssets.TitleFont);
        string[] titles =
        [
            "Kingdom",
            T("t_diff_lvl", "Difficulty Level"),
            T("t_st_res", "Starting Resources"),
            T("t_poplimit", "Population Limit"),
            T("t_fog", "No Fog"),
            T("t_expolre", "Explore"),
            T("t_map_type", "Map Type"),
            T("t_map_size", "Map Size"),
            T("t_terrain", "Terrain"),
            T("t_num_players", "Number of Players"),
        ];
        int[] rows = [58, 113, 171, 230]; // the right panel's rows of back.bmp, measured on the art
        for (int i = 0; i < Slots; i++)
        {
            int slot = i;
            var at = i < 6 ? new Vector2I(i % 2 == 0 ? 66 : 271, 370 + ((i / 2) * 58)) : new Vector2I(566, rows[i - 6]);
            Text(at - new Vector2I(0, 17), titles[i], UiAssets.SmallFont, new Color(0.85f, 0.85f, 0.75f));
            Area(new Rect2I(at, new Vector2I(27, 27)), () => Change(slot, -1));
            Area(new Rect2I(at + new Vector2I(154, 0), new Vector2I(27, 27)), () => Change(slot, +1));
            _values[i] = Text(at + new Vector2I(30, 6), string.Empty, UiAssets.SmallFont, Colors.White);
        }

        const string Btn = "art/screens/s_singlernd/btn.t2img";
        Button(new Vector2I(555, 499), new Vector2I(210, 27), Btn, T("play", "PLAY"), () => Play?.Invoke(_options));
        Button(new Vector2I(555, 554), new Vector2I(210, 27), Btn, T("back", "BACK"), () => Cancel?.Invoke());
        Refresh();
    }

    /// <summary>PLAY.</summary>
    public event Action<SkirmishOptions>? Play;

    /// <summary>BACK.</summary>
    public event Action? Cancel;

    /// <summary>Changes option <paramref name="slot"/> by one step.</summary>
    public void Change(int slot, int step)
    {
        static int Wrap(int v, int n) => ((v % n) + n) % n;
        switch (slot)
        {
            case 0: _options.Civilization = Wrap(_options.Civilization + step, _civilizations.Length); break;
            case 1: _options.Difficulty = (Difficulty)Wrap((int)_options.Difficulty + step, 3); break;
            case 2: _options.StartingResources = Wrap(_options.StartingResources + step, 4); break;
            case 3: _options.PopulationLimit = Wrap(_options.PopulationLimit + step, SkirmishOptions.PopulationLimits.Length); break;
            case 4: _options.NoFog = !_options.NoFog; break;
            case 5: _options.Explore = !_options.Explore; break;
            case 6: _options.MapType = Wrap(_options.MapType + step, SkirmishOptions.MapTypes.Length); break;
            case 7: _options.MapSize = Wrap(_options.MapSize + step, SkirmishOptions.MapSizes.Length); break;
            case 8: _options.Terrain = Wrap(_options.Terrain + step, SkirmishOptions.TerrainSets.Length); break;
            case 9: _options.Players = 2 + Wrap(_options.Players - 2 + step, 7); break;
        }

        Refresh();
    }

    private string T(string key, string fallback) => _catalog?.Text("sp_setuprnd", key, fallback) ?? fallback;

    private Func<string, string, string, string>? Lookup => _catalog is null ? null : _catalog.Text;

    private void Refresh()
    {
        string[] values =
        [
            T($"race_{_options.Civilization + 1}", _civilizations[Math.Min(_options.Civilization, _civilizations.Length - 1)]),
            T($"difflvl_{(int)_options.Difficulty}", _options.Difficulty.ToString()),
            T($"resurcs_{_options.StartingResources}", _options.StartingResources.ToString(CultureInfo.InvariantCulture)),
            SkirmishOptions.PopulationLimits[_options.PopulationLimit].ToString(CultureInfo.InvariantCulture),
            ShellTexts.YesNo(_options.NoFog).In(Lookup),
            ShellTexts.YesNo(_options.Explore).In(Lookup),
            ShellTexts.MapType(SkirmishOptions.MapTypes[_options.MapType]).In(Lookup),
            ShellTexts.MapSize(SkirmishOptions.MapSizes[_options.MapSize]).In(Lookup),
            ShellTexts.Terrain(SkirmishOptions.TerrainSets[_options.Terrain]).In(Lookup),
            _options.Players.ToString(CultureInfo.InvariantCulture),
        ];
        for (int i = 0; i < Slots; i++)
        {
            _values[i].Texture = Ui.Text(values[i], UiAssets.SmallFont, Colors.White);
        }

        if (_preview?.Invoke(_options) is { } image)
        {
            _map.Texture = ImageTexture.CreateFromImage(image);
        }
    }
}

/// <summary>The end-of-game dialog (<c>D_ENDGAME</c>, <c>WIN.BMP</c>/<c>LOSE.BMP</c>) with the match statistics.</summary>
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
        Button(new Vector2I(233, 161), size, Btn, T("restart", "RESTART"), () => Restart?.Invoke());
        Button(new Vector2I(233, 209), size, Btn, T("quit", "QUIT"), () => Quit?.Invoke());
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
