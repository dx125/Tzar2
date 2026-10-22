using System.Collections.Generic;
using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

[Trait("Rule", "HUD-030")]
public class MinimapColoursTests
{
    // An invented [minidatargb] in the original's form: index = r,g,b, plus tree and cliff.
    private static IniSection Section(params (string? Key, string Value)[] entries)
        => new("minidatargb", [.. System.Linq.Enumerable.Select(entries, e => new KeyValuePair<string?, string>(e.Key, e.Value))]);

    [Fact]
    public void The_terrain_sets_data_colours_terrains_trees_and_cliffs()
    {
        MinimapColours c = MinimapColours.FromSection(Section(("0", "1,2,3"), ("5", "10, 20, 30"), ("tree", "4,5,6"), ("cliff", "7,8,9")))!;
        Assert.Equal(((byte)1, (byte)2, (byte)3), c.Terrain(0));
        Assert.Equal(((byte)10, (byte)20, (byte)30), c.Terrain(5));
        Assert.Null(c.Terrain(3));
        Assert.Equal(((byte)4, (byte)5, (byte)6), c.Tree);
        Assert.Equal(((byte)7, (byte)8, (byte)9), c.Cliff);
    }

    [Fact]
    public void Malformed_or_missing_colours_are_left_out()
    {
        Assert.Null(MinimapColours.FromSection(null));
        Assert.Null(MinimapColours.FromSection(Section(("0", "1,2"), ("x", "1,2,3"), (null, "bare"))));
        MinimapColours c = MinimapColours.FromSection(Section(("1", "300,2,3"), ("2", "4,5,6")))!;
        Assert.Null(c.Terrain(1));
        Assert.Null(c.Tree);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(0, 5, 5, 5, 5)]
    [InlineData(5, 0, 0, 5, 5)] // a tie: the top-left corner's
    [InlineData(1, 2, 3, 4, 1)]
    [InlineData(3, 2, 2, 4, 2)]
    public void A_tile_shows_the_terrain_most_of_its_corners_have(int tl, int tr, int br, int bl, int expected)
        => Assert.Equal(expected, MinimapColours.TileTerrain(tl, tr, br, bl));
}
