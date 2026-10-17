using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Tzar2.Formats.Bmp;

/// <summary>
/// An uncompressed Windows BMP (docs/formats/bmp.md §1): 8 bpp with palette, 16 bpp RGB555 (<c>BI_RGB</c>, or
/// <c>BI_BITFIELDS</c> with the 555 masks), 24 or 32 bpp. Pixels are exposed top-down.
/// </summary>
public sealed class BmpImage
{
    private const string Format = "BMP";

    private BmpImage(int width, int height, int bitsPerPixel, Rgb24[] palette, byte[] pixels)
    {
        Width = width;
        Height = height;
        BitsPerPixel = bitsPerPixel;
        Palette = palette;
        Pixels = pixels;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>8, 16, 24 or 32.</summary>
    public int BitsPerPixel { get; }

    /// <summary>The palette between the header and the pixel data (8 bpp; may be empty, e.g. in fonts).</summary>
    public IReadOnlyList<Rgb24> Palette { get; }

    /// <summary>
    /// Tightly packed top-down pixels: 8 bpp = 1 byte index; 16 bpp = little-endian RGB555 (2 bytes);
    /// 24/32 bpp = R, G, B (3 bytes, alpha of 32-bit files dropped).
    /// </summary>
    public byte[] Pixels { get; }

    /// <summary>Bytes per pixel in <see cref="Pixels"/>.</summary>
    public int PixelStride => BitsPerPixel switch
    {
        8 => 1,
        16 => 2,
        _ => 3,
    };

    /// <summary>Palette index at (x, y) of an 8-bit image.</summary>
    public byte IndexAt(int x, int y) => Pixels[(y * Width) + x];

    /// <summary>RGB555 value at (x, y) of a 16-bit image.</summary>
    public ushort Rgb555At(int x, int y) => BinaryPrimitives.ReadUInt16LittleEndian(Pixels.AsSpan(((y * Width) + x) * 2));

    /// <summary>Parses a BMP file.</summary>
    /// <exception cref="LegacyFormatException">The data is malformed or uses an unsupported variant.</exception>
    public static BmpImage Parse(ReadOnlySpan<byte> data)
    {
        if (!ByteReader.Matches(data, 0, "BM"u8))
        {
            throw new LegacyFormatException(Format, "missing 'BM' signature.");
        }

        uint offBits = ByteReader.U32(data, 10, Format, "bfOffBits");
        uint infoSize = ByteReader.U32(data, 14, Format, "biSize");
        if (infoSize < 40 || infoSize > 1024)
        {
            throw new LegacyFormatException(Format, $"unsupported info header size {infoSize}.");
        }

        ByteReader.Require(data, 14, infoSize, Format, "info header");
        int width = ByteReader.I32(data, 18, Format, "biWidth");
        int rawHeight = ByteReader.I32(data, 22, Format, "biHeight");
        ushort bpp = ByteReader.U16(data, 28, Format, "biBitCount");
        uint compression = ByteReader.U32(data, 30, Format, "biCompression");
        uint colorsUsed = ByteReader.U32(data, 46, Format, "biClrUsed");
        if (width <= 0 || rawHeight == 0 || rawHeight == int.MinValue || width > 65535 || Math.Abs(rawHeight) > 65535)
        {
            throw new LegacyFormatException(Format, $"invalid size {width}×{rawHeight}.");
        }

        if (bpp is not (8 or 16 or 24 or 32))
        {
            throw new LegacyFormatException(Format, $"unsupported bit depth {bpp}.");
        }

        if (compression == 3 && bpp == 16)
        {
            // BI_BITFIELDS: accept only the RGB555 masks; they follow a 40-byte header.
            uint r = ByteReader.U32(data, 54, Format, "red mask");
            uint g = ByteReader.U32(data, 58, Format, "green mask");
            uint b = ByteReader.U32(data, 62, Format, "blue mask");
            if (r != 0x7C00 || g != 0x03E0 || b != 0x001F)
            {
                throw new LegacyFormatException(Format, $"unsupported 16-bit masks {r:X4}/{g:X4}/{b:X4} (only RGB555).");
            }
        }
        else if (compression != 0)
        {
            throw new LegacyFormatException(Format, $"unsupported compression {compression}.");
        }

        int height = Math.Abs(rawHeight);
        bool bottomUp = rawHeight > 0;
        long stride = (((long)width * bpp) + 31) / 32 * 4;
        ByteReader.Require(data, offBits, stride * height, Format, "pixel data");

        Rgb24[] palette = [];
        if (bpp == 8)
        {
            long paletteStart = 14 + infoSize;
            long available = Math.Max(0, ((long)offBits - paletteStart) / 4);
            long count = Math.Min(Math.Min(colorsUsed == 0 ? 256 : colorsUsed, available), 256);
            palette = new Rgb24[count];
            for (int i = 0; i < count; i++)
            {
                long p = paletteStart + (4 * i);
                ByteReader.Require(data, p, 4, Format, "palette");
                palette[i] = new Rgb24(data[(int)p + 2], data[(int)p + 1], data[(int)p]);
            }
        }

        int outStride = bpp switch
        {
            8 => 1,
            16 => 2,
            _ => 3,
        };
        byte[] pixels = new byte[(long)width * height * outStride];
        for (int y = 0; y < height; y++)
        {
            int sourceRow = bottomUp ? height - 1 - y : y;
            ReadOnlySpan<byte> src = data.Slice((int)(offBits + (sourceRow * stride)), (int)stride);
            Span<byte> dst = pixels.AsSpan(y * width * outStride, width * outStride);
            switch (bpp)
            {
                case 8:
                case 16:
                    src[..(width * outStride)].CopyTo(dst);
                    break;
                default:
                    int inStride = bpp / 8;
                    for (int x = 0; x < width; x++)
                    {
                        dst[(3 * x) + 0] = src[(inStride * x) + 2];
                        dst[(3 * x) + 1] = src[(inStride * x) + 1];
                        dst[(3 * x) + 2] = src[inStride * x];
                    }

                    break;
            }
        }

        return new BmpImage(width, height, bpp, palette, pixels);
    }
}
