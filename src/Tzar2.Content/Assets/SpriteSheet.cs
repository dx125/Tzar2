using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Core.Serialization;

namespace Tzar2.Content.Assets;

/// <summary>One trimmed cell: its tight box inside the full cell and the offset of its pixels in <see cref="SpriteSheet.PixelData"/>.</summary>
/// <param name="X">Left of the box in cell coordinates.</param>
/// <param name="Y">Top of the box in cell coordinates.</param>
/// <param name="Width">Box width (0 for an empty cell).</param>
/// <param name="Height">Box height (0 for an empty cell).</param>
/// <param name="Offset">Byte offset of the first pixel.</param>
public readonly record struct SpriteCell(int X, int Y, int Width, int Height, int Offset)
{
    /// <summary>Whether the cell has no opaque pixel.</summary>
    public bool IsEmpty => Width == 0 || Height == 0;
}

/// <summary>
/// An imported sprite sheet (<c>.t2spr</c>, ADR 0013): directions × frames cells, direction-major like the original,
/// each trimmed to its opaque bounding box and stored losslessly in one <see cref="PixelFormat"/>.
/// Indexed sheets carry their 256-entry palette (alpha 0 marks the transparent index) plus the player-colour range
/// and the palette entries that are baked shadow; RGB5A1 sheets may name an RGB555 value that is baked shadow.
/// Both shadow hints come from the importer's heuristic (docs/formats/rle.md §5.4) and a renderer may ignore them.
/// </summary>
public sealed class SpriteSheet
{
    /// <summary>File extension.</summary>
    public const string Extension = ".t2spr";

    /// <summary>Current format version.</summary>
    public const ushort Version = 1;

    /// <summary>Value of <see cref="PlayerFirst"/>/<see cref="ShadowColour"/> meaning "none".</summary>
    public const int None = -1;

    private const int HeaderSize = 64;
    private const ushort NoneValue = 0xFFFF;
    private static ReadOnlySpan<byte> Magic => "T2SP"u8;

    /// <summary>Pixel layout.</summary>
    public required PixelFormat Format { get; init; }

    /// <summary>The original RLE kind (1, 3, 7) or 0 for other sources.</summary>
    public byte SourceKind { get; init; }

    /// <summary>Directions (columns in IMAGES.INI).</summary>
    public required int Directions { get; init; }

    /// <summary>Frames per direction (rows in IMAGES.INI).</summary>
    public required int FramesPerDirection { get; init; }

    /// <summary>Full (untrimmed) cell width.</summary>
    public required int CellWidth { get; init; }

    /// <summary>Full (untrimmed) cell height.</summary>
    public required int CellHeight { get; init; }

    /// <summary>256 RGBA entries for <see cref="PixelFormat.Indexed8"/>, empty otherwise.</summary>
    public IReadOnlyList<Rgba32> Palette { get; init; } = [];

    /// <summary>First palette index of the 16-step player-colour ramp, or <see cref="None"/>.</summary>
    public int PlayerFirst { get; init; } = None;

    /// <summary>Length of the player ramp (16) or 0.</summary>
    public int PlayerCount { get; init; }

    /// <summary>RGB555 value treated as baked shadow in an RGB5A1 sheet, or <see cref="None"/>.</summary>
    public int ShadowColour { get; init; } = None;

    /// <summary>Palette indices that are baked shadow (indexed sheets), sorted.</summary>
    public IReadOnlyList<int> ShadowIndices { get; init; } = [];

    /// <summary>Cells in direction-major order (<c>dir × frames + frame</c>).</summary>
    public required IReadOnlyList<SpriteCell> Cells { get; init; }

    /// <summary>Concatenated trimmed pixels.</summary>
    public required byte[] PixelData { get; init; }

    /// <summary>Number of cells.</summary>
    public int CellCount => Directions * FramesPerDirection;

    /// <summary>Bytes per pixel of <see cref="Format"/>.</summary>
    public int BytesPerPixel => PixelFormats.BytesPerPixel(Format);

    /// <summary>Sum of trimmed cell areas in pixels.</summary>
    public long TrimmedPixelCount
    {
        get
        {
            long total = 0;
            foreach (SpriteCell cell in Cells)
            {
                total += (long)cell.Width * cell.Height;
            }

            return total;
        }
    }

    /// <summary>Whether palette index <paramref name="index"/> is a player-colour entry.</summary>
    public bool IsPlayerIndex(int index) => PlayerFirst >= 0 && index >= PlayerFirst && index < PlayerFirst + PlayerCount;

    /// <summary>Serialises the sheet.</summary>
    public byte[] ToBytes()
    {
        Validate();
        var w = new BinaryBufferWriter(HeaderSize + (Palette.Count * 4) + (Cells.Count * 12) + PixelData.Length);
        w.WriteBytes(Magic);
        w.WriteUInt16(Version);
        w.WriteByte((byte)Format);
        w.WriteByte(SourceKind);
        w.WriteUInt16((ushort)Directions);
        w.WriteUInt16((ushort)FramesPerDirection);
        w.WriteUInt16((ushort)CellWidth);
        w.WriteUInt16((ushort)CellHeight);
        w.WriteUInt16((ushort)Palette.Count);
        w.WriteUInt16(PlayerFirst < 0 ? NoneValue : (ushort)PlayerFirst);
        w.WriteUInt16((ushort)PlayerCount);
        w.WriteUInt16(ShadowColour < 0 ? NoneValue : (ushort)ShadowColour);
        w.WriteUInt16(0);
        w.WriteUInt16(0);
        Span<byte> bits = stackalloc byte[32];
        bits.Clear();
        foreach (int index in ShadowIndices)
        {
            bits[index >> 3] |= (byte)(1 << (index & 7));
        }

        w.WriteBytes(bits);
        w.WriteUInt32((uint)PixelData.Length);
        foreach (Rgba32 c in Palette)
        {
            w.WriteByte(c.R);
            w.WriteByte(c.G);
            w.WriteByte(c.B);
            w.WriteByte(c.A);
        }

        foreach (SpriteCell cell in Cells)
        {
            w.WriteInt16((short)cell.X);
            w.WriteInt16((short)cell.Y);
            w.WriteUInt16((ushort)cell.Width);
            w.WriteUInt16((ushort)cell.Height);
            w.WriteUInt32((uint)cell.Offset);
        }

        w.WriteBytes(PixelData);
        return w.ToArray();
    }

