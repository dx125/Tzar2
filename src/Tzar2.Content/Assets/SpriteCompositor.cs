using System;
using System.Collections.Generic;

namespace Tzar2.Content.Assets;

/// <summary>
/// CPU reference expansion of sprites to RGBA8, for debugging (<c>t2 dump</c>), golden tests and as the specification
/// of what the sprite shader does: indexed pixels look up the palette, the player range is replaced by a 16-colour
/// ramp, baked-shadow pixels and shadow-mask pixels become <see cref="Rgba32.ShadowDebug"/> when requested.
/// </summary>
public static class SpriteCompositor
{
    /// <summary>Decodes one RGB5A1 word.</summary>
    public static Rgba32 DecodeRgb5A1(int value) => (value & 0x8000) == 0 ? Rgba32.Transparent : Rgba32.FromRgb555(value & 0x7FFF);

    /// <summary>
    /// Renders every cell into one column of full cells, direction-major (the original sheet layout, without surplus
    /// rows): <see cref="SpriteSheet.CellWidth"/> × (<see cref="SpriteSheet.CellCount"/> × <see cref="SpriteSheet.CellHeight"/>).
    /// </summary>
    /// <param name="sheet">The sheet.</param>
    /// <param name="playerRamp">16 colours replacing the player range, or null to keep the palette's own ramp.</param>
    /// <param name="markShadow">Draw baked shadow as half-transparent black.</param>
    public static byte[] SheetToRgba(SpriteSheet sheet, IReadOnlyList<Rgba32>? playerRamp, bool markShadow)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        int width = sheet.CellWidth;
        byte[] rgba = new byte[(long)width * sheet.CellHeight * sheet.CellCount * 4];
        Rgba32[]? lut = sheet.Format == PixelFormat.Indexed8 ? BuildLut(sheet, playerRamp, markShadow) : null;
        for (int index = 0; index < sheet.CellCount; index++)
        {
            SpriteCell cell = sheet.Cells[index];
            ReadOnlySpan<byte> pixels = sheet.CellPixels(index);
            for (int y = 0; y < cell.Height; y++)
            {
                for (int x = 0; x < cell.Width; x++)
                {
                    int i = (y * cell.Width) + x;
                    Rgba32 c = sheet.Format switch
                    {
                        PixelFormat.Indexed8 => lut![pixels[i]],
                        PixelFormat.Rgb5A1 => Direct(pixels[2 * i] | (pixels[(2 * i) + 1] << 8), sheet.ShadowColour, markShadow),
                        PixelFormat.Rgba8 => new Rgba32(pixels[4 * i], pixels[(4 * i) + 1], pixels[(4 * i) + 2], pixels[(4 * i) + 3]),
                        _ => pixels[i] == 0 ? Rgba32.Transparent : Rgba32.ShadowDebug,
                    };
                    long o = ((((long)index * sheet.CellHeight) + cell.Y + y) * width + cell.X + x) * 4;
                    rgba[o] = c.R;
                    rgba[o + 1] = c.G;
                    rgba[o + 2] = c.B;
                    rgba[o + 3] = c.A;
                }
            }
        }

        return rgba;
    }

    private static Rgba32 Direct(int value, int shadowColour, bool markShadow)
    {
        if ((value & 0x8000) == 0)
        {
            return Rgba32.Transparent;
        }

        return markShadow && (value & 0x7FFF) == shadowColour ? Rgba32.ShadowDebug : Rgba32.FromRgb555(value & 0x7FFF);
    }

    private static Rgba32[] BuildLut(SpriteSheet sheet, IReadOnlyList<Rgba32>? playerRamp, bool markShadow)
    {
        var lut = new Rgba32[256];
        for (int i = 0; i < 256; i++)
        {
            Rgba32 c = sheet.Palette[i];
            if (c.A != 0 && playerRamp is not null && sheet.IsPlayerIndex(i) && i - sheet.PlayerFirst < playerRamp.Count)
            {
                Rgba32 p = playerRamp[i - sheet.PlayerFirst];
                c = new Rgba32(p.R, p.G, p.B, 255);
            }

            lut[i] = c.A == 0 ? Rgba32.Transparent : c;
        }

        if (markShadow)
        {
            foreach (int i in sheet.ShadowIndices)
            {
                if (lut[i].A != 0)
                {
                    lut[i] = Rgba32.ShadowDebug;
                }
            }
        }

        return lut;
    }
}
