using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Content.Compiled;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// The single-player menu (<c>S_SINGLEPLAYER</c>): CAMPAIGN, CUSTOM MAP, RANDOM MAP, LOAD GAME and BACK in the five
/// button slots of its art (800 × 600).
/// </summary>
public partial class SinglePlayerScreen : ArtScreen
{
    /// <summary>Creates the menu; LOAD GAME is greyed out when <paramref name="canLoad"/> is false.</summary>
    public SinglePlayerScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, bool canLoad, string? notice = null)
        : base(ui, screen, "art/screens/s_singleplayer/back.t2img")
    {
        Notice(notice, new Vector2I(66, 566));
        string Label(string key, string fallback) => catalog?.Text("singlescr", key, fallback) ?? fallback;
        const string Image = "art/screens/s_singleplayer/btn.t2img";
        var size = new Vector2I(235, 25);
        int[] rows = [142, 234, 330, 426, 522]; // the slots of back.bmp, measured on the art
        Button(new Vector2I(300, rows[0]), size, Image, Label("campaign", "CAMPAIGN"), () => Campaign?.Invoke());
        Button(new Vector2I(300, rows[1]), size, Image, Label("custom", "CUSTOM MAP"), () => CustomMap?.Invoke());
        Button(new Vector2I(300, rows[2]), size, Image, Label("random", "RANDOM MAP"), () => RandomMap?.Invoke());
        TextureButton load = Button(new Vector2I(300, rows[3]), size, Image, Label("load", "LOAD GAME"), () => LoadGame?.Invoke());
        load.Disabled = !canLoad;
        load.Modulate = canLoad ? Colors.White : new Color(1, 1, 1, 0.45f);
        load.TooltipText = canLoad ? string.Empty : "No saved game yet (F2 saves a campaign mission).";
        Button(new Vector2I(300, rows[4]), size, Image, Label("back", "BACK"), () => Back?.Invoke());
    }

    /// <summary>CAMPAIGN.</summary>
    public event Action? Campaign;

    /// <summary>CUSTOM MAP: the original maps.</summary>
    public event Action? CustomMap;

    /// <summary>RANDOM MAP.</summary>
    public event Action? RandomMap;

    /// <summary>LOAD GAME (the last quick save).</summary>
    public event Action? LoadGame;

    /// <summary>BACK.</summary>
    public event Action? Back;
}

/// <summary>A map offered under CUSTOM MAP: one of the package's original maps, converted (ADR 0120).</summary>
/// <param name="Id">The file name without extension.</param>
/// <param name="Title">The map title.</param>
/// <param name="Description">The map description.</param>
/// <param name="Mission">The map and its scenario.</param>
public sealed record CustomMap(string Id, string Title, string Description, MissionContent Mission)
{
    /// <summary>The kingdoms that play: the active ones of the map's kingdom table (not the Rescue one), else every owner on the map.</summary>
    public int Kingdoms
    {
        get
        {
            int n = 0;
            foreach (var k in Mission.Scenario.Kingdoms)
            {
                n += k.Active && k.PlayerType != Content.Compiled.Scenario.PlayerType.Rescue ? 1 : 0;
            }

            if (n > 0)
            {
                return n;
            }

            foreach (MapObject o in Mission.Map.Objects)
            {
                if (o.Owner != MapObject.Nature)
                {
                    n = Math.Max(n, o.Owner + 1);
                }
            }

            return n;
        }
    }
}

/// <summary>
/// The map choice (<c>S_SINGLECHOOSEMAP</c>) laid out on the panels of its art: the maps (top left, scrolled by the
/// mouse wheel), the chosen map's picture (top right) and its description (bottom), BACK and PLAY in the bar's slots.
/// </summary>
public partial class ChooseMapScreen : ArtScreen
{
    // Panels and slots of back.bmp, measured on the art (top-left x, y, width, height).
    private static readonly Rect2I List = new(84, 46, 240, 246);
    private static readonly Rect2I Picture = new(411, 46, 247, 246);
    private static readonly Rect2I About = new(84, 354, 655, 140);
    private static readonly Vector2I BackSlot = new(78, 545);
    private static readonly Vector2I PlaySlot = new(535, 544);
    private const int RowHeight = 18;

