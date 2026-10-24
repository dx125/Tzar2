using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// The campaign screen (<c>S_CAMPAIGN</c>; ADR 0133) in the original's layout, as ReTzar reads it (the owner's fifth
/// play-test): the campaigns (top left) and the selected one's description (top right); its entry missions, those it
/// starts with and those reached since (bottom left), and the selected mission's description (bottom right); PLAY, the
/// difficulty and BACK below. The lists and the descriptions scroll by their bars' arrows and the mouse wheel. Texts are
/// Arial 12 bold: captions and the chosen rows in gold, the other rows and the descriptions in dark gold.
/// </summary>
public partial class CampaignScreen : ArtScreen
{
    private const int Row = 16;
    private const int Arrow = 27;
    private const int CampaignRowsShown = 15;
    private const int MissionRowsShown = 9;
    private const string Common = "art/screens/common/";

    // The original's places on the 800 × 600 art (ReTzar's reading).
    private static readonly Rect2I CampaignRows = new(80, 43, 251, Row * CampaignRowsShown);
    private static readonly Rect2I MissionRows = new(80, 352, 252, Row * MissionRowsShown);
    private static readonly Rect2I CampaignBar = new(332, 40, 26, 256);
    private static readonly Rect2I MissionBar = new(332, 349, 26, 150);
    private static readonly Rect2I CampaignText = new(408, 45, 328, 250);
    private static readonly Rect2I MissionText = new(406, 352, 332, 145);
    private static readonly Rect2I CampaignTextBar = new(743, 40, 26, 256);
    private static readonly Rect2I MissionTextBar = new(745, 349, 26, 150);
    private static readonly Rect2I DifficultySpin = new(332, 542, 180, 27);
    private static readonly Vector2I PlaySlot = new(78, 545);
    private static readonly Vector2I BackSlot = new(535, 544);

    private readonly IReadOnlyList<ICampaignSource> _campaigns;
    private readonly PlayerProfile _profile;
    private readonly PresentationCatalog? _catalog;
    private readonly int[] _top = new int[4]; // first row shown: campaigns, missions, campaign text, mission text
    private int _selected;
    private string? _map;
    private Control? _details;

