using System;
using System.Linq;
using Tzar2.Content.MapGen;
using Tzar2.Core.Rng;
using Xunit;

namespace Tzar2.Content.Tests;

// WORLD-013 (ADR 0201): random maps grow the original's forests, by the plasma field and [randomtreeset].
[Trait("Rule", "WORLD-013")]
public class ForestsTests
{
    [Theory]
    [InlineData(64, 11, 40, 205)]
    [InlineData(96, 11, 40, 235)]
    [InlineData(128, 19, 40, 234)]
    [InlineData(192, 15, 40, 230)]
    [InlineData(256, 11, 40, 235)]
    [InlineData(384, 20, 30, 223)]
    [InlineData(512, 25, 30, 214)]
    public void Each_map_side_has_its_row_of_randomtreeset(int side, int roughness, int thick, int dense)
    {
        Forests.TreeSettings s = Forests.ForSide(side);
        Assert.Equal((1, 255, roughness, 5, 1, 1, thick, dense), (s.Low, s.High, s.Roughness, s.Divisor, s.Stray, s.Sparse, s.Thick, s.Dense));
    }

    [Fact]
    public void The_plasma_field_is_deterministic_and_smooth()
    {
        byte[] a = Forests.Field(42, 19, 5), b = Forests.Field(42, 19, 5), c = Forests.Field(43, 19, 5);
        Assert.Equal(Forests.FieldSide * Forests.FieldSide, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);

        // Neighbours differ little (midpoint displacement shrinks with the extent), far points freely.
        double step = Enumerable.Range(0, 10_000).Average(i => Math.Abs(a[i * 26] - a[(i * 26) + 1]));
        Assert.True(step < 16, $"mean step {step}"); // about 8; the wraps to 255 make a few big steps
        Assert.True(a.Max() - a.Min() > 100);
    }

    [Fact]
    public void A_tree_grows_with_the_chance_of_its_band()
    {
        Forests.TreeSettings s = Forests.ForSide(128); // thick (divisor 40) above 234, sparse (1) up to it
        var rng = Xoshiro256StarStar.FromSeed(7);
        int thick = 0, sparse = 0, stray = 0;
        for (int i = 0; i < 100_000; i++)
        {
            thick += Forests.Grows(s, 240, 29, ref rng) ? 1 : 0;
            sparse += Forests.Grows(s, 100, 29, ref rng) ? 1 : 0;
            stray += Forests.Grows(s, 0, 29, ref rng) ? 1 : 0;
        }

        // (n + 1) / (n · k + 1) with k = 100 / 40 = 2 and 100 / 1 = 100: about 51 % and 1 %; outside the band 1 in 10,000.
        Assert.InRange(thick, 49_000, 53_000);
        Assert.InRange(sparse, 800, 1_250);
        Assert.InRange(stray, 0, 40);
        Assert.False(Forests.Grows(s, 240, 0, ref rng)); // no tree pictures grow there
    }

    [Fact]
    public void A_generated_map_grows_forests_on_about_a_fifth_of_its_land()
    {
        GeneratedMap map = MapGenerator.Generate(new MapGenOptions { Type = RandomMapType.Continental, Size = RandomMapSize.Medium, Players = 2, Seed = 3 });
        int trees = map.Map.Objects.Count(o => o.TypeKey.StartsWith("comm.tree", StringComparison.Ordinal));
        Assert.InRange(trees, 600, 2_000); // 128 x 128 with the starting forests
        Assert.Empty(MapGenValidator.Validate(map));
    }
}
