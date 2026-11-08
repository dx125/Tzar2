using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

public class CornerTests
{
    [Fact]
    [Trait("Rule", "WORLD-023")]
    public void A_unit_steps_between_two_obstacles_that_touch_at_a_corner()
    {
        // ADR 0282 (ReTzar's reading of the original's path search: a step tests only the tile it enters): a line of
        // cliffs touching corner to corner is no wall for a unit that steps cell by cell.
        string[] rows =
        [
            ".......#",
            "......#.",
            ".....#..",
            "....#...",
            "...#....",
            "..#.....",
            ".#......",
            "#.......",
        ];
        using var d = new SimDriver(AsciiMap(rows, Unit("euro.peon", 0, 1, 1)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.MoveToTile(0, [peon], 6, 6);
        d.RunUntil(() => d.PositionOf(peon) is var p && p.X / SimDriver.Tile == 6 && p.Y / SimDriver.Tile == 6, 600);
        Position at = d.PositionOf(peon);
        Assert.Equal((6, 6), (at.X / SimDriver.Tile, at.Y / SimDriver.Tile));
    }
}
