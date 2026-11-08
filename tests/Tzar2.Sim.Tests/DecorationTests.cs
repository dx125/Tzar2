using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Sim.Map;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

public class DecorationTests
{
    [Fact]
    [Trait("Rule", "WORLD-003")]
    [Trait("Rule", "WORLD-004")]
    public void Ground_objects_and_ruins_close_the_cells_under_their_picture_and_patches_close_none()
    {
        // ADR 0281 (ReTzar's reading of the original, 0x434d10, 0x41ddc0; the picture's .dat grid, 0x497c90): the grass
        // rock covers 2 x 1 cells and its grid leaves the right one free; summer's rock of the same name is 1 x 1 without a
        // grid; the common statue covers 2 x 2. Patches and stumps never stand in the way.
        MapDecoration[] decorations =
        [
            new(MapDecoration.GroundObject, "go_small_mont_1", 1, 1, -1, -1),
            new(MapDecoration.Ruin, "gr_statue_1", 4, 1, -1, -1),
            new(MapDecoration.GroundPatch, "gp_spot", 0, 0, (6 * 32) + 3, (3 * 32) + 4),
            new(MapDecoration.Tree, "stump 2", 7, 3, -1, -1),
            new(MapDecoration.GroundObject, "go_unknown", 7, 0, -1, -1),
        ];

        MapState grass = new(Map("grass", decorations), Obstructing);
        Assert.Equal(LocomotorMask.Air, grass.PassabilityAt(1, 1));
        Assert.True(grass.IsPassable(Locomotor.Foot, 2, 1));
        foreach ((int x, int y) in new[] { (4, 1), (5, 1), (4, 2), (5, 2) })
        {
            Assert.False(grass.IsPassable(Locomotor.Foot, x, y));
            Assert.True(grass.IsPassable(Locomotor.Air, x, y));
        }

        Assert.True(grass.IsPassable(Locomotor.Foot, 6, 3));
        Assert.True(grass.IsPassable(Locomotor.Foot, 7, 3));
        Assert.True(grass.IsPassable(Locomotor.Foot, 7, 0));
        Assert.Equal(32 - 5, Enumerable.Range(0, 8 * 4).Count(i => grass.IsPassable(Locomotor.Foot, i % 8, i / 8)));

        // Summer's own picture of the name: one cell, closed.
        MapState summer = new(Map("summer", decorations), Obstructing);
        Assert.False(summer.IsPassable(Locomotor.Foot, 1, 1));
        Assert.True(summer.IsPassable(Locomotor.Foot, 2, 1));
    }

    [Fact]
    [Trait("Rule", "WORLD-003")]
    public void Classic_leaves_the_cells_open_until_units_step_past_corners()
    {
        // ADR 0281: world.decorationsObstruct is off in classic.yaml for now.
        MapState open = new(Map("grass", [new(MapDecoration.GroundObject, "go_small_mont_1", 1, 1, -1, -1)]), Classic);
        Assert.True(open.IsPassable(Locomotor.Foot, 1, 1));
    }

    [Fact]
    public void Decorations_hash_with_the_map_and_a_map_without_them_hashes_as_before()
    {
        MapData plain = Map("grass", []);
        MapData decorated = plain.WithDecorations([new MapDecoration(MapDecoration.GroundObject, "go_small_mont_1", 1, 1, -1, -1)]);
        Assert.NotEqual(plain.ContentHash, decorated.ContentHash);
        Assert.Equal(plain.ContentHash, decorated.WithDecorations([]).ContentHash);
        Assert.Single(decorated.Decorations);
        Assert.Throws<System.ArgumentException>(() => plain.WithDecorations([new MapDecoration(MapDecoration.GroundPatch, "gp", 8, 0, -1, -1)]));
    }

    private static readonly Ruleset Obstructing = ClassicWith("world: { decorationsObstruct: true }");

    private static MapData Map(string set, MapDecoration[] decorations)
    {
        byte land = (byte)Classic.TerrainTypes.First(t => !t.IsWater).Id;
        byte[] corners = Enumerable.Repeat(land, 9 * 5).ToArray();
        return new MapData("decorated", 8, 4, corners, [], [], null, set, new byte[9 * 5], decorations);
    }
}
