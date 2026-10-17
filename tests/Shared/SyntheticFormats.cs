using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Tzar2.Tests.Shared;

/// <summary>
/// Test-only writers for the legacy formats, built from docs/formats/*.md (REQ-LEGAL-03: fixtures are synthetic,
/// generated in code; no byte of the original game is used).
/// </summary>
internal static class SyntheticFormats
{
    /// <summary>
    /// An <c>.RLE</c> sheet. <paramref name="pixel"/> returns the value at (cell, x, y) or null for transparent:
    /// a palette index (kind 1), an RGB555 word (kind 3) or anything non-null (kind 7).
    /// </summary>
    public static byte[] Rle(int kind, int dirs, int frames, int width, int cellHeight, Func<int, int, int, int?> pixel,
        (byte R, byte G, byte B)[]? palette = null, uint key = 0x03E0, int extraRows = 0, bool padRows = true)
    {
        int cells = dirs * frames;
        int height = (cells * cellHeight) + extraRows;
        palette ??= kind == 1 ? Enumerable.Range(0, 256).Select(i => ((byte)i, (byte)(255 - i), (byte)(i / 2))).ToArray() : [];
        int bpp = kind switch { 1 => 1, 3 => 2, _ => 0 };
        var rows = new List<byte[]>();
        for (int y = 0; y < height; y++)
        {
            int cell = Math.Min(y / cellHeight, cells - 1);
            int cy = y - (cell * cellHeight);
            int?[] values = new int?[width];
            if (y < cells * cellHeight)
            {
                for (int x = 0; x < width; x++)
                {
                    values[x] = pixel(cell, x, cy);
                }
            }

            rows.Add(EncodeRow(values, bpp, padRows));
        }

        var data = new MemoryStream();
        var table = new byte[4 * height];
        int offset = table.Length;
        for (int y = 0; y < height; y++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4 * y), (uint)offset);
            offset += rows[y].Length;
        }

        data.Write(table);
        foreach (byte[] row in rows)
        {
            data.Write(row);
        }

        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        w.Write(" elr"u8);
        w.Write((uint)dirs);
        w.Write((uint)frames);
        w.Write(0u);
        w.Write("RLE\0"u8);
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write((ushort)kind);
        w.Write((uint)data.Length);
        w.Write((uint)palette.Length);
        w.Write(key);
        foreach ((byte r, byte g, byte b) in palette)
        {
            w.Write(b);
            w.Write(g);
            w.Write(r);
            w.Write((byte)0);
        }

        w.Write(data.ToArray());
        return file.ToArray();
    }

    private static byte[] EncodeRow(int?[] values, int bpp, bool padRows)
    {
        var o = new List<byte>();
        int x = 0;
        int width = values.Length;
        while (x < width)
        {
            int skip = 0;
            while (x < width && values[x] is null)
            {
                skip++;
                x++;
            }

            int start = x;
            while (x < width && values[x] is not null)
            {
                x++;
            }

            int n = x - start;
            if (n == 0)
            {
                // Trailing transparent pixels (or an empty row).
                EmitSkip(o, skip);
                break;
            }

            while (skip > 255)
            {
                o.Add(255);
                o.Add(0);
                skip -= 255;
            }

            int p = start;
            while (n > 0)
            {
                int chunk = Math.Min(n, 255);
                o.Add((byte)skip);
                o.Add((byte)chunk);
                for (int i = 0; i < chunk; i++)
                {
                    int v = values[p + i]!.Value;
                    if (bpp == 1)
                    {
                        o.Add((byte)v);
                    }
                    else if (bpp == 2)
                    {
                        o.Add((byte)v);
                        o.Add((byte)(v >> 8));
                    }
                }

                skip = 0;
                p += chunk;
                n -= chunk;
            }
        }

        if (padRows && values[width - 1] is not null)
        {
            o.Add(0);
            o.Add(0);
        }

        return [.. o];
    }

    private static void EmitSkip(List<byte> o, int skip)
    {
        do
        {
            int chunk = Math.Min(skip, 255);
            o.Add((byte)chunk);
            o.Add(0);
            skip -= chunk;
        }
        while (skip > 0);
    }

    /// <summary>A BMP. <paramref name="pixels"/> is top-down: 1 byte (8 bpp), 2 bytes LE (16 bpp) or B,G,R (24 bpp) per pixel.</summary>
    public static byte[] Bmp(int width, int height, int bpp, byte[] pixels, (byte R, byte G, byte B)[]? palette = null,
        bool topDown = false, bool writePalette = true, uint colorsUsed = 0)
    {
        int bytesPerPixel = bpp / 8;
        int stride = ((width * bpp) + 31) / 32 * 4;
        palette ??= bpp == 8 ? Enumerable.Range(0, 256).Select(i => ((byte)i, (byte)i, (byte)i)).ToArray() : [];
        int paletteBytes = writePalette ? palette.Length * 4 : 0;
        int offBits = 14 + 40 + paletteBytes;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("BM"u8);
        w.Write((uint)(offBits + (stride * height)));
        w.Write(0u);
        w.Write((uint)offBits);
        w.Write(40u);
        w.Write(width);
        w.Write(topDown ? -height : height);
        w.Write((ushort)1);
        w.Write((ushort)bpp);
        w.Write(0u);
        w.Write((uint)(stride * height));
        w.Write(2835);
        w.Write(2835);
        w.Write(colorsUsed);
        w.Write(0u);
        if (writePalette)
        {
            foreach ((byte r, byte g, byte b) in palette)
            {
                w.Write(b);
                w.Write(g);
                w.Write(r);
                w.Write((byte)0);
            }
        }

        byte[] row = new byte[stride];
        for (int i = 0; i < height; i++)
        {
            int y = topDown ? i : height - 1 - i;
            Array.Clear(row);
            Array.Copy(pixels, y * width * bytesPerPixel, row, 0, width * bytesPerPixel);
            w.Write(row);
        }

        return ms.ToArray();
    }

    /// <summary>A <c>.FNT</c> with glyphs of <paramref name="cellWidth"/> × <paramref name="cellHeight"/>; glyph g has value (g % 7) + 1 in its first column.</summary>
    public static byte[] Fnt(string face, int cellWidth, int cellHeight, int glyphs, Func<int, (int Width, int Glyph)> chars,
        int declaredGlyphs = -1, (byte R, byte G, byte B)[]? palette = null)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        byte[] name = new byte[32];
        Encoding.ASCII.GetBytes(face).CopyTo(name, 0);
        w.Write(name);
        w.Write((ushort)cellHeight);
        for (int i = 0; i < 7; i++)
        {
            w.Write((ushort)i);
        }

        w.Write((ushort)(declaredGlyphs < 0 ? glyphs : declaredGlyphs));
        var table = Enumerable.Range(0, 256).Select(chars).ToArray();
        foreach ((int width, _) in table)
        {
            w.Write((byte)width);
        }

        foreach ((_, int glyph) in table)
        {
            w.Write((byte)glyph);
        }

        byte[] strip = new byte[cellWidth * cellHeight * glyphs];
        for (int g = 0; g < glyphs; g++)
        {
            for (int y = 0; y < cellHeight; y++)
            {
                strip[((g * cellHeight) + y) * cellWidth] = (byte)((g % 7) + 1);
            }
        }

        w.Write(Bmp(cellWidth, cellHeight * glyphs, 8, strip, palette, writePalette: palette is not null));
        return ms.ToArray();
    }

    /// <summary>A <c>.DAT</c>.</summary>
    public static byte[] Dat((short, short, short, short) rect, byte[] grid, (short, short, short, short)[]? boxes)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(rect.Item1);
        w.Write(rect.Item2);
        w.Write(rect.Item3);
        w.Write(rect.Item4);
        w.Write(grid);
        if (boxes is not null)
        {
            w.Write((ushort)boxes.Length);
            foreach ((short a, short b, short c, short d) in boxes)
            {
                w.Write(a);
                w.Write(b);
                w.Write(c);
                w.Write(d);
            }
        }

        return ms.ToArray();
    }

    /// <summary>A <c>GAME.PAL</c>-style PALF file whose entry i is (i, 255 − i, i / 2).</summary>
    public static byte[] Pal(int count = 256)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("PALF"u8);
        w.Write((ushort)count);
        w.Write((ushort)0);
        w.Write(0u);
        for (int i = 0; i < count; i++)
        {
            w.Write((byte)(i / 2));
            w.Write((byte)(255 - i));
            w.Write((byte)i);
            w.Write((byte)0);
        }

        return ms.ToArray();
    }

    /// <summary>A PCM WAV with <paramref name="frames"/> silent sample frames.</summary>
    public static byte[] Wav(int channels = 1, int rate = 22050, int bits = 16, int frames = 100)
    {
        int align = channels * bits / 8;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + (frames * align));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((ushort)1);
        w.Write((ushort)channels);
        w.Write(rate);
        w.Write(rate * align);
        w.Write((ushort)align);
        w.Write((ushort)bits);
        w.Write("data"u8);
        w.Write(frames * align);
        w.Write(new byte[frames * align]);
        return ms.ToArray();
    }

    /// <summary>A type-0 MIDI file with one empty track.</summary>
    public static byte[] Mid() =>
    [
        (byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, 0, 0, 1, 0, 96,
        (byte)'M', (byte)'T', (byte)'r', (byte)'k', 0, 0, 0, 4, 0, 0xFF, 0x2F, 0,
    ];

    /// <summary>An HPFS image holding <paramref name="files"/> (paths with <c>/</c>), optionally truncated inside its last block.</summary>
    public static byte[] Hpfs(IReadOnlyList<(string Path, byte[] Data)> files, int truncate = 0)
    {
        var root = new Node("", true);
        foreach ((string path, byte[] data) in files)
        {
            string[] parts = path.Split('/');
            Node dir = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                Node? next = dir.Children.FirstOrDefault(c => c.Name == parts[i] && c.IsDirectory);
                if (next is null)
                {
                    next = new Node(parts[i], true);
                    dir.Children.Add(next);
                }

                dir = next;
            }

            dir.Children.Add(new Node(parts[^1], false) { Data = data });
        }

        var blocks = new List<byte[]> { new byte[512] };
        int rootInode = WriteNode(root, blocks);
        if (rootInode != 1)
        {
            throw new InvalidOperationException("Root inode must be block 1.");
        }

        byte[] header = blocks[0];
        "HPFS"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)blocks.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), -1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 1);
        byte[] image = blocks.SelectMany(b => b).ToArray();
        return image[..^truncate];
    }

    private static int WriteNode(Node node, List<byte[]> blocks)
    {
        int inode = blocks.Count;
        blocks.Add(new byte[512]);
        byte[] content;
        if (node.IsDirectory)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            foreach (Node child in node.Children)
            {
                int childInode = WriteNode(child, blocks);
                byte[] name = Encoding.Latin1.GetBytes(child.Name);
                w.Write((uint)childInode);
                w.Write((ushort)(child.IsDirectory ? 1 : 0));
                w.Write((ushort)name.Length);
                w.Write(name);
            }

            content = ms.ToArray();
        }
        else
        {
            content = node.Data;
        }

        int count = (content.Length + 511) / 512;
        var dataBlocks = new List<int>();
        for (int i = 0; i < count; i++)
        {
            byte[] block = new byte[512];
            Array.Copy(content, i * 512, block, 0, Math.Min(512, content.Length - (i * 512)));
            dataBlocks.Add(blocks.Count);
            blocks.Add(block);
        }

        byte[] ino = blocks[inode];
        BinaryPrimitives.WriteUInt32LittleEndian(ino, (uint)content.Length);
        if (count <= 126)
        {
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(8 + (4 * i)), (uint)dataBlocks[i]);
            }
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(4), 1);
            for (int index = 0; index * 128 < count; index++)
            {
                byte[] indexBlock = new byte[512];
                for (int k = 0; k < 128 && (index * 128) + k < count; k++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(indexBlock.AsSpan(4 * k), (uint)dataBlocks[(index * 128) + k]);
                }

                BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(8 + (4 * index)), (uint)blocks.Count);
                blocks.Add(indexBlock);
            }
        }

        return inode;
    }

    private sealed class Node(string name, bool isDirectory)
    {
        public string Name { get; } = name;

        public bool IsDirectory { get; } = isDirectory;

        public List<Node> Children { get; } = [];

        public byte[] Data { get; init; } = [];
    }
}
