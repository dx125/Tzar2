using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Formats.Tests;

/// <summary>
/// RE-01 golden checks against the reference build (GOG v1.10 EN): every file of each format decodes, with the counts
/// recorded in docs/formats/*.md. Skipped without <c>TZAR2_ORIGIN</c>.
/// </summary>
public class OriginFormatsTests
{
    private static IEnumerable<string> Files(string extension)
        => Directory.EnumerateFiles(OriginPaths.RequireOrigin(), "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

    [OriginFact]
    public void All_961_rle_sheets_decode()
    {
        var kinds = new Dictionary<RleKind, int>();
        long cells = 0, opaque = 0;
        int surplus = 0;
        foreach (string file in Files(".rle"))
        {
            RleSheet sheet = RleSheet.Parse(File.ReadAllBytes(file));
            kinds[sheet.Kind] = kinds.GetValueOrDefault(sheet.Kind) + 1;
            cells += sheet.CellCount;
            opaque += sheet.OpaquePixelCount;
            surplus += sheet.ExtraRows > 0 ? 1 : 0;
        }

        Assert.Equal(263, kinds[RleKind.Indexed]);
        Assert.Equal(567, kinds[RleKind.Rgb555]);
        Assert.Equal(131, kinds[RleKind.Shadow]);
        Assert.Equal(30_339, cells);
        Assert.Equal(1, surplus);
        Assert.InRange(opaque, 40_850_000, 40_950_000);
    }

    [OriginFact]
    public void All_621_dats_parse_and_most_boxes_match_the_sheets()
    {
        int files = 0, boxes = 0, matching = 0;
        foreach (string file in Files(".dat"))
        {
            DatFile dat = DatFile.Parse(File.ReadAllBytes(file));
            files++;
            string rle = Path.ChangeExtension(file, ".RLE");
            if (dat.FrameBoxes is null || !File.Exists(rle))
            {
                continue;
            }

            RleSheet sheet = RleSheet.Parse(File.ReadAllBytes(rle));
            if (dat.FrameBoxes.Count != sheet.CellCount)
            {
                continue;
            }

            for (int frame = 0; frame < sheet.FramesPerDirection; frame++)
            {
                for (int dir = 0; dir < sheet.Directions; dir++)
                {
                    boxes++;
                    DatRect box = dat.FrameBoxes[(frame * sheet.Directions) + dir];
                    if (sheet.GetCellBounds(sheet.CellIndex(dir, frame)) is { } b && (b.X0, b.Y0, b.X1, b.Y1) == (box.X0, box.Y0, box.X1, box.Y1))
                    {
                        matching++;
                    }
                }
            }
        }

        Assert.Equal(621, files);
        Assert.Equal(29_494, boxes);
        Assert.Equal(28_948, matching);
    }

    [OriginFact]
    public void All_bitmaps_fonts_palette_and_audio_decode()
    {
        var depths = Files(".bmp").Select(f => BmpImage.Parse(File.ReadAllBytes(f)).BitsPerPixel).GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(87, depths[8]);
        Assert.Equal(537, depths[16]);
        Assert.Equal(1, depths[24]);

        var fonts = Files(".fnt").Select(f => FntFont.Parse(File.ReadAllBytes(f))).ToList();
        Assert.Equal(7, fonts.Count);
        Assert.Equal(new[] { 224, 224, 224, 224, 223, 95, 224 }, fonts.Select(f => f.GlyphCount));

        GamePalette pal = GamePalette.Parse(File.ReadAllBytes(Files(".pal").Single()));
        Assert.Equal(256, pal.Entries.Count);

        Assert.Equal(438, Files(".wav").Count(f => WavInfo.Parse(File.ReadAllBytes(f)).FormatTag == 1));
        Assert.Equal(14, Files(".mid").Count(f => MidiInfo.Parse(File.ReadAllBytes(f)).TrackChunks > 0));
    }

    [OriginFact]
    public void All_ini_files_parse_without_warnings_and_tables_read()
    {
        int sections = 0;
        var duplicates = new List<string>();
        foreach (string file in Files(".ini"))
        {
            IniDocument ini = IniDocument.Parse(File.ReadAllBytes(file), CodePage.Windows1252);
            Assert.Empty(ini.Warnings);
            sections += ini.Sections.Count(s => s.Name is not null);
            duplicates.AddRange(ini.DuplicateSectionNames());
        }

        Assert.Equal(2_619, sections);
        Assert.Equal(["stone_wall_tower", "galleon", "a_church", "item_sword"], duplicates);

        var tables = Files(".txt").Where(f => Path.GetFileName(f).Contains("UNITS", StringComparison.Ordinal) || Path.GetFileName(f).Contains("BUILDINGS", StringComparison.Ordinal)).ToList();
        Assert.Equal(8, tables.Count);
        foreach (string file in tables)
        {
            TsvTable table = TsvTable.Parse(File.ReadAllBytes(file), CodePage.Windows1252);
            Assert.Equal("CLASS", table.Header![1].Trim());
            Assert.Contains(table.Rows, r => !r.IsBlank);
        }
    }

    [OriginFact]
    public void All_maps_and_campaigns_open()
    {
        int containers = 0, nested = 0;
        foreach (string file in Files(".wmp").Concat(Files(".wcm")))
        {
            containers++;
            foreach (HpfsEntry entry in HpfsArchive.Open(File.ReadAllBytes(file)).ReadAll())
            {
                if (entry.IsNestedArchive)
                {
                    nested++;
                    Assert.NotEmpty(HpfsArchive.Open(entry.Data).ReadAll());
                }
            }
        }

        Assert.Equal(9, containers);
        Assert.Equal(26, nested);
    }

    [OriginFact]
    public void Images_ini_rows_and_cols_match_all_971_referenced_sheets()
    {
        string root = OriginPaths.RequireOrigin();
        var index = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/').ToLowerInvariant(), f => f);
        var inis = new List<(string Ini, string Base)> { ("data/images.ini", "images") };
        inis.AddRange(index.Keys.Where(k => k.StartsWith("images/terrain/", StringComparison.Ordinal) && k.EndsWith(".images.ini", StringComparison.Ordinal) && k.Count(c => c == '/') == 2)
            .Order(StringComparer.Ordinal)
            .Select(k => (k, "images/terrain/" + k.Split('/')[2].Split('.')[0])));
        int rle = 0, matching = 0;
        foreach ((string ini, string baseDir) in inis)
        {
            foreach (IniSection section in IniDocument.Parse(File.ReadAllBytes(index[ini]), CodePage.Windows1252).Sections)
            {
                string? image = section.GetValue("image");
                if (image is null)
                {
                    continue;
                }

                string stem = image.Replace('\\', '/').ToLowerInvariant();
                stem = stem.EndsWith(".bmp", StringComparison.Ordinal) ? stem[..^4] : stem;
                string? path = new[] { baseDir, "images" }.Select(b => b + "/" + stem + ".rle").FirstOrDefault(index.ContainsKey);
                if (path is null)
                {
                    continue;
                }

                rle++;
                RleSheet sheet = RleSheet.Parse(File.ReadAllBytes(index[path]));
                int rows = int.Parse(section.GetValue("rows") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
                int cols = int.Parse(section.GetValue("cols") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
                matching += rows == sheet.FramesPerDirection && cols == sheet.Directions ? 1 : 0;
            }
        }

        Assert.Equal(971, rle);
        Assert.Equal(971, matching);
    }
}
