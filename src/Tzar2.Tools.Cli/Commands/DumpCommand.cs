using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Tzar2.Content;
using Tzar2.Content.Assets;
using Tzar2.Content.Packages;
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
using Tzar2.Import.Pipeline;
using Tzar2.Tools.Cli.Imaging;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// <c>t2 dump</c>: structure dumps of packages and legacy files (TDD §23.2), and PNG export of sprites and images into a
/// user-given folder for debugging. PNGs of original art are local files; never commit them.
/// </summary>
internal static class DumpCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? file = null, outDir = null;
        var entries = new List<string>();
        int? player = null;
        bool markShadow = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    PrintUsage(stdout);
                    return ExitCodes.Success;
                case "--out" or "-o" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--player" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int p) || p > 7)
                    {
                        stderr.WriteLine("t2 dump: --player takes 0..7.");
                        return ExitCodes.UsageError;
                    }

                    player = p;
                    break;
                case "--no-shadow-mark":
                    markShadow = false;
                    break;
                case { } option when option.StartsWith('-'):
                    stderr.WriteLine($"t2 dump: unknown or incomplete option '{option}'.");
                    return ExitCodes.UsageError;
                case { } path when file is null:
                    file = path;
                    break;
                default:
                    entries.Add(args[i]);
                    break;
            }
        }

        if (file is null)
        {
            PrintUsage(stderr);
            return ExitCodes.UsageError;
        }

        try
        {
            if (outDir is not null)
            {
                Directory.CreateDirectory(outDir);
            }

            return file.EndsWith(".t2pkg", StringComparison.OrdinalIgnoreCase)
                ? DumpPackage(file, entries, outDir, player, markShadow, stdout, stderr)
                : DumpLegacy(file, outDir, stdout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            stderr.WriteLine($"t2 dump: {ex.Message}");
            return ExitCodes.UsageError;
        }
    }

    private static int DumpPackage(string file, List<string> patterns, string? outDir, int? player, bool markShadow, TextWriter stdout, TextWriter stderr)
    {
        using PackageReader package = PackageReader.Open(file);
        if (patterns.Count == 0)
        {
            if (package.Contains(PackageWriter.ManifestPath))
            {
                stdout.Write(package.ReadText(PackageWriter.ManifestPath));
            }

            stdout.WriteLine($"{package.Entries.Count} entries, {package.Entries.Sum(e => e.Size)} bytes");
            foreach (PackageEntry entry in package.Entries)
            {
                string note = ContentPath.IsNormalized(entry.Path) ? string.Empty : "  (not a normalised content path: invisible to the VFS)";
                stdout.WriteLine($"{entry.Size,10}  {entry.Path}{note}");
            }

            return ExitCodes.Success;
        }

        IReadOnlyList<Rgba32>? ramp = player is { } p ? LoadRamp(package, p) : null;
        // Only normalised paths are exported: a foreign package could name an entry "..\\x" or "C:x" (path traversal).
        foreach (PackageEntry skipped in package.Entries.Where(e => !ContentPath.IsNormalized(e.Path)))
        {
            stderr.WriteLine($"t2 dump: skipping entry '{skipped.Path}' (not a normalised content path).");
        }

        var selected = package.Entries.Where(e => ContentPath.IsNormalized(e.Path) && patterns.Any(pattern => Matches(e.Path, pattern))).ToList();
        if (selected.Count == 0)
        {
            stderr.WriteLine("t2 dump: no entry matches.");
            return ExitCodes.Mismatch;
        }

        foreach (PackageEntry entry in selected)
        {
            byte[] data = package.ReadAllBytes(entry.Path);
            if (entry.Path.EndsWith(SpriteSheet.Extension, StringComparison.Ordinal))
            {
                SpriteSheet sheet = SpriteSheet.Parse(data);
                stdout.WriteLine($"{entry.Path}: {PixelFormats.Name(sheet.Format)}, {sheet.Directions} dirs × {sheet.FramesPerDirection} frames, " +
                    $"cell {sheet.CellWidth}×{sheet.CellHeight}, trimmed {sheet.TrimmedPixelCount} px, player {sheet.PlayerFirst}, shadow idx {sheet.ShadowIndices.Count}, shadow colour {sheet.ShadowColour}");
                if (outDir is not null)
                {
                    WriteSheetPng(sheet, ramp, markShadow, Path.Combine(outDir, FlatName(entry.Path) + ".png"));
                }
            }
            else if (entry.Path.EndsWith(ImageAsset.Extension, StringComparison.Ordinal))
            {
                ImageAsset image = ImageAsset.Parse(data);
                stdout.WriteLine($"{entry.Path}: {PixelFormats.Name(image.Format)}, {image.Width}×{image.Height}");
                if (outDir is not null)
                {
                    PngWriter.Write(Path.Combine(outDir, FlatName(entry.Path) + ".png"), image.Width, image.Height, image.ToRgba());
                }
            }
            else if (outDir is not null)
            {
                File.WriteAllBytes(Path.Combine(outDir, FlatName(entry.Path)), data);
                stdout.WriteLine($"{entry.Path}: {data.Length} bytes written");
            }
            else
            {
                stdout.WriteLine($"{entry.Path}: {data.Length} bytes");
            }
        }

        return ExitCodes.Success;
    }

    private static bool Matches(string path, string pattern)
    {
        string p = pattern.Replace('\\', '/').ToLowerInvariant();
        return p.EndsWith('*') ? path.StartsWith(p[..^1], StringComparison.Ordinal) : path == p;
    }

    /// <summary>A single file name for a (normalised) entry path; separators and drive/stream colons are escaped.</summary>
    internal static string FlatName(string path)
    {
        var sb = new System.Text.StringBuilder(path.Length);
        foreach (char c in path)
        {
            sb.Append(c is '/' or '\\' or ':' || char.IsControl(c) || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        }

        string name = sb.ToString().Trim('.', ' ');
        return name.Length == 0 ? "_" : name;
    }

    private static Rgba32[] LoadRamp(PackageReader package, int player)
    {
        using JsonDocument doc = JsonDocument.Parse(package.ReadAllBytes("palettes/game.json"));
        JsonElement entries = doc.RootElement.GetProperty("entries");
        int start = GamePalette.PlayerRampBase + (GamePalette.RampLength * player);
        var ramp = new Rgba32[GamePalette.RampLength];
        for (int i = 0; i < ramp.Length; i++)
        {
            string hex = entries[start + i].GetString() ?? "000000";
            int v = int.Parse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            ramp[i] = new Rgba32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
        }

        return ramp;
    }

    private static void WriteSheetPng(SpriteSheet sheet, IReadOnlyList<Rgba32>? ramp, bool markShadow, string path)
    {
        byte[] column = SpriteCompositor.SheetToRgba(sheet, ramp, markShadow);
        byte[] grid = PngWriter.ToGrid(column, sheet.CellWidth, sheet.CellHeight, sheet.Directions, sheet.FramesPerDirection);
        PngWriter.Write(path, sheet.CellWidth * sheet.FramesPerDirection, sheet.CellHeight * sheet.Directions, grid);
    }

    private static int DumpLegacy(string file, string? outDir, TextWriter stdout)
    {
        byte[] data = File.ReadAllBytes(file);
        string ext = Path.GetExtension(file).ToLowerInvariant();
        string baseName = Path.GetFileNameWithoutExtension(file);
        try
        {
            switch (ext)
            {
                case ".rle":
                {
                    RleSheet rle = RleSheet.Parse(data);
                    stdout.WriteLine($"RLE kind {(int)rle.Kind} ({rle.Kind}), {rle.Directions} dirs × {rle.FramesPerDirection} frames, {rle.Width}×{rle.Height} " +
                        $"(cell height {rle.CellHeight}, surplus rows {rle.ExtraRows}), palette {rle.Palette.Count}, key 0x{rle.KeyColour:X}, opaque {rle.OpaquePixelCount} px");
                    if (outDir is not null)
                    {
                        bool unit = file.Replace('\\', '/').Contains("/UNIT/", StringComparison.OrdinalIgnoreCase);
                        WriteSheetPng(SpriteConverter.Convert(rle, unit, []), null, true, Path.Combine(outDir, baseName + ".png"));
                    }

                    break;
                }

                case ".bmp":
                {
                    BmpImage bmp = BmpImage.Parse(data);
                    stdout.WriteLine($"BMP {bmp.Width}×{bmp.Height}, {bmp.BitsPerPixel} bpp, palette {bmp.Palette.Count}");
                    if (outDir is not null)
                    {
                        ImageAsset image = ImageConverter.Convert(bmp, ColourKeyPolicy.Green);
                        PngWriter.Write(Path.Combine(outDir, baseName + ".png"), image.Width, image.Height, image.ToRgba());
                    }

                    break;
                }

                case ".fnt":
                {
                    FntFont font = FntFont.Parse(data);
                    stdout.WriteLine($"FNT '{font.Face}', cell {font.CellWidth}×{font.CellHeight}, {font.GlyphCount} glyphs (header says {font.DeclaredGlyphCount})");
                    if (outDir is not null)
                    {
                        ImageAsset strip = TextConverters.FontStrip(font);
                        byte[] rgba = strip.ToRgba();
                        if (strip.Format == PixelFormat.R8)
                        {
                            int max = Math.Max(1, (int)strip.Pixels.Max());
                            for (int i = 0; i < strip.Pixels.Length; i++)
                            {
                                byte v = (byte)(strip.Pixels[i] * 255 / max);
                                rgba[4 * i] = rgba[(4 * i) + 1] = rgba[(4 * i) + 2] = 255;
                                rgba[(4 * i) + 3] = v;
                            }
                        }

                        PngWriter.Write(Path.Combine(outDir, baseName + ".png"), strip.Width, strip.Height, rgba);
                    }

                    break;
                }

                case ".dat":
                {
                    DatFile dat = DatFile.Parse(data);
                    stdout.WriteLine($"DAT footprint {dat.Footprint}, grid {dat.GridWidth}×{dat.GridHeight}, frame boxes {dat.FrameBoxes?.Count.ToString(CultureInfo.InvariantCulture) ?? "none"}");
                    for (int y = 0; y < dat.GridHeight; y++)
                    {
                        stdout.WriteLine("  " + string.Concat(dat.Grid.Skip(y * dat.GridWidth).Take(dat.GridWidth).Select(v => (char)('0' + v))));
                    }

                    break;
                }

                case ".pal":
                {
                    GamePalette pal = GamePalette.Parse(data);
                    stdout.WriteLine($"PAL {pal.Entries.Count} entries");
                    for (int i = 0; i < pal.Entries.Count; i += 16)
                    {
                        stdout.WriteLine($"  {i,3}: " + string.Join(" ", pal.Entries.Skip(i).Take(16).Select(c => $"{c.R:x2}{c.G:x2}{c.B:x2}")));
                    }

                    break;
                }

                case ".ini":
                {
                    IniDocument ini = IniDocument.Parse(data, CodePage.Windows1252);
                    stdout.WriteLine($"INI {ini.Sections.Count} sections, {ini.Sections.Sum(s => s.Entries.Count)} entries, {ini.Warnings.Count} warnings");
                    foreach (string d in ini.DuplicateSectionNames())
                    {
                        stdout.WriteLine($"  duplicate section [{d}]");
                    }

                    break;
                }

                case ".wav":
                {
                    WavInfo wav = WavInfo.Parse(data);
                    stdout.WriteLine($"WAV format {wav.FormatTag}, {wav.Channels} ch, {wav.SampleRate} Hz, {wav.BitsPerSample} bit, {wav.DurationMilliseconds} ms");
                    break;
                }

                case ".mid":
                {
                    MidiInfo mid = MidiInfo.Parse(data);
                    stdout.WriteLine($"MIDI type {mid.FormatType}, {mid.TrackChunks} tracks (header {mid.DeclaredTracks}), division {mid.Division}");
                    break;
                }

                case ".wmp" or ".wcm":
                    DumpHpfs(data, stdout, 0);
                    break;
                default:
                    stdout.WriteLine($"{data.Length} bytes; no structure dump for '{ext}'.");
                    break;
            }
        }
        catch (LegacyFormatException ex)
        {
            stdout.WriteLine($"malformed: {ex.Message}");
            return ExitCodes.Mismatch;
        }

        return ExitCodes.Success;
    }

    private static void DumpHpfs(byte[] data, TextWriter stdout, int depth)
    {
        HpfsArchive archive = HpfsArchive.Open(data);
        string indent = new(' ', depth * 2);
        IReadOnlyList<HpfsEntry> entries = archive.ReadAll();
        stdout.WriteLine($"{indent}HPFS {archive.BlockCount} blocks, {archive.ReferencedBlockCount} referenced, {archive.FreeBlockCount} free, truncated by {archive.TruncatedBytes} bytes");
        foreach (HpfsEntry entry in entries.Where(e => !e.IsDirectory))
        {
            stdout.WriteLine($"{indent}{entry.Data.Length,9}  {entry.Path}");
            if (entry.IsNestedArchive && depth < 3)
            {
                DumpHpfs(entry.Data, stdout, depth + 1);
            }
        }
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Usage: t2 dump <file.t2pkg> [<entry>|<prefix*> ...] [--out <dir>] [--player 0-7] [--no-shadow-mark]");
        output.WriteLine("       t2 dump <legacy file (.rle .bmp .fnt .dat .pal .ini .wav .mid .wmp .wcm)> [--out <dir>]");
        output.WriteLine();
        output.WriteLine("Without entries, lists a package. With --out, sprites (.t2spr/.rle) are written as PNG contact sheets");
        output.WriteLine("(one row per direction, one column per frame), images and fonts as PNG, other entries as raw files.");
        output.WriteLine("--player recolours indexed sprites with that player's ramp from palettes/game.json.");
    }
}
