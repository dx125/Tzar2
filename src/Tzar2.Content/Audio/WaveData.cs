using System;
using System.Buffers.Binary;
using System.IO;

namespace Tzar2.Content.Audio;

/// <summary>
/// A PCM <c>.wav</c> file as kept in the package (<c>sound/**.wav</c>, TDD §12.3; the originals are 22.05 kHz 16-bit
/// mono, REQ-AUD-01): the <c>fmt </c> fields and the location of the <c>data</c> chunk. Only uncompressed PCM
/// (format tag 1) with 8 or 16 bits per sample is accepted.
/// </summary>
public sealed record WaveData(int Channels, int SampleRate, int BitsPerSample, int DataOffset, int DataLength)
{
    /// <summary>Sample frames in the data chunk.</summary>
    public int Frames => DataLength / (Channels * (BitsPerSample / 8));

    /// <summary>Duration in milliseconds (integer, rounded down).</summary>
    public long DurationMs => (long)Frames * 1000 / SampleRate;

    /// <summary>Parses the RIFF chunks of <paramref name="file"/>.</summary>
    /// <exception cref="InvalidDataException">Not a PCM RIFF/WAVE file, or the chunks are inconsistent.</exception>
    public static WaveData Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("wav: not a RIFF/WAVE file.");
        }

        int channels = 0, rate = 0, bits = 0, format = 0;
        int pos = 12;
        while (pos + 8 <= file.Length)
        {
            ReadOnlySpan<byte> id = file.Slice(pos, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(pos + 4, 4));
            int body = pos + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || body + 16 > file.Length)
                {
                    throw new InvalidDataException("wav: truncated fmt chunk.");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(body + 2, 2));
                rate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(body + 4, 4)), int.MaxValue);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format != 1 || channels is < 1 or > 2 || rate <= 0 || bits is not (8 or 16))
                {
                    throw new InvalidDataException($"wav: unsupported format (tag {format}, {channels} ch, {rate} Hz, {bits} bit).");
                }

                // Some writers put a larger size than the file holds; clamp to what is there.
                int length = (int)Math.Min(size, (uint)(file.Length - body));
                length -= length % (channels * (bits / 8));
                return new WaveData(channels, rate, bits, body, length);
            }

            if (size > (uint)(file.Length - body))
            {
                break;
            }

            pos = body + (int)size + (int)(size & 1);
        }

        throw new InvalidDataException("wav: no data chunk.");
    }
}
