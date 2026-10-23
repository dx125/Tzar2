using System;
using System.Collections.Immutable;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Rules;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;
using Tzar2.Tests.Scenario;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>The setup of a custom map (<c>S_SINGLESETUP</c>): its kingdoms and USE MAP SETTINGS.</summary>
public sealed class CustomMapSetupTests
{
    private static readonly Ruleset Rules = SyntheticLegacyContent.CompileRuleset();
    private static readonly string[] Civs = ["euro", "arab", "asia"];

    private static MissionContent Map(int kingdoms = 3, Content.Compiled.Scenario.VictoryCondition victory = Content.Compiled.Scenario.VictoryCondition.Standard, int difficulty = 2)
    {
        var b = new ScenarioBuilder { Width = 64, Height = 48 };
        for (int k = 0; k < kingdoms; k++)
        {
            b.Structure("euro.castle", k, 6 + (k * 18), 6);
            b.Unit("euro.peon", k, 10 + (k * 18), 14);
        }

        ScenarioData plain = b.Scenario();
        var scenario = new ScenarioData(
            plain.Properties with { Victory = victory, Difficulty = difficulty, NoFog = true },
            plain.ObjectCount, plain.Objects, plain.Kingdoms, plain.Diplomacy, plain.Variables, plain.Events, plain.Actions, plain.Rules, plain.Conversations, plain.Sounds, plain.Music);
        return new MissionContent(b.Map(), scenario);
    }

    [Fact]
    [Trait("Rule", "SHELL-007")]
    public void The_maps_settings_apply_until_use_map_settings_is_unchecked()
    {
        var m = new CustomMapScreenModel(Map(victory: Content.Compiled.Scenario.VictoryCondition.TimeLimit), Civs);
        Assert.True(m.UseMapSettings);
        Assert.Equal(Difficulty.Hard, m.Difficulty); // the map's
        Assert.True(m.NoFog);
        Assert.False(m.CanChange(CustomMapOption.Victory));
        Assert.False(m.CanChangeRows);
        m.Change(CustomMapOption.Difficulty, +1);
        m.Change(CustomMapOption.Players, -1);
        Assert.Equal((Difficulty.Hard, 3), (m.Difficulty, m.Players)); // locked
        m.Change(CustomMapOption.Explore, +1); // No Fog and Explore always change
        CustomMapSettings own = m.ToSettings(SkirmishAi.EngineDefault);
        Assert.Equal((Difficulty.Hard, true, true, 0, (Sim.VictoryCondition?)null), (own.Difficulty, own.NoFog, own.Explored, own.Players, own.Victory));
        Assert.True(own.Kingdoms.IsEmpty);

        m.Change(CustomMapOption.UseMapSettings, +1);
        Assert.True(m.CanChange(CustomMapOption.Victory) && m.CanChange(CustomMapOption.GameType) && m.CanChange(CustomMapOption.Players));
        m.Change(CustomMapOption.Players, -1);
        m.Change(CustomMapOption.GameType, +1);
        m.Change(CustomMapOption.Victory, +1); // time limit -> score limit
        m.Change(CustomMapOption.VictoryLimit, +1);
        m.Change(CustomMapOption.Difficulty, -1);
        m.Change(CustomMapOption.StartingResources, +1); // Default -> Low
        CustomMapSettings chosen = m.ToSettings(SkirmishAi.EngineDefault);
        Assert.Equal((2, Sim.VictoryCondition.ScoreLimit, Sim.GameType.Deathmatch, 400, Difficulty.Normal, 0),
            (chosen.Players, chosen.Victory!.Value, chosen.GameType!.Value, chosen.ScoreLimit!.Value, chosen.Difficulty, chosen.StartingResources));

        MatchSetup setup = ScenarioMatch.Setup(Map(victory: Content.Compiled.Scenario.VictoryCondition.TimeLimit), Rules, custom: chosen);
        Assert.Equal((Sim.VictoryCondition.ScoreLimit, Sim.GameType.Deathmatch, 400), (setup.Victory, setup.GameType, setup.ScoreLimit));
    }

    [Fact]
    [Trait("Rule", "SHELL-007")]
    public void Fewer_kingdoms_leave_out_the_last_ones_and_their_objects()
    {
        MissionContent map = Map();
        MatchSetup setup = ScenarioMatch.Setup(map, Rules, custom: new CustomMapSettings { Players = 2 });
        Assert.Equal(ControllerKind.Passive, setup.Players[2].Controller);
        using (var session = new GameSession(setup, Rules))
        {
            session.Step(3); // the match runs without the kingdom left out
            Assert.Single(session.Ai!.Players); // its computer player is not created either
            Sim.World world = session.Simulation.World;
            Assert.Equal(0, Owned(world, 2));
            Assert.Equal(2, Owned(world, 1));
        }

        MatchSetup all = ScenarioMatch.Setup(map, Rules);
        using var full = new GameSession(all, Rules);
        Assert.Equal(2, Owned(full.Simulation.World, 2));
    }

