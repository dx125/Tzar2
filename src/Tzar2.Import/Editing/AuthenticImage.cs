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
/// (the original draws shadows as darkened, offset silhouettes; ⚠ the offset is a placeholder, UNVERIFIED).
/// </summary>
public static class AuthenticImage
{
    /// <summary>Default shadow offset in pixels (right, down).</summary>
    public const int ShadowOffset = 4;

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
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sx = x - ShadowOffset, sy = y - ShadowOffset;
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
