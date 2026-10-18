using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Maps;

/// <summary>
/// The <c>map.bin</c> part of a <c>.t2map</c> package (TDD §18.4 "terrain.bin", ADR 0121): a versioned little-endian
/// encoding of <see cref="MapData"/> (layers and placed objects). Deterministic: the same map gives the same bytes.
/// </summary>
/// <remarks>
/// Layout: <c>T2MP</c>, u16 version, string name, u16 width, u16 height, string terrain set (empty = none),
/// (W+1)(H+1) terrain bytes, (W+1)(H+1) height bytes, W·H cliff bytes, set-corner bytes (same count as terrain, absent
/// without a terrain set), u32 object count, then per object: string type key, u8 owner, i32 x, i32 y. Strings are a
/// u16 byte length plus UTF-8. Version 2 (written only when an object carries editor extras, so maps without them keep
/// their version-1 bytes; ADR 0152) adds per object: u8 flags, u8 facing, i32 amount.
/// </remarks>
public static class MapBinary
{
    /// <summary>Version without per-object extras.</summary>
    public const ushort Version = 1;

    /// <summary>Version with per-object flags, facing and amount (ADR 0152).</summary>
    public const ushort ExtrasVersion = 2;

    private static ReadOnlySpan<byte> Magic => "T2MP"u8;

    /// <summary>Encodes <paramref name="map"/>.</summary>
    public static byte[] Write(MapData map)
    {
        ArgumentNullException.ThrowIfNull(map);
        using var ms = new MemoryStream();
        bool extras = false;
        foreach (MapObject o in map.Objects)
        {
            extras |= o.HasExtras;
        }

        ms.Write(Magic);
        U16(ms, extras ? ExtrasVersion : Version);
        Str(ms, map.Name);
        U16(ms, (ushort)map.Width);
        U16(ms, (ushort)map.Height);
        Str(ms, map.TerrainSet ?? string.Empty);
        ms.Write(map.TerrainCorners);
        ms.Write(map.HeightCorners);
        ms.Write(map.CliffTiles);
        if (map.TerrainSet is not null)
        {
            ms.Write(map.SetTerrainCorners);
        }

        Span<byte> b4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b4, map.Objects.Length);
        ms.Write(b4);
        foreach (MapObject o in map.Objects)
        {
            Str(ms, o.TypeKey);
            ms.WriteByte(o.Owner);
            BinaryPrimitives.WriteInt32LittleEndian(b4, o.X);
            ms.Write(b4);
            BinaryPrimitives.WriteInt32LittleEndian(b4, o.Y);
            ms.Write(b4);
            if (extras)
            {
                ms.WriteByte((byte)o.Flags);
                ms.WriteByte(o.Facing);
                BinaryPrimitives.WriteInt32LittleEndian(b4, o.Amount);
                ms.Write(b4);
            }
        }

        return ms.ToArray();
    }

    /// <summary>Decodes a map written by <see cref="Write"/>.</summary>
    /// <exception cref="FormatException">The data is malformed or of an unknown version.</exception>
    public static MapData Read(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        if (!r.Take(4).SequenceEqual(Magic))
        {
            throw new FormatException("map.bin: bad magic.");
        }

        ushort version = r.U16();
        if (version is not (Version or ExtrasVersion))
        {
            throw new FormatException($"map.bin: unsupported version {version}.");
        }

        string name = r.Str();
        int width = r.U16();
        int height = r.U16();
        string set = r.Str();
        int corners = (width + 1) * (height + 1);
        ReadOnlySpan<byte> terrain = r.Take(corners);
        ReadOnlySpan<byte> heights = r.Take(corners);
        ReadOnlySpan<byte> cliffs = r.Take(width * height);
        ReadOnlySpan<byte> setCorners = set.Length > 0 ? r.Take(corners) : default;
        int count = r.I32();
        if (count < 0 || count > MapData.MaxObjects)
        {
            throw new FormatException($"map.bin: object count {count} out of range.");
        }

        var objects = new List<MapObject>(count);
        for (int i = 0; i < count; i++)
        {
            string key = r.Str();
            byte owner = r.Take(1)[0];
            int x = r.I32();
            int y = r.I32();
            var o = new MapObject(key, owner, x, y);
            if (version == ExtrasVersion)
            {
                byte flags = r.Take(1)[0];
                byte facing = r.Take(1)[0];
                int amount = r.I32();
                if ((flags & ~7) != 0 || facing > 7 || amount < 0)
                {
                    throw new FormatException($"map.bin: object {i} has invalid extras.");
                }

                o = o with { Flags = (MapObjectFlags)flags, Facing = facing, Amount = amount };
            }

            objects.Add(o);
        }

        if (!r.AtEnd)
        {
            throw new FormatException("map.bin: trailing bytes.");
        }

        try
        {
            return new MapData(name, width, height, terrain, heights, cliffs, objects, set.Length > 0 ? set : null, setCorners);
        }
        catch (ArgumentException e)
        {
            throw new FormatException("map.bin: " + e.Message, e);
        }
    }

    private static void U16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }

    private static void Str(Stream s, string v)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(v);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("String too long for map.bin.", nameof(v));
        }

        U16(s, (ushort)bytes.Length);
        s.Write(bytes);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        public readonly bool AtEnd => _offset == _data.Length;

        public ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _offset + n > _data.Length)
            {
                throw new FormatException("map.bin: truncated.");
            }

            ReadOnlySpan<byte> s = _data.Slice(_offset, n);
            _offset += n;
            return s;
        }

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public string Str() => Encoding.UTF8.GetString(Take(U16()));
    }
}
