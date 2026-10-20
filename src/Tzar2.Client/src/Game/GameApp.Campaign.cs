using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Scenario;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// Campaigns in the game mode (ADR 0133): the campaign screen, missions started from the profile's progress, the
/// transition to the next map when a scenario asks for it (or the mission is won or lost), the profile saved after
/// every mission, and mission quick saves (F2) and loads (F3).
/// </summary>
public partial class GameApp
{
    private ProfileStore? _profiles;
    private PlayerProfile? _profile;
    private ICampaignSource? _campaign;
    private string? _missionMap;
    private GameSession? _mission;
    private bool _missionEnding;

    private ProfileStore Profiles => _profiles ??= new ProfileStore(ProjectSettings.GlobalizePath("user://"));

    private PlayerProfile Profile
    {
        get
        {
            if (_profile is null)
            {
                try
                {
                    _profile = Profiles.Load();
                }
                catch (InvalidDataException e)
                {
                    GD.PrintErr($"The profile cannot be read ({e.Message}); starting a new one (the file is kept).");
                    _profile = PlayerProfile.New("Player");
                }
            }

            return _profile;
        }
    }

    // The campaigns offered: the built-in demo, and a campaign converted or edited in the editor this session (M5).
    private List<ICampaignSource> Campaigns() => _converted is null ? [DemoCampaign()] : [DemoCampaign(), _converted];

    private static ICampaignSource DemoCampaign() => Tzar2.Game.Campaigns.DemoCampaign.Create();

    /// <summary>Shows the campaign screen.</summary>
    public void ShowCampaigns()
    {
        var screen = new CampaignScreen(_ui!, Screen, Campaigns(), Profile);
        screen.Play += (source, map) => StartMission(source, map);
        screen.Restart += source =>
        {
            _profile = Profile.With(CampaignFlow.Begin(source.Campaign));
            Profiles.Save(_profile);
            ShowCampaigns();
        };
        screen.Back += ShowMainMenu;
        Replace(screen, music: null);
    }

    /// <summary>Starts a mission of <paramref name="source"/>: <paramref name="mapId"/> if unlocked, else the current one.</summary>
    public void StartMission(ICampaignSource source, string? mapId, SaveGame? resume = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_rules is null)
        {
            ShowMainMenu();
            return;
        }

        CampaignProgress progress = resume?.Progress ?? Profile.Progress(source.Campaign.Id) ?? CampaignFlow.Begin(source.Campaign);
        string? map = resume?.MapId ?? (mapId is not null && progress.Unlocked.Contains(mapId) ? mapId : progress.Current);
        if (map is null)
        {
            // Finished: play it again from the start.
            progress = CampaignFlow.Begin(source.Campaign);
            map = progress.Current!;
        }

        progress = progress with { Current = map };
        GameSession session;
        try
        {
            session = resume is not null ? resume.Resume(source, _rules) : new GameSession(ScenarioMatch.Setup(source.Load(map), _rules, progress), _rules);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or KeyNotFoundException)
        {
            GD.PrintErr($"Mission '{map}' cannot start: {e.Message}");
            ShowCampaigns();
            return;
        }

        _campaign = source;
        _missionMap = map;
        _mission = session;
        _missionEnding = false;
        _profile = Profile.With(progress);
        var screen = new GameScreen(_store, _ui!, session, "grass", _args.SoundFont, _settings) { KeepSpeed = _args.GameSmoke || _args.AutoplayDir is not null };
        screen.MapRequested += notice => EndMission(notice.Text.Length > 0 ? notice.Text : null, transition: true);
        screen.Finished += (_, _) => EndMission(null, transition: false);
        screen.QuitRequested += () =>
        {
            SaveReplay(session, "campaign");
            Profiles.Save(Profile);
            ShowCampaigns();
        };
        screen.RestartRequested += () => StartMission(source, map);
        screen.SaveRequested += () => QuickSave(session);
        screen.LoadRequested += QuickLoad;
        Replace(screen, music: null);
    }

    private void EndMission(string? requested, bool transition)
    {
        if (_missionEnding || _mission is null || _campaign is null || _missionMap is null)
        {
            return;
        }

        _missionEnding = true;
        CampaignProgress progress = Profile.Progress(_campaign.Campaign.Id) ?? CampaignFlow.Begin(_campaign.Campaign);
        MissionEnd end = CampaignFlow.Finish(_campaign.Campaign, progress, _missionMap, _mission, requested, transition);
        SaveReplay(_mission, "campaign"); // after Finish, which reads the session (ADR 0176: every match keeps its replay)
        _profile = Profile.With(end.Progress);
        Profiles.Save(_profile);
        GD.Print($"Mission {_missionMap}: {end.Result}, next {end.NextMapId ?? (end.Progress.Completed ? "(campaign completed)" : "(retry)")}");
        if (end.NextMapId is { } next)
        {
            StartMission(_campaign, next);
        }
        else
        {
            ShowCampaigns(); // completed, or lost (the mission stays current)
        }
    }

    private void QuickSave(GameSession session)
    {
        if (_campaign is null || _missionMap is null)
        {
            return;
        }

        try
        {
            SaveGame save = SaveGame.Capture(session, _campaign.Campaign.Id, _missionMap, Profile.Progress(_campaign.Campaign.Id), DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
            string path = Profiles.SavePath("quick");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, save.ToBytes());
            GD.Print("Saved " + path);
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
        {
            GD.PrintErr("Save failed: " + e.Message);
        }
    }

    private void QuickLoad()
    {
        string path = Profiles.SavePath("quick");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            SaveGame save = SaveGame.FromBytes(File.ReadAllBytes(path));
            foreach (ICampaignSource source in Campaigns())
            {
                if (source.Campaign.Id == save.CampaignId)
                {
                    _mission?.Stop();
                    StartMission(source, save.MapId, save);
                    return;
                }
            }

            GD.PrintErr($"The save belongs to campaign '{save.CampaignId}', which is not available.");
        }
        catch (InvalidDataException e)
        {
            GD.PrintErr("Load failed: " + e.Message);
        }
    }
}
