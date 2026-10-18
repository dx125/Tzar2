using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Tzar2.Import.Maps;

/// <summary>Outcome of converting one corpus file.</summary>
/// <param name="Path">Path relative to the corpus root (<c>/</c>-separated).</param>
/// <param name="Kind"><c>map</c> or <c>campaign</c>.</param>
/// <param name="Succeeded">No errors.</param>
/// <param name="Errors">Error count (including embedded maps).</param>
/// <param name="Warnings">Warning count.</param>
/// <param name="Maps">Maps converted (1 for a map; the campaign's map count).</param>
/// <param name="FirstError">The first error message, or empty.</param>
public sealed record CorpusEntry(string Path, string Kind, bool Succeeded, int Errors, int Warnings, int Maps, string FirstError);

/// <summary>
/// The corpus runner of the M4 exit criterion (“≥ 90 % of the community corpus converts”, TDD §25): converts every
/// <c>.wmp</c>/<c>.wcm</c> under a folder (the original install, the FX campaigns, or a community corpus named by
/// <c>TZAR2_CORPUS</c>) and reports the pass rate. A file passes when it converts with zero errors.
/// </summary>
public static class LegacyCorpus
{
    /// <summary>Environment variable naming a community corpus folder.</summary>
    public const string EnvironmentVariable = "TZAR2_CORPUS";

    /// <summary>Whether <paramref name="path"/> looks like a legacy map or campaign by extension.</summary>
    public static bool IsLegacyFile(string path)
        => path.EndsWith(".wmp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".wcm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Converts a file by extension (<c>.wcm</c> = campaign, otherwise map).</summary>
    public static (MapConversion? Map, CampaignConversion? Campaign) ConvertFile(string path, LegacyConversionOptions? options = null)
    {
        byte[] data = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        return path.EndsWith(".wcm", StringComparison.OrdinalIgnoreCase)
            ? (null, LegacyCampaignConverter.Convert(data, name, options))
            : (LegacyMapConverter.Convert(data, name, options), null);
    }

    /// <summary>The legacy files under <paramref name="root"/>, sorted ordinally by relative path.</summary>
    public static IReadOnlyList<string> Find(string root)
        => [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(IsLegacyFile)
            .OrderBy(f => Path.GetRelativePath(root, f).Replace('\\', '/'), StringComparer.Ordinal)];

    /// <summary>Converts every legacy file under <paramref name="root"/>.</summary>
    /// <param name="root">Corpus folder.</param>
    /// <param name="options">Converter options.</param>
    /// <param name="onFile">Called after each file with its result (e.g. to write packages), or null.</param>
    public static IReadOnlyList<CorpusEntry> Run(string root, LegacyConversionOptions? options = null, Action<string, MapConversion?, CampaignConversion?>? onFile = null)
    {
        var entries = new List<CorpusEntry>();
        foreach (string file in Find(root))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            CorpusEntry entry;
            try
            {
                (MapConversion? map, CampaignConversion? campaign) = ConvertFile(file, options);
                onFile?.Invoke(file, map, campaign);
                ConversionReport report = map?.Report ?? campaign!.Report;
                bool ok = map?.Succeeded ?? campaign!.Succeeded;
                entry = new CorpusEntry(rel, report.Kind, ok, report.ErrorCount, report.WarningCount, campaign?.Maps.Count ?? 1, FirstError(report));
            }
            catch (IOException e)
            {
                entry = new CorpusEntry(rel, "unknown", false, 1, 0, 0, e.Message);
            }

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>Pass rate in per mille (0 when the corpus is empty).</summary>
    public static int PassPermille(IReadOnlyList<CorpusEntry> entries)
        => entries.Count == 0 ? 0 : (int)(entries.Count(e => e.Succeeded) * 1000L / entries.Count);

    /// <summary>A human-readable summary of a corpus run.</summary>
    public static string Summary(IReadOnlyList<CorpusEntry> entries)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        foreach (CorpusEntry e in entries)
        {
            sb.Append(e.Succeeded ? "PASS " : "FAIL ").Append(e.Path).Append(ci, $" ({e.Kind}, {e.Maps} map(s), {e.Errors} errors, {e.Warnings} warnings)");
            if (!e.Succeeded && e.FirstError.Length > 0)
            {
                sb.Append(": ").Append(e.FirstError);
            }

            sb.Append('\n');
        }

        int passed = entries.Count(e => e.Succeeded);
        int permille = PassPermille(entries);
        sb.Append(ci, $"corpus: {passed}/{entries.Count} files convert ({permille / 10}.{permille % 10} %), {entries.Sum(e => e.Maps)} maps\n");
        return sb.ToString();
    }

    private static string FirstError(ConversionReport report)
    {
        foreach (ConversionDiagnostic d in report.Diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Error)
            {
                return d.Message;
            }
        }

        foreach (ConversionReport child in report.Children)
        {
            string e = FirstError(child);
            if (e.Length > 0)
            {
                return child.Source + ": " + e;
            }
        }

        return string.Empty;
    }
}
