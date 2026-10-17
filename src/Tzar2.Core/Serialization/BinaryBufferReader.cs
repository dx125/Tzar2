using System;
using System.Buffers.Binary;
using System.IO;
using Tzar2.Core.Numerics;

namespace Tzar2.Core.Serialization;

/// <summary>
/// Bounds-checked little-endian reader over a byte span, the counterpart of <see cref="BinaryBufferWriter"/>.
/// Malformed or truncated input throws <see cref="InvalidDataException"/>; the reader never reads past its span.
/// </summary>
public ref struct BinaryBufferReader
{
    /// <summary>Default upper bound for <see cref="ReadString(int)"/>, in bytes.</summary>
    public const int DefaultMaxStringBytes = 1 << 20;

    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    /// <summary>Creates a reader over <paramref name="data"/>.</summary>
    public BinaryBufferReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>Current offset in bytes.</summary>
    public readonly int Position => _position;

    /// <summary>Bytes left to read.</summary>
    public readonly int Remaining => _data.Length - _position;

    /// <summary>Whether all bytes have been read.</summary>
    public readonly bool IsAtEnd => _position == _data.Length;

    /// <summary>Reads one byte.</summary>
    public byte ReadByte() => Take(1)[0];

    /// <summary>Reads a boolean written as 0 or 1.</summary>
    public bool ReadBoolean()
    {
        byte value = ReadByte();
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException($"Invalid boolean byte {value} at offset {_position - 1}."),
        };
    }

    /// <summary>Reads a signed 16-bit integer.</summary>
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(sizeof(short)));

    /// <summary>Reads an unsigned 16-bit integer.</summary>
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(ushort)));

    /// <summary>Reads a signed 32-bit integer.</summary>
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));

    /// <summary>Reads an unsigned 32-bit integer.</summary>
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));

    /// <summary>Reads a signed 64-bit integer.</summary>
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(sizeof(long)));

    /// <summary>Reads an unsigned 64-bit integer.</summary>
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(sizeof(ulong)));

    /// <summary>Reads a fixed-point value.</summary>
    public Fixed ReadFixed() => Fixed.FromRaw(ReadInt64());

    /// <summary>Reads a LEB128 unsigned integer, rejecting encodings longer than 10 bytes or above 64 bits.</summary>
    public ulong ReadVarUInt64()
    {
        int start = _position;
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            byte b = ReadByte();
            if (shift == 63 && b > 1)
            {
                throw new InvalidDataException($"Variable-length integer at offset {start} overflows 64 bits.");
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException($"Variable-length integer at offset {start} is too long.");
    }

    /// <summary>Reads a zigzag-encoded LEB128 signed integer.</summary>
    public long ReadVarInt64()
    {
        ulong raw = ReadVarUInt64();
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    /// <summary>Reads <paramref name="count"/> raw bytes.</summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Take(count);
    }

    /// <summary>Reads a length-prefixed UTF-8 string of at most <paramref name="maxBytes"/> bytes (REQ-NET-11: bounded parsing).</summary>
    public string ReadString(int maxBytes = DefaultMaxStringBytes)
    {
        int start = _position;
        ulong length = ReadVarUInt64();
        if (length > (ulong)maxBytes)
        {
            throw new InvalidDataException($"String at offset {start} is {length} bytes, more than the limit of {maxBytes}.");
        }

        ReadOnlySpan<byte> bytes = Take((int)length);
        try
        {
            return BinaryBufferWriter.StrictUtf8.GetString(bytes);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"String at offset {start} is not valid UTF-8.", ex);
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > Remaining)
        {
            throw new InvalidDataException($"Unexpected end of data at offset {_position}: needed {count} bytes, {Remaining} left.");
        }

        ReadOnlySpan<byte> span = _data.Slice(_position, count);
        _position += count;
        return span;
    }
}
