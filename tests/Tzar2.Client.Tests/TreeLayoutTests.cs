using System.Collections.Generic;
using System.Linq;
using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

[Trait("Rule", "PRES-009")]
public class TreeLayoutTests
{
    private static IniSection S(string name, params (string Key, string Value)[] entries)
        => new(name, [.. entries.Select(e => new KeyValuePair<string?, string>(e.Key, e.Value))]);

    // Invented sections in the original's form.
    private static TreeLayout Layout() => TreeLayout.FromIni(
        S("trees", ("img_trees", "t.bmp"), ("pos_tree", "1,3"), ("pos_shadow", "0,1")),
        S("treename", ("Stump", "3"), ("Bush", "2"), ("Pine", "5"), ("Oak", "21")),
        S("treeterrain", ("Stump", "1,2"), ("Pine", "3"), ("Oak", "3, 4")));

    [Fact]
    public void Trees_and_their_shadows_hang_from_their_anchors()
    {
        TreeLayout l = Layout();
        Assert.Equal((10 * 32 - 32, 20 * 32 - 96), l.TreeOrigin(10, 20));
        Assert.Equal((10 * 32, 20 * 32 - 32), l.ShadowOrigin(10, 20));
    }

    [Fact]
    public void The_strips_kinds_follow_treename_in_order()
    {
        Assert.Equal([("Stump", 0, 3), ("Bush", 3, 2), ("Pine", 5, 5), ("Oak", 10, 21)], Layout().Kinds);
    }

    [Fact]
    public void A_tree_takes_a_picture_of_a_kind_that_grows_on_its_terrain()
    {
        TreeLayout l = Layout();
        Assert.All(Enumerable.Range(0, 100), i => Assert.InRange(l.Picture(1, (uint)i), 0, 2));              // stumps only
        Assert.All(Enumerable.Range(0, 100), i => Assert.InRange(l.Picture(4, (uint)i), 10, 30));            // oaks only
        Assert.Equal(26, Enumerable.Range(0, 260).Select(i => l.Picture(3, (uint)i)).Distinct().Count());  // pines and oaks
        Assert.InRange(l.Picture(7, 5), 8, 26); // nothing grows there in the data: the old choice
    }

    [Fact]
    public void Without_the_data_the_anchors_default()
    {
        TreeLayout l = TreeLayout.FromIni(null, null, null);
        Assert.Equal(((1, 3), (0, 1)), (l.TreeAnchor, l.ShadowAnchor));
        Assert.Empty(l.Kinds);
    }
}
