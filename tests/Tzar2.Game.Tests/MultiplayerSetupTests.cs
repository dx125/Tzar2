using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tzar2.Content.MapGen;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;
using Tzar2.Sim;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>
/// The multiplayer setup and settings (SHELL-016, SHELL-017, SHELL-018): each player's colour and strategy, who may
/// change what, the host's settings, and that the choices reach the match on every peer.
/// </summary>
[Collection("multiplayer")]
public sealed class MultiplayerSetupTests
{
    /// <summary>A package with <c>random.ais</c>, a second strategy and an imported <c>AI.INI</c> that also names a missing one.</summary>
    internal static SkirmishAi Library()
    {
        byte[] engine = new EngineDefaultsSource().ReadAllBytes(SkirmishAi.EngineDefaultPath);
        string war = Encoding.UTF8.GetString(engine) + "\nrule say \"war\" forget THIS end\n";
        var package = new MemorySource("scripts")
            .Add(SkirmishAi.PackageFolder + "random.ais", engine)
            .Add(SkirmishAi.PackageFolder + "war.ais", Encoding.UTF8.GetBytes(war))
            .Add(SkirmishAi.AiIniPath, Encoding.UTF8.GetBytes(
                """{"source":"DATA/AI/AI.INI","codePage":1252,"sections":[{"name":"scripts","line":1,"entries":[["War ","War.ais"],["Magic","Magic.ais"]]}]}"""));
        return SkirmishAi.Resolve(package);
    }

    private static readonly AiScriptOption[] Scripts = [new("Random", string.Empty), new("War", "war.ais")];

    private static LobbyView Lobby(bool localIsHost, bool guestReady = false, byte guestColour = SlotChoice.AnyColour, MatchSettings? settings = null) => new(
        "ABCDEF",
        "test",
        settings ?? new MatchSettings(),
        [
            new LobbySlotView(0, SlotKind.Human, "host", false, true, 10, SlotChoice.Default, IsHost: true, IsLocal: localIsHost),
            new LobbySlotView(1, SlotKind.Human, "guest", guestReady, true, 10, SlotChoice.Default with { Colour = guestColour }, IsHost: false, IsLocal: !localIsHost),
            new LobbySlotView(2, SlotKind.Computer, string.Empty, false, true, 0, SlotChoice.Default with { AiScript = "war.ais" }, IsHost: false, IsLocal: false),
            new LobbySlotView(3, SlotKind.Open, string.Empty, false, false, 0, SlotChoice.Default, IsHost: false, IsLocal: false),
        ],
        localIsHost);

    [Fact]
    [Trait("Rule", "SHELL-016")]
    public void Each_player_has_a_colour_of_his_own_and_a_strategy_and_changes_them_only_while_not_ready()
    {
        // The guest wants red (colour 0, the host's by default): a wish beats the default, the host takes the next free one.
        var host = new LobbyScreenModel(Lobby(localIsHost: true, guestColour: 0), Scripts);
        Assert.Equal([1, 0, 2, -1], host.Rows.Select(r => r.Colour));
        Assert.DoesNotContain(0, host.Rows[0].Colours);
        Assert.Contains(1, host.Rows[0].Colours);
        Assert.Equal(8 - 2, host.Rows[0].Colours.Count);
        Assert.Equal(1, host.Rows[2].Script); // the computer plays War

        // The host chooses for himself and the computers, never for the guest; a ready guest's choices are locked.
        Assert.Equal([true, false, true, false], host.Rows.Select(r => r.CanChoose));
        var guest = new LobbyScreenModel(Lobby(localIsHost: false), Scripts);
        Assert.Equal([false, true, false, false], guest.Rows.Select(r => r.CanChoose));
        var ready = new LobbyScreenModel(Lobby(localIsHost: false, guestReady: true), Scripts);
        Assert.All(ready.Rows, r => Assert.False(r.CanChoose));

        Assert.Equal(5, host.WithColour(0, 5).Colour);
        Assert.Equal("war.ais", host.WithScript(0, 1).AiScript);

        // PLAY (START) is the creator's, active once every player is ready.
        Assert.False(host.CanStart);
        Assert.True(new LobbyScreenModel(Lobby(localIsHost: true, guestReady: true), Scripts).CanStart);
        Assert.False(ready.CanStart);
    }

    [Fact]
    [Trait("Rule", "SHELL-016")]
    public void Colour_wishes_are_granted_in_slot_order_and_the_rest_take_the_free_colours()
    {
        Assert.Equal([0, 1, 2], LobbyDescription.Colours([255, 255, 255]));
        Assert.Equal([3, 0, 1], LobbyDescription.Colours([3, 255, 255]));
        Assert.Equal([4, 0, 1], LobbyDescription.Colours([4, 4, 255])); // the second wish for 4 is not granted
    }

