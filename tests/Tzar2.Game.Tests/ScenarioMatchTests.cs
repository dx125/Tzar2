using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Rules;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Session;
using Tzar2.Tests.Scenario;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>Match setup of real (converted) maps (ADR 0134): the passive kingdom and the command-units flag.</summary>
public sealed class ScenarioMatchTests
{
    private static readonly Ruleset Rules = SyntheticLegacyContent.CompileRuleset();

    [Fact]
    public void Owner_less_working_buildings_and_rescue_objects_go_to_a_passive_neutral_kingdom()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.footman", 0, 4, 4);
        b.Unit("euro.footman", 1, 30, 4);
        int keep = b.Structure("euro.castle", MapObject.Nature, 12, 12); // a drop-off without an owner
        int lost = b.Unit("euro.peon", MapObject.Nature, 20, 20, new ObjectInfo("lost", "", ObjectFlags.Rescue));
        MissionContent mission = new(b.Map(), b.Scenario());
        MatchSetup setup = ScenarioMatch.Setup(mission, Rules, computerAi: true);
        Assert.Equal(3, setup.Players.Count);
        Assert.Equal(ControllerKind.Passive, setup.Players[2].Controller);
        using var session = new GameSession(setup, Rules);
        Sim.World world = session.Simulation.World;
        Core.Ids.EntityId castle = Core.Ids.EntityId.FromValue(session.Scenario!.Triggers.State.MapEntities[keep]);
        Core.Ids.EntityId peon = Core.Ids.EntityId.FromValue(session.Scenario.Triggers.State.MapEntities[lost]);
        Assert.Equal(2, world.Identities.Get(castle).Owner);
        Assert.Equal(2, world.Identities.Get(peon).Owner);
        Assert.Equal(Sim.Kingdoms.Stance.Neutral, world.Diplomacy.StanceOf(0, 2));
        Assert.Equal(Sim.Kingdoms.Stance.Neutral, world.Diplomacy.StanceOf(2, 1));
        Assert.NotNull(session.Ai); // the computer kingdom runs the AI…
        Assert.DoesNotContain(session.Ai!.Players, p => p.Settings.Slot == 2); // …the passive one does not
    }

    [Fact]
    public void A_custom_map_keeps_its_own_settings_unless_the_setup_changes_them()
    {
        var b = new ScenarioBuilder();
        b.Structure("euro.castle", 0, 4, 4);
        b.Structure("euro.castle", 1, 30, 20);
        ScenarioData plain = b.Scenario();
        KingdomDef Kingdom(int number, PlayerType type) => new(number, $"k{number}", string.Empty, Civilization.European, type, true, 1234, 567, 89, 10, string.Empty, true, true, false, 0, 0,
            EquatableArray<string>.Empty, EquatableArray<string>.Empty, EquatableArray<string>.Empty, EquatableArray<string>.Empty, EquatableArray<string>.Empty);
        var scenario = new ScenarioData(
            plain.Properties with { PopulationLimit = 120 },
            plain.ObjectCount,
            plain.Objects,
            new[] { Kingdom(1, PlayerType.HumanOrAi), Kingdom(2, PlayerType.AiOnly) },
            plain.Diplomacy,
            plain.Variables,
            plain.Events,
            plain.Actions,
            plain.Rules,
            plain.Conversations,
            plain.Sounds,
            plain.Music);
        var mission = new MissionContent(b.Map(), scenario);
        int food = Rules.ResourceId("food");

        MatchSetup asIs = ScenarioMatch.Setup(mission, Rules, computerAi: false);
        Assert.Equal(("euro", Difficulty.Normal, 120, -1), (asIs.Players[0].Civilization, asIs.Players[1].Difficulty, asIs.PopulationLimit, asIs.StartingResources));
        using (var session = new GameSession(asIs, Rules))
        {
            Assert.Equal(1234, session.Simulation.World.Players[0].Resources[food]);
        }

        // A civilization the ruleset has a castle for (the synthetic tables may have only the European one).
        string civ = new[] { "arab", "asia" }.FirstOrDefault(c => Rules.EntityTypeId(c + ".castle") >= 0) ?? "euro";
        var custom = new CustomMapSettings { Difficulty = Difficulty.Hard, Civilization = civ, StartingResources = 2, PopulationLimit = 300, NoFog = true };
        MatchSetup changed = ScenarioMatch.Setup(mission, Rules, computerAi: false, custom: custom);
        Assert.Equal((civ, "euro", Difficulty.Hard, 300, 2, true), (changed.Players[0].Civilization, changed.Players[1].Civilization, changed.Players[1].Difficulty, changed.PopulationLimit, changed.StartingResources, changed.NoFog));
        Assert.Equal(ControllerKind.Human, changed.Players[0].Controller);
        using (var session = new GameSession(changed, Rules))
        {
            Assert.NotEqual(1234, session.Simulation.World.Players[0].Resources[food]); // the chosen level, not the map's stockpile
        }
    }

    [Fact]
    public void The_solver_issues_only_orders_and_skill_replacing_assistance()
    {
        Assert.True(CampaignAutoplay.IsAllowedAssist(Sim.Commands.CommandKind.Move, 0));
        Assert.True(CampaignAutoplay.IsAllowedAssist(Sim.Commands.CommandKind.DebugKill, 0));
        Assert.True(CampaignAutoplay.IsAllowedAssist(Sim.Commands.CommandKind.Cheat, (int)Sim.Systems.CheatCode.God));
        Assert.True(CampaignAutoplay.IsAllowedAssist(Sim.Commands.CommandKind.Cheat, (int)Sim.Systems.CheatCode.LargeResources));
        Assert.False(CampaignAutoplay.IsAllowedAssist(Sim.Commands.CommandKind.Cheat, (int)Sim.Systems.CheatCode.Next)); // would skip the mission
        Assert.False(CampaignAutoplay.IsAllowedAssist(Sim.Commands.CommandKind.Cheat, (int)Sim.Systems.CheatCode.Reveal));
        Assert.False(CampaignAutoplay.IsAllowedAssist(Sim.Commands.CommandKind.Resign, 0));
        Assert.True(CampaignAutoplay.IsAllowedAssist(Sim.Commands.CommandKind.PickItem, 0)); // player orders (ADR 0135)
        Assert.True(CampaignAutoplay.IsAllowedAssist(Sim.Commands.CommandKind.SetDiplomacy, 0));
        Assert.False(CampaignAutoplay.IsAllowedAssist(Sim.Commands.CommandKind.Delete, 0));
    }

    [Fact]
    public void A_one_sided_command_units_flag_lets_the_ally_command()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.footman", 0, 4, 4);
        b.Unit("euro.footman", 1, 30, 4);
        ScenarioData plain = b.Scenario();
        var scenario = new ScenarioData(
            plain.Properties,
            plain.ObjectCount,
            plain.Objects,
            plain.Kingdoms,
            new[] { new DiplomacyDef(2, 1, Stance.Ally, false, false, true), new DiplomacyDef(1, 2, Stance.Ally, false, false, false) },
            plain.Variables,
            plain.Events,
            plain.Actions,
            plain.Rules,
            plain.Conversations,
            plain.Sounds,
            plain.Music);
        using var session = new GameSession(ScenarioMatch.Setup(new MissionContent(b.Map(), scenario), Rules, computerAi: false), Rules);
        Assert.True(session.Simulation.World.Diplomacy.MayCommand(0, 1));
    }
}