    private static readonly Color Gold = new(1f, 0.92f, 0.65f);
    private static readonly Color Grey = new(0.75f, 0.75f, 0.7f);

    private readonly IReadOnlyList<CustomMap> _maps;
    private readonly Func<MapData, Image?> _preview;
    private readonly PresentationCatalog? _catalog;
    private readonly TextureRect _picture;
    private int _selected;
    private int _top;
    private Control? _details;

    /// <summary>Creates the screen with <paramref name="selected"/> chosen.</summary>
    public ChooseMapScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, IReadOnlyList<CustomMap> maps, Func<MapData, Image?> preview, int selected = 0)
        : base(ui, screen, "art/screens/s_singlechoosemap/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(preview);
        _maps = maps;
        _preview = preview;
        _catalog = catalog;
        _picture = new TextureRect { Position = Origin + Picture.Position, Size = Picture.Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_picture);
        const string Btn = "art/screens/s_singlechoosemap/btn.t2img";
        var size = new Vector2I(235, 25);
        Button(BackSlot, size, Btn, Label("back", "BACK"), () => Back?.Invoke());
        Button(PlaySlot, size, Btn, Label("play", "PLAY"), () =>
        {
            if (_maps.Count > 0)
            {
                Play?.Invoke(_maps[_selected]);
            }
        });
        Select(Math.Clamp(selected, 0, Math.Max(0, maps.Count - 1)));
    }

    /// <summary>PLAY: the chosen map (its setup comes next).</summary>
    public event Action<CustomMap>? Play;

    /// <summary>BACK.</summary>
    public event Action? Back;

    /// <summary>The chosen map's index.</summary>
    public int Selected => _selected;

    private static int RowsShown => (List.Size.Y / RowHeight) - 1;

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } wheel && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            int top = Math.Clamp(_top + (wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1), 0, Math.Max(0, _maps.Count - RowsShown));
            if (top != _top)
            {
                _top = top;
                Redraw();
            }

            AcceptEvent();
        }
    }

    /// <summary>Chooses map <paramref name="index"/>.</summary>
    public void Select(int index)
    {
        if (index < 0 || index >= _maps.Count)
        {
            Redraw();
            return;
        }

        _selected = index;
        _top = Math.Clamp(_top, Math.Max(0, index - RowsShown + 1), index);
        _picture.Texture = _preview(_maps[index].Mission.Map) is { } image ? ImageTexture.CreateFromImage(image) : null;
        Redraw();
    }

    private string Label(string key, string fallback) => _catalog?.Text("sp_choosemap", key, fallback) ?? fallback;

    private void Redraw()
    {
        _details?.QueueFree();
        _details = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_details);
        Add(List.Position, Label("t_custommap", "Custom Maps"), UiAssets.BoldFont, Gold);
        if (_maps.Count == 0)
        {
            Add(About.Position, "No maps in this package (the import keeps the install's Maps/*.wmp).", UiAssets.BoldFont, Gold);
            return;
        }

        for (int row = 0; row < RowsShown && _top + row < _maps.Count; row++)
        {
            int index = _top + row;
            Vector2I at = List.Position + new Vector2I(0, (row + 1) * RowHeight);
            Add(at, Fit((index == _selected ? "> " : "  ") + _maps[index].Title, List.Size.X, UiAssets.BoldFont), UiAssets.BoldFont, index == _selected ? Gold : Grey);
            Click(new Rect2I(at, new Vector2I(List.Size.X, RowHeight)), () => Select(index));
        }

        CustomMap map = _maps[_selected];
        MapData m = map.Mission.Map;
        Add(About.Position, Fit($"{Label("t_map", "Map Title")}: {map.Title}", About.Size.X, UiAssets.BoldFont), UiAssets.BoldFont, Gold);
        Add(About.Position + new Vector2I(0, 20), string.Create(CultureInfo.InvariantCulture, $"{m.Width} x {m.Height} tiles, {map.Kingdoms} kingdoms"), UiAssets.SmallFont, Grey);
        List<string> lines = Wrap(map.Description, About.Size.X);
        int max = (About.Size.Y - 40) / 14;
        for (int i = 0; i < lines.Count && i < max; i++)
        {
            string line = i == max - 1 && lines.Count > max ? Fit(lines[i] + "...", About.Size.X, UiAssets.SmallFont) : lines[i];
            Add(About.Position + new Vector2I(0, 40 + (i * 14)), line, UiAssets.SmallFont, Colors.White);
        }
    }

    private void Add(Vector2I at, string text, string font, Color colour)
    {
        if (text.Length > 0 && Ui.Text(text, font, colour) is { } t)
        {
            _details!.AddChild(new TextureRect { Texture = t, Position = Origin + at, MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    private void Click(Rect2I rect, Action pressed)
    {
        var area = new Button { Position = Origin + rect.Position, Size = rect.Size, Flat = true, Modulate = new Color(1, 1, 1, 0.02f) };
        area.Pressed += pressed;
        _details!.AddChild(area);
    }
}

/// <summary>The settings of a custom map chosen on its setup screen (index 0 of every list keeps the map's own).</summary>
public sealed class CustomMapOptions
{
    /// <summary>Own civilization: 0 the map's, then <see cref="SkirmishOptions.Civilizations"/> + 1.</summary>
    public int Civilization { get; set; }

    /// <summary>Computer difficulty.</summary>
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;

    /// <summary>Starting resources: 3 the map's stockpiles, else 0 low, 1 normal, 2 high.</summary>
    public int StartingResources { get; set; } = 3;

    /// <summary>Population limit: 0 the map's, else index + 1 into <see cref="SkirmishOptions.PopulationLimits"/>.</summary>
    public int PopulationLimit { get; set; }

    /// <summary>No Fog: 0 the map's, 1 on, 2 off.</summary>
    public int NoFog { get; set; }

    /// <summary>Explore: 0 the map's, 1 on, 2 off.</summary>
    public int Explore { get; set; }

    /// <summary>The match settings these options ask for.</summary>
    public CustomMapSettings ToSettings(SkirmishAi ai) => new()
    {
        Difficulty = Difficulty,
        Civilization = Civilization > 0 ? SkirmishOptions.Civilizations[Civilization - 1] : null,
        StartingResources = StartingResources is >= 0 and <= 2 ? StartingResources : -1,
        PopulationLimit = PopulationLimit > 0 ? SkirmishOptions.PopulationLimits[PopulationLimit - 1] : -1,
        NoFog = NoFog == 0 ? null : NoFog == 1,
        Explored = Explore == 0 ? null : Explore == 1,
        Ai = ai,
    };
}

/// <summary>
/// The setup of a custom map (<c>S_SINGLESETUP</c>): the map's picture and description, six option sliders (kingdom,
/// difficulty, starting resources, population limit, No Fog, Explore; "Map" keeps the map's setting), PLAY and BACK.
/// </summary>
public partial class MapSetupScreen : ArtScreen
{
    private static readonly Rect2I Description = new(557, 64, 176, 224);

    private readonly CustomMapOptions _options;
    private readonly PresentationCatalog? _catalog;
    private readonly TextureRect[] _values = new TextureRect[6];

    /// <summary>Creates the dialog for <paramref name="map"/>.</summary>
    public MapSetupScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, CustomMap map, CustomMapOptions options, Image? picture, string? notice = null)
        : base(ui, screen, "art/screens/s_singlesetup/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _catalog = catalog;
        Notice(notice, new Vector2I(66, 566));
        Text(new Vector2I(66, 40), Fit(map.Title, 460, UiAssets.TitleFont), UiAssets.TitleFont);
        if (picture is not null)
        {
            AddChild(new TextureRect
            {
                Texture = ImageTexture.CreateFromImage(picture),
                Position = Origin + new Vector2I(66, 70),
                Size = new Vector2(460, 260),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            });
        }

        Text(Description.Position, T("t_map_decr", "Map Description"), UiAssets.BoldFont);
        List<string> lines = Wrap(map.Description, Description.Size.X);
        int max = (Description.Size.Y - 20) / 14;
        for (int i = 0; i < lines.Count && i < max; i++)
        {
            Text(Description.Position + new Vector2I(0, 20 + (i * 14)), i == max - 1 && lines.Count > max ? Fit(lines[i] + "...", Description.Size.X, UiAssets.SmallFont) : lines[i], UiAssets.SmallFont, Colors.White);
        }

        string[] titles =
        [
            "Kingdom",
            T("t_diff_lvl", "Difficulty Level"),
            T("t_st_res", "Starting Resources"),
            T("t_poplimit", "Population Limit"),
            T("t_fog", "No Fog"),
            T("t_expolre", "Explore"),
        ];
        for (int i = 0; i < 6; i++)
        {
            int slot = i;
            var at = new Vector2I(i % 2 == 0 ? 66 : 271, 370 + ((i / 2) * 58));
            Text(at - new Vector2I(0, 18), titles[i], UiAssets.SmallFont, new Color(0.85f, 0.85f, 0.75f));
            Area(new Rect2I(at, new Vector2I(27, 27)), () => Change(slot, -1));
            Area(new Rect2I(at + new Vector2I(154, 0), new Vector2I(27, 27)), () => Change(slot, +1));
            _values[i] = Text(at + new Vector2I(30, 6), string.Empty, UiAssets.SmallFont, Colors.White);
        }

        const string Btn = "art/screens/s_singlesetup/btn.t2img";
        Button(new Vector2I(555, 499), new Vector2I(210, 27), Btn, T("play", "PLAY"), () => Play?.Invoke(_options));
        Button(new Vector2I(555, 554), new Vector2I(210, 27), Btn, T("back", "BACK"), () => Back?.Invoke());
        Refresh();
    }

    /// <summary>PLAY.</summary>
    public event Action<CustomMapOptions>? Play;

    /// <summary>BACK: the map choice.</summary>
    public event Action? Back;

    /// <summary>Changes option <paramref name="slot"/> by one step.</summary>
    public void Change(int slot, int step)
    {
        static int Wrap(int v, int n) => ((v % n) + n) % n;
        switch (slot)
        {
            case 0: _options.Civilization = Wrap(_options.Civilization + step, SkirmishOptions.Civilizations.Length + 1); break;
            case 1: _options.Difficulty = (Difficulty)Wrap((int)_options.Difficulty + step, 3); break;
            case 2: _options.StartingResources = Wrap(_options.StartingResources + step, 4); break;
            case 3: _options.PopulationLimit = Wrap(_options.PopulationLimit + step, SkirmishOptions.PopulationLimits.Length + 1); break;
            case 4: _options.NoFog = Wrap(_options.NoFog + step, 3); break;
            case 5: _options.Explore = Wrap(_options.Explore + step, 3); break;
        }

        Refresh();
    }

    private string T(string key, string fallback) => _catalog?.Text("sp_setupmap", key, fallback) ?? fallback;

    private void Refresh()
    {
        const string Map = "Map";
        string Toggle(int v) => v == 0 ? Map : v == 1 ? "Yes" : "No";
        string[] values =
        [
            _options.Civilization == 0 ? Map : T($"race_{_options.Civilization}", SkirmishOptions.Civilizations[_options.Civilization - 1]),
            T($"difflvl_{(int)_options.Difficulty}", _options.Difficulty.ToString()),
            T($"resurcs_{_options.StartingResources}", _options.StartingResources.ToString(CultureInfo.InvariantCulture)),
            _options.PopulationLimit == 0 ? Map : SkirmishOptions.PopulationLimits[_options.PopulationLimit - 1].ToString(CultureInfo.InvariantCulture),
            Toggle(_options.NoFog),
            Toggle(_options.Explore),
        ];
        for (int i = 0; i < values.Length; i++)
        {
            _values[i].Texture = Ui.Text(values[i], UiAssets.SmallFont, Colors.White);
        }
    }
}