    [Fact]
    [Trait("Rule", "SHELL-018")]
    public void The_settings_set_the_map_its_options_game_type_victory_resources_population_fog_explore_day_and_weather()
    {
        var model = new LobbyScreenModel(Lobby(localIsHost: true), Scripts);
        MatchSettings Apply(LobbySettingKey key, int index) => model.Apply(key, index);

        Assert.Equal(RandomMapType.Lakes, Apply(LobbySettingKey.MapType, (int)RandomMapType.Lakes).MapType);
        Assert.Equal(RandomMapSize.Huge, Apply(LobbySettingKey.MapSize, (int)RandomMapSize.Huge).MapSize);
        Assert.Equal(MapTerrainSet.Winter, Apply(LobbySettingKey.Terrain, (int)MapTerrainSet.Winter).Terrain);
        Assert.Equal(MapResourceLevel.High, Apply(LobbySettingKey.MapResources, 2).Resources);
        Assert.True(Apply(LobbySettingKey.GuardedTown, 1).Features.HasFlag(MapFeatures.GuardedTowns));
        Assert.False(Apply(LobbySettingKey.Animals, 0).Features.HasFlag(MapFeatures.Animals));
        Assert.True(Apply(LobbySettingKey.Gateways, 1).Features.HasFlag(MapFeatures.Gateways));
        Assert.True(Apply(LobbySettingKey.MagicItems, 1).Features.HasFlag(MapFeatures.MagicItems));
        Assert.True(Apply(LobbySettingKey.Cliffs, 1).Features.HasFlag(MapFeatures.Cliffs));
        Assert.Equal(GameType.Deathmatch, Apply(LobbySettingKey.GameType, 1).GameType);
        Assert.Equal(VictoryCondition.DestroyCastles, Apply(LobbySettingKey.Victory, 1).Victory);
        Assert.Equal(VictoryCondition.ScoreLimit, Apply(LobbySettingKey.Victory, 3).Victory);
        Assert.Equal([-1, 0, 1, 2], Enumerable.Range(0, 4).Select(i => Apply(LobbySettingKey.StartingResources, i).StartingResources));
        Assert.Equal(-1, Apply(LobbySettingKey.PopulationLimit, 0).PopulationLimit);
        Assert.Equal(200, Apply(LobbySettingKey.PopulationLimit, 4).PopulationLimit);
        Assert.True(Apply(LobbySettingKey.NoFog, 1).NoFog);
        Assert.True(Apply(LobbySettingKey.Explore, 1).Explored);
        Assert.False(Apply(LobbySettingKey.DayNight, 0).DayNight);
        Assert.False(Apply(LobbySettingKey.Weather, 0).Weather);

        // What the panel shows follows the settings, and only the host may change them.
        LobbySetting victory = Assert.Single(model.Settings, s => s.Key == LobbySettingKey.Victory);
        Assert.Equal(0, victory.Selected);
        // (The custom map choice needs maps; the limits follow their victory condition: MultiplayerCustomMapTests.)
        Assert.All(model.Settings.Where(s => s.Key is not (LobbySettingKey.MapSource or LobbySettingKey.TimeLimit or LobbySettingKey.ScoreLimit)), s => Assert.True(s.Enabled));
        var shown = new LobbyScreenModel(Lobby(localIsHost: true, settings: new MatchSettings { GameType = GameType.Deathmatch, PopulationLimit = 500, Explored = true }), Scripts);
        Assert.Equal(1, shown.Settings.Single(s => s.Key == LobbySettingKey.GameType).Selected);
        Assert.Equal("500", shown.Settings.Single(s => s.Key == LobbySettingKey.PopulationLimit).Items[shown.Settings.Single(s => s.Key == LobbySettingKey.PopulationLimit).Selected]);
        Assert.Equal(1, shown.Settings.Single(s => s.Key == LobbySettingKey.Explore).Selected);
        Assert.All(new LobbyScreenModel(Lobby(localIsHost: false), Scripts).Settings, s => Assert.False(s.Enabled));

        // Never cheats in a ranked match.
        var ranked = new LobbyScreenModel(Lobby(localIsHost: true, settings: new MatchSettings { Ranked = true }), Scripts);
        Assert.False(ranked.Settings.Single(s => s.Key == LobbySettingKey.Cheats).Enabled);
        Assert.False(ranked.Apply(LobbySettingKey.Cheats, 1).CheatsAllowed);
    }

