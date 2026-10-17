using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Tzar2.Content.Packages;

namespace Tzar2.Tools.Cli.Imaging;

/// <summary>Minimal RGBA8 PNG encoder for debug output (<c>t2 dump</c>).</summary>
internal static class PngWriter
{
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(file, width, height, rgba);
    }

    public static void Write(Stream output, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != (long)width * height * 4)
        {
            throw new ArgumentException("Pixel buffer does not match the size.", nameof(rgba));
        }

        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        Chunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgba.Slice(y * width * 4, width * 4));
            }
        }

        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        byte[] typeBytes = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        output.Write(typeBytes);
        output.Write(data);
        uint crc = Crc32Checksum.Finish(Crc32Checksum.Append(Crc32Checksum.Append(Crc32Checksum.Start, typeBytes), data));
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    /// <summary>
    /// Rearranges a sheet column (cells stacked vertically, direction-major) into a grid with one row per direction and
    /// one column per frame, like the RE prototype's contact sheets.
    /// </summary>
    public static byte[] ToGrid(ReadOnlySpan<byte> column, int cellWidth, int cellHeight, int directions, int frames)
    {
        byte[] grid = new byte[(long)cellWidth * frames * cellHeight * directions * 4];
        int gridStride = cellWidth * frames * 4;
        for (int d = 0; d < directions; d++)
        {
            for (int f = 0; f < frames; f++)
            {
                int cell = (d * frames) + f;
                for (int y = 0; y < cellHeight; y++)
                {
                    column.Slice(((cell * cellHeight) + y) * cellWidth * 4, cellWidth * 4)
                        .CopyTo(grid.AsSpan((((d * cellHeight) + y) * gridStride) + (f * cellWidth * 4)));
                }
            }
        }

        return grid;
    }
}
