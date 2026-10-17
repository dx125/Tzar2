using System;

namespace Tzar2.Content.Packages;

/// <summary>
/// CRC-32 (ISO-HDLC / ZIP / PNG: reflected polynomial 0xEDB88320, init and final XOR 0xFFFFFFFF). Implemented here so
/// Content needs no package beyond its allowed map (ADR 0011); it is only used for ZIP entry checksums and debug PNGs.
/// </summary>
public static class Crc32Checksum
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>The CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Append(Start, data));

    /// <summary>Initial running value for <see cref="Append"/>.</summary>
    public const uint Start = 0xFFFFFFFF;

    /// <summary>Feeds <paramref name="data"/> into a running value.</summary>
    public static uint Append(uint running, ReadOnlySpan<byte> data)
    {
        uint crc = running;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>Turns a running value into the checksum.</summary>
    public static uint Finish(uint running) => running ^ 0xFFFFFFFF;

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
