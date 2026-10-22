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
