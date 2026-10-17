using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tzar2.Content.Packages;

/// <summary>
/// Builds a <c>.t2pkg</c> package (TDD REQ-DATA-01): a ZIP archive with a <c>package.yaml</c> manifest.
/// The output is <b>byte-deterministic</b> (REQ-IMP-02) on every OS and runtime: entries are sorted by path (ordinal),
/// stored uncompressed (no dependency on a zlib build), with a fixed timestamp (1980-01-01 00:00), fixed
/// "version made by" (MS-DOS, 2.0), no extra fields and no comments. Stored entries can also be read in place
/// (memory-mapped) by the client. Limits of classic ZIP apply (no ZIP64): fewer than 65,535 entries, every offset
/// and size below 4 GiB.
/// </summary>
public sealed class PackageWriter
{
    /// <summary>Name of the manifest entry.</summary>
    public const string ManifestPath = "package.yaml";

    internal const uint LocalHeaderSignature = 0x04034B50;
    internal const uint CentralHeaderSignature = 0x02014B50;
    internal const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const ushort VersionNeeded = 10;
    private const ushort VersionMadeBy = 20;
    private const ushort Utf8Flag = 0x0800;
    private const ushort DosDate1980 = (1 << 5) | 1;

    private readonly SortedDictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

    /// <summary>Number of entries added so far.</summary>
    public int Count => _entries.Count;

    /// <summary>Adds an entry. <paramref name="path"/> must be a normalised content path (see <see cref="ContentPath"/>).</summary>
    /// <exception cref="ArgumentException">The path is not normalised or already present.</exception>
    public void Add(string path, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(data);
        if (!ContentPath.IsNormalized(path))
        {
            throw new ArgumentException($"'{path}' is not a normalised content path.", nameof(path));
        }

        if (!_entries.TryAdd(path, data))
        {
            throw new ArgumentException($"Duplicate package entry '{path}'.", nameof(path));
        }
    }

    /// <summary>Adds a UTF-8 text entry (no BOM).</summary>
    public void AddText(string path, string text) => Add(path, new UTF8Encoding(false).GetBytes(text));

    /// <summary>Whether an entry exists.</summary>
    public bool Contains(string path) => _entries.ContainsKey(path);

    /// <summary>The entries in output order.</summary>
    public IEnumerable<KeyValuePair<string, byte[]>> Entries => _entries;

    /// <summary>Writes the archive.</summary>
    /// <exception cref="InvalidOperationException">The package exceeds the classic ZIP limits.</exception>
    public void WriteTo(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_entries.Count >= ushort.MaxValue)
        {
            throw new InvalidOperationException($"Too many entries ({_entries.Count}) for a package without ZIP64.");
        }

        var central = new MemoryStream();
        long offset = 0;
        Span<byte> header = stackalloc byte[46];
        foreach ((string path, byte[] data) in _entries)
        {
            byte[] name = Encoding.UTF8.GetBytes(path);
            uint crc = Crc32Checksum.Compute(data);
            if (offset + data.Length > uint.MaxValue)
            {
                throw new InvalidOperationException("Package exceeds 4 GiB; ZIP64 is not supported.");
            }

            header.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(header, LocalHeaderSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], VersionNeeded);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], Utf8Flag);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], DosDate1980);
            BinaryPrimitives.WriteUInt32LittleEndian(header[14..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(header[18..], (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[22..], (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 0);
            output.Write(header[..30]);
            output.Write(name);
            output.Write(data);

            header.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(header, CentralHeaderSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], VersionMadeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], VersionNeeded);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], Utf8Flag);
            BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], DosDate1980);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header[28..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[42..], (uint)offset);
            central.Write(header);
            central.Write(name);

            offset += 30 + name.Length + data.Length;
        }

        if (offset + central.Length > uint.MaxValue)
        {
            throw new InvalidOperationException("Package exceeds 4 GiB; ZIP64 is not supported.");
        }

        central.Position = 0;
        central.CopyTo(output);
        Span<byte> end = stackalloc byte[22];
        end.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(end, EndOfCentralDirectorySignature);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)central.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], (uint)offset);
        output.Write(end);
    }

    /// <summary>Writes the archive to a file (replacing it).</summary>
    public void WriteTo(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        WriteTo(stream);
    }
}
