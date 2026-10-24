using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

[Trait("Rule", "PRES-008")]
public class StartRowColTests
{
    [Fact]
    public void A_knight_striking_east_shows_column_four()
    {
        // e_knight_attack has startcol 2 (IMAGES.INI); east is column 2 of an 8-way sheet.
        int east = SpriteRules.Direction(0, 8);
        Assert.Equal(2, east);
        Assert.Equal(4, SpriteRules.TurnedColumn(east, 2, 8));
        Assert.Equal(1, SpriteRules.TurnedColumn(7, 2, 8)); // it wraps
        Assert.Equal(east, SpriteRules.TurnedColumn(east, 0, 8));
        Assert.Equal(0, SpriteRules.TurnedColumn(0, 2, 1)); // one-direction pictures do not turn
    }

    [Fact]
    public void A_swing_starts_on_its_start_row_and_wraps()
    {
        Assert.Equal(4, SpriteRules.SwingFrame(4, 0, 14));
        Assert.Equal(5, SpriteRules.SwingFrame(4, 1, 14));
        Assert.Equal(0, SpriteRules.SwingFrame(4, 10, 14));
        Assert.Equal(3, SpriteRules.SwingFrame(0, 3, 14));
        Assert.Equal(0, SpriteRules.SwingFrame(2, 5, 0));
    }
}

[Trait("Rule", "PRES-008")]
public class AnimationStartsTests
{
    [Fact]
    public void A_picture_runs_from_when_it_was_set_and_a_new_picture_starts_again()
    {
        var starts = new AnimationStarts();
        Assert.Equal(0, starts.Elapsed(7, 1, 1_000)); // first seen: just set
        Assert.Equal(250, starts.Elapsed(7, 1, 1_250));
        Assert.Equal(0, starts.Elapsed(7, 2, 1_400)); // another picture (walk after idle)
        Assert.Equal(100, starts.Elapsed(7, 2, 1_500));
        Assert.Equal(0, starts.Elapsed(8, 2, 1_500)); // each unit on its own
    }

    [Fact]
    public void Units_no_longer_drawn_are_dropped()
    {
        var starts = new AnimationStarts();
        starts.Elapsed(1, 0, 0);
        starts.Elapsed(2, 0, 0);
        starts.Sweep(0);
        for (long t = 1_000; t <= 12_000; t += 1_000)
        {
            starts.Elapsed(1, 0, t); // unit 2 is gone
            starts.Sweep(t);
        }

        Assert.Equal(1, starts.Count);
        Assert.Equal(12_000, starts.Elapsed(1, 0, 12_000));
    }
}