    private static int Owned(Sim.World world, int player)
    {
        int n = 0;
        foreach (Core.Ids.EntityId e in world.MapObjectEntities)
        {
            n += world.Entities.IsAlive(e) && world.Identities.Get(e).Owner == player ? 1 : 0;
        }

        return n;
    }

    [Fact]
    [Trait("Rule", "SHELL-006")]
    public void Every_kingdom_shows_its_name_and_colour_and_takes_a_civilization_colour_and_team()
    {
        var m = new CustomMapScreenModel(Map(), Civs);
        Assert.Equal([0, 1, 2], m.Rows.Select(r => (int)r.Slot));
        Assert.Equal(["Kingdom 1", "Kingdom 2", "Kingdom 3"], m.Rows.Select(r => r.Name));
        Assert.True(m.Rows[0].Human);
        Assert.All(m.Rows, r => Assert.Equal(r.Slot, r.Colour));

        m.ChangeRow(1, RandomMapRowField.Civilization, +1); // locked with the map's settings
        Assert.Equal(0, m.Rows[1].Civilization);
        m.Change(CustomMapOption.UseMapSettings, +1);
        m.ChangeRow(1, RandomMapRowField.Civilization, +2); // RANDOM -> arab
        m.ChangeRow(2, RandomMapRowField.Team, +1);
        m.ChangeRow(1, RandomMapRowField.Team, +1);
        m.ChangeRow(0, RandomMapRowField.Colour, +1); // 1 and 2 are taken
        Assert.Equal(3, m.Rows[0].Colour);

        CustomMapSettings chosen = m.ToSettings(SkirmishAi.EngineDefault, seed: 5);
        Assert.Equal("arab", chosen.KingdomAt(1)!.Civilization);
        MatchSetup setup = ScenarioMatch.Setup(Map(), Rules, custom: chosen);
        Assert.Equal(3, setup.Players[0].ColourIndex);
        Assert.Equal(1UL, (setup.Teams >> 4) & 0xF);
        Assert.Equal(1UL, (setup.Teams >> 8) & 0xF);
        Assert.Equal("euro", setup.Players[1].Civilization); // the synthetic ruleset has no arab castle: the first playable one
        using (var session = new GameSession(setup, Rules))
        {
            Assert.Equal(Sim.Kingdoms.Stance.Ally, session.Simulation.World.Diplomacy.StanceOf(1, 2)); // a team allies over the map's diplomacy
            Assert.NotEqual(Sim.Kingdoms.Stance.Ally, session.Simulation.World.Diplomacy.StanceOf(0, 1));
        }


        // The kingdom's units and buildings take its civilization where the ruleset has their class for it.
        MissionContent map = Map();
        PlayerSetup[] players = [new(0, "euro", ControllerKind.Human), new(1, "arab", ControllerKind.Computer), new(2, "euro", ControllerKind.Computer)];
        MapData converted = ScenarioMatch.Kingdoms(map.Map, Rules, players, converted: [1]);
        Assert.Contains(converted.Objects, o => o.Owner == 1 && o.TypeKey == "arab.peon");
        Assert.Contains(converted.Objects, o => o.Owner == 1 && o.TypeKey == "euro.castle"); // no arab castle in the ruleset
        Assert.DoesNotContain(converted.Objects, o => o.Owner == 1 && o.TypeKey == "euro.peon");
        Assert.Same(map.Map, ScenarioMatch.Kingdoms(map.Map, Rules, players, converted: [])); // nothing chosen: the map as it is
    }

    [Fact]
    [Trait("Rule", "SHELL-006")]
    public void A_custom_maps_choices_survive_a_save()
    {
        var chosen = new CustomMapSettings
        {
            Players = 2,
            Victory = Sim.VictoryCondition.TimeLimit,
            TimeLimitMinutes = 90,
            GameType = Sim.GameType.Deathmatch,
            Kingdoms = [new CustomKingdom(0, "asia", 4, 1), new CustomKingdom(1, null, 255, 0)],
        };
        var save = new SaveGame(string.Empty, "m", "label", 3, null, [1, 2, 3]) { Kind = SaveKind.CustomMap, Custom = chosen };
        SaveGame back = SaveGame.FromBytes(save.ToBytes());
        Assert.Equal(chosen, back.Custom);

        var plain = new SaveGame(string.Empty, "m", "label", 3, null, [1, 2, 3]) { Kind = SaveKind.CustomMap, Custom = new CustomMapSettings { Difficulty = Difficulty.Hard } };
        Assert.Equal(2, plain.ToBytes()[4]); // a setup without the new choices keeps the version 2 bytes
        Assert.Equal(Difficulty.Hard, SaveGame.FromBytes(plain.ToBytes()).Custom!.Difficulty);
    }
}
