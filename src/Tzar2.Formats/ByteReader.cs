using System;
using System.Buffers.Binary;

namespace Tzar2.Formats;

/// <summary>Bounds-checked little-endian reads that throw <see cref="LegacyFormatException"/>.</summary>
internal static class ByteReader
{
    public static void Require(ReadOnlySpan<byte> data, long offset, long length, string format, string what)
    {
        if (offset < 0 || length < 0 || offset + length > data.Length)
        {
            throw new LegacyFormatException(format, $"truncated: {what} needs bytes {offset}..{offset + length} but the data has {data.Length}.");
        }
    }

    public static byte U8(ReadOnlySpan<byte> data, long offset, string format, string what)
    {
        Require(data, offset, 1, format, what);
        return data[(int)offset];
    }

    public static ushort U16(ReadOnlySpan<byte> data, long offset, string format, string what)
    {
        Require(data, offset, 2, format, what);
        return BinaryPrimitives.ReadUInt16LittleEndian(data[(int)offset..]);
    }

    public static short I16(ReadOnlySpan<byte> data, long offset, string format, string what)
    {
        Require(data, offset, 2, format, what);
        return BinaryPrimitives.ReadInt16LittleEndian(data[(int)offset..]);
    }

    public static uint U32(ReadOnlySpan<byte> data, long offset, string format, string what)
    {
        Require(data, offset, 4, format, what);
        return BinaryPrimitives.ReadUInt32LittleEndian(data[(int)offset..]);
    }

    public static int I32(ReadOnlySpan<byte> data, long offset, string format, string what)
    {
        Require(data, offset, 4, format, what);
        return BinaryPrimitives.ReadInt32LittleEndian(data[(int)offset..]);
    }

    public static uint U32BigEndian(ReadOnlySpan<byte> data, long offset, string format, string what)
    {
        Require(data, offset, 4, format, what);
        return BinaryPrimitives.ReadUInt32BigEndian(data[(int)offset..]);
    }

    public static ushort U16BigEndian(ReadOnlySpan<byte> data, long offset, string format, string what)
    {
        Require(data, offset, 2, format, what);
        return BinaryPrimitives.ReadUInt16BigEndian(data[(int)offset..]);
    }

    public static bool Matches(ReadOnlySpan<byte> data, long offset, ReadOnlySpan<byte> magic)
        => offset >= 0 && offset + magic.Length <= data.Length && data.Slice((int)offset, magic.Length).SequenceEqual(magic);
}
