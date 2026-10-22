using System;
using System.Collections.Generic;

namespace Tzar2.Client.Logic;

/// <summary>
/// One display the UI smoke test checks (ADR 0200): a window, the display preset and the interface scale, and the logical
/// screen and its visible part they give.
/// </summary>
/// <param name="WindowWidth">Window width in pixels.</param>
/// <param name="WindowHeight">Window height in pixels.</param>
/// <param name="Authentic">The Authentic preset (1024 × 768) rather than Wide.</param>
/// <param name="Setting">The interface scale setting of Wide: 0 Auto, 1-4.</param>
/// <param name="Scale">The whole-number scale the window shows the logical screen at.</param>
/// <param name="Width">Logical screen width.</param>
/// <param name="Height">Logical screen height.</param>
/// <param name="Visible">The part of the logical screen the window shows (left, top, right, bottom): all of it, unless the
/// window is smaller than the logical screen at scale 1; then its top-left part.</param>
public readonly record struct DisplayCase(int WindowWidth, int WindowHeight, bool Authentic, int Setting, int Scale, int Width, int Height, (int Left, int Top, int Right, int Bottom) Visible)
{
    /// <summary>A name for reports and screenshot files: <c>1920x1080-wide-auto</c>, <c>1280x720-authentic</c>.</summary>
    public string Name => $"{WindowWidth}x{WindowHeight}-" + (Authentic ? "authentic" : "wide-" + (Setting == 0 ? "auto" : Setting + "x"));
}

/// <summary>
/// The displays of the UI review (STATUS work queue 15, ADR 0200) and the logical screen each gives under the rules of
/// <see cref="DisplayMath"/> and Godot's integer stretch with kept aspect.
/// </summary>
public static class DisplayCases
{
    /// <summary>
    /// The window sizes of the review: 4:3 1024 × 768, then the common modern screens from 720p to 4K (16:9, 16:10 and
    /// 21:9).
    /// </summary>
    public static readonly (int Width, int Height)[] Resolutions =
    [
        (1024, 768), (1280, 720), (1366, 768), (1600, 900), (1920, 1080), (1920, 1200), (2560, 1440), (3440, 1440), (3840, 2160),
    ];

    /// <summary>
    /// The display of one window: Wide with interface scale <paramref name="setting"/> (0 Auto, 1-4), or Authentic.
    /// Godot's integer stretch never scales below 1: a window smaller than the logical screen shows its top-left part.
    /// </summary>
    public static DisplayCase For(int width, int height, bool authentic, int setting)
    {
        int lw, lh;
        if (authentic)
        {
            (lw, lh) = DisplayMath.Classic;
        }
        else
        {
            int k = DisplayMath.Scale(width, height, setting);
            (lw, lh) = DisplayMath.Logical(width, height, k);
            lw = Math.Max(lw, DisplayMath.MinWidth);
            lh = Math.Max(lh, DisplayMath.MinHeight);
        }

        // Godot's integer stretch never goes below 1: a window smaller than the logical screen shows its top-left part
        // (measured with Godot 4.7.2 on macOS, 2026-10-22).
        int scale = Math.Max(1, Math.Min(width / lw, height / lh));
        return new DisplayCase(width, height, authentic, authentic ? 0 : setting, scale, lw, lh, (0, 0, Math.Min(lw, width / scale), Math.Min(lh, height / scale)));
    }

    /// <summary>
    /// Every display of the review: each resolution in Wide at Auto and at every interface scale it allows (1-4; the
    /// scales Windows' 100-200 % display scaling asks for are among them), and in Authentic. Settings that give the same
    /// logical screen as one already listed for that window are left out.
    /// </summary>
    public static List<DisplayCase> All()
    {
        var cases = new List<DisplayCase>();
        foreach ((int w, int h) in Resolutions)
        {
            var seen = new HashSet<(int, int)>();
            for (int setting = 0; setting <= DisplayMath.MaxScale; setting++)
            {
                DisplayCase c = For(w, h, authentic: false, setting);
                if (seen.Add((c.Width, c.Height)))
                {
                    cases.Add(c);
                }
            }

            cases.Add(For(w, h, authentic: true, 0));
        }

        return cases;
    }
}
