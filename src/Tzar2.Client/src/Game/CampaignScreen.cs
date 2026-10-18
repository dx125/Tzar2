using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Game.Campaigns;

namespace Tzar2.Client.Game;

/// <summary>
/// The campaign screen (<c>S_CAMPAIGN</c>; ADR 0133): the campaigns available (the built-in demo and converted ones),
/// the selected campaign's description and maps with their state from the profile (done, unlocked, locked), and PLAY
/// (the current map, or a chosen unlocked one), RESTART (new progress) and BACK. Positions are first estimates
/// (UNVERIFIED until RE-09).
/// </summary>
public partial class CampaignScreen : ArtScreen
{
    private readonly IReadOnlyList<ICampaignSource> _campaigns;
    private readonly PlayerProfile _profile;
    private int _selected;
    private string? _map;
    private Control? _details;

    /// <summary>Creates the screen.</summary>
    public CampaignScreen(UiAssets ui, Vector2I screen, IReadOnlyList<ICampaignSource> campaigns, PlayerProfile profile)
        : base(ui, screen, "art/screens/s_campaign/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(campaigns);
        ArgumentNullException.ThrowIfNull(profile);
        _campaigns = campaigns;
        _profile = profile;
        Text(new Vector2I(40, 30), "CAMPAIGNS", UiAssets.TitleFont);
        for (int i = 0; i < campaigns.Count && i < 10; i++)
        {
            int index = i;
            Area(new Rect2I(new Vector2I(40, 70 + (i * 22)), new Vector2I(300, 20)), () => Select(index));
            Text(new Vector2I(44, 72 + (i * 22)), campaigns[i].Campaign.Name, UiAssets.BoldFont);
        }

        const string Btn = "art/screens/s_campaign/btn.t2img";
        var size = new Vector2I(150, 27);
        Button(new Vector2I(40, 520), size, Btn, "PLAY", () =>
        {
            if (_campaigns.Count > 0)
            {
                Play?.Invoke(_campaigns[_selected], _map);
            }
        });
        Button(new Vector2I(210, 520), size, Btn, "RESTART", () =>
        {
            if (_campaigns.Count > 0)
            {
                Restart?.Invoke(_campaigns[_selected]);
            }
        });
        Button(new Vector2I(380, 520), size, Btn, "BACK", () => Back?.Invoke());
        Select(0);
    }

    /// <summary>PLAY: the campaign and the map chosen (null: its current map).</summary>
    public event Action<ICampaignSource, string?>? Play;

    /// <summary>RESTART: new progress for the campaign.</summary>
    public event Action<ICampaignSource>? Restart;

    /// <summary>BACK.</summary>
    public event Action? Back;

    private void Select(int index)
    {
        if (index >= _campaigns.Count)
        {
            return;
        }

        _selected = index;
        _map = null;
        _details?.QueueFree();
        _details = new Control { MouseFilter = MouseFilterEnum.Pass };
        AddChild(_details);
        CampaignData c = _campaigns[index].Campaign;
        CampaignProgress? progress = _profile.Progress(c.Id);
        AddText(new Vector2I(380, 70), c.Name, UiAssets.TitleFont, null);
        int y = 100;
        foreach (string line in Wrap(c.Description, 400))
        {
            AddText(new Vector2I(380, y), line, UiAssets.SmallFont, Colors.White);
            y += 14;
        }

        y += 16;
        foreach (CampaignMap m in c.Maps)
        {
            bool done = progress is not null && progress.History.Any(h => h.MapId == m.Id && h.Result != MissionResult.Lost);
            bool open = progress is null ? m.IsEntry : progress.Unlocked.Contains(m.Id);
            bool current = progress is null ? m.IsEntry : progress.Current == m.Id;
            string mark = done ? "[done] " : open ? "[open] " : "[locked] ";
            Color colour = open ? new Color(1f, 0.92f, 0.65f) : new Color(0.6f, 0.6f, 0.55f);
            AddText(new Vector2I(380, y), mark + m.MapName + (current ? "  <" : string.Empty), UiAssets.BoldFont, colour);
            if (open)
            {
                string id = m.Id;
                var area = new Button { Position = Origin + new Vector2I(376, y - 2), Size = new Vector2I(400, 18), Flat = true, Modulate = new Color(1, 1, 1, 0.02f) };
                area.Pressed += () => _map = id;
                _details.AddChild(area);
            }

            y += 20;
        }

        if (progress?.Completed == true)
        {
            AddText(new Vector2I(380, y + 10), "Campaign completed.", UiAssets.BoldFont, null);
        }
    }

    private void AddText(Vector2I at, string text, string font, Color? colour)
    {
        if (Ui.Text(text, font, colour ?? new Color(1f, 0.92f, 0.65f)) is { } t)
        {
            _details!.AddChild(new TextureRect { Texture = t, Position = Origin + at, MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    private List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var line = new System.Text.StringBuilder();
        foreach (string word in text.Split(' '))
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
        return lines;
    }
}
