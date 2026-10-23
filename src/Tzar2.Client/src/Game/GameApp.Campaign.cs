using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Tzar2.Client.Logic;
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
    private List<ICampaignSource>? _original;

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

    // The campaigns offered: the original's from the package (the tutorial first), the built-in demo, and a campaign
    // converted or edited in the editor this session (M5).
    private List<ICampaignSource> Campaigns()
    {
        var all = new List<ICampaignSource>(_original ?? []) { DemoCampaign() };
        if (_converted is not null)
        {
            all.Add(_converted);
        }

        return all;
    }

    private static ICampaignSource DemoCampaign() => Tzar2.Game.Campaigns.DemoCampaign.Create();

    // The original campaigns the import put in the package (legacy/campaign/*.wcm), converted once per run (ADR 0120).
    private List<ICampaignSource> OriginalCampaigns()
    {
        var sources = new List<ICampaignSource>();
        if (_rules is null)
        {
            return sources;
        }

        var options = new Tzar2.Import.Maps.LegacyConversionOptions { Ruleset = _rules, ConvertImages = false };
        var problems = new List<string>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var files = new List<string>(_store.Vfs.Enumerate(OriginalCampaignFolder));
        files.Sort((a, b) => (IsTutorial(a), IsTutorial(b)) switch
        {
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(a, b),
        });
        foreach (string file in files)
        {
            if (!file.EndsWith(".wcm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Tzar2.Import.Maps.LegacyPlayables.Convert(_store.Read(file), Path.GetFileName(file), options, problems) is { } playable)
            {
                var maps = new Dictionary<string, MissionContent>(StringComparer.Ordinal);
                foreach (var (id, map) in playable.Maps)
                {
                    maps[id] = new MissionContent(map.Map, map.Scenario);
                }

                sources.Add(new InMemoryCampaignSource(playable.Campaign, maps));
            }
        }

        ClientLog.Info($"original campaigns: {sources.Count} converted in {watch.ElapsedMilliseconds} ms from {OriginalCampaignFolder}");
        foreach (string problem in problems)
        {
            ClientLog.Warn("original campaigns: " + problem);
        }

        return sources;

        static bool IsTutorial(string path) => path.Contains("tutorial", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where the import puts the original campaigns (<c>Campaign/*.wcm</c> of the install).</summary>
    public const string OriginalCampaignFolder = "legacy/campaign/";

    /// <summary>Shows the campaign screen (the original campaigns are converted the first time, behind the loading screen).</summary>
    public void ShowCampaigns()
    {
        if (_original is null)
        {
            ShowLoading(LoadingStage.Load);
            _ = AfterFrames(2, () =>
            {
                _original = OriginalCampaigns();
                ShowCampaigns();
            });
            return;
        }

        var screen = new CampaignScreen(_ui!, _catalog, Screen, Campaigns(), Profile);
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

    /// <summary>
    /// Starts a mission of <paramref name="source"/>: <paramref name="mapId"/> if unlocked, else the current one (the
    /// loading screen at once, the mission a frame later).
    /// </summary>
    public void StartMission(ICampaignSource source, string? mapId, SaveGame? resume = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ShowLoading(LoadingStage.NewGame, source.Campaign.Name);
        _ = AfterFrames(2, () => BuildMission(source, mapId, resume));
    }

    private void BuildMission(ICampaignSource source, string? mapId, SaveGame? resume)
    {
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
        screen.Saves = Saves;
        screen.CaptureSave = label => SaveGame.Capture(session, source.Campaign.Id, map, Profile.Progress(source.Campaign.Id), label);
        screen.LoadRequested += save =>
        {
            _mission?.Stop();
            LoadSave(save);
        };
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

    /// <summary>The player's saves (<c>user://saves</c>).</summary>
    private SaveLibrary Saves => new(Path.Combine(Profiles.Directory, "saves"));

    // A save of any kind continued (SHELL-014): a campaign mission, a custom map or a skirmish. The original campaigns and
    // maps are converted first when needed (behind the loading screen).
    private void LoadSave(SaveGame save)
    {
        switch (save.Kind)
        {
            case SaveKind.Campaign when _original is null:
                ShowLoading(LoadingStage.Load);
                _ = AfterFrames(2, () =>
                {
                    _original = OriginalCampaigns();
                    LoadSave(save);
                });
                return;
            case SaveKind.Campaign:
                foreach (ICampaignSource source in Campaigns())
                {
                    if (source.Campaign.Id == save.CampaignId)
                    {
                        StartMission(source, save.MapId, save);
                        return;
                    }
                }

                GD.PrintErr($"The save belongs to campaign '{save.CampaignId}', which is not available.");
                ShowSinglePlayer();
                return;
            case SaveKind.CustomMap when _customMaps is null:
                ShowLoading(LoadingStage.Load);
                _ = AfterFrames(2, () =>
                {
                    _customMaps = OriginalMaps();
                    LoadSave(save);
                });
                return;
            case SaveKind.CustomMap:
                if (_customMaps.Find(m => m.Id == save.MapId) is { } map)
                {
                    ShowLoading(LoadingStage.NewGame, map.Title);
                    _ = AfterFrames(2, () => BuildCustomMap(map, null, save));
                    return;
                }

                GD.PrintErr($"The save's map '{save.MapId}' is not in the package.");
                ShowSinglePlayer();
                return;
            default:
                ShowLoading(LoadingStage.Load);
                _ = AfterFrames(2, () => BuildSkirmish(null, save));
                return;
        }
    }
}
