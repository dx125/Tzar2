using System;
using System.Buffers.Binary;
using System.Text;
using Tzar2.Core.Numerics;

namespace Tzar2.Core.Serialization;

/// <summary>
/// Growable little-endian binary writer for snapshots, replays and commands (REQ-ARCH-02: hand-written,
/// reflection-free serialization). Pair with <see cref="BinaryBufferReader"/>.
/// </summary>
public sealed class BinaryBufferWriter
{
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private byte[] _buffer;
    private int _length;

    /// <summary>Creates a writer with the given initial capacity in bytes.</summary>
    public BinaryBufferWriter(int initialCapacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _buffer = new byte[initialCapacity];
    }

    /// <summary>Number of bytes written.</summary>
    public int Length => _length;

    /// <summary>The bytes written so far. Invalidated by further writes.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    /// <summary>Copies the written bytes into a new array.</summary>
    public byte[] ToArray() => WrittenSpan.ToArray();

    /// <summary>Discards everything written, keeping the buffer.</summary>
    public void Clear() => _length = 0;

    /// <summary>Writes one byte.</summary>
    public void WriteByte(byte value) => Reserve(1)[0] = value;

    /// <summary>Writes a boolean as one byte (0 or 1).</summary>
    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    /// <summary>Writes a signed 16-bit integer.</summary>
    public void WriteInt16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Reserve(sizeof(short)), value);

    /// <summary>Writes an unsigned 16-bit integer.</summary>
    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(sizeof(ushort)), value);

    /// <summary>Writes a signed 32-bit integer.</summary>
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(sizeof(int)), value);

    /// <summary>Writes an unsigned 32-bit integer.</summary>
    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(sizeof(uint)), value);

    /// <summary>Writes a signed 64-bit integer.</summary>
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(sizeof(long)), value);

    /// <summary>Writes an unsigned 64-bit integer.</summary>
    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(sizeof(ulong)), value);

    /// <summary>Writes a fixed-point value as its 64-bit raw representation.</summary>
    public void WriteFixed(Fixed value) => WriteInt64(value.Raw);

    /// <summary>Writes an unsigned integer as LEB128 (1-10 bytes).</summary>
    public void WriteVarUInt64(ulong value)
    {
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    /// <summary>Writes a signed integer as zigzag-encoded LEB128 (1-10 bytes).</summary>
    public void WriteVarInt64(long value) => WriteVarUInt64((ulong)((value << 1) ^ (value >> 63)));

    /// <summary>Writes raw bytes without a length prefix.</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

    /// <summary>Writes a UTF-8 string prefixed with its byte length (LEB128).</summary>
    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount = StrictUtf8.GetByteCount(value);
        WriteVarUInt64((ulong)byteCount);
        StrictUtf8.GetBytes(value, Reserve(byteCount));
    }

    private Span<byte> Reserve(int count)
    {
        int required = checked(_length + count);
        if (required > _buffer.Length)
        {
            int newCapacity = Math.Max(required, Math.Max(16, _buffer.Length * 2));
            Array.Resize(ref _buffer, newCapacity);
        }

        Span<byte> span = _buffer.AsSpan(_length, count);
        _length = required;
        return span;
    }
}
