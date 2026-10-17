using System;
using System.Collections.Generic;
using Tzar2.Content.Assets;
using Tzar2.Formats;
using Tzar2.Formats.Rle;

namespace Tzar2.Import.Pipeline;

/// <summary>
/// Converts a decoded <see cref="RleSheet"/> to the lossless trimmed <see cref="SpriteSheet"/> layout of ADR 0013:
/// kind 1 → Indexed8 (+ palette, player range 240–255, baked-shadow palette entries for unit sheets),
/// kind 3 → RGB5A1 (baked shadow 0x0020 for unit sheets), kind 7 → R8 mask.
/// </summary>
public static class SpriteConverter
{
    /// <summary>First palette index of the player ramp in indexed sheets (docs/formats/rle.md §5.2).</summary>
    public const int PlayerFirst = 240;

    /// <summary>The baked unit shadow colour of RGB555 sheets (docs/formats/rle.md §5.4).</summary>
    public const int BakedShadowRgb555 = 0x0020;

    /// <summary>Largest trimmed cell side: one texture-array layer (ADR 0013).</summary>
    public const int MaxCellSide = 2048;

    /// <summary>Largest trimmed pixel payload of one sheet in bytes (the reference build's largest is about 6 MB).</summary>
    public const long MaxSheetBytes = 1L << 27;

    /// <summary>Converts <paramref name="rle"/>.</summary>
    /// <param name="rle">The decoded sheet.</param>
    /// <param name="isUnitSheet">Whether the baked-shadow heuristic applies (sheets under <c>IMAGES/UNIT</c>).</param>
    /// <param name="warnings">Receives conversion notes.</param>
    public static SpriteSheet Convert(RleSheet rle, bool isUnitSheet, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(rle);
        ArgumentNullException.ThrowIfNull(warnings);
        if (rle.ExtraRows > 0)
        {
            warnings.Add($"{rle.ExtraRows} surplus row(s) below the last cell ignored");
        }

        PixelFormat format = rle.Kind switch
        {
            RleKind.Indexed => PixelFormat.Indexed8,
            RleKind.Rgb555 => PixelFormat.Rgb5A1,
            _ => PixelFormat.R8,
        };

        int transparent = -1;
        if (format == PixelFormat.Indexed8)
        {
            transparent = ChooseTransparentIndex(rle);
            if (transparent < 0)
            {
                format = PixelFormat.Rgba8;
                warnings.Add(rle.Palette.Count == 256
                    ? "all 256 palette indices are used; stored as RGBA8, so player colours (indices 240-255) can NOT be recoloured"
                    : "all 256 palette indices are used; stored as RGBA8");
            }
        }

        var bounds = new CellBounds?[rle.CellCount];
        long total = 0;
        int bpp = PixelFormats.BytesPerPixel(format);
        for (int c = 0; c < rle.CellCount; c++)
        {
            bounds[c] = rle.GetCellBounds(c);
            if (bounds[c] is { } b)
            {
                if (b.Width > MaxCellSide || b.Height > MaxCellSide)
                {
                    throw new LegacyFormatException("RLE", $"cell {c} is {b.Width}×{b.Height} after trimming; cells must fit a {MaxCellSide}² texture layer (ADR 0013).");
                }

                total = checked(total + ((long)b.Width * b.Height * bpp));
                if (total > MaxSheetBytes)
                {
                    throw new LegacyFormatException("RLE", $"trimmed cells exceed {MaxSheetBytes} bytes.");
                }
            }
        }

        byte[] pixels = new byte[total];
        var cells = new SpriteCell[rle.CellCount];
        int offset = 0;
        bool highBit = false;
        Rgba32[] palette = format is PixelFormat.Indexed8 or PixelFormat.Rgba8 ? BuildPalette(rle, transparent) : [];
        for (int c = 0; c < rle.CellCount; c++)
        {
            if (bounds[c] is not { } b)
            {
                cells[c] = new SpriteCell(0, 0, 0, 0, offset);
                continue;
            }

            cells[c] = new SpriteCell(b.X0, b.Y0, b.Width, b.Height, offset);
            int cellBytes = b.Width * b.Height * bpp;
            Span<byte> cellPixels = pixels.AsSpan(offset, cellBytes);
            if (format == PixelFormat.Indexed8)
            {
                cellPixels.Fill((byte)transparent);
            }

            for (int row = b.Y0; row <= b.Y1; row++)
            {
                int rowBase = (row - b.Y0) * b.Width;
                foreach (RleRun run in rle.CellRowRuns(c, row))
                {
                    for (int i = 0; i < run.Count; i++)
                    {
                        int p = rowBase + run.X - b.X0 + i;
                        switch (format)
                        {
                            case PixelFormat.Indexed8:
                                cellPixels[p] = rle.IndexAt(run, i);
                                break;
                            case PixelFormat.Rgba8:
                                Rgba32 colour = palette[rle.IndexAt(run, i)];
                                cellPixels[(4 * p) + 0] = colour.R;
                                cellPixels[(4 * p) + 1] = colour.G;
                                cellPixels[(4 * p) + 2] = colour.B;
                                cellPixels[(4 * p) + 3] = 255;
                                break;
                            case PixelFormat.Rgb5A1:
                                ushort v = rle.Rgb555At(run, i);
                                highBit |= (v & 0x8000) != 0;
                                int packed = (v & 0x7FFF) | 0x8000;
                                cellPixels[2 * p] = (byte)packed;
                                cellPixels[(2 * p) + 1] = (byte)(packed >> 8);
                                break;
                            default:
                                cellPixels[p] = 1;
                                break;
                        }
                    }
                }
            }

            offset += cellBytes;
        }

        if (highBit)
        {
            warnings.Add("RGB555 pixels with bit 15 set; bit 15 dropped");
        }

        bool fullPalette = format == PixelFormat.Indexed8 && rle.Palette.Count == 256;
        return new SpriteSheet
        {
            Format = format,
            SourceKind = (byte)rle.Kind,
            Directions = rle.Directions,
            FramesPerDirection = rle.FramesPerDirection,
            CellWidth = rle.Width,
            CellHeight = rle.CellHeight,
            Palette = format == PixelFormat.Indexed8 ? palette : [],
            PlayerFirst = fullPalette ? PlayerFirst : SpriteSheet.None,
            PlayerCount = fullPalette ? 16 : 0,
            ShadowColour = isUnitSheet && format == PixelFormat.Rgb5A1 ? BakedShadowRgb555 : SpriteSheet.None,
            ShadowIndices = isUnitSheet && format == PixelFormat.Indexed8 ? ShadowIndices(rle, transparent) : [],
            Cells = cells,
            PixelData = pixels,
        };
    }

