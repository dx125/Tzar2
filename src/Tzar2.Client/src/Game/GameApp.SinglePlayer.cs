using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Scenario;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;

namespace Tzar2.Client.Game;

/// <summary>
/// Single player (<c>S_SINGLEPLAYER</c>): the campaigns, the original maps of the package (CUSTOM MAP: choice, setup,
/// match), random maps (the skirmish setup) and LOAD GAME (the last quick save).
/// </summary>
public partial class GameApp
{
    /// <summary>Where the import puts the original maps (<c>Maps/*.wmp</c> of the install).</summary>
    public const string OriginalMapFolder = "legacy/maps/";

    private List<CustomMap>? _customMaps;
    private int _customMapIndex;
    private ulong _customStarts; // draws the RANDOM civilizations of a custom map's setup

    /// <summary>Shows the single-player menu.</summary>
    public void ShowSinglePlayer()
    {
        var screen = new SinglePlayerScreen(_ui!, _catalog, Screen, Saves.List().Count > 0, _ai.Notice);
        screen.Campaign += ShowCampaigns;
        screen.CustomMap += ShowCustomMaps;
        screen.RandomMap += ShowSetup;
        screen.LoadGame += ShowLoadGame;
        screen.Back += ShowMainMenu;
        Replace(screen, music: "music/intro.mid");
    }

    /// <summary>Shows the map choice (the original maps are converted the first time, behind the loading screen).</summary>
    public void ShowCustomMaps()
    {
        if (_customMaps is null)
        {
            ShowLoading(LoadingStage.Load);
            _ = AfterFrames(2, () =>
            {
                _customMaps = OriginalMaps();
                ShowCustomMaps();
            });
            return;
        }

        var screen = new ChooseMapScreen(_ui!, _catalog, Screen, _customMaps, map => MapPreview(map, 240), _customMapIndex);
        screen.Play += map =>
        {
            _customMapIndex = screen.Selected;
            ShowMapSetup(map);
        };
        screen.Back += ShowSinglePlayer;
        Replace(screen, music: null);
    }

    private void ShowMapSetup(CustomMap map)
    {
        var model = new Tzar2.Game.Ui.CustomMapScreenModel(map.Mission, SkirmishOptions.CivilizationsOf(_rules));
        var screen = new MapSetupScreen(_ui!, _catalog, Screen, map, model, _ai.Notice);
        screen.Play += chosen => StartCustomMap(map, chosen.ToSettings(_ai, map.Mission.Map.ContentHash + _customStarts++));
        screen.Back += ShowCustomMaps;
        Replace(screen, music: null);
    }

    /// <summary>Starts <paramref name="map"/> with <paramref name="options"/> (the loading screen at once, the match a frame later).</summary>
    public void StartCustomMap(CustomMap map, CustomMapSettings options)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        ShowLoading(LoadingStage.NewGame, map.Title);
        _ = AfterFrames(2, () => BuildCustomMap(map, options, null));
    }

    // A custom map started with its options, or continued from a save (whose setup it carries).
    private void BuildCustomMap(CustomMap map, CustomMapSettings? options, SaveGame? resume)
    {
        if (_rules is null)
        {
            ShowMainMenu();
            return;
        }

        GameSession session;
        CustomMapSettings settings = resume?.Custom is { } saved ? saved with { Ai = _ai } : (options ?? new CustomMapSettings()) with { Ai = _ai };
        try
        {
            session = resume is not null ? resume.ResumeCustomMap(map.Mission, _rules, _ai) : new GameSession(ScenarioMatch.Setup(map.Mission, _rules, custom: settings), _rules);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or KeyNotFoundException)
        {
            if (resume is not null)
            {
                ShowFailure("The save cannot be loaded", e.Message, ShowLoadGame);
            }
            else
            {
                ShowFailure($"Map '{map.Title}' cannot start", e.Message, ShowCustomMaps);
            }

            return;
        }

        var screen = new GameScreen(_store, _ui!, session, "grass", _args.SoundFont, _settings) { KeepSpeed = _args.GameSmoke || _args.AutoplayDir is not null };
        screen.Finished += (outcome, stats) =>
        {
            SaveReplay(session, "custom");
            ShowResults(outcome, stats, () => StartCustomMap(map, settings), ShowSinglePlayer);
        };
        screen.QuitRequested += () =>
        {
            SaveReplay(session, "custom");
            ShowSinglePlayer();
        };
        screen.RestartRequested += () =>
        {
            if (resume is not null)
            {
                LoadSave(resume);
            }
            else
            {
                StartCustomMap(map, settings);
            }
        };
        screen.Saves = Saves;
        screen.CaptureSave = label => SaveGame.CaptureMatch(session, label, map.Id, settings);
        screen.LoadRequested += save =>
        {
            session.Stop();
            LoadSave(save);
        };
        Replace(screen, music: null);
    }

    // A save or a match that cannot start says why, and goes back where the player was (the owner, 2026-10-28: a save of
    // an older version silently opened the list of maps).
    private void ShowFailure(string title, string reason, Action back)
    {
        GD.PrintErr($"{title}: {reason}");
        var notice = new ScenarioNotice(ScenarioNoticeKind.Message, title, reason, null, null, null, 0, false, 0, 0, true);
        var dialog = new ScenarioMessageDialog(_ui!, Screen, notice, null, (section, key, fallback) => _catalog?.Text(section, key, fallback) ?? fallback);
        dialog.Closed += back;
        Replace(dialog, music: null);
    }

    // LOAD GAME of the single player menu: the load dialog over it.
    private void ShowLoadGame()
    {
        var dialog = new LoadDialog(_ui!, Screen, new LoadGameScreen(Saves), (section, key, fallback) => _catalog?.Text(section, key, fallback) ?? fallback);
        dialog.Closed += ShowSinglePlayer;
        dialog.Loading += model =>
        {
            try
            {
                LoadSave(model.Load()!);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                ShowFailure("The save cannot be loaded", e.Message, ShowLoadGame);
            }
        };
        Replace(dialog, music: null);
    }

    // The original maps the import put in the package (legacy/maps/*.wmp), converted once per run (ADR 0120), by title.
    private List<CustomMap> OriginalMaps()
    {
        var maps = new List<CustomMap>();
        if (_rules is null)
        {
            return maps;
        }

        var options = new Tzar2.Import.Maps.LegacyConversionOptions { Ruleset = _rules, ConvertImages = false };
        var problems = new List<string>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var files = new List<string>(_store.Vfs.Enumerate(OriginalMapFolder));
        files.Sort(StringComparer.Ordinal);
        foreach (string file in files)
        {
            if (!file.EndsWith(".wmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Tzar2.Import.Maps.LegacyPlayables.Convert(_store.Read(file), Path.GetFileName(file), options, problems) is { } playable)
            {
                foreach (var (_, (map, scenario)) in playable.Maps)
                {
                    string title = scenario.Properties.Title.Length > 0 ? scenario.Properties.Title : Path.GetFileNameWithoutExtension(file);
                    maps.Add(new CustomMap(Path.GetFileNameWithoutExtension(file), title, scenario.Properties.Description, new MissionContent(map, scenario)));
                }
            }
        }

        maps.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        ClientLog.Info($"original maps: {maps.Count} converted in {watch.ElapsedMilliseconds} ms from {OriginalMapFolder}");
        foreach (string problem in problems)
        {
            ClientLog.Warn("original maps: " + problem);
        }

        return maps;
    }

}