    /// <summary>Creates the screen.</summary>
    public CampaignScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, IReadOnlyList<ICampaignSource> campaigns, PlayerProfile profile, Difficulty difficulty = Difficulty.Normal)
        : base(ui, screen, "art/screens/s_campaign/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(campaigns);
        ArgumentNullException.ThrowIfNull(profile);
        _campaigns = campaigns;
        _profile = profile;
        _catalog = catalog;
        Difficulty = difficulty;

        foreach ((string key, string fallback, int x, int y) in new[]
        {
            ("t_cam", "Campaigns", 78, 17), ("t_descr", "Campaign Description", 401, 17),
            ("t_emiss", "Entry Missions", 78, 326), ("t_emisdes", "Entry Mission Description", 401, 326),
        })
        {
            Text(new Vector2I(x, y), Label(key, fallback), UiAssets.BoldFont, Gold);
        }

        Text(new Vector2I(338, 526), _catalog?.Text("mp_setupmap", "t_diff_lvl", "Difficulty Level") ?? "Difficulty Level", UiAssets.BoldFont, Gold, 149);
        const string Btn = "art/screens/s_campaign/btn.t2img";
        var size = new Vector2I(235, 25);
        TextureButton play = Button(PlaySlot, size, Btn, Label("play", "PLAY"), () =>
        {
            if (_campaigns.Count > 0)
            {
                Play?.Invoke(_campaigns[_selected], _map, Difficulty);
            }
        });
        play.Disabled = _campaigns.Count == 0;
        Button(BackSlot, size, Btn, Label("back", "BACK"), () => Back?.Invoke());
        Select(0);

        // SHELL-026: the original's names of these controls (ReTzar's reading).
        Hint(CampaignRows, "CN_CAMPAIGNS");
        Hint(CampaignText, "CN_CAMPAIGN_DESCRIPTION");
        Hint(MissionRows, "CN_ENTRY_MISSONS");
        Hint(MissionText, "CN_ENTRY_MISSONS_DESCRIPTION");
        Hint(new Rect2I(BackSlot, size), "CN_BACK");
        Hint(new Rect2I(PlaySlot, size), "CN_PLAY");
        HelpBall(k => catalog?.Text("HELP", k, k) ?? k);
    }

    /// <summary>PLAY: the campaign, the entry mission chosen (null: its current one) and the difficulty.</summary>
    public event Action<ICampaignSource, string?, Difficulty>? Play;

    /// <summary>BACK.</summary>
    public event Action? Back;

    /// <summary>The computer kingdoms' difficulty for the campaign's missions.</summary>
    public Difficulty Difficulty { get; private set; }

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } wheel && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            Vector2I at = (Vector2I)(wheel.Position - (Vector2)Origin);
            int step = wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1;
            Rect2I[] panels = [Wider(CampaignRows, CampaignBar), Wider(MissionRows, MissionBar), Wider(CampaignText, CampaignTextBar), Wider(MissionText, MissionTextBar)];
            for (int i = 0; i < panels.Length; i++)
            {
                if (panels[i].HasPoint(at))
                {
                    Scroll(i, step);
                    break;
                }
            }

            AcceptEvent();
        }
    }

    private static Rect2I Wider(Rect2I panel, Rect2I bar) => new(panel.Position, new Vector2I(bar.End.X - panel.Position.X, Math.Max(panel.Size.Y, bar.Size.Y)));

    private string Label(string key, string fallback) => _catalog?.Text("campaign", key, fallback) ?? fallback;

    private void Select(int index)
    {
        if (index >= _campaigns.Count)
        {
            return;
        }

        _selected = index;
        CampaignData c = _campaigns[index].Campaign;
        CampaignProgress? progress = _profile.Progress(c.Id);
        IReadOnlyList<CampaignMap> entries = CampaignFlow.EntryMissions(c, progress);

        // The mission to play next when it is an entry, else the first.
        int at = Math.Max(0, entries.ToList().FindIndex(m => m.Id == progress?.Current));
        _map = entries.Count > 0 ? entries[at].Id : null;
        _top[1] = Math.Clamp(at - (MissionRowsShown - 1), 0, Math.Max(0, entries.Count - MissionRowsShown));
        _top[2] = 0;
        _top[3] = 0;
        Redraw();
    }

    private void Scroll(int panel, int step)
    {
        (int total, int shown) = Extent(panel);
        int next = Math.Clamp(_top[panel] + step, 0, Math.Max(0, total - shown));
        if (next != _top[panel])
        {
            _top[panel] = next;
            Redraw();
        }
    }

    // The rows a panel holds and how many show.
    private (int Total, int Shown) Extent(int panel)
    {
        if (_campaigns.Count == 0)
        {
            return (0, 1);
        }

        CampaignData c = _campaigns[_selected].Campaign;
        return panel switch
        {
            0 => (_campaigns.Count, CampaignRowsShown),
            1 => (CampaignFlow.EntryMissions(c, _profile.Progress(c.Id)).Count, MissionRowsShown),
            2 => (Lines(c.Description, CampaignText.Size.X).Count, CampaignText.Size.Y / Row),
            _ => (Lines(c.Maps.FirstOrDefault(m => m.Id == _map)?.Description ?? string.Empty, MissionText.Size.X).Count, MissionText.Size.Y / Row),
        };
    }

    private List<string> Lines(string text, int width)
        => WrapIn(text.Trim().Length > 0 ? text : _catalog?.Text("defmsg", "nodecr", "No description") ?? "No description", width, UiAssets.BoldFont);

    private void Redraw()
    {
        _details?.QueueFree();
        _details = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_details);
        Bar(0, CampaignBar);
        if (_campaigns.Count == 0)
        {
            Add(CampaignText.Position, "No campaigns in this package.", DarkGold);
            return;
        }

        // Campaigns.
        for (int i = _top[0]; i < Math.Min(_campaigns.Count, _top[0] + CampaignRowsShown); i++)
        {
            int index = i;
            var row = new Rect2I(CampaignRows.Position + new Vector2I(0, (i - _top[0]) * Row), new Vector2I(CampaignRows.Size.X, Row));
            Add(row.Position + new Vector2I(2, 0), Fit(_campaigns[i].Campaign.Name, row.Size.X - 4, UiAssets.BoldFont), i == _selected ? Gold : DarkGold);
            Click(row, () => Select(index));
        }

        // Its entry missions.
        CampaignData c = _campaigns[_selected].Campaign;
        IReadOnlyList<CampaignMap> entries = CampaignFlow.EntryMissions(c, _profile.Progress(c.Id));
        for (int i = _top[1]; i < Math.Min(entries.Count, _top[1] + MissionRowsShown); i++)
        {
            string id = entries[i].Id;
            var row = new Rect2I(MissionRows.Position + new Vector2I(0, (i - _top[1]) * Row), new Vector2I(MissionRows.Size.X, Row));
            Add(row.Position + new Vector2I(2, 0), Fit(entries[i].MapName, row.Size.X - 4, UiAssets.BoldFont), id == _map ? Gold : DarkGold);
            Click(row, () =>
            {
                _map = id;
                _top[3] = 0;
                Redraw();
            });
        }

        if (entries.Count > MissionRowsShown)
        {
            Bar(1, MissionBar);
        }

        // The descriptions.
        Describe(2, c.Description, CampaignText, CampaignTextBar);
        Describe(3, c.Maps.FirstOrDefault(m => m.Id == _map)?.Description ?? string.Empty, MissionText, MissionTextBar);

        // The difficulty: the art has the arrows; a click on the left one lowers it, elsewhere raises it.
        string level = _catalog?.Text("mp_setupmap", $"difflvl_{(int)Difficulty}", Difficulty.ToString()) ?? Difficulty.ToString();
        Add(DifficultySpin.Position + new Vector2I(Arrow, 6), level.Split('|')[0].Trim(), Gold, DifficultySpin.Size.X - (2 * Arrow));
        Click(new Rect2I(DifficultySpin.Position, new Vector2I(Arrow, Arrow)), () => Step(-1));
        Click(new Rect2I(DifficultySpin.Position + new Vector2I(Arrow, 0), new Vector2I(DifficultySpin.Size.X - Arrow, Arrow)), () => Step(+1));
    }

    private void Step(int delta)
    {
        Difficulty = (Difficulty)Math.Clamp((int)Difficulty + delta, (int)Difficulty.Easy, (int)Difficulty.Hard);
        Redraw();
    }

    // A description in its panel from the row it is scrolled to, with its bar.
    private void Describe(int panel, string text, Rect2I box, Rect2I bar)
    {
        List<string> lines = Lines(text, box.Size.X);
        int shown = box.Size.Y / Row;
        for (int i = _top[panel]; i < Math.Min(lines.Count, _top[panel] + shown); i++)
        {
            Add(box.Position + new Vector2I(0, (i - _top[panel]) * Row), lines[i], DarkGold);
        }

        Bar(panel, bar);
    }

    // A scroll bar of the original (COMMON/BTN UP, BTN DOWN, THUMB VERT): its arrows scroll the panel a row.
    private void Bar(int panel, Rect2I track)
    {
        (int total, int shown) = Extent(panel);
        int max = Math.Max(0, total - shown);
        int travel = Math.Max(0, track.Size.Y - (3 * Arrow));
        int thumb = max == 0 ? 0 : (int)Math.Round(travel * (double)Math.Min(_top[panel], max) / max);
        Picture(track.Position, Ui.Region(Common + "btn up.t2img", new Rect2I(0, 0, Arrow, Arrow)));
        Picture(track.Position + new Vector2I(0, track.Size.Y - Arrow), Ui.Region(Common + "btn down.t2img", new Rect2I(0, 0, Arrow, Arrow)));
        Picture(track.Position + new Vector2I(0, Arrow + thumb), Ui.Region(Common + "thumb vert.t2img", new Rect2I(0, 0, Arrow, Arrow)));
        Click(new Rect2I(track.Position, new Vector2I(Arrow, Arrow)), () => Scroll(panel, -1));
        Click(new Rect2I(track.Position + new Vector2I(0, track.Size.Y - Arrow), new Vector2I(Arrow, Arrow)), () => Scroll(panel, +1));
    }

    private void Picture(Vector2I at, Texture2D? texture)
    {
        if (texture is not null)
        {
            _details!.AddChild(new TextureRect { Texture = texture, Position = Origin + at, MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    private void Add(Vector2I at, string text, Color colour, int width = 0)
    {
        if (text.Length > 0 && Ui.Text(text, UiAssets.BoldFont, colour) is { } t)
        {
            int dx = width > 0 ? (width - t.GetWidth()) / 2 : 0;
            _details!.AddChild(new TextureRect { Texture = t, Position = Origin + at + new Vector2I(dx, 0), MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    private void Click(Rect2I rect, Action pressed)
    {
        var area = new Button { Position = Origin + rect.Position, Size = rect.Size, Flat = true, Modulate = new Color(1, 1, 1, 0.02f) };
        area.Pressed += pressed;
        _details!.AddChild(area);
    }
}
