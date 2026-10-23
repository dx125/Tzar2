using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Ui;

namespace Tzar2.Client.Game;

/// <summary>
/// What the random map screen and the custom map setup share (<c>S_SINGLERND</c>, <c>S_SINGLESETUP</c>; ReTzar reads
/// both constructors with the same places): the kingdoms' rows, 35 apart from (62, 20); spins of 180 × 27 with their
/// captions; 30 × 30 check boxes; the victory limit's selector. Everything that changes is drawn on one layer and
/// redrawn after each press.
/// </summary>
public abstract partial class SetupScreen : ArtScreen
{
    /// <summary>The common screen art.</summary>
    protected const string Common = "art/screens/common/";

    /// <summary>The colour of the captions.</summary>
    protected static readonly Color Caption = new(0.85f, 0.85f, 0.75f);

    private static readonly Color Disabled = new(0.55f, 0.52f, 0.42f);
    private readonly Control _layer;

    /// <summary>Creates the screen on <paramref name="background"/>.</summary>
    protected SetupScreen(UiAssets ui, Vector2I screen, string background)
        : base(ui, screen, background)
    {
        _layer = new Control { MouseFilter = MouseFilterEnum.Ignore, Position = Origin }; // moves with the art (Resize)
        AddChild(_layer);
    }

    /// <summary>Draws what changes (called again after each press).</summary>
    protected abstract void DrawLayer();

    /// <summary>Clears the layer and draws it again.</summary>
    protected void Refresh()
    {
        foreach (Node child in _layer.GetChildren())
        {
            child.QueueFree();
        }

        DrawLayer();
    }

    /// <summary>
    /// The row of kingdom <paramref name="index"/> on <paramref name="rowArt"/>: its colour (<c>COMMON/COLOR BARS</c>),
    /// name, civilization spin and team (<c>COMMON/TEAMS</c>); <paramref name="change"/> takes the field and the step.
    /// </summary>
    protected void KingdomRow(int index, string rowArt, byte colour, string name, string civilization, byte team, Action<RandomMapRowField, int> change, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(change);
        int y = 35 * index;
        Picture(new Vector2I(62, 20 + y), Ui.Texture(rowArt));
        Picture(new Vector2I(73, 31 + y), Ui.Region(Common + "color bars.t2img", new Rect2I(0, Math.Min((int)colour, 7) * 13, 13, 13)));
        Label(new Vector2I(110, 30 + y), name, Colors.White);
        Spin(new Vector2I(311, 23 + y), civilization, step => change(RandomMapRowField.Civilization, step), arrows: true, enabled: enabled);
        Picture(new Vector2I(511, 31 + y), Ui.Region(Common + "teams.t2img", new Rect2I(0, ((team + 4) % 5) * 13, 13, 13)));
        if (enabled)
        {
            Click(new Rect2I(73, 31 + y, 13, 13), () => change(RandomMapRowField.Colour, +1));
            Click(new Rect2I(511, 31 + y, 13, 13), () => change(RandomMapRowField.Team, +1));
        }
    }

    /// <summary>A baked 180 × 27 spin of the art with its caption.</summary>
    protected void Option(Vector2I at, Vector2I captionAt, string caption, string value, Action<int> change, bool enabled)
    {
        Label(captionAt, caption, Caption);
        Spin(at, value, change, arrows: false, enabled: enabled);
    }

    /// <summary>A 30 × 30 check box (<c>COMMON/CHK</c>: the green light on top, the red one below) with its caption.</summary>
    protected void Check(Vector2I at, Vector2I captionAt, string caption, bool on, Action toggle, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(toggle);
        Picture(at, Ui.Region(Common + "chk.t2img", new Rect2I(0, on ? 0 : 31, 31, 31)));
        if (enabled)
        {
            Click(new Rect2I(at, new Vector2I(30, 30)), toggle); // the original's 30 × 30 boxes, 30 apart
        }

        Label(captionAt, caption, enabled ? Caption : Disabled);
    }

