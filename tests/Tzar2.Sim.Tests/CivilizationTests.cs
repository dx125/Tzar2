using Tzar2.Content.Compiled;
using Tzar2.Sim.Map;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>ADR 0211: a kingdom's civilization is an index into the ruleset's ordered list of civilizations.</summary>
public class CivilizationTests
{
    private const int Tile = MapState.UnitsPerTile;

    [Fact]
    public void A_kingdom_takes_the_civilization_of_its_objects_else_the_rulesets_first()
    {
        // Kingdom 0 has an Arabian peasant; kingdom 1 has nothing, so it takes the first playable civilization of the
        // ruleset's list: euro (before ADR 0211 the list was sorted and it was arab). The common key is never chosen.
        var map = TestMap(16, 16, new MapObject("arab.peon", 0, (2 * Tile) + (Tile / 2), (2 * Tile) + (Tile / 2)));
        using var sim = new Simulation(Classic, map, new SimulationConfig { PlayerCount = 2, MatchSeed = 3 });
        Assert.Equal("arab", sim.World.PlayerCivilization(0));
        Assert.Equal("euro", sim.World.PlayerCivilization(1));
        Assert.Equal(1, sim.World.Players[0].Civilization);
        Assert.Equal(0, sim.World.Players[1].Civilization);

        // A fork that lists its own civilization first gives it to kingdoms without objects, and indexes shift with it.
        using var fork = new Simulation(ClassicWith("ruleset: { civilizations: [elves, euro, arab, asia] }"), map, new SimulationConfig { PlayerCount = 2, MatchSeed = 3 });
        Assert.Equal("arab", fork.World.PlayerCivilization(0));
        Assert.Equal("elves", fork.World.PlayerCivilization(1));
    }
}
