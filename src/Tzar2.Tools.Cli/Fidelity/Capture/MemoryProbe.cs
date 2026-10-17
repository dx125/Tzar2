using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Tzar2.Tools.Cli.Fidelity.Scenarios;

namespace Tzar2.Tools.Cli.Fidelity.Capture;

/// <summary>The addresses (and their values at scan time) found by a memory scan.</summary>
internal sealed record MemoryScanResult(string Name, int Width, IReadOnlyList<ulong> Addresses, IReadOnlyList<long> Values, bool Truncated, long BytesScanned, int Regions);

/// <summary>
/// Read-only memory probes (ADR 0030 §memory): value scans over the game's writable memory, narrowing scans
/// ("which of these changed / decreased"), and background sampling of the found addresses. Values are little-endian
/// signed integers of 1, 2 or 4 bytes at addresses aligned to their width.
/// </summary>
internal static class MemoryProbe
{
    private const int ChunkSize = 1 << 20;

    public static MemoryScanResult Scan(IMemoryReader reader, string name, long value, int width, int maxResults)
    {
        var addresses = new List<ulong>();
        var values = new List<long>();
        long scanned = 0;
        IReadOnlyList<MemoryRegion> regions = reader.WritableRegions();
        byte[] buffer = new byte[ChunkSize];
        bool truncated = false;
        foreach (MemoryRegion region in regions)
        {
            for (long offset = 0; offset < region.Size && !truncated; offset += ChunkSize)
            {
                int length = (int)Math.Min(ChunkSize, region.Size - offset);
                ulong start = region.BaseAddress + (ulong)offset;
                int read = reader.Read(start, buffer.AsSpan(0, length));
                scanned += read;
                for (int i = 0; i + width <= read; i += width)
                {
                    if (Decode(buffer.AsSpan(i, width)) == value)
                    {
                        if (addresses.Count >= maxResults)
                        {
                            truncated = true;
                            break;
                        }

                        addresses.Add(start + (ulong)i);
                        values.Add(value);
                    }
                }
            }
        }

        return new MemoryScanResult(name, width, addresses, values, truncated, scanned, regions.Count);
    }

    public static MemoryScanResult Filter(IMemoryReader reader, string name, MemoryScanResult from, ScanMode mode, long? value, int maxResults)
    {
        var addresses = new List<ulong>();
        var values = new List<long>();
        Span<byte> cell = stackalloc byte[8];
        bool truncated = false;
        for (int i = 0; i < from.Addresses.Count; i++)
        {
            if (reader.Read(from.Addresses[i], cell[..from.Width]) != from.Width)
            {
                continue;
            }

            long now = Decode(cell[..from.Width]);
            long before = from.Values[i];
            bool keep = mode switch
            {
                ScanMode.Exact => now == value,
                ScanMode.Changed => now != before,
                ScanMode.Unchanged => now == before,
                ScanMode.Decreased => now < before,
                ScanMode.Increased => now > before,
                _ => false,
            };
            if (keep && value is { } v && mode != ScanMode.Exact)
            {
                keep = now == v;
            }

            if (!keep)
            {
                continue;
            }

            if (addresses.Count >= maxResults)
            {
                truncated = true;
                break;
            }

            addresses.Add(from.Addresses[i]);
            values.Add(now);
        }

        return new MemoryScanResult(name, from.Width, addresses, values, truncated || from.Truncated, (long)from.Addresses.Count * from.Width, 0);
    }

    public static long Decode(ReadOnlySpan<byte> data) => data.Length switch
    {
        1 => (sbyte)data[0],
        2 => BinaryPrimitives.ReadInt16LittleEndian(data),
        4 => BinaryPrimitives.ReadInt32LittleEndian(data),
        _ => throw new ArgumentException("width must be 1, 2 or 4", nameof(data)),
    };

    /// <summary>
    /// Samples addresses every <paramref name="interval"/> until the duration elapses or <paramref name="cancel"/> fires,
    /// writing the <c>T2MS</c> sample file: "T2MS", u32 version 1, u32 address count, u32 width, u64 addresses,
    /// then records of (i64 microseconds since <paramref name="epoch"/>, count × width-byte values).
    /// </summary>
    public static int Sample(IMemoryReader reader, IReadOnlyList<ulong> addresses, int width, TimeSpan interval, TimeSpan duration,
        Stream output, long epoch, CancellationToken cancel)
    {
        Span<byte> header = stackalloc byte[16];
        "T2MS"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)addresses.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)width);
        output.Write(header);
        Span<byte> address = stackalloc byte[8];
        foreach (ulong a in addresses)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(address, a);
            output.Write(address);
        }

        byte[] record = new byte[8 + (addresses.Count * width)];
        long start = Stopwatch.GetTimestamp();
        int samples = 0;
        while (!cancel.IsCancellationRequested && Stopwatch.GetElapsedTime(start) < duration)
        {
            long t = Stopwatch.GetTimestamp();
            BinaryPrimitives.WriteInt64LittleEndian(record, (long)Stopwatch.GetElapsedTime(epoch, t).TotalMicroseconds);
            for (int i = 0; i < addresses.Count; i++)
            {
                Span<byte> slot = record.AsSpan(8 + (i * width), width);
                if (reader.Read(addresses[i], slot) != width)
                {
                    slot.Clear();
                }
            }

            output.Write(record);
            samples++;
            TimeSpan wait = interval - Stopwatch.GetElapsedTime(t);
            if (wait > TimeSpan.Zero && cancel.WaitHandle.WaitOne(wait))
            {
                break;
            }
        }

        return samples;
    }

    /// <summary>A parsed <c>T2MS</c> sample file.</summary>
    internal sealed record SampleSeries(IReadOnlyList<ulong> Addresses, int Width, IReadOnlyList<long> TimesMicroseconds, long[][] Values);

    /// <exception cref="InvalidDataException">The file is not a valid sample file.</exception>
    public static SampleSeries ReadSamples(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data[..4].SequenceEqual("T2MS"u8) || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 1)
        {
            throw new InvalidDataException("not a T2MS sample file");
        }

        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (width is not (1 or 2 or 4) || count < 0 || count > 1 << 20 || 16L + (8L * count) > data.Length)
        {
            throw new InvalidDataException("corrupt T2MS header");
        }

        var addresses = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            addresses[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(16 + (8 * i))..]);
        }

        int recordSize = 8 + (count * width);
        int body = 16 + (8 * count);
        int records = (data.Length - body) / recordSize;
        var times = new long[records];
        long[][] values = new long[count][];
        for (int a = 0; a < count; a++)
        {
            values[a] = new long[records];
        }

        for (int r = 0; r < records; r++)
        {
            ReadOnlySpan<byte> rec = data.Slice(body + (r * recordSize), recordSize);
            times[r] = BinaryPrimitives.ReadInt64LittleEndian(rec);
            for (int a = 0; a < count; a++)
            {
                values[a][r] = Decode(rec.Slice(8 + (a * width), width));
            }
        }

        return new SampleSeries(addresses, width, times, values);
    }
}
