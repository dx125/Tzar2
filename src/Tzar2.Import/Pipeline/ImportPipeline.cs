using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Tzar2.Content;
using Tzar2.Content.Assets;
using Tzar2.Content.Packages;
using Tzar2.Content.Vfs;
using Tzar2.Formats;
using Tzar2.Formats.Audio;
using Tzar2.Formats.Bmp;
using Tzar2.Formats.Dat;
using Tzar2.Formats.Fnt;
using Tzar2.Formats.Hpfs;
using Tzar2.Formats.Ini;
using Tzar2.Formats.Pal;
using Tzar2.Formats.Rle;
using Tzar2.Formats.Text;
using Tzar2.Formats.Tsv;
using Tzar2.Import.Editions;

namespace Tzar2.Import.Pipeline;

/// <summary>Options for <see cref="ImportPipeline"/>.</summary>
public sealed class ImportOptions
{
    /// <summary>The original install (read only, REQ-IMP-04).</summary>
    public required string OriginPath { get; init; }

    /// <summary>Folder that receives the package and the report.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Package id; also the file name (<c>&lt;id&gt;.t2pkg</c>).</summary>
    public string PackageId { get; init; } = "base.original";

    /// <summary>Forces a code page instead of the detected edition's.</summary>
    public CodePage? CodePageOverride { get; init; }

    /// <summary>Worker threads for decoding (results never depend on it).</summary>
    public int MaxParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Skips edition detection entirely (faster). This changes the package, not only the report: without an edition the
    /// manifest has version 0.0.0 and no <c>sourceEdition</c>, <c>manifests/edition.json</c> is empty, and text is read as
    /// CP1252 unless <see cref="CodePageOverride"/> is set.
    /// </summary>
    public bool SkipEditionDetection { get; init; }
}

/// <summary>Result of an import.</summary>
/// <param name="Report">The report (also written as <c>import-report.json</c>/<c>.txt</c>).</param>
/// <param name="PackagePath">The written package.</param>
public sealed record ImportResult(ImportReport Report, string PackagePath);

/// <summary>
/// Original install → base package (TDD §12.1): locate → fingerprint edition → mount the legacy folder → decode →
/// convert → write a deterministic <c>.t2pkg</c> → report. Decoding runs in parallel; every output is sorted by path,
/// so the package bytes depend only on the input files (REQ-IMP-02).
/// </summary>
public static class ImportPipeline
{
    /// <summary>Report file names written next to the package.</summary>
    public const string ReportJson = "import-report.json";

    /// <summary>Report file names written next to the package.</summary>
    public const string ReportText = "import-report.txt";

