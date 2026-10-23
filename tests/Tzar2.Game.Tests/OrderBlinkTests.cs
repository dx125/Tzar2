using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Game.Input;
using Tzar2.Game.Ui;
using Tzar2.Sim.Commands;
using Xunit;

namespace Tzar2.Game.Tests;

public sealed class OrderBlinkTests
{
    [Fact]
    [Trait("Rule", "CTRL-017")]
    public void The_target_blinks_three_times_in_the_colour_of_the_order()
    {
        // On at 0, 200 and 400 ms for 100 ms each (ReTzar's reading of 0x502a3b).
        Assert.Equal(
            [true, false, true, false, true, false],
            new long[] { 0, 150, 250, 399, 499, 500 }.Select(OrderBlink.On));
        Assert.False(OrderBlink.On(-1));

        PlannedCommand[] attack = [Order(CommandKind.Attack)];
        PlannedCommand[] capture = [Order(CommandKind.Capture)];
        PlannedCommand[] move = [Order(CommandKind.Move)];
        Assert.Equal(BlinkColour.Red, OrderBlink.Of(OrderMode.None, attack));
        Assert.Equal(BlinkColour.Red, OrderBlink.Of(OrderMode.None, capture));
        Assert.Equal(BlinkColour.Green, OrderBlink.Of(OrderMode.None, move)); // another right click
        Assert.Equal(BlinkColour.Red, OrderBlink.Of(OrderMode.Patrol, move)); // a command button's order
        Assert.Equal(BlinkColour.Blue, OrderBlink.Of(OrderMode.Guard, [Order(CommandKind.Follow)]));
    }

    private static PlannedCommand Order(CommandKind kind) => new(kind, CommandFlags.None, EntityId.FromValue(7), 0, 0, 0, 0, []);
}
