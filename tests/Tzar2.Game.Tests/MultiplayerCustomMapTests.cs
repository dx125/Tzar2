using System.Collections.Generic;
using System.Linq;
using ScenarioData = Tzar2.Content.Compiled.Scenario.ScenarioData;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;
using Tzar2.Replay;
using Tzar2.Sim;
using Tzar2.Tests.Scenario;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>
/// Custom maps in multiplayer (SHELL-018; ADR 0233): the map chosen in the lobby is found on every peer by its hash,
/// lobby slot k plays kingdom k, MAP SETTINGS keeps the map's own settings, and the time and score limits.
/// </summary>
[Collection("multiplayer")]
public sealed class MultiplayerCustomMapTests
{
    private static readonly AiScriptOption[] Scripts = [new("Random", string.Empty)];

    /// <summary>Three kingdoms with castles and peasants; the map says population limit 120, fog on.</summary>
    internal static CustomMapEntry Map(string id = "three", int kingdoms = 3)
    {
        var b = new ScenarioBuilder { Width = 64, Height = 48 };
        for (int k = 0; k < kingdoms; k++)
        {
            b.Structure("euro.castle", k, 6 + (k % 3 * 20), 6 + (k / 3 * 20) + (k % 3 * 6));
            b.Unit("euro.peon", k, 10 + (k % 3 * 20), 10 + (k / 3 * 20) + (k % 3 * 6));
        }

        ScenarioData plain = b.Scenario();
        var scenario = new ScenarioData(
            plain.Properties with { Title = "Three kingdoms", PopulationLimit = 120 },
            plain.ObjectCount,
            plain.Objects,
            plain.Kingdoms,
            plain.Diplomacy,
            plain.Variables,
            plain.Events,
            plain.Actions,
            plain.Rules,
            plain.Conversations,
            plain.Sounds,
            plain.Music);
        return new CustomMapEntry(id, "Three kingdoms", "invented", new MissionContent(b.Map(), scenario));
    }

    private static LobbyView Lobby(MatchSettings settings, bool host = true, SlotKind third = SlotKind.Open) => new(
        "ABCDEF",
        "test",
        settings,
        [
            new LobbySlotView(0, SlotKind.Human, "host", false, true, 10, SlotChoice.Default, IsHost: true, IsLocal: host),
            new LobbySlotView(1, SlotKind.Human, "guest", true, true, 10, SlotChoice.Default, IsHost: false, IsLocal: !host),
            new LobbySlotView(2, third, string.Empty, false, false, 0, SlotChoice.Default, IsHost: false, IsLocal: false),
            new LobbySlotView(3, SlotKind.Open, string.Empty, false, false, 0, SlotChoice.Default, IsHost: false, IsLocal: false),
        ],
        host);

