using System;
using Tzar2.Content.Compiled;

namespace Tzar2.Client.Logic;

/// <summary>Engine-free sprite choices of the world renderer (unit-tested).</summary>
public static class SpriteRules
{
    /// <summary>
    /// The sheet column of a sim facing. Sim: 0 = east, clockwise (ADR 0005). 8-direction sheets: S, SE, E, NE, N, NW,
    /// W, SW (counter-clockwise from south, as the viewer labels them).
    /// </summary>
    public static int Direction(int facing, int directions)
        => directions < 8 ? 0 : (((2 - facing) % 8) + 8) % 8;

    /// <summary>The construction-site sheet for a footprint (<c>build1x1</c> … <c>build8x8</c> of IMAGES.INI).</summary>
    public static string BuildSiteName(EntityTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        int n = Math.Clamp(Math.Max(type.FootprintWidth, type.FootprintHeight), 1, 8);
        return $"build{n}x{n}";
    }

    /// <summary>The construction stage frame (0 … frames − 1) of a site at <paramref name="permille"/> progress.</summary>
    public static int SiteFrame(int permille, int frames)
        => Math.Clamp(permille * frames / 1000, 0, Math.Max(0, frames - 1));

    /// <summary>
    /// The depletion frame of a rock (frame 0 = full) holding <paramref name="left"/> of <paramref name="full"/>
    /// (⚠ the original's thresholds are not measured).
    /// </summary>
    public static int DepletionFrame(int left, int full, int frames)
    {
        if (full <= 0 || frames <= 1)
        {
            return 0;
        }

        int remaining = (int)Math.Clamp(left * (long)frames / full, 0, frames - 1);
        return frames - 1 - remaining;
    }
}
