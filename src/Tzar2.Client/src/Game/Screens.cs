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
    protected ArtScreen(UiAssets ui, Vector2I screen, string background, bool dim = false, Vector2I? missing = null)
    {
        Ui = ui;
        Size = screen;
        MouseFilter = MouseFilterEnum.Stop;
        _backdrop = new ColorRect { Size = screen, Color = dim ? new Color(0, 0, 0, 0.45f) : Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_backdrop);
        ImageTexture? back = ui.Texture(background);
        Vector2I size = back is null ? missing ?? new Vector2I(500, 320) : new Vector2I(back.GetWidth(), back.GetHeight());
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

    /// <summary>The menus' text colours, as ReTzar reads the original: gold (lit, chosen, captions).</summary>
    public static readonly Color Gold = Color.Color8(0xE2, 0xA3, 0x19);

    /// <summary>Dark gold: a button's text at rest, the lists' other rows, descriptions.</summary>
    public static readonly Color DarkGold = Color.Color8(0xA3, 0x7B, 0x1B);

    /// <summary>A disabled button's text.</summary>
    public static readonly Color DisabledGold = Color.Color8(0x5D, 0x49, 0x11);

    /// <summary>
    /// A button from a strip of states side by side (<paramref name="image"/>; state 0 normal, 1 pressed) at a position
    /// relative to the background, with a centred label as the original's menus draw it (ReTzar's reading): Arial 14
    /// bold in dark gold, gold under the pointer and while pressed, the pressed text a pixel down and right, a darker
    /// gold when disabled (the fifth play-test: the buttons did not light up).
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
        if (label.Length > 0 && Ui.Text(label, UiAssets.TitleFont, DarkGold) is { } rest)
        {
            ImageTexture lit = Ui.Text(label, UiAssets.TitleFont, Gold) ?? rest;
            ImageTexture off = Ui.Text(label, UiAssets.TitleFont, DisabledGold) ?? rest;
            var place = new Vector2((size.X - rest.GetWidth()) / 2, (size.Y - rest.GetHeight()) / 2);
            var text = new TextureRect { Texture = rest, Position = place, MouseFilter = MouseFilterEnum.Ignore };
            button.AddChild(text);
            void Show()
            {
                bool down = button.ButtonPressed || button.IsPressed();
                text.Texture = button.Disabled ? off : down || button.IsHovered() ? lit : rest;
                text.Position = place + (down && !button.Disabled ? Vector2.One : Vector2.Zero);
            }

            button.MouseEntered += Show;
            button.MouseExited += Show;
            button.ButtonDown += Show;
            button.ButtonUp += Show;
            button.Draw += Show;
        }

        return button;
    }

    /// <summary>
    /// A small text button at a position relative to the background: Arial 12 bold in dark gold, gold under the pointer;
    /// returns the x where it ends.
    /// </summary>
    protected int Link(Vector2I at, string label, Action pressed)
    {
        ImageTexture? rest = Ui.Text(label, UiAssets.BoldFont, DarkGold);
        if (rest is null)
        {
            return at.X;
        }

        ImageTexture lit = Ui.Text(label, UiAssets.BoldFont, Gold) ?? rest;
        var button = new TextureButton { Position = Origin + at, Size = rest.GetSize(), TextureNormal = rest, TextureHover = lit, TexturePressed = lit, IgnoreTextureSize = true };
        button.Pressed += pressed;
        AddChild(button);
        return at.X + rest.GetWidth();
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
    protected List<string> Wrap(string text, int width) => WrapIn(text, width, UiAssets.SmallFont);

    /// <summary>The text broken into lines of <paramref name="font"/> no wider than <paramref name="width"/> (paragraphs at line breaks).</summary>
    protected List<string> WrapIn(string text, int width, string font)
    {
        var lines = new List<string>();
        foreach (string paragraph in (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = new System.Text.StringBuilder();
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Ui.Measure(next, font) > width)
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

    private readonly System.Collections.Generic.List<(Rect2I Rect, string Key)> _hints = [];
    private readonly Tzar2.Game.Ui.MenuHelp _help = new();
    private Control? _helpLayer;
    private TextureRect? _ball;
    private Control? _hintBox;
    private Rect2I _ballRect;
    private string _ballImage = string.Empty;
    private Func<string, string>? _hintText;

    /// <summary>
    /// The control at <paramref name="rect"/> (relative to the art) explains itself with <c>LOCAL/HELP.INI</c> key
    /// <paramref name="key"/> in hint mode (SHELL-026). Later registrations come first.
    /// </summary>
    protected void Hint(Rect2I rect, string key) => _hints.Insert(0, (rect, key));

    /// <summary>Forgets the hints registered so far (a screen that draws its controls again).</summary>
    protected void ClearHints() => _hints.Clear();

    /// <summary>
    /// The help ball (SHELL-026; ReTzar 0x45c800): <c>COMMON/BTN_HELP_B</c> (57 × 74) at the art's top left, or the small
    /// <c>BTN_HELP_S</c> (50 × 43) at <paramref name="at"/>. A press turns hint mode on (the ball red) or off (green); in
    /// hint mode the screen takes no other press and the control under the pointer shows its hint. Call it last, so that
    /// its layer lies over the screen's controls. <paramref name="text"/> gives a key's text.
    /// </summary>
    protected void HelpBall(Func<string, string> text, Vector2I at = default, bool small = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        _hintText = text;
        _ballImage = small ? "art/screens/common/btn_help_s.t2img" : "art/screens/common/btn_help_b.t2img";
        _ballRect = new Rect2I(at, small ? new Vector2I(50, 43) : new Vector2I(57, 74));
        _ball = new TextureRect { Position = Origin + at, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_ball);
        Area(_ballRect, ToggleHelp);
        _helpLayer = new Control { Position = Origin, Size = ArtSize, MouseFilter = MouseFilterEnum.Ignore, Name = "Help" };
        _helpLayer.GuiInput += HelpInput;
        AddChild(_helpLayer);
        ChildEnteredTree += _ => Callable.From(() =>
        {
            if (_helpLayer is not null && IsInstanceValid(_helpLayer) && _helpLayer.GetIndex() != GetChildCount() - 1)
            {
                MoveChild(_helpLayer, -1);
            }
        }).CallDeferred();
        ShowBall();
    }

    // The ball's frame: the column normal (0) or disabled (1), the row red (0) in hint mode, green (1) otherwise.
    private void ShowBall()
    {
        if (_ball is not null)
        {
            Vector2I size = _ballRect.Size;
            _ball.Texture = Ui.Region(_ballImage, new Rect2I(0, (_help.On ? 0 : 1) * size.Y, size.X, size.Y));
        }
    }

    private void ToggleHelp()
    {
        _help.Toggle();
        ShowBall();
        UpdateHelpLayer();
    }

    // Hint mode takes the presses and the keys (Esc ends it).
    private void UpdateHelpLayer()
    {
        ShowBall();
        if (_helpLayer is not null)
        {
            _helpLayer.MouseFilter = _help.On ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
            _helpLayer.FocusMode = _help.On ? FocusModeEnum.All : FocusModeEnum.None;
            if (_help.On)
            {
                _helpLayer.GrabFocus();
            }
        }

        HideHint();
    }

    private void HelpInput(InputEvent @event)
    {
        if (_helpLayer is null || !_help.On)
        {
            return;
        }

        switch (@event)
        {
            case InputEventKey { Pressed: true, Keycode: Key.Escape }:
                if (_help.Escape())
                {
                    UpdateHelpLayer();
                    _helpLayer.AcceptEvent();
                }

                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press:
                if (_ballRect.HasPoint((Vector2I)press.Position))
                {
                    ToggleHelp();
                }

                _helpLayer.AcceptEvent();
                break;
            case InputEventMouseButton:
                _helpLayer.AcceptEvent();
                break;
            case InputEventMouseMotion motion:
                Vector2I at = (Vector2I)motion.Position;
                HideHint();
                foreach ((Rect2I rect, string key) in _hints)
                {
                    if (rect.HasPoint(at))
                    {
                        ShowHint(_hintText?.Invoke(key) ?? key, at);
                        break;
                    }
                }

                break;
        }
    }

    // The box, black edged on cream with black text in Arial 12 bold (ReTzar: 0x45c6c6), below the pointer
    // (MenuHelp.Box on the screen's size).
    private void ShowHint(string text, Vector2I at)
    {
        if (_helpLayer is null || Ui.Text(text, UiAssets.BoldFont, Colors.Black) is not { } t)
        {
            return;
        }

        Vector2I screen = Origin + at;
        (int x, int y, int w) = Tzar2.Game.Ui.MenuHelp.Box(screen.X, screen.Y, t.GetWidth(), (int)Size.X, (int)Size.Y);
        _hintBox = new ColorRect { Position = new Vector2(x, y) - Origin, Size = new Vector2(w, Tzar2.Game.Ui.MenuHelp.BoxHeight), Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        _hintBox.AddChild(new ColorRect { Position = Vector2.One, Size = new Vector2(w - 2, Tzar2.Game.Ui.MenuHelp.BoxHeight - 2), Color = new Color(1f, 0.953f, 0.82f), MouseFilter = MouseFilterEnum.Ignore });
        _hintBox.AddChild(new TextureRect { Texture = t, Position = new Vector2((w - t.GetWidth()) / 2, (Tzar2.Game.Ui.MenuHelp.BoxHeight - t.GetHeight()) / 2), MouseFilter = MouseFilterEnum.Ignore });
        _helpLayer.AddChild(_hintBox);
    }

    private void HideHint()
    {
        _hintBox?.QueueFree();
        _hintBox = null;
    }
}

/// <summary>
/// The main menu (<c>S_MAIN</c>; SHELL-003): the original's single player, multiplayer, preferences, credits and quit in
/// the art's five slots; the editor and the asset viewer as small links at the bottom left (the owner, 2026-10-24).
/// Campaigns are in single player (the owner, 2026-10-22).
/// </summary>
public partial class MainMenuScreen : ArtScreen
{
    /// <summary>Creates the menu.</summary>
    public MainMenuScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, string? notice = null)
        : base(ui, screen, "art/screens/s_main/back.t2img")
    {
        Notice(notice, new Vector2I(24, 546));
        string Label(string key, string fallback) => catalog?.Text("mainscr", key, fallback) ?? fallback;
        var size = new Vector2I(235, 25);
        const string Image = "art/screens/s_main/btn.t2img";
        // The original's five items in the art's five carved slots (ReTzar: 270, 277 + 60 i); Tzar2's editor and asset
        // viewer as small links at the bottom left, the version at the bottom right as the original's (the owner's
        // answer to UI review question 1, the fifth play-test).
        (string Label, Action Pressed)[] items =
        [
            (Label("single", "SINGLE PLAYER"), () => SinglePlayer?.Invoke()),
            (Label("multi", "MULTIPLAYER"), () => MultiplayerPressed?.Invoke()),
            (ShellTexts.Preferences.In(catalog is null ? null : catalog.Text), () => Settings?.Invoke()),
            (Label("credits", "CREDITS"), () => Credits?.Invoke()),
            (Label("quit", "QUIT"), () => Quit?.Invoke()),
        ];
        for (int i = 0; i < items.Length; i++)
        {
            Button(new Vector2I(270, 277 + (i * 60)), size, Image, items[i].Label, items[i].Pressed);
        }

        int x = Link(new Vector2I(24, 570), "EDITOR", () => Editor?.Invoke());
        Link(new Vector2I(x + 24, 570), "ASSET VIEWER", () => Viewer?.Invoke());
        string version = $"{Tzar2.Game.GameInfo.Codename} {Tzar2.Game.GameInfo.Version.Split('+')[0]}";
        Text(new Vector2I(776 - Ui.Measure(version, UiAssets.BoldFont), 570), version, UiAssets.BoldFont, DarkGold);
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
/// map's size, type, terrain and resources and its five options on the right, PLAY and BACK. Positions, the captions'
/// included, are the original's as ReTzar reads its constructor and its labels.
/// What the screen set is kept in <c>user://random-map.cfg</c>.
/// </summary>
public partial class SkirmishSetupScreen : SetupScreen
{
    private const string Art = "art/screens/s_singlernd/";
    private const string SavePath = "user://random-map.cfg";

    private readonly Tzar2.Game.Ui.RandomMapScreenModel _model;
    private readonly SkirmishOptions _options;
    private readonly PresentationCatalog? _catalog;

    /// <summary>Creates the screen.</summary>
    public SkirmishSetupScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, SkirmishOptions options, System.Collections.Generic.IReadOnlyList<AiScriptOption> scripts, string? notice = null)
        : base(ui, screen, Art + "back.t2img")
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _catalog = catalog;
        _model = new Tzar2.Game.Ui.RandomMapScreenModel(SkirmishOptions.CivilizationsOf(catalog?.Ruleset), scripts, Load());
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
        HelpBall(k => catalog?.Text("HELP", k, k) ?? k); // SHELL-026
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

    /// <inheritdoc/>
    protected override void DrawLayer()
    {
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
            string name = i == 0 ? "Player" : row.Script > 0 ? model.Scripts[row.Script].Name : $"Computer {i}";
            string civ = model.CivilizationOf(row) is { } key ? T($"race_{row.Civilization}", key) : T("race_0", "RANDOM");
            KingdomRow(i, Art + "row back.t2img", row.Colour, Fit(name, 160, UiAssets.SmallFont), civ, row.Team, (field, step) => model.ChangeRow(index, field, step), true);
            if (i > 0)
            {
                // The computer's AI button (COMMON/AI: dim, lit once it has a script of its own); each press the next script.
                Picture(new Vector2I(273, 26 + y), Ui.Region(Common + "ai.t2img", new Rect2I(0, row.Script > 0 ? 20 : 0, 20, 20)));
                Click(new Rect2I(273, 26 + y, 20, 20), () => model.ChangeRow(index, Tzar2.Game.Ui.RandomMapRowField.Script, +1));
            }
        }
    }

    private void Options()
    {
        var model = _model;
        Tzar2.Game.Session.MatchSettings s = model.Settings;
        string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        string[] victories = ["Standard", "Destroy Castles", "Time Limit", "Score Limit"];
        string victory = T($"vcond_{model.VictoryChoice}", victories[model.VictoryChoice]);
        Option(new Vector2I(66, 369), new Vector2I(68, 353), T("t_num_players", "Number of Players") + $" (2 - {model.MostPlayers})", N(model.Players), Tzar2.Game.Ui.RandomMapOption.Players);
        Option(new Vector2I(66, 427), new Vector2I(68, 410), T("t_game_type", "Game Type"), model.IsDeathmatch ? "Deathmatch" : T("g_type_0", "Standard"), Tzar2.Game.Ui.RandomMapOption.GameType);
        Option(new Vector2I(66, 487), new Vector2I(68, 470), T("t_vict_cond", "Victory Condition"), victory, Tzar2.Game.Ui.RandomMapOption.Victory);
        Option(new Vector2I(270, 369), new Vector2I(273, 353), T("t_poplimit", "Population Limit"), N(s.PopulationLimit), Tzar2.Game.Ui.RandomMapOption.PopulationLimit);
        Option(new Vector2I(270, 427), new Vector2I(273, 412), T("t_diff_lvl", "Difficulty Level"), T($"difflvl_{(int)model.Difficulty}", model.Difficulty.ToString()), Tzar2.Game.Ui.RandomMapOption.Difficulty);
        Option(new Vector2I(270, 487), new Vector2I(273, 470), T("t_st_res", "Starting Resources"), T($"resurcs_{s.StartingResources}", N(s.StartingResources)), Tzar2.Game.Ui.RandomMapOption.StartingResources);
        if (model.HasVictoryLimit)
        {
            // The limit's selector (COMMON/SCROLL CTRL) under the victory condition.
            LimitSelector(model.IsScoreLimit ? T("t_score_lim", "Score Limit") : T("t_time_lim", "Time Limit (min.)"), N(model.VictoryLimit),
                step => model.Change(Tzar2.Game.Ui.RandomMapOption.VictoryLimit, step), true);
        }

        // The AI resign flag (AIRESIGN.BMP, 2 x 2 frames of 32: the white flag on top, the swords below; the right
        // column is the lit one, not used here).
        Picture(new Vector2I(471, 427), Ui.Region("art/airesign.t2img", new Rect2I(0, model.IsOn(Tzar2.Game.Ui.RandomMapOption.AiResign) ? 0 : 32, 32, 32)));
        Click(new Rect2I(471, 427, 32, 32), () => model.Change(Tzar2.Game.Ui.RandomMapOption.AiResign, +1));
        Check(new Vector2I(268, 539), new Vector2I(302, 549), T("t_fog", "No Fog"), Tzar2.Game.Ui.RandomMapOption.NoFog);
        Check(new Vector2I(268, 569), new Vector2I(302, 579), T("t_expolre", "Explore"), Tzar2.Game.Ui.RandomMapOption.Explore);

        // Map Size and Map Type sit a pixel above the original's 43 and 98: Arial 12 bold's cell is a pixel taller than
        // the 13 the original gives them above their selectors.
        Option(new Vector2I(565, 56), new Vector2I(568, 42), T("t_map_size", "Map Size"), ShellTexts.MapSize(s.MapSize).In(Lookup), Tzar2.Game.Ui.RandomMapOption.MapSize);
        Option(new Vector2I(565, 111), new Vector2I(568, 97), T("t_map_type", "Map Type"), ShellTexts.MapType(s.MapType).In(Lookup), Tzar2.Game.Ui.RandomMapOption.MapType);
        Option(new Vector2I(565, 169), new Vector2I(568, 155), T("t_terrain", "Terrain"), ShellTexts.Terrain(s.Terrain).In(Lookup), Tzar2.Game.Ui.RandomMapOption.Terrain);
        Option(new Vector2I(565, 228), new Vector2I(568, 214), T("t_map_res", "Map Resources"), T($"m_res_{(int)s.Resources}", s.Resources.ToString()), Tzar2.Game.Ui.RandomMapOption.MapResources);
        Check(new Vector2I(564, 267), new Vector2I(610, 278), T("t_gslocations", "Guarded Town"), Tzar2.Game.Ui.RandomMapOption.GuardedTown);
        Check(new Vector2I(564, 303), new Vector2I(610, 313), T("t_animals", "Animals"), Tzar2.Game.Ui.RandomMapOption.Animals);
        Check(new Vector2I(564, 339), new Vector2I(610, 350), T("t_mitems", "Magic Items"), Tzar2.Game.Ui.RandomMapOption.MagicItems);
        Check(new Vector2I(564, 376), new Vector2I(610, 386), T("t_cliffs", "Cliffs"), Tzar2.Game.Ui.RandomMapOption.Cliffs);
        Check(new Vector2I(564, 411), new Vector2I(610, 423), T("t_gateways", "Gateways"), Tzar2.Game.Ui.RandomMapOption.Gateways);
    }

    private void Option(Vector2I at, Vector2I captionAt, string caption, string value, Tzar2.Game.Ui.RandomMapOption option)
        => Option(at, captionAt, caption, value, step => _model.Change(option, step), true, RandomHint(option));

    private void Check(Vector2I at, Vector2I captionAt, string caption, Tzar2.Game.Ui.RandomMapOption option)
        => Check(at, captionAt, caption, _model.IsOn(option), () => _model.Change(option, +1), true, RandomHint(option));

    // The LOCAL/HELP.INI key of a control of the random map screen (the original's names, ReTzar's reading).
    private static string RandomHint(Tzar2.Game.Ui.RandomMapOption option) => option switch
    {
        Tzar2.Game.Ui.RandomMapOption.Players => "RM_NUMBER_OF_PLAYERS",
        Tzar2.Game.Ui.RandomMapOption.GameType => "RM_GAME_TYPE",
        Tzar2.Game.Ui.RandomMapOption.Victory => "RM_VICTORY_CONDITION",
        Tzar2.Game.Ui.RandomMapOption.PopulationLimit => "RM_POP_LIMIT",
        Tzar2.Game.Ui.RandomMapOption.Difficulty => "RM_DIFFICULTY_LEVEL",
        Tzar2.Game.Ui.RandomMapOption.StartingResources => "RM_STARTING_RESOURCES",
        Tzar2.Game.Ui.RandomMapOption.MapSize => "RM_MAP_SIZE",
        Tzar2.Game.Ui.RandomMapOption.MapType => "RM_MAP_TYPE",
        Tzar2.Game.Ui.RandomMapOption.Terrain => "RM_TERRAIN",
        Tzar2.Game.Ui.RandomMapOption.MapResources => "RM_MAP_RESOURCES",
        Tzar2.Game.Ui.RandomMapOption.GuardedTown => "RM_GUARDED_TOWN",
        Tzar2.Game.Ui.RandomMapOption.Animals => "RM_AMNIMALS",
        Tzar2.Game.Ui.RandomMapOption.MagicItems => "RM_MAGIC_ITEMS",
        Tzar2.Game.Ui.RandomMapOption.Cliffs => "RM_CLIFFS",
        Tzar2.Game.Ui.RandomMapOption.Gateways => "RM_GATEWAYS",
        Tzar2.Game.Ui.RandomMapOption.NoFog => "RM_NO_FOG",
        Tzar2.Game.Ui.RandomMapOption.Explore => "RM_EXPLORE",
        _ => "RM_VALUE",
    };
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

    /// <summary>INSTRUCTIONS (SHELL-021): the online help's hot keys.</summary>
    Instructions,
}

/// <summary>The in-game menu (<c>D_MENU</c>; its labels are baked into the bitmap, UNK-15): instructions, load, save, options, restart, quit, back.</summary>
public partial class GameMenu : ArtScreen
{
    /// <summary>Creates the menu.</summary>
    public GameMenu(UiAssets ui, Vector2I screen)
        : base(ui, screen, "art/screens/d_menu/back.t2img", dim: true)
    {
        var size = new Vector2I(150, 25);
        Area(new Rect2I(new Vector2I(63, 46), size), () => Closed?.Invoke(GameMenuChoice.Instructions)); // ReTzar's place of INSTRUCTIONS
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
