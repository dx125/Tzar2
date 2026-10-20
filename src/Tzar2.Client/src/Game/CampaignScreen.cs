using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Game.Campaigns;

namespace Tzar2.Client.Game;

/// <summary>
/// The campaign screen (<c>S_CAMPAIGN</c>; ADR 0133) laid out on the panels of its art (800 × 600): the selected
/// campaign's missions (top left, paged by the arrows of the bottom bar and the mouse wheel) and the selected mission's
/// briefing (top right); the campaigns (bottom left) and the selected campaign's description and progress with RESTART
/// (bottom right); BACK and PLAY in the bar's two button slots. Missions show their state from the profile (done, open,
/// locked); PLAY starts the chosen open mission, or the campaign's current one.
/// </summary>
public partial class CampaignScreen : ArtScreen
{
    // Panels and slots of back.bmp, measured on the art (top-left x, y, width, height).
    private static readonly Rect2I Missions = new(84, 46, 270, 246);
    private static readonly Rect2I Briefing = new(411, 46, 356, 246);
    private static readonly Rect2I CampaignList = new(84, 355, 272, 140);
    private static readonly Rect2I About = new(410, 355, 358, 140);
    private static readonly Vector2I BackSlot = new(78, 544);
    private static readonly Vector2I PlaySlot = new(536, 544);
    private static readonly Rect2I PreviousArrow = new(333, 544, 24, 24);
    private static readonly Rect2I NextArrow = new(487, 544, 24, 24);
    private static readonly Rect2I PageText = new(360, 548, 125, 16);
    private const int RowHeight = 18;

    private static readonly Color Gold = new(1f, 0.92f, 0.65f);
    private static readonly Color Grey = new(0.6f, 0.6f, 0.55f);
    private static readonly Color Done = new(0.6f, 0.85f, 0.55f);

    private readonly IReadOnlyList<ICampaignSource> _campaigns;
    private readonly PlayerProfile _profile;
    private readonly PresentationCatalog? _catalog;
    private int _selected;
    private string? _map;
    private int _page;
    private Control? _details;

