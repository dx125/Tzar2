using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Tzar2.Core.Numerics;

namespace Tzar2.Core.Hashing;

/// <summary>
/// Incremental xxHash64 over simulation state (REQ-SIM-12). Values are fed in little-endian byte order,
/// so a given sequence of <c>Add</c> calls yields the same hash on every platform.
/// </summary>
/// <remarks>
/// Encoding contract: fixed-size values (<c>Add(int)</c>, <c>Add(Fixed)</c>, ...) are appended as their little-endian
/// bytes with no framing. Variable-length data (<see cref="Add(ReadOnlySpan{byte})"/>, <see cref="AddSpan{T}"/>) is
/// preceded by its length in bytes as a 64-bit little-endian integer, so that e.g. <c>Add([1, 2]).Add([3])</c> and
/// <c>Add([1]).Add([2, 3])</c> hash differently. Use <see cref="Hash"/> for the plain xxHash64 of a buffer.
/// </remarks>
public sealed class StateHasher
{
    private readonly XxHash64 _hash;

    /// <summary>Creates a hasher with the given xxHash64 seed.</summary>
    public StateHasher(long seed = 0)
    {
        if (!BitConverter.IsLittleEndian)
        {
            // Span hashing reinterprets memory; every supported target (x64, ARM64) is little-endian.
            throw new PlatformNotSupportedException("StateHasher requires a little-endian platform.");
        }

        _hash = new XxHash64(seed);
    }

    /// <summary>Adds one byte.</summary>
    public StateHasher Add(byte value)
    {
        _hash.Append([value]);
        return this;
    }

    /// <summary>Adds a boolean as one byte (0 or 1).</summary>
    public StateHasher Add(bool value) => Add(value ? (byte)1 : (byte)0);

    /// <summary>Adds a 32-bit integer.</summary>
    public StateHasher Add(int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        _hash.Append(buffer);
        return this;
    }

    /// <summary>Adds an unsigned 32-bit integer.</summary>
    public StateHasher Add(uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _hash.Append(buffer);
        return this;
    }

    /// <summary>Adds a 64-bit integer.</summary>
    public StateHasher Add(long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        _hash.Append(buffer);
        return this;
    }

    /// <summary>Adds an unsigned 64-bit integer.</summary>
    public StateHasher Add(ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        _hash.Append(buffer);
        return this;
    }

    /// <summary>Adds a fixed-point value (its raw representation).</summary>
    public StateHasher Add(Fixed value) => Add(value.Raw);

    /// <summary>Adds variable-length bytes: their length (64-bit little-endian), then the bytes themselves.</summary>
    public StateHasher Add(ReadOnlySpan<byte> bytes)
    {
        Add((long)bytes.Length);
        _hash.Append(bytes);
        return this;
    }

    /// <summary>
    /// Adds the memory of a span of unmanaged values, e.g. a dense component array, length-prefixed like
    /// <see cref="Add(ReadOnlySpan{byte})"/> (the prefix is the length in bytes). <typeparamref name="T"/>
    /// MUST NOT contain padding bytes or references to other memory, or the hash is not reproducible.
    /// </summary>
    public StateHasher AddSpan<T>(ReadOnlySpan<T> values)
        where T : unmanaged
        => Add(MemoryMarshal.AsBytes(values));

    /// <summary>The hash of everything added since creation or the last reset.</summary>
    public ulong GetCurrentHash() => _hash.GetCurrentHashAsUInt64();

    /// <summary>Returns the current hash and resets the hasher.</summary>
    public ulong GetHashAndReset()
    {
        ulong hash = _hash.GetCurrentHashAsUInt64();
        _hash.Reset();
        return hash;
    }

    /// <summary>Resets the hasher to its initial state.</summary>
    public void Reset() => _hash.Reset();

    /// <summary>One-shot xxHash64 of <paramref name="bytes"/>.</summary>
    public static ulong Hash(ReadOnlySpan<byte> bytes, long seed = 0) => XxHash64.HashToUInt64(bytes, seed);
}