    /// <summary>The victory limit's selector (<c>COMMON/SCROLL CTRL</c>) under the victory condition, with its caption.</summary>
    protected void LimitSelector(string caption, string value, Action<int> change, bool enabled)
    {
        Picture(new Vector2I(66, 538), Ui.Texture(Common + "scroll ctrl.t2img"));
        Label(new Vector2I(68, 527), caption, Caption);
        Spin(new Vector2I(66, 541), value, change, arrows: false, width: 191, enabled: enabled);
    }

    /// <summary>
    /// A spin: arrows at both ends (drawn from <c>COMMON/BTN LEFT</c> and <c>RIGHT</c> where the art has none), the
    /// value between them; a disabled one shows its value dimmed and takes no press.
    /// </summary>
    protected void Spin(Vector2I at, string value, Action<int> change, bool arrows, int width = 180, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(change);
        const int Arrow = 27;
        if (arrows)
        {
            Picture(at, Ui.Region(Common + "btn left.t2img", new Rect2I(0, 0, Arrow, Arrow)));
            Picture(at + new Vector2I(width - Arrow, 0), Ui.Region(Common + "btn right.t2img", new Rect2I(0, 0, Arrow, Arrow)));
        }

        if (enabled)
        {
            Click(new Rect2I(at, new Vector2I(Arrow, Arrow)), () => change(-1));
            Click(new Rect2I(at + new Vector2I(width - Arrow, 0), new Vector2I(Arrow, Arrow)), () => change(+1));
            Click(new Rect2I(at + new Vector2I(Arrow, 0), new Vector2I(width - (2 * Arrow), Arrow)), () => change(+1));
        }

        string shown = Fit(value, width - (2 * Arrow) - 6, UiAssets.SmallFont);
        if (Ui.Text(shown, UiAssets.SmallFont, enabled ? Colors.White : Disabled) is { } t)
        {
            Picture(at + new Vector2I((width - t.GetWidth()) / 2, (Arrow - t.GetHeight()) / 2), t);
        }
    }

    /// <summary>A line of small text at a position of the art.</summary>
    protected void Label(Vector2I at, string text, Color colour)
    {
        if (Ui.Text(text, UiAssets.SmallFont, colour) is { } t)
        {
            Picture(at, t);
        }
    }

