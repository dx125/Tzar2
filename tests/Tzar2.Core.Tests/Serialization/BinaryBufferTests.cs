using System;
using System.IO;
using FsCheck.Xunit;
using Tzar2.Core.Numerics;
using Tzar2.Core.Serialization;
using Xunit;

namespace Tzar2.Core.Tests.Serialization;

public class BinaryBufferTests
{
    [Fact]
    public void Primitives_round_trip_in_little_endian()
    {
        var writer = new BinaryBufferWriter(initialCapacity: 0);
        writer.WriteByte(0xAB);
        writer.WriteBoolean(true);
        writer.WriteInt16(-2);
        writer.WriteUInt16(0xBEEF);
        writer.WriteInt32(0x01020304);
        writer.WriteUInt32(uint.MaxValue);
        writer.WriteInt64(long.MinValue);
        writer.WriteUInt64(ulong.MaxValue);
        writer.WriteFixed(Fixed.FromRatio(-3, 2));
        writer.WriteBytes([9, 8, 7]);

        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, writer.WrittenSpan.Slice(6, 4).ToArray());

        var reader = new BinaryBufferReader(writer.ToArray());
        Assert.Equal(0xAB, reader.ReadByte());
        Assert.True(reader.ReadBoolean());
        Assert.Equal(-2, reader.ReadInt16());
        Assert.Equal(0xBEEF, reader.ReadUInt16());
        Assert.Equal(0x01020304, reader.ReadInt32());
        Assert.Equal(uint.MaxValue, reader.ReadUInt32());
        Assert.Equal(long.MinValue, reader.ReadInt64());
        Assert.Equal(ulong.MaxValue, reader.ReadUInt64());
        Assert.Equal(Fixed.FromRatio(-3, 2), reader.ReadFixed());
        Assert.Equal(new byte[] { 9, 8, 7 }, reader.ReadBytes(3).ToArray());
        Assert.True(reader.IsAtEnd);
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(0UL, new byte[] { 0x00 })]
    [InlineData(127UL, new byte[] { 0x7F })]
    [InlineData(128UL, new byte[] { 0x80, 0x01 })]
    [InlineData(300UL, new byte[] { 0xAC, 0x02 })]
    [InlineData(ulong.MaxValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })]
    public void VarUInt64_uses_leb128(ulong value, byte[] encoded)
    {
        var writer = new BinaryBufferWriter();
        writer.WriteVarUInt64(value);
        Assert.Equal(encoded, writer.ToArray());
    }

    [Theory]
    [InlineData(0L, 0x00)]
    [InlineData(-1L, 0x01)]
    [InlineData(1L, 0x02)]
    [InlineData(-64L, 0x7F)]
    public void VarInt64_uses_zigzag(long value, byte encoded)
    {
        var writer = new BinaryBufferWriter();
        writer.WriteVarInt64(value);
        Assert.Equal(new[] { encoded }, writer.ToArray());
    }

    [Property(MaxTest = 2000)]
    public bool Varints_round_trip(ulong unsigned, long signed)
    {
        var writer = new BinaryBufferWriter();
        writer.WriteVarUInt64(unsigned);
        writer.WriteVarInt64(signed);
        var reader = new BinaryBufferReader(writer.WrittenSpan);
        return reader.ReadVarUInt64() == unsigned && reader.ReadVarInt64() == signed && reader.IsAtEnd;
    }

    [Property(MaxTest = 1000)]
    public bool Strings_round_trip(string? value)
    {
        string text = value ?? string.Empty;
        if (!IsWellFormedUtf16(text))
        {
            return true; // lone surrogates cannot be encoded as UTF-8
        }

        var writer = new BinaryBufferWriter();
        writer.WriteString(text);
        var reader = new BinaryBufferReader(writer.WrittenSpan);
        return reader.ReadString() == text && reader.IsAtEnd;
    }

    [Fact]
    public void Unicode_string_round_trips()
    {
        const string Text = "Огнем и мечом — Tzar ✓";
        var writer = new BinaryBufferWriter();
        writer.WriteString(Text);
        var reader = new BinaryBufferReader(writer.WrittenSpan);
        Assert.Equal(Text, reader.ReadString());
    }

    [Fact]
    public void Truncated_input_throws()
    {
        Assert.Throws<InvalidDataException>(() => new BinaryBufferReader([1, 2, 3]).ReadInt32());
        Assert.Throws<InvalidDataException>(() => new BinaryBufferReader([0x80]).ReadVarUInt64());
        Assert.Throws<InvalidDataException>(() => new BinaryBufferReader([5, (byte)'a']).ReadString());
    }

    [Fact]
    public void Malformed_input_throws()
    {
        Assert.Throws<InvalidDataException>(() => new BinaryBufferReader([2]).ReadBoolean());
        Assert.Throws<InvalidDataException>(() => new BinaryBufferReader([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02]).ReadVarUInt64());
        Assert.Throws<InvalidDataException>(() => new BinaryBufferReader([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x81, 0x00]).ReadVarUInt64());
        Assert.Throws<InvalidDataException>(() => new BinaryBufferReader([2, 0xC3, 0x28]).ReadString());
        Assert.Throws<InvalidDataException>(() => new BinaryBufferReader([10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]).ReadString(maxBytes: 4));
    }

    [Fact]
    public void Clear_reuses_the_buffer()
    {
        var writer = new BinaryBufferWriter();
        writer.WriteInt64(1);
        writer.Clear();
        Assert.Equal(0, writer.Length);
        writer.WriteByte(1);
        Assert.Equal(new byte[] { 1 }, writer.ToArray());
    }

    private static bool IsWellFormedUtf16(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
