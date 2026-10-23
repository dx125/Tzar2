using Tzar2.Game.Ui;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>The help ball of the menu screens.</summary>
public class MenuHelpTests
{
    [Fact]
    [Trait("Rule", "SHELL-026")]
    public void The_ball_turns_hint_mode_on_and_off_and_hint_mode_takes_the_presses()
    {
        var help = new MenuHelp();
        Assert.True(help.Allows);
        help.Toggle();
        Assert.True(help.On);
        Assert.False(help.Allows); // only the ball works now
        help.Toggle();
        Assert.True(help.Allows);
    }

    [Fact]
    [Trait("Rule", "SHELL-026")]
    public void The_box_goes_below_the_pointer_pushed_left_in_proportion_and_stays_on_the_screen()
    {
        Assert.Equal((0, 120, 120), MenuHelp.Box(0, 100, 100, 800, 600)); // at the left edge: no push
        Assert.Equal((340, 320, 120), MenuHelp.Box(400, 300, 100, 800, 600)); // half way: half the width
        Assert.Equal((680, 584, 120), MenuHelp.Box(800, 590, 100, 800, 600)); // at the right edge: the whole width; never below the screen
    }
}