    /// <summary>Parses a <c>.t2spr</c> file.</summary>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    public static SpriteSheet Parse(ReadOnlySpan<byte> data)
    {
        var r = new BinaryBufferReader(data);
        if (data.Length < HeaderSize || !r.ReadBytes(4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("t2spr: bad magic.");
        }

        ushort version = r.ReadUInt16();
        if (version != Version)
        {
            throw new InvalidDataException($"t2spr: unsupported version {version}.");
        }

        byte format = r.ReadByte();
        if (!PixelFormats.IsDefined(format))
        {
            throw new InvalidDataException($"t2spr: unknown pixel format {format}.");
        }

        byte kind = r.ReadByte();
        int dirs = r.ReadUInt16();
        int frames = r.ReadUInt16();
        int cellWidth = r.ReadUInt16();
        int cellHeight = r.ReadUInt16();
        int paletteCount = r.ReadUInt16();
        ushort playerFirst = r.ReadUInt16();
        int playerCount = r.ReadUInt16();
        ushort shadowColour = r.ReadUInt16();
        r.ReadUInt16();
        r.ReadUInt16();
        ReadOnlySpan<byte> bits = r.ReadBytes(32);
        uint pixelSize = r.ReadUInt32();
        if (paletteCount > 256)
        {
            throw new InvalidDataException("t2spr: palette larger than 256 entries.");
        }

        var palette = new Rgba32[paletteCount];
        for (int i = 0; i < paletteCount; i++)
        {
            palette[i] = new Rgba32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
        }

        long cellCount = (long)dirs * frames;
        if (cellCount * 12 > r.Remaining)
        {
            throw new InvalidDataException("t2spr: truncated cell table.");
        }

        var cells = new SpriteCell[cellCount];
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = new SpriteCell(r.ReadInt16(), r.ReadInt16(), r.ReadUInt16(), r.ReadUInt16(), (int)Math.Min(r.ReadUInt32(), int.MaxValue));
        }

        if (pixelSize != r.Remaining)
        {
            throw new InvalidDataException("t2spr: pixel data size does not match.");
        }

        var shadowIndices = new List<int>();
        for (int i = 0; i < 256; i++)
        {
            if ((bits[i >> 3] & (1 << (i & 7))) != 0)
            {
                shadowIndices.Add(i);
            }
        }

        var sheet = new SpriteSheet
        {
            Format = (PixelFormat)format,
            SourceKind = kind,
            Directions = dirs,
            FramesPerDirection = frames,
            CellWidth = cellWidth,
            CellHeight = cellHeight,
            Palette = palette,
            PlayerFirst = playerFirst == NoneValue ? None : playerFirst,
            PlayerCount = playerCount,
            ShadowColour = shadowColour == NoneValue ? None : shadowColour,
            ShadowIndices = shadowIndices,
            Cells = cells,
            PixelData = r.ReadBytes((int)pixelSize).ToArray(),
        };
        sheet.Validate();
        return sheet;
    }

    /// <summary>Checks internal consistency (cell boxes inside the cell and inside the pixel data).</summary>
    /// <exception cref="InvalidDataException">The sheet is inconsistent.</exception>
    public void Validate()
    {
        if (Directions is <= 0 or > ushort.MaxValue || FramesPerDirection is <= 0 or > ushort.MaxValue
            || CellWidth is < 0 or > ushort.MaxValue || CellHeight is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("t2spr: invalid geometry.");
        }

        if (Cells.Count != CellCount)
        {
            throw new InvalidDataException("t2spr: cell count does not match directions × frames.");
        }

        if (Palette.Count != (Format == PixelFormat.Indexed8 ? 256 : 0))
        {
            throw new InvalidDataException("t2spr: indexed sheets need exactly 256 palette entries, others none.");
        }

        if (PlayerFirst != None && (PlayerFirst < 0 || PlayerFirst + PlayerCount > 256))
        {
            throw new InvalidDataException("t2spr: player range out of the palette.");
        }

        foreach (int index in ShadowIndices)
        {
            if (index is < 0 or > 255)
            {
                throw new InvalidDataException("t2spr: shadow index out of range.");
            }
        }

        int bpp = BytesPerPixel;
        foreach (SpriteCell cell in Cells)
        {
            if (cell.X < 0 || cell.Y < 0 || cell.Width < 0 || cell.Height < 0
                || cell.X + cell.Width > CellWidth || cell.Y + cell.Height > CellHeight
                || cell.Offset < 0 || (long)cell.Offset + ((long)cell.Width * cell.Height * bpp) > PixelData.Length)
            {
                throw new InvalidDataException("t2spr: cell box outside the cell or the pixel data.");
            }
        }
    }

    /// <summary>The trimmed pixels of cell <paramref name="index"/>.</summary>
    public ReadOnlySpan<byte> CellPixels(int index)
    {
        SpriteCell cell = Cells[index];
        return PixelData.AsSpan(cell.Offset, cell.Width * cell.Height * BytesPerPixel);
    }
}