    /// <summary>A picture at a position of the art.</summary>
    protected void Picture(Vector2I at, Texture2D? texture)
    {
        if (texture is not null)
        {
            _layer.AddChild(new TextureRect { Texture = texture, Position = at, MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    /// <summary>A click area at a position of the art; the screen is redrawn after the press.</summary>
    protected void Click(Rect2I rect, Action pressed)
    {
        ArgumentNullException.ThrowIfNull(pressed);
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
/// The setup of a custom map (<c>S_SINGLESETUP</c>; SHELL-006, SHELL-007) drawing <see cref="CustomMapScreenModel"/>:
/// one row per kingdom of the map (colour, name, civilization, team), the six options of the left panel, the victory
/// limit, No Fog and Explore, the map's description on the right and USE MAP SETTINGS under it, PLAY and BACK. With the
/// map's settings only No Fog and Explore change. Places are the original's as ReTzar reads its constructor and labels.
/// </summary>
public partial class MapSetupScreen : SetupScreen
{
    private const string Art = "art/screens/s_singlesetup/";
    private static readonly Rect2I Description = new(549, 54, 190, 240);
    private readonly CustomMapScreenModel _model;
    private readonly PresentationCatalog? _catalog;

    /// <summary>Creates the setup of <paramref name="map"/>.</summary>
    public MapSetupScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, CustomMap map, CustomMapScreenModel model, string? notice = null)
        : base(ui, screen, Art + "back.t2img")
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _catalog = catalog;
        Notice(notice, new Vector2I(66, 304), 470); // below the kingdoms' rows
        Text(new Vector2I(549, 35), T("t_map_decr", "Map Description"), UiAssets.SmallFont, Caption);
        List<string> lines = Wrap(map.Description.Length > 0 ? map.Description : map.Title, Description.Size.X);
        int max = Description.Size.Y / 14;
        for (int i = 0; i < lines.Count && i < max; i++)
        {
            string line = i == max - 1 && lines.Count > max ? Fit(lines[i] + "...", Description.Size.X, UiAssets.SmallFont) : lines[i];
            Text(Description.Position + new Vector2I(0, i * 14), line, UiAssets.SmallFont, Colors.White);
        }

        const string Btn = Art + "btn.t2img";
        Button(new Vector2I(556, 499), new Vector2I(210, 27), Btn, T("play", "PLAY"), () => Play?.Invoke(_model));
        Button(new Vector2I(556, 554), new Vector2I(210, 27), Btn, T("back", "BACK"), () => Back?.Invoke());
        Refresh();
    }

    /// <summary>PLAY.</summary>
    public event Action<CustomMapScreenModel>? Play;

    /// <summary>BACK: the map choice.</summary>
    public event Action? Back;

    /// <summary>The model (test drivers).</summary>
    public CustomMapScreenModel Model => _model;

    private string T(string key, string fallback) => _catalog?.Text("sp_setupmap", key, fallback) ?? fallback;

    /// <inheritdoc/>
    protected override void DrawLayer()
    {
        CustomMapScreenModel m = _model;
        for (int i = 0; i < m.Rows.Count; i++)
        {
            CustomMapRow row = m.Rows[i];
            int index = i;
            string civ = m.CivilizationOf(row) is { } key ? T($"race_{row.Civilization}", key) : T("race_0", "RANDOM");
            KingdomRow(i, Art + "row back.t2img", row.Colour, Fit(row.Name, 180, UiAssets.SmallFont), civ, row.Team, (field, step) => m.ChangeRow(index, field, step), m.CanChangeRows);
        }

        string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        string[] victories = ["Standard", "Destroy Castles", "Time Limit", "Score Limit"];
        void Spin(Vector2I at, Vector2I captionAt, string caption, string value, CustomMapOption option)
            => Option(at, captionAt, caption, value, step => m.Change(option, step), m.CanChange(option));
        Spin(new Vector2I(66, 369), new Vector2I(68, 353), T("t_num_players", "Number of Players") + $" ({m.FewestPlayers} - {m.MostPlayers})", N(m.Players), CustomMapOption.Players);
        Spin(new Vector2I(66, 427), new Vector2I(68, 410), T("t_game_type", "Game Type"), m.IsDeathmatch ? "Deathmatch" : T("g_type_0", "Standard"), CustomMapOption.GameType);
        Spin(new Vector2I(66, 487), new Vector2I(68, 470), T("t_vict_cond", "Victory Condition"), T($"vcond_{m.VictoryChoice}", victories[Math.Max(0, m.VictoryChoice)]), CustomMapOption.Victory);
        Spin(new Vector2I(270, 369), new Vector2I(273, 353), T("t_poplimit", "Population Limit"), N(m.PopulationLimit), CustomMapOption.PopulationLimit);
        Spin(new Vector2I(270, 427), new Vector2I(273, 412), T("t_diff_lvl", "Difficulty Level"), T($"difflvl_{(int)m.Difficulty}", m.Difficulty.ToString()), CustomMapOption.Difficulty);
        Spin(new Vector2I(270, 487), new Vector2I(273, 470), T("t_st_res", "Starting Resources"), T($"resurcs_{m.StartingResources}", m.StartingResources == 3 ? "Default" : N(m.StartingResources)), CustomMapOption.StartingResources);
        if (m.HasVictoryLimit)
        {
            LimitSelector(m.IsScoreLimit ? T("t_score_lim", "Score Limit") : T("t_time_lim", "Time Limit (min.)"), N(m.VictoryLimit),
                step => m.Change(CustomMapOption.VictoryLimit, step), m.CanChange(CustomMapOption.VictoryLimit));
        }

        void Box(Vector2I at, Vector2I captionAt, string caption, bool on, CustomMapOption option)
            => Check(at, captionAt, caption, on, () => m.Change(option, +1), m.CanChange(option));
        Box(new Vector2I(268, 539), new Vector2I(302, 549), T("t_fog", "No Fog"), m.NoFog, CustomMapOption.NoFog);
        Box(new Vector2I(268, 569), new Vector2I(302, 579), T("t_expolre", "Explore"), m.Explore, CustomMapOption.Explore);
        Box(new Vector2I(548, 309), new Vector2I(584, 320), T("t_mapdef", "Use Map Settings"), m.UseMapSettings, CustomMapOption.UseMapSettings);
    }
}