    /// <summary>
    /// The baked-shadow test of the prototype (<c>is_shadow_rgb</c>): the colour quantised to RGB555 has R5 = 0,
    /// B5 = 0 and G5 ≤ 2.
    /// </summary>
    public static bool IsBakedShadowColour(Rgb24 c) => (c.R >> 3) == 0 && (c.B >> 3) == 0 && (c.G >> 3) <= 2;

    private static int ChooseTransparentIndex(RleSheet rle)
    {
        Span<bool> used = stackalloc bool[256];
        for (int y = 0; y < rle.Height; y++)
        {
            foreach (RleRun run in rle.RowRuns(y))
            {
                for (int i = 0; i < run.Count; i++)
                {
                    used[rle.IndexAt(run, i)] = true;
                }
            }
        }

        if (rle.KeyColour < 256 && !used[(int)rle.KeyColour])
        {
            return (int)rle.KeyColour;
        }

        for (int i = 0; i < 256; i++)
        {
            if (!used[i])
            {
                return i;
            }
        }

        return -1;
    }

    private static Rgba32[] BuildPalette(RleSheet rle, int transparent)
    {
        var palette = new Rgba32[256];
        for (int i = 0; i < 256; i++)
        {
            Rgb24 c = i < rle.Palette.Count ? rle.Palette[i] : default;
            palette[i] = new Rgba32(c.R, c.G, c.B, i == transparent ? (byte)0 : (byte)255);
        }

        return palette;
    }

    private static List<int> ShadowIndices(RleSheet rle, int transparent)
    {
        var result = new List<int>();
        for (int i = 0; i < Math.Min(rle.Palette.Count, PlayerFirst); i++)
        {
            if (i != transparent && IsBakedShadowColour(rle.Palette[i]))
            {
                result.Add(i);
            }
        }

        return result;
    }
}
