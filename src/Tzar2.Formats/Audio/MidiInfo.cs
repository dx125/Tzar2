using System;

namespace Tzar2.Formats.Audio;

/// <summary>
/// Header facts of a Standard MIDI File. MIDI is passed through unchanged (TDD §12.3); this reader validates the
/// <c>MThd</c> header and counts the <c>MTrk</c> chunks.
/// </summary>
/// <param name="FormatType">SMF type 0, 1 or 2.</param>
/// <param name="DeclaredTracks">Track count from the header.</param>
/// <param name="TrackChunks">Number of complete <c>MTrk</c> chunks found.</param>
/// <param name="Division">Time division word.</param>
public sealed record MidiInfo(int FormatType, int DeclaredTracks, int TrackChunks, int Division)
{
    private const string Format = "MID";

    /// <summary>Parses the header and walks the chunks.</summary>
    /// <exception cref="LegacyFormatException">The data is not a Standard MIDI File.</exception>
    public static MidiInfo Parse(ReadOnlySpan<byte> data)
    {
        if (!ByteReader.Matches(data, 0, "MThd"u8))
        {
            throw new LegacyFormatException(Format, "missing 'MThd' signature.");
        }

        uint headerLength = ByteReader.U32BigEndian(data, 4, Format, "header length");
        if (headerLength < 6)
        {
            throw new LegacyFormatException(Format, "header shorter than 6 bytes.");
        }

        int type = ByteReader.U16BigEndian(data, 8, Format, "format");
        int tracks = ByteReader.U16BigEndian(data, 10, Format, "track count");
        int division = ByteReader.U16BigEndian(data, 12, Format, "division");
        if (type > 2)
        {
            throw new LegacyFormatException(Format, $"unknown SMF type {type}.");
        }

        int found = 0;
        long p = 8L + headerLength;
        while (p + 8 <= data.Length)
        {
            uint length = ByteReader.U32BigEndian(data, p + 4, Format, "chunk length");
            if (p + 8 + length > data.Length)
            {
                break;
            }

            if (ByteReader.Matches(data, p, "MTrk"u8))
            {
                found++;
            }

            p += 8 + length;
        }

        if (found == 0)
        {
            throw new LegacyFormatException(Format, "no complete MTrk chunk.");
        }

        return new MidiInfo(type, tracks, found, division);
    }
}
