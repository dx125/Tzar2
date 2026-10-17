using System;
using System.Collections.Generic;
using Tzar2.Content.Assets;
using Tzar2.Formats;
using Tzar2.Formats.Bmp;

namespace Tzar2.Import.Pipeline;

/// <summary>Which colours of a 16/24-bit bitmap are transparency keys (docs/formats/bmp.md §1.2, §3).</summary>
public enum ColourKeyPolicy
{
    /// <summary>No key: terrain base textures and water are opaque.</summary>
    None,

    /// <summary>Green <c>0x03E0</c> is transparent (sprites, icons, UI, trees, overlays).</summary>
    Green,

    /// <summary>Green and magenta <c>0x7C1F</c> are transparent (terrain transition tiles).</summary>
    GreenAndMagenta,
}

/// <summary>
/// Converts a <see cref="BmpImage"/> to a lossless <see cref="ImageAsset"/>: 16 bpp → RGB5A1 (key pixels keep their
/// colour with the opacity bit clear), 8 bpp → Indexed8 (pure-green palette entries get alpha 0), 24/32 bpp → RGBA8.
/// </summary>
public static class ImageConverter
{
    /// <summary>The key policy for a source path (normalised, e.g. <c>images/terrain/grass/12.bmp</c>).</summary>
    public static ColourKeyPolicy PolicyFor(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        string[] parts = sourcePath.Split('/');
        if (parts.Length == 4 && parts[0] == "images" && parts[1] == "terrain")
        {
            string name = parts[3];
            if (name.Length >= 6 && char.IsAsciiDigit(name[0]) && char.IsAsciiDigit(name[1])
                && (name[2] == '.' || name[2] == '_'))
            {
                return name[0] == name[1] ? ColourKeyPolicy.None : ColourKeyPolicy.GreenAndMagenta;
            }
        }

        return ColourKeyPolicy.Green;
    }

    /// <summary>Converts <paramref name="bmp"/> with <paramref name="policy"/>.</summary>
    public static ImageAsset Convert(BmpImage bmp, ColourKeyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        int n = bmp.Width * bmp.Height;
        switch (bmp.BitsPerPixel)
        {
            case 8:
            {
                return new ImageAsset
                {
                    Format = PixelFormat.Indexed8,
                    Width = bmp.Width,
                    Height = bmp.Height,
                    Palette = IndexedPalette(bmp.Palette, policy != ColourKeyPolicy.None),
                    Pixels = (byte[])bmp.Pixels.Clone(),
                };
            }

            case 16:
            {
                byte[] pixels = new byte[n * 2];
                for (int i = 0; i < n; i++)
                {
                    int v = (bmp.Pixels[2 * i] | (bmp.Pixels[(2 * i) + 1] << 8)) & 0x7FFF;
                    bool key = policy switch
                    {
                        ColourKeyPolicy.Green => v == Rgb555.GreenKey,
                        ColourKeyPolicy.GreenAndMagenta => v is Rgb555.GreenKey or Rgb555.MagentaKey,
                        _ => false,
                    };
                    int packed = key ? v : v | 0x8000;
                    pixels[2 * i] = (byte)packed;
                    pixels[(2 * i) + 1] = (byte)(packed >> 8);
                }

                return new ImageAsset { Format = PixelFormat.Rgb5A1, Width = bmp.Width, Height = bmp.Height, Pixels = pixels };
            }

            default:
            {
                byte[] pixels = new byte[n * 4];
                for (int i = 0; i < n; i++)
                {
                    byte r = bmp.Pixels[3 * i], g = bmp.Pixels[(3 * i) + 1], b = bmp.Pixels[(3 * i) + 2];
                    pixels[4 * i] = r;
                    pixels[(4 * i) + 1] = g;
                    pixels[(4 * i) + 2] = b;
                    pixels[(4 * i) + 3] = policy != ColourKeyPolicy.None && r == 0 && g == 255 && b == 0 ? (byte)0 : (byte)255;
                }

                return new ImageAsset { Format = PixelFormat.Rgba8, Width = bmp.Width, Height = bmp.Height, Pixels = pixels };
            }
        }
    }

    /// <summary>A 256-entry palette; pure-green entries become transparent when <paramref name="greenKey"/> is set.</summary>
    public static Rgba32[] IndexedPalette(IReadOnlyList<Rgb24> source, bool greenKey)
    {
        ArgumentNullException.ThrowIfNull(source);
        var palette = new Rgba32[256];
        for (int i = 0; i < 256; i++)
        {
            Rgb24 c = i < source.Count ? source[i] : default;
            palette[i] = new Rgba32(c.R, c.G, c.B, greenKey && i < source.Count && c.IsGreenKey ? (byte)0 : (byte)255);
        }

        return palette;
    }
}
