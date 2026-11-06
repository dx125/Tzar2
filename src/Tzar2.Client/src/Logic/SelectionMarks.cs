using System;
using System.Collections.Generic;

namespace Tzar2.Client.Logic;

/// <summary>
/// The original's selection mark (HUD-007; ReTzar's reading): a corner bracket at each corner of the object's selection
/// box, six pixels long (at most the box's side), one pixel thick.
/// </summary>
public static class SelectionMarks
{
    /// <summary>The length of a bracket's arm, in map pixels.</summary>
    public const float Arm = 6f;

    /// <summary>The eight one-pixel bars of the brackets round the box (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>, <paramref name="height"/>).</summary>
    public static IEnumerable<(float X, float Y, float Width, float Height)> Brackets(float x, float y, float width, float height)
    {
        float lx = Math.Min(Arm, width), ly = Math.Min(Arm, height);
        float right = x + width - 1, bottom = y + height - 1;
        yield return (x, y, lx, 1);
        yield return (x + width - lx, y, lx, 1);
        yield return (x, bottom, lx, 1);
        yield return (x + width - lx, bottom, lx, 1);
        yield return (x, y, 1, ly);
        yield return (right, y, 1, ly);
        yield return (x, y + height - ly, 1, ly);
        yield return (right, y + height - ly, 1, ly);
    }
}
