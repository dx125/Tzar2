using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// The sixth play-test's N12: units answered orders late, a close group most. With units stepping cell by cell (ADR 0270)
/// a block of soldiers ordered away all start within a quarter of a second, the back rows as the front ones free their
/// tiles.
/// </summary>
public class GroupStartTests
{
    [Theory]
    [Trait("Rule", "WORLD-020")]
    [InlineData(4)]
    [InlineData(6)]
    public void A_close_block_ordered_away_starts_at_once(int side)
    {
        var objects = new List<MapObject> { Structure("euro.castle", 0, 2, 2) };
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                objects.Add(Unit("euro.footman", 0, 14 + x, 10 + y));
            }
        }

        using var d = new SimDriver(TestMap(64, 32, [.. objects]), ruleset: Classic);
        EntityId[] units = [.. OfType(d.World, "euro.footman")];
        var start = units.ToDictionary(u => u, u => d.PositionOf(u));
        d.MoveToTile(0, units, 50, 12);
        int ticks = 0;
        while (ticks < 5 && units.Any(u => d.PositionOf(u).Equals(start[u])))
        {
            d.Step();
            ticks++;
        }

        Assert.All(units, u => Assert.False(d.PositionOf(u).Equals(start[u]), $"a soldier of the {side} x {side} block still stood after {ticks} ticks"));
    }
}
