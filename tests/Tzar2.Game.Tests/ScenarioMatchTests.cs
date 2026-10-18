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
        MatchSetup setup = ScenarioMatch.Setup(mission, Rules, computerAi: false);
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
        Assert.Null(session.Ai);
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
