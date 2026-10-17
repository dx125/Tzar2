using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Tzar2.Formats.Text;

namespace Tzar2.Import.Pipeline;

/// <summary>Per-category file counts.</summary>
public sealed class CategoryCount
{
    /// <summary>Files of this kind in the install.</summary>
    public int Found { get; set; }

    /// <summary>Files decoded and converted.</summary>
    public int Imported { get; set; }

    /// <summary>Files that failed.</summary>
    public int Failed { get; set; }
}

/// <summary>A message about one source file.</summary>
/// <param name="Source">Normalised source path (or empty for global messages).</param>
/// <param name="Message">Text.</param>
public sealed record ImportIssue(string Source, string Message);

/// <summary>Counters for one pixel format of the sprite memory measurement (spike S6).</summary>
public sealed class FormatStats
{
    /// <summary>Sheets.</summary>
    public int Sheets { get; set; }

    /// <summary>Cells.</summary>
    public long Cells { get; set; }

    /// <summary>Empty cells.</summary>
    public long EmptyCells { get; set; }

    /// <summary>Untrimmed cell pixels.</summary>
    public long FullCellPixels { get; set; }

    /// <summary>Trimmed cell pixels.</summary>
    public long TrimmedPixels { get; set; }

    /// <summary>Opaque pixels.</summary>
    public long OpaquePixels { get; set; }

    /// <summary>Bytes stored in the chosen layout.</summary>
    public long StoredBytes { get; set; }

    /// <summary>Texture-array layers needed at 2,048².</summary>
    public int Pages2048 { get; set; }

    /// <summary>Occupancy of those layers (per mille).</summary>
    public int Occupancy2048 { get; set; }

    /// <summary>Texture-array layers needed at 4,096².</summary>
    public int Pages4096 { get; set; }

    /// <summary>Occupancy of those layers (per mille).</summary>
    public int Occupancy4096 { get; set; }
}

/// <summary>The import report (TDD REQ-IMP-03). Written next to the package, never into it (it holds timings).</summary>
public sealed class ImportReport
{
    /// <summary>Install folder.</summary>
    public string OriginPath { get; set; } = string.Empty;

    /// <summary>Detected edition id, or null.</summary>
    public string? EditionId { get; set; }

    /// <summary>Detected edition name, or null.</summary>
    public string? EditionName { get; set; }

    /// <summary>Whether the install is byte-identical to the edition.</summary>
    public bool EditionExact { get; set; }

    /// <summary>Share of the edition's files present with the right size (per mille).</summary>
    public int EditionSizeMatchPerMille { get; set; }

    /// <summary>Edition files that differ.</summary>
    public IReadOnlyList<string> ModifiedFiles { get; set; } = [];

    /// <summary>Edition files that are missing.</summary>
    public IReadOnlyList<string> MissingFiles { get; set; } = [];

    /// <summary>Files not in the edition manifest.</summary>
    public int ExtraFiles { get; set; }

    /// <summary>Code page used for text.</summary>
    public CodePage CodePage { get; set; } = CodePage.Windows1252;

    /// <summary>Counts per <see cref="SourceKind"/> name.</summary>
    public SortedDictionary<string, CategoryCount> Categories { get; } = new(StringComparer.Ordinal);

    /// <summary>Failures (the import is unsuccessful when any exist).</summary>
    public List<ImportIssue> Errors { get; } = [];

    /// <summary>Notes that do not fail the import.</summary>
    public List<ImportIssue> Warnings { get; } = [];

    /// <summary>Sprite memory measurement per pixel format name.</summary>
    public SortedDictionary<string, FormatStats> SpriteMemory { get; } = new(StringComparer.Ordinal);

    /// <summary>Worst-case residency (all non-unit sheets + common units + the largest civilisation) in stored bytes.</summary>
    public long ResidencyWorstCivBytes { get; set; }

    /// <summary>The civilisation prefix that is the largest.</summary>
    public string ResidencyWorstCiv { get; set; } = string.Empty;

    /// <summary>Trimmed pixels × 5 B (layout a, RGBA8 + R8 mask).</summary>
    public long LayoutRgba8MaskBytes { get; set; }

    /// <summary>Trimmed pixels × 3 B (layout b, RGB5A1 + R8 mask).</summary>
    public long LayoutRgb5A1MaskBytes { get; set; }

