using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Session;

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
    private readonly CustomMapOptions _customOptions = new();

    /// <summary>Shows the single-player menu.</summary>
    public void ShowSinglePlayer()
    {
        var screen = new SinglePlayerScreen(_ui!, _catalog, Screen, File.Exists(Profiles.SavePath("quick")), _ai.Notice);
        screen.Campaign += ShowCampaigns;
        screen.CustomMap += ShowCustomMaps;
        screen.RandomMap += ShowSetup;
        screen.LoadGame += LoadQuickSave;
        screen.Back += ShowMainMenu;
        Replace(screen, music: "music/intro.mid");
    }

    /// <summary>Shows the map choice (the original maps are converted the first time, behind the loading screen).</summary>
    public void ShowCustomMaps()
    {
        if (_customMaps is null)
        {
            ShowLoading("the maps");
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
        var screen = new MapSetupScreen(_ui!, _catalog, Screen, map, _customOptions, MapPreview(map.Mission.Map, 256), _ai.Notice);
        screen.Play += options => StartCustomMap(map, options);
        screen.Back += ShowCustomMaps;
        Replace(screen, music: null);
    }

    /// <summary>Starts <paramref name="map"/> with <paramref name="options"/> (the loading screen at once, the match a frame later).</summary>
    public void StartCustomMap(CustomMap map, CustomMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        ShowLoading(map.Title);
        _ = AfterFrames(2, () => BuildCustomMap(map, options));
    }

    private void BuildCustomMap(CustomMap map, CustomMapOptions options)
    {
        if (_rules is null)
        {
            ShowMainMenu();
            return;
        }

        GameSession session;
        try
        {
            session = new GameSession(ScenarioMatch.Setup(map.Mission, _rules, custom: options.ToSettings(_ai)), _rules);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or KeyNotFoundException)
        {
            GD.PrintErr($"Map '{map.Title}' cannot start: {e.Message}");
            ShowCustomMaps();
            return;
        }

        var screen = new GameScreen(_store, _ui!, session, "grass", _args.SoundFont, _settings) { KeepSpeed = _args.GameSmoke || _args.AutoplayDir is not null };
        screen.Finished += (outcome, stats) =>
        {
            SaveReplay(session, "custom");
            ShowResults(outcome, stats, () => StartCustomMap(map, options), ShowSinglePlayer);
        };
        screen.QuitRequested += () =>
        {
            SaveReplay(session, "custom");
            ShowSinglePlayer();
        };
        screen.RestartRequested += () => StartCustomMap(map, options);
        Replace(screen, music: null);
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

    // LOAD GAME: the last quick save (a campaign mission); the original campaigns are converted first if needed.
    private void LoadQuickSave()
    {
        if (_original is null)
        {
            ShowLoading("the campaigns");
            _ = AfterFrames(2, () =>
            {
                _original = OriginalCampaigns();
                LoadQuickSave();
            });
            return;
        }

        QuickLoad();
    }
}