    [Fact]
    [Trait("Rule", "SHELL-018")]
    public void The_settings_choose_a_custom_map_and_with_map_settings_only_fog_explore_day_and_weather_change()
    {
        CustomMapEntry[] maps = [Map(), Map("two", kingdoms: 2)];
        var random = new LobbyScreenModel(Lobby(new MatchSettings()), Scripts, maps);
        MatchSettings custom = random.Apply(LobbySettingKey.MapSource, 1);
        Assert.Equal((MapSourceKind.CustomMap, "three", maps[0].Hash, true), (custom.Map, custom.CustomMapId, custom.CustomMapHash, custom.UseMapSettings));
        Assert.Equal("two", new LobbyScreenModel(Lobby(custom), Scripts, maps).Apply(LobbySettingKey.CustomMap, 1).CustomMapId);
        Assert.Equal(MapSourceKind.RandomMap, new LobbyScreenModel(Lobby(custom), Scripts, maps).Apply(LobbySettingKey.MapSource, 0).Map);

        // With MAP SETTINGS: the random map's options are gone, and of the rest only No Fog, Explore, day and night and weather change.
        var model = new LobbyScreenModel(Lobby(custom), Scripts, maps);
        Assert.Equal(3, model.MapKingdoms);
        Assert.DoesNotContain(model.Settings, s => s.Key is LobbySettingKey.MapType or LobbySettingKey.MapSize or LobbySettingKey.Animals);
        LobbySettingKey[] free = [LobbySettingKey.NoFog, LobbySettingKey.Explore, LobbySettingKey.DayNight, LobbySettingKey.Weather];
        LobbySettingKey[] mapOwn = [LobbySettingKey.GameType, LobbySettingKey.Victory, LobbySettingKey.TimeLimit, LobbySettingKey.ScoreLimit, LobbySettingKey.StartingResources, LobbySettingKey.PopulationLimit];
        Assert.All(model.Settings.Where(s => free.Contains(s.Key)), s => Assert.True(s.Enabled));
        Assert.All(model.Settings.Where(s => mapOwn.Contains(s.Key)), s => Assert.False(s.Enabled));

        // Without them the lobby's game type, victory, resources and population limit apply.
        var unlocked = new LobbyScreenModel(Lobby(custom with { UseMapSettings = false }), Scripts, maps);
        Assert.True(unlocked.Settings.Single(s => s.Key == LobbySettingKey.PopulationLimit).Enabled);
        Assert.True(unlocked.Settings.Single(s => s.Key == LobbySettingKey.Victory).Enabled);

        // A guest without the map is told and cannot be ready; a kingdom beyond the map's blocks START.
        var lacking = new LobbyScreenModel(Lobby(custom, host: false), Scripts, [maps[1]]);
        Assert.False(lacking.HasMap);
        Assert.Contains("three", lacking.MapNotice, System.StringComparison.Ordinal);
        var two = new LobbyScreenModel(Lobby(custom with { CustomMapId = "two", CustomMapHash = maps[1].Hash }, third: SlotKind.Computer), Scripts, maps);
        Assert.NotNull(two.MapNotice);
        Assert.False(two.CanStart);
    }

    [Fact]
    [Trait("Rule", "SHELL-018")]
    public void Lobbies_have_eight_slots_and_the_map_opens_or_closes_them_to_its_kingdoms()
    {
        CustomMapEntry five = Map("five", kingdoms: 5);
        using var h = new MultiplayerHarness { CustomMaps = [five, Map("two", kingdoms: 2)] };
        NetworkMatch host = NetworkMatch.Create(h.Options("host"), "test", LobbyScreenModel.LobbySlots, isPublic: true, LobbyScreenModel.DefaultOpenSlots);
        h.Matches.Add(host);
        h.Pump(() => host.Lobby is { } l && l.Slots.Count(s => s.Kind == SlotKind.Closed) == 4, what: "the extra slots closed");
        Assert.Equal([SlotKind.Human, SlotKind.Open, SlotKind.Open, SlotKind.Open, SlotKind.Closed, SlotKind.Closed, SlotKind.Closed, SlotKind.Closed], host.Lobby!.Slots.Select(s => s.Kind));
        NetworkMatch guest = h.Join(host.Code!);
        h.Pump(() => host.Lobby!.Slots[1].Kind == SlotKind.Human, what: "join");
        host.SetSlot(2, SlotKind.Computer);
        h.Pump(() => host.Lobby!.Slots[2].Kind == SlotKind.Computer, what: "computer");

        // The five-kingdom map opens slot 4.
        new LobbyScreenModel(host.Lobby!, host.AiScripts, host.CustomMaps).Choose(host, LobbySettingKey.MapSource, 1);
        h.Pump(() => host.Lobby!.Slots[4].Kind == SlotKind.Open && host.Lobby.Settings.Map == MapSourceKind.CustomMap, what: "five kingdoms");
        Assert.Equal(5, host.Lobby!.Slots.Count(s => s.Kind != SlotKind.Closed));

        // The two-kingdom map closes the open slots beyond, but never a taken one (the computer in slot 3 is reported).
        var model = new LobbyScreenModel(host.Lobby, host.AiScripts, host.CustomMaps);
        Assert.Equal([(3, SlotKind.Closed), (4, SlotKind.Closed)], model.SlotChanges(model.Apply(LobbySettingKey.CustomMap, 1)).Select(c => ((int)c.Slot, c.Kind)));
        model.Choose(host, LobbySettingKey.CustomMap, 1);
        h.Pump(() => host.Lobby!.Slots[4].Kind == SlotKind.Closed && host.Lobby.Slots[3].Kind == SlotKind.Closed, what: "two kingdoms");
        Assert.Equal(SlotKind.Computer, host.Lobby!.Slots[2].Kind);
        Assert.NotNull(new LobbyScreenModel(host.Lobby, host.AiScripts, host.CustomMaps).MapNotice);

        // A tiny random map takes four kingdoms: open slots beyond them close.
        var six = new LobbyView("ABCDEF", "test", new MatchSettings(), [.. Enumerable.Range(0, 6).Select(i => new LobbySlotView(
            (byte)i, i == 0 ? SlotKind.Human : SlotKind.Open, string.Empty, false, true, 0, SlotChoice.Default, IsHost: i == 0, IsLocal: i == 0))], true);
        var random = new LobbyScreenModel(six, Scripts);
        Assert.Empty(random.SlotChanges(new MatchSettings { MapSize = Content.MapGen.RandomMapSize.Large }));
        Assert.Equal([(4, SlotKind.Closed), (5, SlotKind.Closed)], random.SlotChanges(random.Apply(LobbySettingKey.MapSize, (int)Content.MapGen.RandomMapSize.Tiny)).Select(c => ((int)c.Slot, c.Kind)));
        _ = guest;
    }

