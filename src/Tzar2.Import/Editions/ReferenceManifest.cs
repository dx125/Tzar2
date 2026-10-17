using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Tzar2.Import.Editions;

/// <summary>One file of a reference build: relative path (forward slashes), size in bytes and MD5 (lowercase hex).</summary>
public sealed record ManifestEntry(string Path, long Size, string Md5);

/// <summary>
/// A file manifest of an original build, as stored in <c>docs/original-build/manifests/*.tsv</c>: a header row with
/// at least the tab-separated columns <c>path</c>, <c>size</c> and <c>md5</c> (any order; extra columns such as
/// <c>provenance</c> are ignored), then one row per file.
/// </summary>
public sealed class ReferenceManifest
{
    private ReferenceManifest(string name, IReadOnlyList<ManifestEntry> entries)
    {
        Name = name;
        Entries = entries;
    }

    /// <summary>A display name, usually the manifest file name.</summary>
    public string Name { get; }

    /// <summary>The entries in file order.</summary>
    public IReadOnlyList<ManifestEntry> Entries { get; }

    /// <summary>Total size of all files in bytes.</summary>
    public long TotalSize
    {
        get
        {
            long total = 0;
            foreach (ManifestEntry entry in Entries)
            {
                total += entry.Size;
            }

            return total;
        }
    }

    /// <summary>Loads a manifest file.</summary>
    public static ReferenceManifest Load(string path)
    {
        using var reader = new StreamReader(path);
        return Parse(reader, System.IO.Path.GetFileName(path));
    }

    /// <summary>Parses a manifest.</summary>
    /// <exception cref="FormatException">The manifest is malformed; the message names the line.</exception>
    public static ReferenceManifest Parse(TextReader reader, string name)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string? header = reader.ReadLine() ?? throw new FormatException($"{name}: the manifest is empty.");
        string[] columns = header.Split('\t');
        int pathColumn = Array.IndexOf(columns, "path");
        int sizeColumn = Array.IndexOf(columns, "size");
        int md5Column = Array.IndexOf(columns, "md5");
        if (pathColumn < 0 || sizeColumn < 0 || md5Column < 0)
        {
            throw new FormatException($"{name}:1: the header must contain the columns 'path', 'size' and 'md5'.");
        }

        var entries = new List<ManifestEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int lineNumber = 1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split('\t');
            if (fields.Length < columns.Length)
            {
                throw new FormatException($"{name}:{lineNumber}: expected {columns.Length} tab-separated fields, found {fields.Length}.");
            }

            string path = fields[pathColumn];
            if (path.Length == 0 || path.Contains('\\', StringComparison.Ordinal) || path.StartsWith('/') || path.Split('/').AsSpan().Contains(".."))
            {
                throw new FormatException($"{name}:{lineNumber}: '{path}' is not a relative path with forward slashes.");
            }

            if (!long.TryParse(fields[sizeColumn], NumberStyles.None, CultureInfo.InvariantCulture, out long size))
            {
                throw new FormatException($"{name}:{lineNumber}: invalid size '{fields[sizeColumn]}'.");
            }

            string md5 = fields[md5Column].ToLowerInvariant();
            if (md5.Length != 32 || !IsHex(md5))
            {
                throw new FormatException($"{name}:{lineNumber}: invalid MD5 '{fields[md5Column]}'.");
            }

            if (!seen.Add(path))
            {
                throw new FormatException($"{name}:{lineNumber}: duplicate path '{path}'.");
            }

            entries.Add(new ManifestEntry(path, size, md5));
        }

        return new ReferenceManifest(name, entries);
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
