using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Tzar2.Formats.Text;

namespace Tzar2.Import.Editions;

/// <summary>A known original edition (RE-13 baseline).</summary>
/// <param name="Id">Stable id, e.g. <c>gog-1.10-en</c>.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="GameVersion">Game version, e.g. <c>1.10</c>.</param>
/// <param name="Language">ISO language code.</param>
/// <param name="CodePage">ANSI code page of its text files (docs/formats/ini-tsv.md §1).</param>
/// <param name="Packed">Whether data lives in <c>.wdt</c> packs (not supported by the M1 importer).</param>
/// <param name="Manifest">The edition's file list (paths relative to the game root).</param>
public sealed record KnownEdition(string Id, string DisplayName, string GameVersion, string Language, CodePage CodePage,
    bool Packed, ReferenceManifest Manifest);

/// <summary>
/// The editions the importer can fingerprint, built from the manifests in <c>docs/original-build/manifests/</c>
/// (embedded; metadata only). Depot lists are combined with the language-independent depot, the GOG <c>app/</c>
/// prefix is removed and GOG Galaxy files are dropped, as for the reference manifest.
/// </summary>
public static class EditionCatalog
{
    private const string ResourcePrefix = "Tzar2.Import.Manifests.";

    private static readonly Lazy<IReadOnlyList<KnownEdition>> Lazy = new(Build);

    /// <summary>All known editions; the reference build first.</summary>
    public static IReadOnlyList<KnownEdition> Editions => Lazy.Value;

    /// <summary>The reference build (GOG v1.10 English).</summary>
    public static KnownEdition Reference => Editions[0];

    private static List<KnownEdition> Build()
    {
        string common = Read("gog-depot-common.tsv");
        return
        [
            new("gog-1.10-en", "GOG v1.10 English (reference build)", "1.10", "en", CodePage.Windows1252, false,
                ReferenceManifest.Parse(new StringReader(Read("gog-1.10-en.reference.tsv")), "gog-1.10-en.reference.tsv")),
            Depot("gog-1.10-de", "GOG v1.10 German", "1.10", "de", CodePage.Windows1252, false, "gog-depot-de.tsv", common),
            Depot("gog-1.10-es", "GOG v1.10 Spanish", "1.10", "es", CodePage.Windows1252, false, "gog-depot-es.tsv", common),
            Depot("gog-1.01-pl", "GOG v1.01 Polish (packed)", "1.01", "pl", CodePage.Windows1250, true, "gog-depot-pl.tsv", common),
            Depot("gog-1.01a-pt", "GOG v1.01A Portuguese (packed)", "1.01A", "pt", CodePage.Windows1252, true, "gog-depot-pt.tsv", common),
        ];
    }

    private static KnownEdition Depot(string id, string name, string version, string language, CodePage codePage, bool packed,
        string depot, string common)
    {
        var sb = new StringBuilder("path\tsize\tmd5\n");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string text in new[] { Read(depot), common })
        {
            using var reader = new StringReader(text);
            string? header = reader.ReadLine();
            string[] columns = (header ?? string.Empty).Split('\t');
            int p = Array.IndexOf(columns, "path"), s = Array.IndexOf(columns, "size"), m = Array.IndexOf(columns, "md5");
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                string[] f = line.Split('\t');
                if (f.Length <= Math.Max(p, Math.Max(s, m)))
                {
                    continue;
                }

                string path = f[p].StartsWith("app/", StringComparison.Ordinal) ? f[p][4..] : f[p];
                if (path.StartsWith("goggame", StringComparison.OrdinalIgnoreCase) || !seen.Add(path))
                {
                    continue;
                }

                sb.Append(path).Append('\t').Append(f[s]).Append('\t').Append(f[m]).Append('\n');
            }
        }

        return new KnownEdition(id, name, version, language, codePage, packed, ReferenceManifest.Parse(new StringReader(sb.ToString()), depot));
    }

    private static string Read(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException($"Embedded manifest '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