    [Fact]
    [Trait("Rule", "SHELL-018")]
    public void The_time_and_score_limits_are_typed_in_for_their_victory_condition()
    {
        var standard = new LobbyScreenModel(Lobby(new MatchSettings()), Scripts);
        LobbySetting time = standard.Settings.Single(s => s.Key == LobbySettingKey.TimeLimit);
        Assert.True(time.IsNumber);
        Assert.Equal((60, 1, LobbyScreenModel.MaxTimeLimit, false), (time.Selected, time.Min, time.Max, time.Enabled));

        var timed = new LobbyScreenModel(Lobby(new MatchSettings { Victory = VictoryCondition.TimeLimit }), Scripts);
        Assert.True(timed.Settings.Single(s => s.Key == LobbySettingKey.TimeLimit).Enabled);
        Assert.False(timed.Settings.Single(s => s.Key == LobbySettingKey.ScoreLimit).Enabled);
        Assert.Equal(90, timed.Apply(LobbySettingKey.TimeLimit, 90).TimeLimitMinutes);
        Assert.Equal(LobbyScreenModel.MaxTimeLimit, timed.Apply(LobbySettingKey.TimeLimit, 99_999).TimeLimitMinutes);
        var scored = new LobbyScreenModel(Lobby(new MatchSettings { Victory = VictoryCondition.ScoreLimit }), Scripts);
        Assert.Equal(25_000, scored.Apply(LobbySettingKey.ScoreLimit, 25_000).ScoreLimit);
        Assert.Equal(1, scored.Apply(LobbySettingKey.ScoreLimit, 0).ScoreLimit);

        // The limits reach the match.
        MatchSettings settings = scored.Apply(LobbySettingKey.ScoreLimit, 25_000);
        Assert.Equal(settings, MatchSettings.Parse(settings.ToBytes()));
        MatchSetup setup = new MatchDescriptor(settings, [new MatchPlayer(0, "a", "euro", ControllerKind.Human, Difficulty.Normal, 0)]).ToSetup(SkirmishAi.EngineDefault, false, null);
        Assert.Equal((VictoryCondition.ScoreLimit, 25_000), (setup.Victory, setup.ScoreLimit));
    }

    [Fact]
    [Trait("Rule", "SHELL-018")]
    public void Custom_map_settings_keep_their_old_bytes_without_a_map_and_round_trip_with_one()
    {
        var random = new MatchSettings();
        Assert.Equal(1, random.ToBytes()[0]); // version 1 as before: lobbies, replays and saves without a custom map are unchanged
        var custom = random with { Map = MapSourceKind.CustomMap, CustomMapId = "three", CustomMapHash = 0x1234, UseMapSettings = false };
        Assert.Equal(custom, MatchSettings.Parse(custom.ToBytes()));
        Assert.Throws<System.IO.InvalidDataException>(() => new MatchDescriptor(custom, [new MatchPlayer(0, "a", "euro", ControllerKind.Human, Difficulty.Normal, 0)]).ToSetup(SkirmishAi.EngineDefault, false, null));
    }