    /// <summary>Runs the full import and writes package and report into <see cref="ImportOptions.OutputDirectory"/>.</summary>
    public static ImportResult Run(ImportOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var total = Stopwatch.StartNew();
        log?.Invoke($"Indexing {options.OriginPath} ...");
        var source = new DirectorySource(options.OriginPath);
        EditionMatch? match = null;
        if (!options.SkipEditionDetection)
        {
            log?.Invoke("Fingerprinting the edition ...");
            match = EditionDetector.Detect(options.OriginPath);
        }

        (PackageWriter package, ImportReport report) = Build(source, match, options, log);
        Directory.CreateDirectory(options.OutputDirectory);
        string packagePath = Path.Combine(options.OutputDirectory, options.PackageId + ".t2pkg");
        log?.Invoke($"Writing {packagePath} ...");
        package.WriteTo(packagePath);
        report.PackagePath = Path.GetFullPath(packagePath);
        report.PackageEntries = package.Count;
        report.PackageBytes = new FileInfo(packagePath).Length;
        using (FileStream stream = File.OpenRead(packagePath))
        {
            report.PackageSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        report.TotalTime = total.Elapsed;
        File.WriteAllBytes(Path.Combine(options.OutputDirectory, ReportJson), report.ToJson());
        File.WriteAllText(Path.Combine(options.OutputDirectory, ReportText), report.ToText(), new UTF8Encoding(false));
        return new ImportResult(report, packagePath);
    }

    /// <summary>Decodes and converts everything into an in-memory package (no files written).</summary>
    public static (PackageWriter Package, ImportReport Report) Build(DirectorySource source, EditionMatch? match, ImportOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        var report = new ImportReport { OriginPath = source.Root };
        CodePage codePage = options.CodePageOverride ?? match?.Edition?.CodePage ?? CodePage.Windows1252;
        report.CodePage = codePage;
        ApplyEdition(report, match);

        var items = new List<(string Path, SourceKind Kind)>();
        foreach (string path in source.Paths)
        {
            SourceKind kind = SourceClassifier.Classify(path);
            if (kind == SourceKind.Ignored)
            {
                continue;
            }

            report.Category(kind).Found++;
            if (kind == SourceKind.Deferred)
            {
                report.Warnings.Add(new ImportIssue(path, "not converted in M1 (movies: RE-11/M8, demos: DEC-12)"));
                continue;
            }

            items.Add((path, kind));
        }

        log?.Invoke($"Converting {items.Count} files ...");
        var convert = Stopwatch.StartNew();
        var results = new ItemResult[items.Count];
        var context = new Context(source, codePage);
        Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism) },
            i => results[i] = Process(items[i].Path, items[i].Kind, context));
        report.ConvertTime = convert.Elapsed;

        var package = new PackageWriter();
        foreach (ItemResult result in results)
        {
            CategoryCount count = report.Category(result.Kind);
            foreach (string warning in result.Warnings)
            {
                report.Warnings.Add(new ImportIssue(result.Source, warning));
            }

            if (result.Error is not null)
            {
                count.Failed++;
                report.Errors.Add(new ImportIssue(result.Source, result.Error));
                continue;
            }

            string? clash = result.Outputs.Select(o => o.Path).FirstOrDefault(package.Contains);
            if (clash is not null)
            {
                count.Failed++;
                report.Errors.Add(new ImportIssue(result.Source, $"output '{clash}' is already produced by another file"));
                result.Error = "duplicate output";
                continue;
            }

            count.Imported++;
            foreach ((string path, byte[] data) in result.Outputs)
            {
                package.Add(path, data);
            }
        }

        WriteManifests(package, results, report, match, codePage);
        string gameplayHash = GameplayHash(package);
        report.GameplayHash = gameplayHash;
        var manifest = new PackageManifest
        {
            Id = options.PackageId,
            Name = "Imported original content",
            Version = match?.Edition is { } e ? VersionOf(e.GameVersion) : "0.0.0",
            Kind = PackageKind.Base,
            Provides = ProvidedCategories(package),
            GameplayHash = gameplayHash,
            SourceEdition = match?.Edition?.Id,
        };
        package.AddText(PackageWriter.ManifestPath, manifest.ToYaml());
        return (package, report);
    }

    private static void ApplyEdition(ImportReport report, EditionMatch? match)
    {
        if (match is null)
        {
            report.Warnings.Add(new ImportIssue(string.Empty, "edition detection skipped"));
            return;
        }

        report.EditionSizeMatchPerMille = match.SizeMatchPerMille;
        if (match.Edition is null)
        {
            report.Warnings.Add(new ImportIssue(string.Empty, "unknown edition; using code page 1252"));
            return;
        }

        report.EditionId = match.Edition.Id;
        report.EditionName = match.Edition.DisplayName;
        report.EditionExact = match.IsExact;
        if (match.Verification is { } v)
        {
            report.ModifiedFiles = v.Modified.Select(m => m.Path).ToList();
            report.MissingFiles = v.Missing;
            report.ExtraFiles = v.Extra.Count;
        }

        if (match.Edition.Packed)
        {
            report.Errors.Add(new ImportIssue(string.Empty, "packed editions (.wdt) are not supported by the M1 importer"));
        }

        if (!match.IsExact)
        {
            report.Warnings.Add(new ImportIssue(string.Empty, $"install differs from {match.Edition.Id}: {report.ModifiedFiles.Count} modified, {report.MissingFiles.Count} missing"));
        }
    }

    private static string VersionOf(string gameVersion)
    {
        string digits = new(gameVersion.Where(c => char.IsAsciiDigit(c) || c == '.').ToArray());
        return digits.Count(c => c == '.') == 1 ? digits + ".0" : digits;
    }

    private static List<string> ProvidedCategories(PackageWriter package)
    {
        var provides = new SortedSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, byte[]> entry in package.Entries)
        {
            string top = entry.Key[..Math.Max(0, entry.Key.IndexOf('/', StringComparison.Ordinal))];
            if (top.Length > 0 && top != "manifests")
            {
                provides.Add(top);
            }
        }

        return [.. provides];
    }

    /// <summary>SHA-256 over the rules-relevant data (<c>tables/</c>): path, NUL, length, bytes, in path order.</summary>
    private static string GameplayHash(PackageWriter package)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach ((string path, byte[] data) in package.Entries)
        {
            if (!path.StartsWith("tables/", StringComparison.Ordinal))
            {
                continue;
            }

            sha.AppendData(Encoding.UTF8.GetBytes(path));
            sha.AppendData([0]);
            BinaryPrimitives.WriteInt64LittleEndian(length, data.Length);
            sha.AppendData(length);
            sha.AppendData(data);
        }

        return "sha256:" + Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class Context(DirectorySource source, CodePage codePage)
    {
        public DirectorySource Source { get; } = source;

        public CodePage CodePage { get; } = codePage;
    }

    private sealed class ItemResult(string source, SourceKind kind)
    {
        public string Source { get; } = source;

        public SourceKind Kind { get; } = kind;

        public List<(string Path, byte[] Data)> Outputs { get; } = [];

        public List<string> Warnings { get; } = [];

        public string? Error { get; set; }

        public object? Info { get; set; }
    }

    private sealed record SpriteInfo(string Asset, SpriteSheet Sheet, long FullCellPixels, long OpaquePixels, string? Dat, int DatBoxes, int DatBoxesMatching);

    private sealed record ImageInfo(string Asset, ImageAsset Image, ColourKeyPolicy Keys);

    private sealed record DatInfo(DatFile Dat);

    private sealed record FontInfo(string Asset, string Strip, string Face);

    private sealed record SoundInfo(string Asset, WavInfo Wav);

    private sealed record MusicInfo(string Asset, MidiInfo Midi);

    private sealed record IniInfo(string Asset, IniDocument Document);

    private sealed record TsvInfo(string Asset, TsvTable Table);

    private sealed record TextInfo(string Asset);

    private sealed record ContainerInfo(int Members, int NestedMaps, int TruncatedBytes);

    private sealed record PaletteInfo(GamePalette Palette);

    private static ItemResult Process(string path, SourceKind kind, Context context)
    {
        var result = new ItemResult(path, kind);
        try
        {
            byte[] data = context.Source.ReadAllBytes(path);
            string? asset = SourceClassifier.PackagePath(path, kind);
            switch (kind)
            {
                case SourceKind.Sprite:
                    ProcessSprite(result, data, asset!, context);
                    break;
                case SourceKind.Dat:
                    result.Info = new DatInfo(DatFile.Parse(data));
                    break;
                case SourceKind.Bitmap:
                {
                    ColourKeyPolicy policy = ImageConverter.PolicyFor(path);
                    ImageAsset image = ImageConverter.Convert(BmpImage.Parse(data), policy);
                    result.Outputs.Add((asset!, image.ToBytes()));
                    result.Info = new ImageInfo(asset!, image, policy);
                    break;
                }

                case SourceKind.Palette:
                {
                    GamePalette palette = GamePalette.Parse(data);
                    result.Outputs.Add((asset!, PaletteJson(palette, path)));
                    result.Info = new PaletteInfo(palette);
                    break;
                }

                case SourceKind.Font:
                {
                    FntFont font = FntFont.Parse(data);
                    string strip = ContentPath.ChangeExtension(asset!, ImageAsset.Extension);
                    result.Outputs.Add((strip, TextConverters.FontStrip(font).ToBytes()));
                    result.Outputs.Add((asset!, TextConverters.FontToJson(font, path, strip, context.CodePage, result.Warnings)));
                    result.Info = new FontInfo(asset!, strip, font.Face);
                    break;
                }

                case SourceKind.Sound:
                {
                    WavInfo wav = WavInfo.Parse(data);
                    if (wav.DataTruncated)
                    {
                        result.Warnings.Add("data chunk declares more bytes than present");
                    }

                    result.Outputs.Add((asset!, data));
                    result.Info = new SoundInfo(asset!, wav);
                    break;
                }

                case SourceKind.Music:
                    result.Info = new MusicInfo(asset!, MidiInfo.Parse(data));
                    result.Outputs.Add((asset!, data));
                    break;
                case SourceKind.Ini:
                {
                    IniDocument ini = IniDocument.Parse(data, context.CodePage);
                    foreach (FormatWarning w in ini.Warnings)
                    {
                        result.Warnings.Add($"line {w.Line}: {w.Message}");
                    }

                    foreach (string duplicate in ini.DuplicateSectionNames())
                    {
                        result.Warnings.Add($"duplicate section [{duplicate}] (all occurrences kept)");
                    }

                    result.Outputs.Add((asset!, TextConverters.IniToJson(ini, path, context.CodePage)));
                    result.Info = new IniInfo(asset!, ini);
                    break;
                }

                case SourceKind.Tsv:
                {
                    TsvTable table = TsvTable.Parse(data, context.CodePage);
                    result.Outputs.Add((asset!, TextConverters.TsvToJson(table, path, context.CodePage)));
                    result.Info = new TsvInfo(asset!, table);
                    break;
                }

                case SourceKind.Text:
                    result.Outputs.Add((asset!, new UTF8Encoding(false).GetBytes(LegacyText.Decode(data, context.CodePage))));
                    result.Info = new TextInfo(asset!);
                    break;
                case SourceKind.Container:
                    result.Info = ValidateContainer(data, context.CodePage, 0);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled kind {kind}.");
            }
        }
        catch (LegacyFormatException ex)
        {
            result.Error = ex.Message;
            result.Outputs.Clear();
        }
        catch (InvalidDataException ex)
        {
            result.Error = ex.Message;
            result.Outputs.Clear();
        }
        catch (IOException ex)
        {
            result.Error = "read error: " + ex.Message;
            result.Outputs.Clear();
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or IndexOutOfRangeException or InvalidOperationException)
        {
            // Never let one file abort the import (REQ-IMP-03); this is a converter bug or an unforeseen input, so say so.
            result.Error = $"internal error ({ex.GetType().Name}): {ex.Message}";
            result.Outputs.Clear();
        }

        return result;
    }

    private static void ProcessSprite(ItemResult result, byte[] data, string asset, Context context)
    {
        RleSheet rle = RleSheet.Parse(data);
        bool unit = result.Source.StartsWith("images/unit/", StringComparison.Ordinal);
        SpriteSheet sheet = SpriteConverter.Convert(rle, unit, result.Warnings);
        result.Outputs.Add((asset, sheet.ToBytes()));
        string datPath = ContentPath.ChangeExtension(result.Source, ".dat");
        string? dat = null;
        int boxes = 0, matching = 0;
        if (context.Source.Contains(datPath))
        {
            dat = datPath;
            try
            {
                DatFile parsed = DatFile.Parse(context.Source.ReadAllBytes(datPath));
                if (parsed.FrameBoxes is { } list)
                {
                    boxes = list.Count;
                    if (list.Count == rle.CellCount)
                    {
                        for (int frame = 0; frame < rle.FramesPerDirection; frame++)
                        {
                            for (int dir = 0; dir < rle.Directions; dir++)
                            {
                                DatRect box = list[(frame * rle.Directions) + dir];
                                CellBounds? b = rle.GetCellBounds(rle.CellIndex(dir, frame));
                                if (b is { } bb && bb.X0 == box.X0 && bb.Y0 == box.Y0 && bb.X1 == box.X1 && bb.Y1 == box.Y1)
                                {
                                    matching++;
                                }
                            }
                        }
                    }
                }
            }
            catch (LegacyFormatException)
            {
                // Counted as a failure of the DAT item itself.
            }
        }

        long full = (long)rle.Width * rle.CellHeight * rle.CellCount;
        result.Info = new SpriteInfo(asset, sheet, full, rle.OpaquePixelCount, dat, boxes, matching);
    }

    private static ContainerInfo ValidateContainer(byte[] data, CodePage codePage, int depth)
    {
        HpfsArchive archive = HpfsArchive.Open(data, codePage);
        int members = 0, nested = 0;
        foreach (HpfsEntry entry in archive.ReadAll())
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            members++;
            if (entry.IsNestedArchive && depth < 4)
            {
                ContainerInfo inner = ValidateContainer(entry.Data, codePage, depth + 1);
                nested += 1 + inner.NestedMaps;
                members += inner.Members;
            }
        }

        return new ContainerInfo(members, nested, archive.TruncatedBytes);
    }

    private static byte[] PaletteJson(GamePalette palette, string source) => JsonOutput.Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("source", source);
        w.WriteStartArray("entries");
        foreach (Rgb24 c in palette.Entries)
        {
            w.WriteStringValue($"{c.R:x2}{c.G:x2}{c.B:x2}");
        }

        w.WriteEndArray();
        w.WriteNumber("playerRampBase", GamePalette.PlayerRampBase);
        w.WriteNumber("rampLength", GamePalette.RampLength);
        w.WriteNumber("players", GamePalette.PlayerCount);
        w.WriteEndObject();
    });

    private static void WriteManifests(PackageWriter package, ItemResult[] results, ImportReport report, EditionMatch? match, CodePage codePage)
    {
        IEnumerable<T> Infos<T>() => results.Where(r => r.Error is null).Select(r => r.Info).OfType<T>();

        package.Add("manifests/sprites.json", JsonOutput.Write(w =>
        {
            w.WriteStartArray();
            foreach ((ItemResult r, SpriteInfo s) in results.Where(r => r.Error is null && r.Info is SpriteInfo).Select(r => (r, (SpriteInfo)r.Info!)))
            {
                SpriteSheet sheet = s.Sheet;
                w.WriteStartObject();
                w.WriteString("asset", s.Asset);
                w.WriteString("source", r.Source);
                w.WriteString("format", PixelFormats.Name(sheet.Format));
                w.WriteNumber("kind", sheet.SourceKind);
                w.WriteNumber("directions", sheet.Directions);
                w.WriteNumber("frames", sheet.FramesPerDirection);
                w.WriteNumber("cellWidth", sheet.CellWidth);
                w.WriteNumber("cellHeight", sheet.CellHeight);
                w.WriteNumber("playerFirst", sheet.PlayerFirst);
                w.WriteNumber("shadowColour", sheet.ShadowColour);
                w.WriteStartArray("shadowIndices");
                foreach (int i in sheet.ShadowIndices)
                {
                    w.WriteNumberValue(i);
                }

                w.WriteEndArray();
                w.WriteNumber("trimmedPixels", sheet.TrimmedPixelCount);
                w.WriteNumber("emptyCells", sheet.Cells.Count(c => c.IsEmpty));
                w.WriteString("dat", s.Dat);
                w.WriteNumber("datBoxes", s.DatBoxes);
                w.WriteNumber("datBoxesMatching", s.DatBoxesMatching);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));

        package.Add("manifests/images.json", JsonOutput.Write(w =>
        {
            w.WriteStartArray();
            foreach ((ItemResult r, ImageInfo i) in results.Where(r => r.Error is null && r.Info is ImageInfo).Select(r => (r, (ImageInfo)r.Info!)))
            {
                w.WriteStartObject();
                w.WriteString("asset", i.Asset);
                w.WriteString("source", r.Source);
                w.WriteString("format", PixelFormats.Name(i.Image.Format));
                w.WriteNumber("width", i.Image.Width);
                w.WriteNumber("height", i.Image.Height);
                w.WriteString("keys", i.Keys.ToString().ToLowerInvariant());
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));

        package.Add("manifests/footprints.json", JsonOutput.Write(w =>
        {
            w.WriteStartArray();
            foreach ((ItemResult r, DatInfo d) in results.Where(r => r.Error is null && r.Info is DatInfo).Select(r => (r, (DatInfo)r.Info!)))
            {
                DatFile dat = d.Dat;
                w.WriteStartObject();
                w.WriteString("source", r.Source);
                JsonOutput.IntArray(w, "footprint", dat.Footprint.X0, dat.Footprint.Y0, dat.Footprint.X1, dat.Footprint.Y1);
                w.WriteNumber("gridWidth", dat.GridWidth);
                w.WriteNumber("gridHeight", dat.GridHeight);
                JsonOutput.IntArray(w, "grid", dat.Grid.Select(b => (int)b).ToArray());
                w.WriteNumber("frameBoxes", dat.FrameBoxes?.Count ?? -1);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));

        package.Add("manifests/sounds.json", JsonOutput.Write(w =>
        {
            w.WriteStartArray();
            foreach (SoundInfo s in Infos<SoundInfo>())
            {
                w.WriteStartObject();
                w.WriteString("asset", s.Asset);
                w.WriteNumber("channels", s.Wav.Channels);
                w.WriteNumber("sampleRate", s.Wav.SampleRate);
                w.WriteNumber("bitsPerSample", s.Wav.BitsPerSample);
                w.WriteNumber("sampleFrames", s.Wav.SampleFrames);
                w.WriteNumber("durationMs", s.Wav.DurationMilliseconds);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));

        package.Add("manifests/music.json", JsonOutput.Write(w =>
        {
            w.WriteStartArray();
            foreach (MusicInfo m in Infos<MusicInfo>())
            {
                w.WriteStartObject();
                w.WriteString("asset", m.Asset);
                w.WriteNumber("smfType", m.Midi.FormatType);
                w.WriteNumber("tracks", m.Midi.TrackChunks);
                w.WriteNumber("division", m.Midi.Division);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));

        package.Add("manifests/fonts.json", JsonOutput.Write(w =>
        {
            w.WriteStartArray();
            foreach (FontInfo f in Infos<FontInfo>())
            {
                w.WriteStartObject();
                w.WriteString("asset", f.Asset);
                w.WriteString("strip", f.Strip);
                w.WriteString("face", f.Face);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));

        package.Add("manifests/tables.json", JsonOutput.Write(w =>
        {
            w.WriteStartArray();
            foreach (ItemResult r in results.Where(r => r.Error is null))
            {
                (string? asset, string? type) = r.Info switch
                {
                    IniInfo i => (i.Asset, "ini"),
                    TsvInfo t => (t.Asset, "tsv"),
                    TextInfo x => (x.Asset, "text"),
                    _ => (null, null),
                };
                if (asset is null)
                {
                    continue;
                }

                w.WriteStartObject();
                w.WriteString("asset", asset);
                w.WriteString("source", r.Source);
                w.WriteString("type", type);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));

        WriteSpriteIndex(package, results, report);
        WriteTerrain(package, results, report);

        package.Add("manifests/edition.json", JsonOutput.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", match?.Edition?.Id);
            w.WriteString("name", match?.Edition?.DisplayName);
            w.WriteString("gameVersion", match?.Edition?.GameVersion);
            w.WriteString("language", match?.Edition?.Language);
            w.WriteNumber("codePage", (int)codePage);
            w.WriteBoolean("exact", match?.IsExact ?? false);
            w.WriteEndObject();
        }));

        foreach (ContainerInfo c in Infos<ContainerInfo>())
        {
            report.ContainerMembers += c.Members;
            report.NestedMaps += c.NestedMaps;
        }

        MeasureSpriteMemory(results, report);
    }

    /// <summary>Resolves every IMAGES.INI entry (data and terrain sets) to its asset (docs/formats/ini-tsv.md §4).</summary>
    private static void WriteSpriteIndex(PackageWriter package, ItemResult[] results, ImportReport report)
    {
        var assets = new HashSet<string>(results.Where(r => r.Error is null).SelectMany(r => r.Outputs.Select(o => o.Path)), StringComparer.Ordinal);
        var inis = new List<(string Source, IniDocument Doc, string[] Bases)>();
        foreach (ItemResult r in results)
        {
            if (r.Error is not null || r.Info is not IniInfo ini)
            {
                continue;
            }

            if (r.Source == "data/images.ini")
            {
                inis.Add((r.Source, ini.Document, [string.Empty]));
            }
            else if (r.Source.StartsWith("images/terrain/", StringComparison.Ordinal) && r.Source.EndsWith(".images.ini", StringComparison.Ordinal)
                && r.Source.Count(c => c == '/') == 2)
            {
                string set = r.Source["images/terrain/".Length..^".images.ini".Length];
                inis.Add((r.Source, ini.Document, ["terrain/" + set + "/", string.Empty]));
            }
        }

        package.Add("manifests/sprite-index.json", JsonOutput.Write(w =>
        {
            w.WriteStartArray();
            foreach ((string iniSource, IniDocument doc, string[] bases) in inis)
            {
                foreach (IniSection section in doc.Sections)
                {
                    string? image = section.GetValue("image");
                    if (section.Name is null || image is null)
                    {
                        continue;
                    }

                    string? asset = ResolveImage(image, bases, assets);
                    if (asset is null)
                    {
                        report.Warnings.Add(new ImportIssue(iniSource, $"[{section.Name}] image '{image}' does not exist"));
                    }

                    w.WriteStartObject();
                    w.WriteString("name", section.Name);
                    w.WriteString("ini", iniSource);
                    w.WriteString("image", image);
                    w.WriteString("asset", asset);
                    w.WriteStartObject("properties");
                    foreach (IGrouping<string, IniEntry> group in section.Entries.Where(e => e.Key is not null)
                        .GroupBy(e => e.Key!.ToLowerInvariant(), StringComparer.Ordinal))
                    {
                        TextConverters.WriteStringArray(w, group.Key, group.Select(e => e.Value));
                    }

                    w.WriteEndObject();
                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
        }));
    }

    private static string? ResolveImage(string logical, string[] bases, HashSet<string> assets)
    {
        string relative;
        try
        {
            relative = ContentPath.Normalize(logical);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (relative.EndsWith(".bmp", StringComparison.Ordinal))
        {
            relative = relative[..^4];
        }

        foreach (string b in bases)
        {
            foreach (string ext in new[] { SpriteSheet.Extension, ImageAsset.Extension })
            {
                string candidate = "art/" + b + relative + ext;
                if (assets.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>One entry per terrain set (<c>IMAGES/TERRAIN/&lt;SET&gt;.TERRAIN.INI</c>, docs/formats/bmp.md §3).</summary>
    private static void WriteTerrain(PackageWriter package, ItemResult[] results, ImportReport report)
    {
        var assets = new HashSet<string>(results.Where(r => r.Error is null).SelectMany(r => r.Outputs.Select(o => o.Path)), StringComparer.Ordinal);
        package.Add("manifests/terrain.json", JsonOutput.Write(w =>
        {
            w.WriteStartArray();
            foreach (ItemResult r in results)
            {
                if (r.Error is not null || r.Info is not IniInfo ini || !r.Source.EndsWith(".terrain.ini", StringComparison.Ordinal))
                {
                    continue;
                }

                IniSection? terrain = ini.Document.FindSection("terrain");
                string set = r.Source["images/terrain/".Length..^".terrain.ini".Length];
                string dir = SafeSegment(terrain?.GetValue("dir")) ?? set;
                if (terrain?.GetValue("dir") is { } rawDir && SafeSegment(rawDir) is null)
                {
                    report.Warnings.Add(new ImportIssue(r.Source, $"terrain dir '{rawDir}' is not a plain folder name; using '{set}'"));
                }

                string folder = "art/terrain/" + dir + "/";
                w.WriteStartObject();
                w.WriteString("set", r.Source["images/terrain/".Length..^".terrain.ini".Length]);
                w.WriteString("name", terrain?.GetValue("name"));
                w.WriteString("table", ini.Asset);
                w.WriteString("folder", folder);
                w.WriteStartArray("terrains");
                foreach (IniEntry e in ini.Document.FindSection("terrainname")?.Entries ?? [])
                {
                    if (e.Key is not null)
                    {
                        w.WriteStartObject();
                        w.WriteString("index", e.Key);
                        w.WriteString("name", e.Value);
                        w.WriteEndObject();
                    }
                }

                w.WriteEndArray();
                IniSection? sizes = ini.Document.FindSection("terrainsize");
                w.WriteStartArray("files");
                foreach (IniEntry e in ini.Document.FindSection("terrainfiles")?.Entries ?? [])
                {
                    if (e.Key is null)
                    {
                        continue;
                    }

                    string? file = SafeSegment(e.Value);
                    string? asset = file is null ? null : folder + ContentPath.ChangeExtension(file, ImageAsset.Extension);
                    bool exists = asset is not null && assets.Contains(asset);
                    if (!exists)
                    {
                        report.Warnings.Add(new ImportIssue(r.Source, $"terrain file {e.Key} = {e.Value} does not exist"));
                    }

                    w.WriteStartObject();
                    w.WriteString("pair", e.Key);
                    w.WriteString("asset", exists ? asset : null);
                    w.WriteString("size", sizes?.GetValue(e.Key));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                foreach ((string key, string file) in new[] { ("light", "light"), ("trees", "trees"), ("treeShadows", "trees_s") })
                {
                    string asset = folder + file + ImageAsset.Extension;
                    w.WriteString(key, assets.Contains(asset) ? asset : null);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));
    }

    /// <summary>A single normalised path segment (no separators, no <c>..</c>), or null.</summary>
    private static string? SafeSegment(string? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            string normal = ContentPath.Normalize(value);
            return normal.Contains('/', StringComparison.Ordinal) ? null : normal;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Spike S6 measurement (ADR 0013) over the converted sheets.</summary>
    private static void MeasureSpriteMemory(ItemResult[] results, ImportReport report)
    {
        var sizes = new SortedDictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        var civBytes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long shared = 0;
        foreach (ItemResult r in results)
        {
            if (r.Error is not null || r.Info is not SpriteInfo s)
            {
                continue;
            }

            string name = PixelFormats.Name(s.Sheet.Format);
            if (!report.SpriteMemory.TryGetValue(name, out FormatStats? stats))
            {
                stats = new FormatStats();
                report.SpriteMemory.Add(name, stats);
                sizes.Add(name, []);
            }

            stats.Sheets++;
            stats.Cells += s.Sheet.CellCount;
            stats.EmptyCells += s.Sheet.Cells.Count(c => c.IsEmpty);
            stats.FullCellPixels += s.FullCellPixels;
            stats.TrimmedPixels += s.Sheet.TrimmedPixelCount;
            stats.OpaquePixels += s.OpaquePixels;
            stats.StoredBytes += s.Sheet.PixelData.Length;
            sizes[name].AddRange(s.Sheet.Cells.Select(c => (c.Width, c.Height)));
            report.LayoutRgba8MaskBytes += s.Sheet.TrimmedPixelCount * 5;
            report.LayoutRgb5A1MaskBytes += s.Sheet.TrimmedPixelCount * 3;

            string? civ = r.Source.StartsWith("images/unit/", StringComparison.Ordinal)
                ? r.Source["images/unit/".Length..].Split(' ')[0] switch
                {
                    "euro" => "euro",
                    "asian" or "asia" => "asian",
                    "arab" => "arab",
                    _ => null,
                }
                : null;
            if (civ is null)
            {
                shared += s.Sheet.PixelData.Length;
            }
            else
            {
                civBytes[civ] = civBytes.GetValueOrDefault(civ) + s.Sheet.PixelData.Length;
            }
        }

        foreach ((string name, List<(int, int)> list) in sizes)
        {
            FormatStats stats = report.SpriteMemory[name];
            // The converter rejects cells above one layer; filter anyway so the measurement can never fail an import.
            AtlasPackResult p2 = AtlasPacker.Pack(list.Where(c => c.Item1 <= 2048 && c.Item2 <= 2048).ToList(), 2048);
            AtlasPackResult p4 = AtlasPacker.Pack(list.Where(c => c.Item1 <= 4096 && c.Item2 <= 4096).ToList(), 4096);
            stats.Pages2048 = p2.Layers;
            stats.Occupancy2048 = p2.OccupancyPerMille(2048);
            stats.Pages4096 = p4.Layers;
            stats.Occupancy4096 = p4.OccupancyPerMille(4096);
        }

        if (civBytes.Count > 0)
        {
            KeyValuePair<string, long> worst = civBytes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
            report.ResidencyWorstCiv = worst.Key;
            report.ResidencyWorstCivBytes = shared + worst.Value;
        }
        else
        {
            report.ResidencyWorstCivBytes = shared;
        }
    }
}
