using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

/// <summary>The click's pick as the original finds it (CTRL-001; ReTzar's reading, 0x4e4280).</summary>
public sealed class PickRulesTests
{
    [Fact]
    [Trait("Rule", "CTRL-001")]
    public void A_click_takes_the_nearest_unit_then_what_stands_on_the_cell_then_a_tree()
    {
        // A house taking cells (10..12, 10..12), its middle-top cell (11, 10) left free by its picture; a tree on (11, 10)
        // whose crown is drawn over the house; two units side by side on (5, 5) and (6, 5).
        PickCandidate[] all =
        [
            new(1, PickKind.Structure, 0, 0, 0, 0, Newest: 1, Covers: true),
            new(2, PickKind.Tree, 0, 0, 0, 0, Newest: 9, Covers: true),
            new(3, PickKind.Unit, 5, 5, 176, 170, Newest: 3, Covers: false),
            new(4, PickKind.Unit, 6, 5, 208, 170, Newest: 4, Covers: false),
        ];

        // The house under the tree's crown wins over the tree (the sixth play-test); the tree alone is still picked.
        Assert.Equal(1, PickRules.Pointed(all[..2], 11 * 32, 11 * 32));
        Assert.Equal(2, PickRules.Pointed(all[1..2], 11 * 32, 11 * 32));

        // Between two units, the one whose picture's middle is nearer the point, not the one drawn in front.
        Assert.Equal(3, PickRules.Pointed(all, 186, 168));
        Assert.Equal(4, PickRules.Pointed(all, 199, 175));

        // Too far from any picture's middle: what stands on the cell, here nothing.
        Assert.Equal(-1, PickRules.Pointed(all[2..], 176, 230));
        Assert.Equal(4, PickRules.Pointed([all[3] with { MidY = 100 }], 200, 170)); // 70 px off, but on its cell

        // A drag is a click until it is longer than 16 pixels; the box shows from 8.
        Assert.True(PickRules.BoxShown < PickRules.BoxSelects);
    }
}
