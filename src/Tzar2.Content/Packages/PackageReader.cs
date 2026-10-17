using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Tzar2.Content.Packages;

/// <summary>An entry of a package's central directory.</summary>
/// <param name="Path">Entry path as stored.</param>
/// <param name="Method">0 = stored, 8 = deflate.</param>
/// <param name="CompressedSize">Bytes in the archive.</param>
/// <param name="Size">Uncompressed bytes.</param>
/// <param name="Crc32">CRC-32 of the content.</param>
/// <param name="LocalHeaderOffset">Offset of the local file header.</param>
public sealed record PackageEntry(string Path, int Method, long CompressedSize, long Size, uint Crc32, long LocalHeaderOffset);

/// <summary>
/// Reads <c>.t2pkg</c> packages (ZIP, stored or deflate entries, no ZIP64). Malformed archives throw
/// <see cref="InvalidDataException"/>. Thread-safe: reads are serialised on the underlying stream.
/// </summary>
public sealed class PackageReader : IDisposable
{
    /// <summary>Largest entry the reader accepts (1 GiB).</summary>
    public const long MaxEntrySize = 1L << 30;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly Dictionary<string, PackageEntry> _byPath;
    private readonly object _gate = new();

    private PackageReader(Stream stream, bool ownsStream, List<PackageEntry> entries, string name)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Entries = entries;
        Name = name;
        _byPath = new Dictionary<string, PackageEntry>(StringComparer.Ordinal);
        foreach (PackageEntry entry in entries)
        {
            if (!_byPath.TryAdd(entry.Path, entry))
            {
                throw new InvalidDataException($"{name}: duplicate entry '{entry.Path}'.");
            }
        }
    }

    /// <summary>Display name (file name).</summary>
    public string Name { get; }

    /// <summary>Entries in central-directory order.</summary>
    public IReadOnlyList<PackageEntry> Entries { get; }

    /// <summary>Opens a package file.</summary>
    public static PackageReader Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            return Open(stream, System.IO.Path.GetFileName(path), ownsStream: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens a package from a seekable stream.</summary>
    /// <exception cref="InvalidDataException">The archive is malformed.</exception>
    public static PackageReader Open(Stream stream, string name, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        long length = stream.Length;
        int tail = (int)Math.Min(length, 22 + ushort.MaxValue);
        byte[] buffer = new byte[tail];
        stream.Position = length - tail;
        stream.ReadExactly(buffer);
        int eocd = -1;
        for (int i = tail - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) == PackageWriter.EndOfCentralDirectorySignature)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            throw new InvalidDataException($"{name}: not a ZIP archive (no end of central directory).");
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(eocd + 10));
        uint cdSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(eocd + 12));
        uint cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(eocd + 16));
        if ((long)cdOffset + cdSize > length)
        {
            throw new InvalidDataException($"{name}: central directory out of range.");
        }

        byte[] cd = new byte[cdSize];
        stream.Position = cdOffset;
        stream.ReadExactly(cd);
        var entries = new List<PackageEntry>(count);
        int p = 0;
        for (int i = 0; i < count; i++)
        {
            if (p + 46 > cd.Length || BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p)) != PackageWriter.CentralHeaderSignature)
            {
                throw new InvalidDataException($"{name}: bad central directory entry {i}.");
            }

            ReadOnlySpan<byte> h = cd.AsSpan(p);
            int method = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
            uint csize = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
            uint usize = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(h[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
            uint local = BinaryPrimitives.ReadUInt32LittleEndian(h[42..]);
            if (p + 46 + nameLength > cd.Length)
            {
                throw new InvalidDataException($"{name}: truncated central directory entry {i}.");
            }

            string path = Encoding.UTF8.GetString(cd, p + 46, nameLength);
            if (method is not (0 or 8))
            {
                throw new InvalidDataException($"{name}: entry '{path}' uses unsupported compression method {method}.");
            }

            if ((long)local + 30 + csize > length)
            {
                throw new InvalidDataException($"{name}: entry '{path}' out of range.");
            }

            // Deflate expands at most ~1032:1; a larger claim is a decompression bomb (the buffer is allocated up front).
            if (usize > MaxEntrySize || (method == 0 && csize != usize) || (method == 8 && usize > ((long)csize * 1032) + 1024))
            {
                throw new InvalidDataException($"{name}: entry '{path}' has an invalid size.");
            }

            entries.Add(new PackageEntry(path, method, csize, usize, crc, local));
            p += 46 + nameLength + extraLength + commentLength;
        }

        return new PackageReader(stream, ownsStream, entries, name);
    }

    /// <summary>Whether an entry exists (exact path).</summary>
    public bool Contains(string path) => _byPath.ContainsKey(path);

    /// <summary>The entry for <paramref name="path"/>, or null.</summary>
    public PackageEntry? Find(string path) => _byPath.GetValueOrDefault(path);

    /// <summary>Reads and CRC-checks an entry.</summary>
    /// <exception cref="FileNotFoundException">No such entry.</exception>
    /// <exception cref="InvalidDataException">The entry is corrupt.</exception>
    public byte[] ReadAllBytes(string path)
    {
        PackageEntry entry = Find(path) ?? throw new FileNotFoundException($"{Name}: no entry '{path}'.", path);
        byte[] raw;
        lock (_gate)
        {
            Span<byte> local = stackalloc byte[30];
            _stream.Position = entry.LocalHeaderOffset;
            _stream.ReadExactly(local);
            if (BinaryPrimitives.ReadUInt32LittleEndian(local) != PackageWriter.LocalHeaderSignature)
            {
                throw new InvalidDataException($"{Name}: bad local header for '{path}'.");
            }

            int skip = BinaryPrimitives.ReadUInt16LittleEndian(local[26..]) + BinaryPrimitives.ReadUInt16LittleEndian(local[28..]);
            long dataStart = entry.LocalHeaderOffset + 30 + skip;
            if (dataStart + entry.CompressedSize > _stream.Length)
            {
                throw new InvalidDataException($"{Name}: entry '{path}' runs past the archive.");
            }

            raw = new byte[entry.CompressedSize];
            _stream.Position = dataStart;
            _stream.ReadExactly(raw);
        }

        byte[] data;
        if (entry.Method == 0)
        {
            data = raw;
        }
        else
        {
            data = new byte[entry.Size];
            using var inflater = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
            try
            {
                inflater.ReadExactly(data);
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException($"{Name}: entry '{path}' inflates to fewer bytes than declared.", ex);
            }
        }

        if (data.Length != entry.Size || Crc32Checksum.Compute(data) != entry.Crc32)
        {
            throw new InvalidDataException($"{Name}: CRC mismatch in '{path}'.");
        }

        return data;
    }

    /// <summary>Reads a UTF-8 text entry.</summary>
    public string ReadText(string path) => new UTF8Encoding(false, true).GetString(ReadAllBytes(path));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
