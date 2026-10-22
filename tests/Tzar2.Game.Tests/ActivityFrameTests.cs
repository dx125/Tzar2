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
        for (int t = 0; t < 400 && world.Workers.Get(peon).Phase != WorkerPhase.Capturing; t++)
        {
            sim.Tick(new CommandList());
        }

        Assert.Equal(WorkerPhase.Capturing, world.Workers.Get(peon).Phase);
        var frame = new GameFrame();
        FrameBuilder.Fill(frame, world, 0, MatchOutcome.None);
        Assert.Equal(UnitActivity.Capture, frame.Entities.ToArray().Single(e => e.Id == peon).Activity);
    }

    [Fact]
    public void The_new_activities_keep_the_old_numbers()
    {
        // The new activities come after Milk, so the old ones keep their values.
        Assert.Equal((7, 8, 9, 10), ((int)UnitActivity.Milk, (int)UnitActivity.Capture, (int)UnitActivity.Meat, (int)UnitActivity.Cast));
    }
}
