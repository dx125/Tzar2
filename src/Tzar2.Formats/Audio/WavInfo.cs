using System;

namespace Tzar2.Formats.Audio;

/// <summary>
/// Header facts of a RIFF/WAVE file. WAV is passed through unchanged (TDD §12.3); this reader only validates the
/// container and reports the PCM format for the sound manifest.
/// </summary>
/// <param name="FormatTag">1 = PCM.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="BitsPerSample">Bits per sample.</param>
/// <param name="BlockAlign">Bytes per sample frame.</param>
/// <param name="DataOffset">Offset of the sample data.</param>
/// <param name="DataLength">Length of the sample data actually present.</param>
/// <param name="DataTruncated">Whether the <c>data</c> chunk declares more bytes than the file holds.</param>
public sealed record WavInfo(int FormatTag, int Channels, int SampleRate, int BitsPerSample, int BlockAlign,
    int DataOffset, int DataLength, bool DataTruncated)
{
    private const string Format = "WAV";

    /// <summary>Number of sample frames.</summary>
    public long SampleFrames => BlockAlign == 0 ? 0 : DataLength / BlockAlign;

    /// <summary>Duration in milliseconds (integer).</summary>
    public long DurationMilliseconds => SampleRate == 0 ? 0 : SampleFrames * 1000 / SampleRate;

    /// <summary>Parses the RIFF header and the <c>fmt </c> and <c>data</c> chunks.</summary>
    /// <exception cref="LegacyFormatException">The data is not a RIFF/WAVE file with both chunks.</exception>
    public static WavInfo Parse(ReadOnlySpan<byte> data)
    {
        if (!ByteReader.Matches(data, 0, "RIFF"u8) || !ByteReader.Matches(data, 8, "WAVE"u8))
        {
            throw new LegacyFormatException(Format, "missing RIFF/WAVE signature.");
        }

        int? formatTag = null, channels = null, rate = null, bits = null, align = null;
        long p = 12;
        while (p + 8 <= data.Length)
        {
            uint chunkSize = ByteReader.U32(data, p + 4, Format, "chunk size");
            long body = p + 8;
            if (ByteReader.Matches(data, p, "fmt "u8))
            {
                if (chunkSize < 16)
                {
                    throw new LegacyFormatException(Format, "fmt chunk shorter than 16 bytes.");
                }

                formatTag = ByteReader.U16(data, body, Format, "format tag");
                channels = ByteReader.U16(data, body + 2, Format, "channels");
                rate = (int)Math.Min(ByteReader.U32(data, body + 4, Format, "sample rate"), int.MaxValue);
                align = ByteReader.U16(data, body + 12, Format, "block align");
                bits = ByteReader.U16(data, body + 14, Format, "bits per sample");
            }
            else if (ByteReader.Matches(data, p, "data"u8))
            {
                if (formatTag is null)
                {
                    throw new LegacyFormatException(Format, "data chunk before fmt chunk.");
                }

                long available = data.Length - body;
                bool truncated = chunkSize > available;
                int length = (int)Math.Min(chunkSize, available);
                return new WavInfo(formatTag.Value, channels!.Value, rate!.Value, bits!.Value, align!.Value, (int)body, length, truncated);
            }

            p = body + chunkSize + (chunkSize & 1);
        }

        throw new LegacyFormatException(Format, formatTag is null ? "no fmt chunk." : "no data chunk.");
    }
}
