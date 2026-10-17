using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Tzar2.Tools.Cli.Fidelity.Imaging;

/// <summary>Pixel layout of a capture.</summary>
/// <param name="Bits">16 for the game's own snapshot of its 16-bit surface; 24 or 32 for window captures.</param>
internal readonly record struct PixelFormat(int Bits, uint RedMask, uint GreenMask, uint BlueMask)
{
    public static readonly PixelFormat Rgb555 = new(16, 0x7C00, 0x03E0, 0x001F);
    public static readonly PixelFormat Rgb565 = new(16, 0xF800, 0x07E0, 0x001F);
    public static readonly PixelFormat Rgb888 = new(32, 0xFF0000, 0x00FF00, 0x0000FF);

    public int RedBits => BitOperations.PopCount(RedMask);

    public int GreenBits => BitOperations.PopCount(GreenMask);

    public int BlueBits => BitOperations.PopCount(BlueMask);

    public string Name => Bits == 16
        ? $"rgb{RedBits}{GreenBits}{BlueBits}"
        : $"rgb888 ({Bits}-bit)";
}

/// <summary>
/// A decoded capture (ADR 0030 §capture formats). <see cref="Native"/> keeps the raw pixel value (16-bit surface word,
/// or 0xRRGGBB for 24/32-bit), so nothing is lost; <see cref="Canonical555"/> converts every format to RGB555, the
/// colour space of the original art, by keeping the top 5 bits of each channel. That inverse is exact whenever the
/// display path expands 5-bit (or 6-bit) channels by any rule that preserves the top bits (bit replication or a
/// plain shift), which the colour-depth analysis verifies (UNK-39).
/// </summary>
internal sealed class CaptureImage
{
    public CaptureImage(int width, int height, PixelFormat format, uint[] native)
    {
        if (native.Length != width * height)
        {
            throw new ArgumentException("Pixel count does not match the size.", nameof(native));
        }

        Width = width;
        Height = height;
        Format = format;
        Native = native;
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    /// <summary>Raw pixel values, top-down, row-major.</summary>
    public uint[] Native { get; }

    public uint NativeAt(int x, int y) => Native[(y * Width) + x];

    /// <summary>The channel levels of a native value at the format's own depth (e.g. 5/6/5 bits).</summary>
    public (int R, int G, int B) Levels(uint value) =>
        (Extract(value, Format.RedMask), Extract(value, Format.GreenMask), Extract(value, Format.BlueMask));

    /// <summary>The RGB555 value of pixel <paramref name="index"/> (top 5 bits of each channel).</summary>
    public ushort Canonical555(int index) => To555(Native[index], Format);

    public ushort[] ToCanonical555()
    {
        var result = new ushort[Native.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = To555(Native[i], Format);
        }

        return result;
    }

    public static ushort To555(uint value, PixelFormat format)
    {
        int r = Top5(value, format.RedMask);
        int g = Top5(value, format.GreenMask);
        int b = Top5(value, format.BlueMask);
        return (ushort)((r << 10) | (g << 5) | b);
    }

    private static int Extract(uint value, uint mask) => mask == 0 ? 0 : (int)((value & mask) >> BitOperations.TrailingZeroCount(mask));

    private static int Top5(uint value, uint mask)
    {
        int bits = BitOperations.PopCount(mask);
        int level = Extract(value, mask);
        return bits >= 5 ? level >> (bits - 5) : level << (5 - bits);
    }

    // ---- BMP ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Decodes an uncompressed BMP: 16-bit <c>BI_RGB</c> (RGB555) or <c>BI_BITFIELDS</c> (any masks, e.g. RGB565),
    /// 24-bit, or 32-bit (<c>BI_RGB</c> or bit fields). Bottom-up and top-down files are accepted.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a supported BMP.</exception>
    public static CaptureImage DecodeBmp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M')
        {
            throw new InvalidDataException("not a BMP file");
        }

        uint offBits = BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);
        uint infoSize = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(data[30..]);
        if (infoSize < 40 || width <= 0 || width > 16384 || rawHeight == 0 || rawHeight == int.MinValue || Math.Abs(rawHeight) > 16384)
        {
            throw new InvalidDataException($"unsupported BMP header (info {infoSize}, size {width}×{rawHeight})");
        }