    [Fact]
    [Trait("Rule", "SHELL-018")]
    public void Two_players_play_a_custom_map_found_by_its_hash_and_its_replay_verifies()
    {
        CustomMapEntry map = Map();
        using var h = new MultiplayerHarness { CustomMaps = [map] };
        NetworkMatch host = h.Host(slots: 4);
        var model = new LobbyScreenModel(host.Lobby!, host.AiScripts, host.CustomMaps);
        MatchSettings settings = model.Apply(LobbySettingKey.MapSource, 1) with { DayNight = false, Seed = 77 };
        host.SetSettings(settings);
        NetworkMatch guest = h.Join(host.Code!);
        h.Pump(() => host.Lobby!.Slots[1].Kind == SlotKind.Human && guest.Lobby?.Settings.Map == MapSourceKind.CustomMap, what: "join");
        Assert.True(new LobbyScreenModel(guest.Lobby!, guest.AiScripts, guest.CustomMaps).HasMap);
        guest.SetReady(true);
        h.Pump(() => host.Lobby!.Slots[1].Ready, what: "ready");
        host.Start(new MatchRules());
        h.Pump(() => h.Matches.All(m => m.Session is { } s && s.Simulation.World.Tick.Value > 60), timeoutMs: 30_000, what: "ticks");

        foreach (NetworkMatch m in h.Matches)
        {
            MatchSetup setup = m.Session!.Setup;
            Assert.NotNull(setup.Scenario);
            Assert.Equal([ControllerKind.Human, ControllerKind.Human, ControllerKind.Passive], setup.Players.Select(p => p.Controller));
            Assert.Equal((120, false, true), (setup.PopulationLimit, setup.DayNight, setup.Multiplayer)); // the map's limit, the lobby's day and night
        }

        int tick = h.Settle(h.Matches);
        Assert.Equal(host.Session!.Simulation.ComputeFullHash(), guest.Session!.Simulation.ComputeFullHash());

        // The replay names the map by its hash and re-simulates with it.
        ReplayFile replay = ReplayFile.Parse(host.Session.FinishReplay()!);
        Assert.Throws<System.IO.InvalidDataException>(() => ReplayPlayback.SetupOf(replay.Header, SkirmishAi.EngineDefault));
        MatchSetup again = ReplayPlayback.SetupOf(replay.Header, SkirmishAi.EngineDefault, HumanVsAiTests.SyntheticRules, [map]);
        using var check = GameSession.ForReplay(again, HumanVsAiTests.SyntheticRules);
        ReplayVerification v = ReplayVerifier.Verify(replay, check.Simulation);
        Assert.True(v.Succeeded, $"replay desynced at {v.FirstMismatch}");
        Assert.Equal(tick, replay.Ticks.Count);
    }

    [Fact]
    [Trait("Rule", "SHELL-018")]
    public void A_player_without_the_map_stops_the_match_at_the_loading_barrier()
    {
        using var h = new MultiplayerHarness { CustomMaps = [Map()] };
        NetworkMatch host = h.Host(slots: 3);
        host.SetSettings(new LobbyScreenModel(host.Lobby!, host.AiScripts, host.CustomMaps).Apply(LobbySettingKey.MapSource, 1));
        NetworkMatch guest = NetworkMatch.Join(h.Options("guest", maps: []), host.Code!);
        h.Matches.Add(guest);
        h.Pump(() => host.Lobby!.Slots[1].Kind == SlotKind.Human && guest.Lobby?.Settings.Map == MapSourceKind.CustomMap, what: "join");
        guest.SetReady(true); // the screen would not let him (HasMap); the barrier is the guard
        h.Pump(() => host.Lobby!.Slots[1].Ready, what: "ready");
        host.Start(new MatchRules());
        h.Pump(() => h.Matches.All(m => m.Stage == LobbyStage.Ended), what: "the barrier stops the match");
        var lines = new List<ChatLine>();
        guest.DrainChat(lines);
        Assert.Contains(lines, l => l.Text.Contains("does not have the custom map", System.StringComparison.Ordinal));
    }
}
