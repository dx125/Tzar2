using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Ids;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Xunit;

namespace Tzar2.Game.Tests;

// HUD-037: the frame says what a resource item on the ground holds and wears.
[Trait("Rule", "HUD-037")]
public class ItemFrameTests
{
    private const int Tile = Tzar2.Sim.Map.MapState.UnitsPerTile;

    private static MapObject At(string type, byte owner, int x, int y) => new(type, owner, (x * Tile) + (Tile / 2), (y * Tile) + (Tile / 2));

    [Fact]
    public void A_slaughtered_cows_meat_is_food_in_its_own_picture()
    {
        // The eighth play-test: the meat was not drawn, and the selection said Treasury Item instead of the food it holds.
        Ruleset rules = SyntheticLegacyContent.CompileRuleset();
        MapData map = Tzar2.Sim.Scenarios.ScenarioMaps.WithObjects(Tzar2.Sim.Scenarios.ScenarioMaps.Build("meat", 32, 16, new byte[32 * 16], []),
            [At("euro.castle", 0, 2, 2), At("comm.cow", 0, 14, 8)]);
        using var sim = new Simulation(rules, map, new SimulationConfig { PlayerCount = 1, MatchSeed = 3, CheatsAllowed = true });
        World world = sim.World;
        EntityId cow = world.Identities.Entities.ToArray().First(e => world.TypeOf(e).Key == "comm.cow");
        var kill = new CommandList();
        kill.Add(CommandKind.DebugKill, [cow]);
        sim.Tick(kill);
        sim.Tick(new CommandList());
        var frame = new GameFrame();
        FrameBuilder.Fill(frame, world, 0, MatchOutcome.None);
        FrameEntity[] meat = [.. frame.Entities.ToArray().Where(e => rules.EntityTypes[e.TypeId].Key == "comm.meat")];
        Assert.NotEmpty(meat);
        Assert.All(meat, m => Assert.Equal(("item_meat", rules.ResourceId("food")), (m.ItemLook, m.ItemResource)));
        Assert.All(meat, m => Assert.InRange(m.ItemPower, 1, 100));
        Assert.All(frame.Entities.ToArray().Where(e => rules.EntityTypes[e.TypeId].Key == "euro.castle"), e => Assert.Equal(-1, e.ItemResource));
    }
}
