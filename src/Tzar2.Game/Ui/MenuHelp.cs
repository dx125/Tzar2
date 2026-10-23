using System;

namespace Tzar2.Game.Ui;

/// <summary>
/// The help ball of the menu screens and their dialogs (SHELL-026), as ReTzar reads the original (0x45c800, 0x45c340,
/// 0x45c6c6): a press on the ball turns hint mode on (the ball turns red) or off (green); in hint mode the screen takes
/// no other press, and the control under the pointer shows its <c>LOCAL/HELP.INI</c> text in a box 20 pixels below the
/// pointer, as wide as the text plus 20, pushed left in proportion to the pointer's place so it stays on the screen.
/// </summary>
public sealed class MenuHelp
{
    /// <summary>The box's height.</summary>
    public const int BoxHeight = 16;

    /// <summary>Whether hint mode is on.</summary>
    public bool On { get; private set; }

    /// <summary>A press on the ball.</summary>
    public void Toggle() => On = !On;

    /// <summary>Whether a press on a control (not the ball) does anything now: not in hint mode.</summary>
    public bool Allows => !On;

    /// <summary>
    /// Where the hint box of <paramref name="textWidth"/> goes for the pointer at (<paramref name="x"/>, <paramref name="y"/>)
    /// on a screen of <paramref name="width"/> × <paramref name="height"/>: its top left and its width.
    /// </summary>
    public static (int X, int Y, int Width) Box(int x, int y, int textWidth, int width, int height)
    {
        int w = textWidth + 20;
        int left = (int)(x - ((long)x * w / Math.Max(1, width)));
        int top = Math.Min(y + 20, height - BoxHeight);
        return (left, top, w);
    }
}