    /// <summary>Files inside maps and campaigns (incl. embedded maps).</summary>
    public int ContainerMembers { get; set; }

    /// <summary>Maps embedded in campaigns.</summary>
    public int NestedMaps { get; set; }

    /// <summary>Package file.</summary>
    public string PackagePath { get; set; } = string.Empty;

    /// <summary>Package size.</summary>
    public long PackageBytes { get; set; }

    /// <summary>Package entries.</summary>
    public int PackageEntries { get; set; }

    /// <summary>SHA-256 of the package file (hex).</summary>
    public string PackageSha256 { get; set; } = string.Empty;

    /// <summary>Gameplay hash written to package.yaml.</summary>
    public string GameplayHash { get; set; } = string.Empty;

    /// <summary>Wall time of the decode/convert phase.</summary>
    public TimeSpan ConvertTime { get; set; }

    /// <summary>Total wall time.</summary>
    public TimeSpan TotalTime { get; set; }

    /// <summary>Whether no file failed.</summary>
    public bool Succeeded => Errors.Count == 0;

    /// <summary>The category counter for <paramref name="kind"/>.</summary>
    public CategoryCount Category(SourceKind kind)
    {
        string name = kind.ToString().ToLowerInvariant();
        if (!Categories.TryGetValue(name, out CategoryCount? count))
        {
            count = new CategoryCount();
            Categories.Add(name, count);
        }

        return count;
    }

    /// <summary>JSON form.</summary>
    public byte[] ToJson() => JsonOutput.Write(w =>
    {
        w.WriteStartObject();
        w.WriteBoolean("succeeded", Succeeded);
        w.WriteString("origin", OriginPath);
        w.WriteStartObject("edition");
        w.WriteString("id", EditionId);
        w.WriteString("name", EditionName);
        w.WriteBoolean("exact", EditionExact);
        w.WriteNumber("sizeMatchPerMille", EditionSizeMatchPerMille);
        TextConverters.WriteStringArray(w, "modified", ModifiedFiles);
        TextConverters.WriteStringArray(w, "missing", MissingFiles);
        w.WriteNumber("extraFiles", ExtraFiles);
        w.WriteNumber("codePage", (int)CodePage);
        w.WriteEndObject();
        w.WriteStartObject("categories");
        foreach ((string name, CategoryCount c) in Categories)
        {
            w.WriteStartObject(name);
            w.WriteNumber("found", c.Found);
            w.WriteNumber("imported", c.Imported);
            w.WriteNumber("failed", c.Failed);
            w.WriteEndObject();
        }

        w.WriteEndObject();
        w.WriteStartObject("containers");
        w.WriteNumber("members", ContainerMembers);
        w.WriteNumber("nestedMaps", NestedMaps);
        w.WriteEndObject();
        w.WriteStartObject("spriteMemory");
        foreach ((string name, FormatStats s) in SpriteMemory)
        {
            w.WriteStartObject(name);
            w.WriteNumber("sheets", s.Sheets);
            w.WriteNumber("cells", s.Cells);
            w.WriteNumber("emptyCells", s.EmptyCells);
            w.WriteNumber("fullCellPixels", s.FullCellPixels);
            w.WriteNumber("trimmedPixels", s.TrimmedPixels);
            w.WriteNumber("opaquePixels", s.OpaquePixels);
            w.WriteNumber("storedBytes", s.StoredBytes);
            w.WriteNumber("pages2048", s.Pages2048);
            w.WriteNumber("occupancy2048PerMille", s.Occupancy2048);
            w.WriteNumber("pages4096", s.Pages4096);
            w.WriteNumber("occupancy4096PerMille", s.Occupancy4096);
            w.WriteEndObject();
        }

        w.WriteNumber("layoutRgba8MaskBytes", LayoutRgba8MaskBytes);
        w.WriteNumber("layoutRgb5A1MaskBytes", LayoutRgb5A1MaskBytes);
        w.WriteString("residencyWorstCiv", ResidencyWorstCiv);
        w.WriteNumber("residencyWorstCivBytes", ResidencyWorstCivBytes);
        w.WriteEndObject();
        w.WriteStartObject("package");
        w.WriteString("path", PackagePath);
        w.WriteNumber("bytes", PackageBytes);
        w.WriteNumber("entries", PackageEntries);
        w.WriteString("sha256", PackageSha256);
        w.WriteString("gameplayHash", GameplayHash);
        w.WriteEndObject();
        w.WriteStartObject("timing");
        w.WriteNumber("convertMilliseconds", (long)ConvertTime.TotalMilliseconds);
        w.WriteNumber("totalMilliseconds", (long)TotalTime.TotalMilliseconds);
        w.WriteEndObject();
        WriteIssues(w, "errors", Errors);
        WriteIssues(w, "warnings", Warnings);
        w.WriteEndObject();
    });

