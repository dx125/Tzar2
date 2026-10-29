using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Ids;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Xunit;

namespace Tzar2.Game.Tests;

// PRES-005: the frame names the work a unit's picture shows: capturing, cutting meat, casting.
[Trait("Rule", "PRES-005")]
public class ActivityFrameTests
{
    private const int Tile = Tzar2.Sim.Map.MapState.UnitsPerTile;

    private static MapObject At(string type, byte owner, int x, int y) => new(type, owner, (x * Tile) + (Tile / 2), (y * Tile) + (Tile / 2));

    [Fact]
    public void A_peasant_digging_at_an_enemy_house_is_shown_capturing()
    {
        Ruleset rules = SyntheticLegacyContent.CompileRuleset();
        MapData map = Tzar2.Sim.Scenarios.ScenarioMaps.WithObjects(Tzar2.Sim.Scenarios.ScenarioMaps.Build("capture", 48, 16, new byte[48 * 16], []),
            [At("euro.castle", 1, 40, 2), At("euro.house", 1, 14, 4), At("euro.peon", 0, 12, 4)]);
        using var sim = new Simulation(rules, map, new SimulationConfig { PlayerCount = 2, MatchSeed = 3 });
        World world = sim.World;
        EntityId peon = world.Identities.Entities.ToArray().First(e => world.Identities.Get(e).Owner == 0);
        EntityId house = world.Identities.Entities.ToArray().First(e => world.TypeOf(e).Key == "euro.house");
        var order = new CommandList();
        order.Add(CommandKind.Capture, [peon], target: house);
        sim.Tick(order);
        // Until it digs: its last step onto its tile ends first (ADR 0270).
        for (int t = 0; t < 400 && (world.Workers.Get(peon).Phase != WorkerPhase.Capturing || (world.CellSteps.IndexOf(peon) is >= 0 and var s && world.CellSteps.At(s).To >= 0)); t++)
        {
            sim.Tick(new CommandList());
        }

        Assert.Equal(WorkerPhase.Capturing, world.Workers.Get(peon).Phase);
        var frame = new GameFrame();
        FrameBuilder.Fill(frame, world, 0, MatchOutcome.None);
        Assert.Equal(UnitActivity.Capture, frame.Entities.ToArray().Single(e => e.Id == peon).Activity);
    }

    [Fact]
    [Trait("Rule", "PRES-016")]
    public void A_unit_in_the_middle_of_a_step_is_drawn_walking_whatever_its_order()
    {
        // The eighth play-test: a knight's walk could freeze in a crowd while it still moved. A unit stopped mid-step (a
        // fight halts it, it makes way for a friend) ends its step (ADR 0270), with no order and no velocity left; it slid
        // there with its standing or its attack picture.
        Ruleset rules = SyntheticLegacyContent.CompileRuleset();
        MapData map = Tzar2.Sim.Scenarios.ScenarioMaps.WithObjects(Tzar2.Sim.Scenarios.ScenarioMaps.Build("step", 32, 16, new byte[32 * 16], []),
            [At("euro.castle", 0, 2, 2), At("euro.knight", 0, 12, 8)]);
        using var sim = new Simulation(rules, map, new SimulationConfig { PlayerCount = 1, MatchSeed = 3 });
        World world = sim.World;
        EntityId knight = world.Identities.Entities.ToArray().First(e => world.TypeOf(e).Key == "euro.knight");
        sim.Tick(new CommandList());
        ref CellStep step = ref world.CellSteps.Get(knight);
        step.To = (8 * map.Width) + 13; // entering the tile east of it
        step.Progress = Tile / 4;
        Assert.Equal(MotionState.Idle, world.Motions.Get(knight).State);
        Assert.Equal(0, world.Motions.Get(knight).VelocityX);
        var frame = new GameFrame();
        FrameBuilder.Fill(frame, world, 0, MatchOutcome.None);
        Assert.Equal(UnitActivity.Walk, frame.Entities.ToArray().Single(e => e.Id == knight).Activity);
    }

    [Fact]
    public void The_new_activities_keep_the_old_numbers()
    {
        // The new activities come after Milk, so the old ones keep their values.
        Assert.Equal((7, 8, 9, 10), ((int)UnitActivity.Milk, (int)UnitActivity.Capture, (int)UnitActivity.Meat, (int)UnitActivity.Cast));
    }
}