    [Fact]
    [Trait("Rule", "SHELL-016")]
    public void Colours_and_strategies_survive_the_lobby_blob_and_the_match_description()
    {
        var choice = new SlotChoice("arab", 2, Difficulty.Hard, "war.ais", 6);
        Assert.Equal(choice, SlotChoice.Parse(choice.ToBytes()));
        Assert.Equal(SlotChoice.Default.ToBytes(), new SlotChoice("euro", 0, Difficulty.Normal).ToBytes()); // no wish: the old bytes

        var plain = new MatchDescriptor(new MatchSettings(), [new MatchPlayer(0, "a", "euro", ControllerKind.Human, Difficulty.Normal, 0)]);
        var chosen = plain with { Players = [plain.Players[0] with { AiScript = "war.ais", Colour = 3 }] };
        Assert.Equal(plain.ToBytes().Length + 1 + 1 + 1 + "war.ais".Length, chosen.ToBytes().Length);
        Assert.Equal(chosen.Players[0], MatchDescriptor.Parse(chosen.ToBytes()).Players[0]);
        Assert.Equal(plain.Players[0], MatchDescriptor.Parse(plain.ToBytes()).Players[0]);
        PlayerSetup p = chosen.ToSetup(SkirmishAi.EngineDefault, multiplayer: false, localSlot: null).Players[0];
        Assert.Equal((3, "war.ais"), (p.ColourIndex, p.AiScript));
    }

    [Fact]
    [Trait("Rule", "SHELL-017")]
    [Trait("Rule", "SHELL-016")]
    public void A_player_chooses_his_own_strategy_and_everyone_is_told_when_the_host_changes_a_computers()
    {
        SkirmishAi ai = Library();
        Assert.Equal(["Random", "War"], ai.Scripts.Select(s => s.Name)); // Magic is in AI.INI but not in the package
        Assert.NotEqual(ai.ContentHash, ai.ForScript("war.ais").ContentHash);
        Assert.Same(ai, ai.ForScript("magic.ais"));

        using var h = new MultiplayerHarness { Ai = ai };
        NetworkMatch host = h.Host(slots: 3);
        host.SetSettings(MultiplayerHarness.Settings());
        host.SetSlot(2, SlotKind.Computer, new SlotChoice("euro", 0, Difficulty.Normal));
        NetworkMatch guest = h.Join(host.Code!);
        h.Pump(() => host.Lobby!.Slots[1].Kind == SlotKind.Human && guest.Lobby?.Slots[2].Kind == SlotKind.Computer, what: "join");
        var lines = new List<ChatLine>();
        guest.DrainChat(lines);
        lines.Clear();

        // The host gives the computer another strategy: every player is told.
        host.SetComputerChoice(2, host.Lobby!.Slots[2].Choice with { AiScript = "war.ais" });
        h.Pump(() => guest.Lobby!.Slots[2].Choice.AiScript == "war.ais", what: "computer's script");
        guest.DrainChat(lines);
        Assert.Contains(lines, l => l.Kind == ChatLineKind.System && l.Text.Contains("Computer 3", StringComparison.Ordinal) && l.Text.Contains("War", StringComparison.Ordinal));

        // A player chooses his own strategy and colour; nobody else can change them, and he cannot change a computer's.
        guest.SetChoice(guest.Lobby!.Slots[1].Choice with { AiScript = "war.ais", Colour = 5 });
        host.SetComputerChoice(1, SlotChoice.Default);
        guest.SetComputerChoice(2, SlotChoice.Default);
        h.Pump(() => host.Lobby!.Slots[1].Choice.Colour == 5, what: "guest's choice");
        h.PumpFor(200);
        Assert.Equal("war.ais", host.Lobby!.Slots[1].Choice.AiScript);
        Assert.Equal("war.ais", host.Lobby.Slots[2].Choice.AiScript);

        guest.SetReady(true);
        h.Pump(() => host.Lobby!.Slots[1].Ready, what: "ready");
        host.Start(new MatchRules());
        h.Pump(() => h.Matches.All(m => m.Session is { } s && s.Simulation.World.Tick.Value > 60), timeoutMs: 30_000, what: "ticks");

        // Both peers run the chosen strategies (the standby AI of the guest and the computer) and draw the guest in colour 5.
        foreach (NetworkMatch m in h.Matches)
        {
            MatchSetup setup = m.Session!.Setup;
            Assert.Equal([ai.ContentHash, ai.ForScript("war.ais").ContentHash, ai.ForScript("war.ais").ContentHash], setup.AiPrograms.Select(p => p.ProgramHash));
            Assert.Equal([0, 5, 1], setup.Players.Select(p => p.ColourIndex));
        }

        h.Settle(h.Matches);
        Assert.Equal(host.Session!.Simulation.ComputeFullHash(), guest.Session!.Simulation.ComputeFullHash());
    }
}
