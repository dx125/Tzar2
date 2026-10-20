using System;

namespace Tzar2.Client.Logic;

/// <summary>
/// The arithmetic of the display presets (ADR 0177), engine-free: the interface scale of the Wide preset and the
/// logical screen it gives. The HUD needs a logical screen of at least <see cref="MinWidth"/> × <see cref="MinHeight"/>.
/// </summary>
public static class DisplayMath
{
    /// <summary>The narrowest logical screen: the HUD's left part (805 px) and its right ornament (219 px).</summary>
    public const int MinWidth = 1024;

    /// <summary>The lowest logical screen Auto allows: the HUD bars (184 px) and a useful view.</summary>
    public const int MinHeight = 720;

    /// <summary>The largest interface scale offered.</summary>
    public const int MaxScale = 4;

    /// <summary>The original's logical screen (Authentic preset).</summary>
    public static (int Width, int Height) Classic => (1024, 768);

    /// <summary>
    /// The interface scale for a window of <paramref name="width"/> × <paramref name="height"/>: the chosen
    /// <paramref name="setting"/> (1-4) or, for Auto (0), the largest whole number whose logical screen is at least
    /// <see cref="MinWidth"/> × <see cref="MinHeight"/>. Never so large that the logical screen gets narrower or lower than
    /// that, and never less than 1.
    /// </summary>
    public static int Scale(int width, int height, int setting)
    {
        int fits = Math.Max(1, Math.Min(width / MinWidth, height / MinHeight));
        return setting <= 0 ? fits : Math.Clamp(setting, 1, fits);
    }

    /// <summary>The logical screen of the Wide preset: the window divided by the scale (whole pixels).</summary>
    public static (int Width, int Height) Logical(int width, int height, int scale)
        => (Math.Max(1, width / Math.Max(1, scale)), Math.Max(1, height / Math.Max(1, scale)));
}