    /// <summary>Creates the screen.</summary>
    public CampaignScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, IReadOnlyList<ICampaignSource> campaigns, PlayerProfile profile)
        : base(ui, screen, "art/screens/s_campaign/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(campaigns);
        ArgumentNullException.ThrowIfNull(profile);
        _campaigns = campaigns;
        _profile = profile;
        _catalog = catalog;

        const string Btn = "art/screens/s_campaign/btn.t2img";
        var size = new Vector2I(235, 25);
        Button(BackSlot, size, Btn, Label("cancel", "BACK"), () => Back?.Invoke());
        Button(PlaySlot, size, Btn, Label("start", "PLAY"), () =>
        {
            if (_campaigns.Count > 0)
            {
                Play?.Invoke(_campaigns[_selected], _map);
            }
        });
        Area(PreviousArrow, () => Turn(-1));
        Area(NextArrow, () => Turn(1));
        Select(0);
    }

    /// <summary>PLAY: the campaign and the map chosen (null: its current map).</summary>
    public event Action<ICampaignSource, string?>? Play;

    /// <summary>RESTART: new progress for the campaign.</summary>
    public event Action<ICampaignSource>? Restart;

    /// <summary>BACK.</summary>
    public event Action? Back;

    /// <summary>Missions shown per page of the mission panel.</summary>
    public static int MissionsPerPage => (Missions.Size.Y / RowHeight) - 1;

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } wheel && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            Turn(wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
            AcceptEvent();
        }
    }

    private string Label(string key, string fallback) => _catalog?.Text("campaign", key, fallback) ?? fallback;

    private void Turn(int pages)
    {
        if (_campaigns.Count == 0)
        {
            return;
        }

        int count = Pages(_campaigns[_selected].Campaign);
        int next = Math.Clamp(_page + pages, 0, count - 1);
        if (next != _page)
        {
            _page = next;
            Redraw();
        }
    }

    private static int Pages(CampaignData c) => Math.Max(1, (c.Maps.Length + MissionsPerPage - 1) / MissionsPerPage);

    private void Select(int index)
    {
        if (index >= _campaigns.Count)
        {
            return;
        }

        _selected = index;
        CampaignData c = _campaigns[index].Campaign;
        CampaignProgress? progress = _profile.Progress(c.Id);
        string? current = progress?.Current ?? c.Maps.FirstOrDefault(m => m.IsEntry)?.Id;
        _map = null;
        int at = Math.Max(0, Array.FindIndex(c.Maps.ToArray(), m => m.Id == current));
        _page = at / MissionsPerPage; // open on the page of the mission to play next
        Redraw();
    }

    private void Redraw()
    {
        _details?.QueueFree();
        _details = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_details);
        if (_campaigns.Count == 0)
        {
            Add(Briefing.Position, "No campaigns in this package.", UiAssets.BoldFont, Gold);
            return;
        }

        CampaignData c = _campaigns[_selected].Campaign;
        CampaignProgress? progress = _profile.Progress(c.Id);
        string? current = progress?.Current ?? c.Maps.FirstOrDefault(m => m.IsEntry)?.Id;
        string? shown = _map ?? current;

        // Campaigns (bottom left).
        Add(CampaignList.Position, Label("campaigns", "CAMPAIGNS"), UiAssets.BoldFont, Gold);
        for (int i = 0; i < _campaigns.Count && (i + 1) * RowHeight < CampaignList.Size.Y; i++)
        {
            int index = i;
            Vector2I row = CampaignList.Position + new Vector2I(0, (i + 1) * RowHeight);
            Add(row, Fit((i == _selected ? "> " : "  ") + _campaigns[i].Campaign.Name, CampaignList.Size.X, UiAssets.BoldFont), UiAssets.BoldFont, i == _selected ? Gold : Grey);
            Click(new Rect2I(row, new Vector2I(CampaignList.Size.X, RowHeight)), () => Select(index));
        }

        // Missions (top left), one page.
        int done = 0;
        Add(Missions.Position, $"{c.Name}", UiAssets.BoldFont, Gold);
        for (int i = 0; i < c.Maps.Length; i++)
        {
            CampaignMap m = c.Maps[i];
            bool finished = progress is not null && progress.History.Any(h => h.MapId == m.Id && h.Result != MissionResult.Lost);
            done += finished ? 1 : 0;
            int slot = i - (_page * MissionsPerPage);
            if (slot < 0 || slot >= MissionsPerPage)
            {
                continue;
            }

            bool open = progress is null ? m.IsEntry : progress.Unlocked.Contains(m.Id);
            string mark = m.Id == shown ? "> " : "  ";
            string state = finished ? " (done)" : open ? string.Empty : " (locked)";
            Color colour = finished ? Done : open ? Gold : Grey;
            Vector2I row = Missions.Position + new Vector2I(0, (slot + 1) * RowHeight);
            Add(row, Fit($"{mark}{i + 1}. {m.MapName}{state}", Missions.Size.X, UiAssets.BoldFont), UiAssets.BoldFont, colour);
            if (open)
            {
                string id = m.Id;
                Click(new Rect2I(row, new Vector2I(Missions.Size.X, RowHeight)), () =>
                {
                    _map = id;
                    Redraw();
                });
            }
        }

        Add(PageText.Position, $"{_page + 1} / {Pages(c)}", UiAssets.BoldFont, Gold, PageText.Size.X);

        // Briefing of the mission shown (top right).
        if (c.Maps.FirstOrDefault(m => m.Id == shown) is { } map)
        {
            Add(Briefing.Position, Fit(map.MapName, Briefing.Size.X, UiAssets.BoldFont), UiAssets.BoldFont, Gold);
            Paragraph(Briefing.Position + new Vector2I(0, 22), map.Description, Briefing.Size.X, Briefing.Size.Y - 22);
        }

        // The campaign (bottom right): description, progress, RESTART.
        Add(About.Position, Fit(c.Name, About.Size.X, UiAssets.BoldFont), UiAssets.BoldFont, Gold);
        int used = Paragraph(About.Position + new Vector2I(0, 20), c.Description, About.Size.X, About.Size.Y - 60);
        string status = progress?.Completed == true ? "Campaign completed." : $"{done} of {c.Maps.Length} missions done.";
        Add(About.Position + new Vector2I(0, 24 + used), status, UiAssets.SmallFont, Colors.White);
        Vector2I restart = About.Position + new Vector2I(0, About.Size.Y - 18);
        Add(restart, Label("restart", "RESTART CAMPAIGN"), UiAssets.BoldFont, Gold);
        Click(new Rect2I(restart, new Vector2I(Ui.Measure(Label("restart", "RESTART CAMPAIGN"), UiAssets.BoldFont) + 8, RowHeight)), () => Restart?.Invoke(_campaigns[_selected]));
    }

    // Wrapped text in a panel; returns the height used. Lines that do not fit are cut with an ellipsis on the last one.
    private int Paragraph(Vector2I at, string text, int width, int height)
    {
        const int Line = 14;
        List<string> lines = Wrap(text, width);
        int max = Math.Max(1, height / Line);
        for (int i = 0; i < lines.Count && i < max; i++)
        {
            string line = i == max - 1 && lines.Count > max ? Fit(lines[i] + "...", width, UiAssets.SmallFont) : lines[i];
            Add(at + new Vector2I(0, i * Line), line, UiAssets.SmallFont, Colors.White);
        }

        return Math.Min(lines.Count, max) * Line;
    }

    private void Add(Vector2I at, string text, string font, Color colour, int width = 0)
    {
        if (text.Length > 0 && Ui.Text(text, font, colour) is { } t)
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

    // The text cut to the width (with "..."), so nothing spills over the panel frames.
    private string Fit(string text, int width, string font)
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

    private List<string> Wrap(string text, int width)
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
}
