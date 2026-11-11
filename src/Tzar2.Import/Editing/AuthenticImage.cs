using System;
using System.Collections.Generic;

namespace Tzar2.Import.Editing;

/// <summary>The result of an "authentic" asset conversion.</summary>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="Rgba">The image quantised to RGB555 (bit replication back to 8 bits), RGBA, row-major.</param>
/// <param name="OutsidePalette">Opaque pixels whose colour is farther than the tolerance from every palette colour.</param>
/// <param name="Shadow">A shadow sprite from the silhouette (RGBA, black at half alpha), or null.</param>
public sealed record AuthenticResult(int Width, int Height, byte[] Rgba, int OutsidePalette, byte[]? Shadow);

/// <summary>
/// The optional "authentic" conversion of imported user images (TDD §18.3 SHOULD): quantise to RGB555 like the original's
/// 16-bit art, warn about colours outside the terrain set's palette, and generate a shadow sprite from the silhouette
/// (PRES-023: the original's building shadows are their silhouettes moved right, east, by about a tenth of their height
/// and a little down, of which only the part the body does not cover is stored; measured on 44 buildings).
/// </summary>
public static class AuthenticImage
{
    /// <summary>The shadow's move right as a share of the silhouette's height (PRES-023: median 0.09 of 44 buildings).</summary>
    public const int ShadowRightPerMille = 100;

    /// <summary>The shadow's move down as a share of the silhouette's height (PRES-023: a little; 0.05).</summary>
    public const int ShadowDownPerMille = 50;

    /// <summary>The shadow's move (right, down) for a silhouette <paramref name="height"/> pixels tall: at least 1 px right.</summary>
    public static (int Right, int Down) ShadowOffset(int height)
        => (Math.Max(1, ((height * ShadowRightPerMille) + 500) / 1000), ((height * ShadowDownPerMille) + 500) / 1000);

    /// <summary>Converts an RGBA image.</summary>
    /// <param name="rgba">Pixels, 4 bytes each, row-major.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="palette">Reference colours (r, g, b), or null to skip the palette check.</param>
    /// <param name="tolerance">Largest per-channel distance from a palette colour that counts as inside.</param>
    /// <param name="shadow">Whether to generate a shadow sprite.</param>
    public static AuthenticResult Convert(ReadOnlySpan<byte> rgba, int width, int height, IReadOnlyList<(byte R, byte G, byte B)>? palette = null, int tolerance = 24, bool shadow = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"Expected {width * height * 4} bytes, got {rgba.Length}.", nameof(rgba));
        }

        byte[] output = new byte[rgba.Length];
        int outside = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            byte r = Q(rgba[i]), g = Q(rgba[i + 1]), b = Q(rgba[i + 2]);
            bool opaque = rgba[i + 3] >= 128;
            output[i] = r;
            output[i + 1] = g;
            output[i + 2] = b;
            output[i + 3] = opaque ? (byte)255 : (byte)0; // RGB5A1: one bit of alpha
            if (opaque && palette is { Count: > 0 } && !Near(palette, r, g, b, tolerance))
            {
                outside++;
            }
        }

        byte[]? shadowRgba = null;
        if (shadow)
        {
            shadowRgba = new byte[rgba.Length];
            int top = height, bottom = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (output[(((y * width) + x) * 4) + 3] != 0)
                    {
                        top = Math.Min(top, y);
                        bottom = y;
                        break;
                    }
                }
            }

            (int right, int down) = ShadowOffset(bottom >= top ? bottom - top + 1 : 0);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sx = x - right, sy = y - down;
                    bool caster = sx >= 0 && sy >= 0 && output[(((sy * width) + sx) * 4) + 3] != 0;
                    bool self = output[(((y * width) + x) * 4) + 3] != 0;
                    if (caster && !self)
                    {
                        shadowRgba[(((y * width) + x) * 4) + 3] = 128;
                    }
                }
            }
        }

        return new AuthenticResult(width, height, output, outside, shadowRgba);
    }

    // 8 → 5 → 8 bits by bit replication (reversible for 5-bit values).
    private static byte Q(byte c)
    {
        int five = c >> 3;
        return (byte)((five << 3) | (five >> 2));
    }

    private static bool Near(IReadOnlyList<(byte R, byte G, byte B)> palette, byte r, byte g, byte b, int tolerance)
    {
        foreach ((byte pr, byte pg, byte pb) in palette)
        {
            if (Math.Abs(pr - r) <= tolerance && Math.Abs(pg - g) <= tolerance && Math.Abs(pb - b) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }
}