    /// <summary>Human-readable summary.</summary>
    public string ToText(int maxIssues = 40)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(inv, $"Import of {OriginPath}\n");
        sb.Append(inv, $"Edition:   {EditionName ?? "unknown"} ({EditionId ?? "-"}), {(EditionExact ? "exact" : "not exact")}, size match {EditionSizeMatchPerMille / 10.0:0.0} %, modified {ModifiedFiles.Count}, missing {MissingFiles.Count}, extra {ExtraFiles}\n");
        sb.Append(inv, $"Code page: {(int)CodePage}\n");
        sb.Append("Files:\n");
        foreach ((string name, CategoryCount c) in Categories)
        {
            sb.Append(inv, $"  {name,-10} found {c.Found,5}  imported {c.Imported,5}  failed {c.Failed,3}\n");
        }

        sb.Append(inv, $"Containers: {ContainerMembers} members, {NestedMaps} embedded maps\n");
        sb.Append("Sprite memory (trimmed, chosen layout):\n");
        foreach ((string name, FormatStats s) in SpriteMemory)
        {
            sb.Append(inv, $"  {name,-9} {s.Sheets,4} sheets {s.Cells,6} cells  trimmed {s.TrimmedPixels / 1e6,7:0.0} Mpx  stored {s.StoredBytes / 1e6,7:0.0} MB  pages 2048²: {s.Pages2048} ({s.Occupancy2048 / 10.0:0.0} %)  4096²: {s.Pages4096} ({s.Occupancy4096 / 10.0:0.0} %)\n");
        }

        sb.Append(inv, $"  layout (a) RGBA8+R8 {LayoutRgba8MaskBytes / 1e6:0.0} MB, (b) RGB5A1+R8 {LayoutRgb5A1MaskBytes / 1e6:0.0} MB, chosen {SpriteMemory.Values.Sum(s => s.StoredBytes) / 1e6:0.0} MB\n");
        sb.Append(inv, $"  worst-case residency ({ResidencyWorstCiv}): {ResidencyWorstCivBytes / 1e6:0.0} MB\n");
        sb.Append(inv, $"Package:   {PackagePath} ({PackageBytes / 1e6:0.0} MB, {PackageEntries} entries)\n");
        sb.Append(inv, $"  sha256 {PackageSha256}\n  gameplay {GameplayHash}\n");
        sb.Append(inv, $"Time:      convert {ConvertTime.TotalSeconds:0.0} s, total {TotalTime.TotalSeconds:0.0} s\n");
        AppendIssues(sb, "Errors", Errors, maxIssues);
        AppendIssues(sb, "Warnings", Warnings, maxIssues);
        sb.Append(Succeeded ? "Result: OK\n" : "Result: FAILED\n");
        return sb.ToString();
    }

    private static void WriteIssues(System.Text.Json.Utf8JsonWriter w, string name, List<ImportIssue> issues)
    {
        w.WriteStartArray(name);
        foreach (ImportIssue issue in issues)
        {
            w.WriteStartObject();
            w.WriteString("source", issue.Source);
            w.WriteString("message", issue.Message);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void AppendIssues(StringBuilder sb, string title, List<ImportIssue> issues, int max)
    {
        sb.Append(CultureInfo.InvariantCulture, $"{title}: {issues.Count}\n");
        foreach (ImportIssue issue in issues.Take(max))
        {
            sb.Append("  ").Append(issue.Source).Append(": ").Append(issue.Message).Append('\n');
        }

        if (issues.Count > max)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  ... {issues.Count - max} more in the JSON report\n");
        }
    }
}
