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

    /// <summary>
    /// The picture of a structure turned by 90° (BLD-019): a bridge's <c>bridge_horz</c> becomes <c>bridge_vert</c>; others
    /// keep theirs.
    /// </summary>
    public static string? Turned(string? image, bool rotated)
        => rotated && image is not null && image.Contains("_horz", StringComparison.Ordinal) ? image.Replace("_horz", "_vert", StringComparison.Ordinal) : image;

    /// <summary>
    /// A gate's drawbridge picture for its facing (0 and 1 across, 2 and 3 down; CTRL-024): IMAGES.INI <c>gw0_anim</c> …
    /// <c>gw3_anim</c> for wooden gates, <c>gs…</c> for stone ones, <c>…_build</c> for sites; null for what is no gate.
    /// </summary>
    public static string? GateImage(EntityTypeDefinition type, int facing, bool site)
    {
        ArgumentNullException.ThrowIfNull(type);
        string? material = type.Traits.LegacyClass switch
        {
            "gate_wood" => "gw",
            "gate_stone" => "gs",
            _ => null,
        };
        return material is null ? null : $"{material}{facing & 3}_{(site ? "build" : "anim")}";
    }

    /// <summary>The drawbridge's frame: raised (closed) on row 0, lowered (open) on row 6 (ReTzar's reading, 0x456080).</summary>
    public static int GateFrame(bool open) => open ? 6 : 0;

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