        PixelFormat format;
        if (compression == 3 && bpp is 16 or 32)
        {
            // BI_BITFIELDS: masks follow a 40-byte header, or sit inside a V4/V5 header at the same offsets.
            if (data.Length < 66)
            {
                throw new InvalidDataException("truncated BMP bit fields");
            }

            format = new PixelFormat(bpp, BinaryPrimitives.ReadUInt32LittleEndian(data[54..]), BinaryPrimitives.ReadUInt32LittleEndian(data[58..]),
                BinaryPrimitives.ReadUInt32LittleEndian(data[62..]));
            if (format.RedMask == 0 || format.GreenMask == 0 || format.BlueMask == 0 ||
                (bpp == 16 && ((format.RedMask | format.GreenMask | format.BlueMask) & 0xFFFF0000u) != 0))
            {
                throw new InvalidDataException("invalid BMP bit-field masks");
            }
        }
        else if (compression == 0 && bpp == 16)
        {
            format = PixelFormat.Rgb555;
        }
        else if (compression == 0 && bpp is 24 or 32)
        {
            format = PixelFormat.Rgb888 with { Bits = bpp };
        }
        else
        {
            throw new InvalidDataException($"unsupported BMP variant ({bpp} bpp, compression {compression})");
        }

        int height = Math.Abs(rawHeight);
        long stride = (((long)width * bpp) + 31) / 32 * 4;
        if (offBits + (stride * height) > data.Length)
        {
            throw new InvalidDataException("truncated BMP pixel data");
        }

        var native = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            int srcRow = rawHeight > 0 ? height - 1 - y : y;
            ReadOnlySpan<byte> row = data.Slice((int)(offBits + (srcRow * stride)), (int)stride);
            for (int x = 0; x < width; x++)
            {
                native[(y * width) + x] = bpp switch
                {
                    16 => BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]),
                    24 => (uint)((row[(x * 3) + 2] << 16) | (row[(x * 3) + 1] << 8) | row[x * 3]),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]) & (format.RedMask | format.GreenMask | format.BlueMask),
                };
            }
        }

        return new CaptureImage(width, height, format, native);
    }

    /// <summary>Encodes the image as a top-down BMP in its own format (16-bit as <c>BI_BITFIELDS</c>, 32-bit as BI_RGB).</summary>
    public byte[] EncodeBmp()
    {
        bool sixteen = Format.Bits == 16;
        int bpp = sixteen ? 16 : 32;
        int stride = ((Width * bpp) + 31) / 32 * 4;
        int header = 14 + 40 + (sixteen ? 12 : 0);
        byte[] file = new byte[header + (stride * Height)];
        Span<byte> span = file;
        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], header);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..], -Height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)bpp);
        BinaryPrimitives.WriteInt32LittleEndian(span[30..], sixteen ? 3 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[34..], stride * Height);
        if (sixteen)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[54..], Format.RedMask);
            BinaryPrimitives.WriteUInt32LittleEndian(span[58..], Format.GreenMask);
            BinaryPrimitives.WriteUInt32LittleEndian(span[62..], Format.BlueMask);
        }

        for (int y = 0; y < Height; y++)
        {
            Span<byte> row = span.Slice(header + (y * stride), stride);
            for (int x = 0; x < Width; x++)
            {
                uint v = Native[(y * Width) + x];
                if (sixteen)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(row[(x * 2)..], (ushort)v);
                }
                else
                {
                    // Window captures are stored as 0xRRGGBB, which is BGRA byte order in a 32-bit BI_RGB file.
                    BinaryPrimitives.WriteUInt32LittleEndian(row[(x * 4)..], ToRgb888Value(v));
                }
            }
        }

        return file;
    }

    /// <summary>The value as 0xRRGGBB (8 bits per channel by bit replication for narrower formats).</summary>
    private uint ToRgb888Value(uint v)
    {
        if (Format.RedMask == 0xFF0000 && Format.GreenMask == 0xFF00 && Format.BlueMask == 0xFF)
        {
            return v;
        }

        (int r, int g, int b) = Levels(v);
        return (uint)((Widen(r, Format.RedBits) << 16) | (Widen(g, Format.GreenBits) << 8) | Widen(b, Format.BlueBits));
    }

    private static int Widen(int level, int bits)
    {
        int value = 0;
        for (int shift = 8 - bits; shift > -bits; shift -= bits)
        {
            value |= shift >= 0 ? level << shift : level >> -shift;
        }

        return value & 0xFF;
    }
}
