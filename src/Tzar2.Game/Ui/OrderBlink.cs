using System;
using System.Collections.Generic;
using Tzar2.Game.Input;
using Tzar2.Sim.Commands;

namespace Tzar2.Game.Ui;

/// <summary>The colour an order's target blinks in (CTRL-017).</summary>
public enum BlinkColour
{
    /// <summary>A right click that is not an attack or a capture.</summary>
    Green,

    /// <summary>An attack or a capture, and the orders of the command card's buttons.</summary>
    Red,

    /// <summary>A guard order.</summary>
    Blue,
}

/// <summary>
/// The confirmation of an order on an object (CTRL-017), as ReTzar reads the original (0x5008c0, 0x502a3b, 0x43b240):
/// the target shows a one-pixel frame one pixel inside its selection rectangle, on at 0, 200 and 400 ms for 100 ms
/// each; red for an attack, a capture and the card's orders, blue for a guard, green for the other right clicks.
/// </summary>
public static class OrderBlink
{
    /// <summary>How long the blinking lasts, in milliseconds.</summary>
    public const int DurationMs = 500;

    /// <summary>Whether the frame shows <paramref name="ageMs"/> after the order.</summary>
    public static bool On(long ageMs) => ageMs is >= 0 and < DurationMs && (ageMs / 100) % 2 == 0;

    /// <summary>The colour for <paramref name="orders"/>, given with the card's <paramref name="mode"/> (none: a right click).</summary>
    public static BlinkColour Of(OrderMode mode, IReadOnlyList<PlannedCommand> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);
        if (mode == OrderMode.Guard)
        {
            return BlinkColour.Blue;
        }

        if (mode != OrderMode.None)
        {
            return BlinkColour.Red;
        }

        foreach (PlannedCommand c in orders)
        {
            if (c.Kind is CommandKind.Attack or CommandKind.Capture)
            {
                return BlinkColour.Red;
            }
        }

        return BlinkColour.Green;
    }
}
