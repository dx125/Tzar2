using System;
using System.Collections.Generic;

namespace Tzar2.Content.Assets;

/// <summary>
/// Turns a <c>.t2img</c> that <c>IMAGES.INI</c> uses as a sprite (building <c>_c</c> overlays, tree strips, BMP shadows,
/// effects) into a trimmed <see cref="SpriteSheet"/>, so the renderer handles BMP and RLE sprites the same way.
/// BMP sprite grids are <c>cols</c> cells across and <c>rows</c> cells down (bmp.md §2: <c>rows</c> frames stacked
/// vertically; <c>cols</c> variants side by side, e.g. <c>euro houses c</c> 288 × 96 with <c>cols = 3</c>); a cell
/// <c>(column, row)</c> becomes direction <c>column</c>, frame <c>row</c>.
/// </summary>
public static class ImageSheets
{
    /// <summary>First index of the 16-step player ramp in 8-bit sprite palettes (pal.md, bmp.md §2).</summary>
    public const int PlayerFirst = 240;

    /// <summary>Converts <paramref name="image"/> into a sheet of <paramref name="columns"/> × <paramref name="rows"/> cells.</summary>
    /// <param name="image">The image.</param>
    /// <param name="columns">Cells across (directions); clamped to the image width.</param>
    /// <param name="rows">Cells down (frames); clamped to the image height.</param>
    /// <param name="asShadowMask">Store opaque pixels as an R8 shadow mask (BMP shadow sprites such as <c>trees_s</c>).</param>
    /// <param name="playerColours">Mark indices 240–255 of an indexed image as the player range (building <c>_c</c> overlays).</param>
    public static SpriteSheet FromImage(ImageAsset image, int columns, int rows, bool asShadowMask = false, bool playerColours = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        columns = Math.Clamp(columns, 1, Math.Max(1, image.Width));
        rows = Math.Clamp(rows, 1, Math.Max(1, image.Height));
        int cellWidth = image.Width / columns;
        int cellHeight = image.Height / rows;
        PixelFormat format = asShadowMask ? PixelFormat.R8 : image.Format;
        int srcBpp = PixelFormats.BytesPerPixel(image.Format);
        int dstBpp = PixelFormats.BytesPerPixel(format);
        var cells = new SpriteCell[columns * rows];
        var data = new List<byte>();
        for (int dir = 0; dir < columns; dir++)
        {
            for (int frame = 0; frame < rows; frame++)
            {
                int ox = dir * cellWidth, oy = frame * cellHeight;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                for (int y = 0; y < cellHeight; y++)
                {
                    for (int x = 0; x < cellWidth; x++)
                    {
                        if (IsOpaque(image, ox + x, oy + y, srcBpp))
                        {
                            minX = Math.Min(minX, x);
                            minY = Math.Min(minY, y);
                            maxX = Math.Max(maxX, x);
                            maxY = Math.Max(maxY, y);
                        }
                    }
                }

                int index = (dir * rows) + frame;
                if (maxX < 0)
                {
                    cells[index] = new SpriteCell(0, 0, 0, 0, data.Count);
                    continue;
                }

                cells[index] = new SpriteCell(minX, minY, maxX - minX + 1, maxY - minY + 1, data.Count);
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        int src = ((((oy + y) * image.Width) + ox + x) * srcBpp);
                        if (asShadowMask)
                        {
                            data.Add(IsOpaque(image, ox + x, oy + y, srcBpp) ? (byte)1 : (byte)0);
                        }
                        else
                        {
                            for (int b = 0; b < dstBpp; b++)
                            {
                                data.Add(image.Pixels[src + b]);
                            }
                        }
                    }
                }
            }
        }

        bool indexed = format == PixelFormat.Indexed8;
        return new SpriteSheet
        {
            Format = format,
            SourceKind = 0,
            Directions = columns,
            FramesPerDirection = rows,
            CellWidth = cellWidth,
            CellHeight = cellHeight,
            Palette = indexed ? image.Palette : [],
            PlayerFirst = indexed && playerColours ? PlayerFirst : SpriteSheet.None,
            PlayerCount = indexed && playerColours ? PlayerPalette.RampLength : 0,
            Cells = cells,
            PixelData = [.. data],
        };
    }

    /// <summary>Whether pixel (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="image"/> is opaque.</summary>
    public static bool IsOpaque(ImageAsset image, int x, int y, int bytesPerPixel)
    {
        ArgumentNullException.ThrowIfNull(image);
        int i = ((y * image.Width) + x) * bytesPerPixel;
        return image.Format switch
        {
            PixelFormat.Indexed8 => image.Palette[image.Pixels[i]].A != 0,
            PixelFormat.Rgb5A1 => (image.Pixels[i + 1] & 0x80) != 0,
            PixelFormat.Rgba8 => image.Pixels[i + 3] != 0,
            _ => image.Pixels[i] != 0,
        };
    }
}
