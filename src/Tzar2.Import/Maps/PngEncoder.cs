using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Tzar2.Content.Packages;
using Tzar2.Formats.Bmp;

namespace Tzar2.Import.Maps;

/// <summary>
/// Converts user BMPs to PNG (REQ-EDT-09) with byte-stable output: RGB8 pixels (16-bit RGB555 expanded by bit
/// replication, which is reversible), zlib with <b>stored</b> deflate blocks so no compressor version can change the
/// bytes (REQ-EDT-05). Colour keys stay opaque colours; nothing is lost.
/// </summary>
public static class PngEncoder
{
    private const int MaxStored = 65535;

    /// <summary>Converts a BMP file to PNG bytes.</summary>
    /// <exception cref="Formats.LegacyFormatException">The BMP is malformed.</exception>
    public static byte[] FromBmp(ReadOnlySpan<byte> bmpData)
    {
        BmpImage bmp = BmpImage.Parse(bmpData);
        int w = bmp.Width, h = bmp.Height;
        byte[] rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = ((y * w) + x) * 3;
                switch (bmp.BitsPerPixel)
                {
                    case 8:
                        int index = bmp.IndexAt(x, y);
                        if (index < bmp.Palette.Count)
                        {
                            rgb[o] = bmp.Palette[index].R;
                            rgb[o + 1] = bmp.Palette[index].G;
                            rgb[o + 2] = bmp.Palette[index].B;
                        }

                        break;
                    case 16:
                        ushort c = bmp.Rgb555At(x, y);
                        rgb[o] = Expand((c >> 10) & 31);
                        rgb[o + 1] = Expand((c >> 5) & 31);
                        rgb[o + 2] = Expand(c & 31);
                        break;
                    default:
                        int s = ((y * w) + x) * 3;
                        rgb[o] = bmp.Pixels[s];
                        rgb[o + 1] = bmp.Pixels[s + 1];
                        rgb[o + 2] = bmp.Pixels[s + 2];
                        break;
                }
            }
        }

        return Encode(w, h, rgb);
    }

    /// <summary>Encodes tightly packed top-down RGB8 pixels.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length != (long)width * height * 3)
        {
            throw new ArgumentException("Pixel buffer does not match the size.", nameof(rgb));
        }

        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(output, "IHDR", header);

        byte[] raw = new byte[height * ((width * 3) + 1)];
        for (int y = 0; y < height; y++)
        {
            rgb.Slice(y * width * 3, width * 3).CopyTo(raw.AsSpan((y * ((width * 3) + 1)) + 1));
        }

        Chunk(output, "IDAT", ZlibStored(raw));
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte Expand(int c5) => (byte)((c5 << 3) | (c5 >> 2));

    private static byte[] ZlibStored(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x01);
        int offset = 0;
        do
        {
            int n = Math.Min(MaxStored, data.Length - offset);
            bool last = offset + n >= data.Length;
            ms.WriteByte(last ? (byte)1 : (byte)0);
            ms.WriteByte((byte)(n & 0xFF));
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)(~n & 0xFF));
            ms.WriteByte((byte)((~n >> 8) & 0xFF));
            ms.Write(data, offset, n);
            offset += n;
        }
        while (offset < data.Length);

        uint a = 1, b = 0;
        foreach (byte v in data)
        {
            a = (a + v) % 65521;
            b = (b + a) % 65521;
        }

        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, (b << 16) | a);
        ms.Write(adler);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32Checksum.Finish(Crc32Checksum.Append(Crc32Checksum.Append(Crc32Checksum.Start, typeBytes), data));
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }
}
