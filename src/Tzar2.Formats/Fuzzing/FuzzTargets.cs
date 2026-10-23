using System;
using System.Collections.Immutable;
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

namespace Tzar2.Formats.Fuzzing;

/// <summary>
/// Fuzzing entry points (TDD §22.6), one per decoder, usable from a SharpFuzz harness
/// (<c>Fuzzer.OutOfProcess.Run(s => FuzzTargets.Run("rle", ReadAll(s)))</c>) and from property tests. Each target
/// parses the input and touches every accessor; a <see cref="LegacyFormatException"/> is the expected outcome for
/// bad input and is swallowed, any other exception escapes and is a bug.
/// </summary>
public static class FuzzTargets
{
    /// <summary>Names accepted by <see cref="Run"/>.</summary>
    public static readonly ImmutableArray<string> Names = ["rle", "dat", "bmp", "pal", "fnt", "hpfs", "ini", "tsv", "wav", "mid", "wmp", "wcm"];

    /// <summary>Runs the target <paramref name="name"/> on <paramref name="data"/>.</summary>
    /// <returns>True when the input parsed, false when it was rejected with <see cref="LegacyFormatException"/>.</returns>
    public static bool Run(string name, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            switch (name)
            {
                case "rle": Rle(data); break;
                case "dat": DatFile.Parse(data); break;
                case "bmp": Bmp(data); break;
                case "pal": Pal(data); break;
                case "fnt": Fnt(data); break;
                case "hpfs": Hpfs(data); break;
                case "ini": IniDocument.Parse(data, CodePage.Windows1252); break;
                case "tsv": TsvTable.Parse(data, CodePage.Windows1251); break;
                case "wav": WavInfo.Parse(data); break;
                case "mid": MidiInfo.Parse(data); break;
                case "wmp": Maps.LegacyMapReader.ReadMap(data); break;
                case "wcm": Maps.LegacyMapReader.ReadCampaign(data); break;
                default: throw new ArgumentException($"Unknown fuzz target '{name}'.", nameof(name));
            }

            return true;
        }
        catch (LegacyFormatException)
        {
            return false;
        }
    }

    private static void Rle(byte[] data)
    {
        RleSheet sheet = RleSheet.Parse(data);
        long sum = 0;
        for (int cell = 0; cell < sheet.CellCount; cell++)
        {
            _ = sheet.GetCellBounds(cell);
            for (int row = 0; row < sheet.CellHeight; row++)
            {
                foreach (RleRun run in sheet.CellRowRuns(cell, row))
                {
                    for (int i = 0; i < run.Count; i++)
                    {
                        sum += sheet.Kind switch
                        {
                            RleKind.Indexed => sheet.IndexAt(run, i),
                            RleKind.Rgb555 => sheet.Rgb555At(run, i),
                            _ => 1,
                        };
                    }
                }
            }
        }

        GC.KeepAlive(sum);
    }

    private static void Bmp(byte[] data)
    {
        BmpImage image = BmpImage.Parse(data);
        if (image.BitsPerPixel == 16)
        {
            _ = image.Rgb555At(image.Width - 1, image.Height - 1);
        }
        else if (image.BitsPerPixel == 8)
        {
            _ = image.IndexAt(image.Width - 1, image.Height - 1);
        }
    }

    private static void Pal(byte[] data)
    {
        GamePalette palette = GamePalette.Parse(data);
        if (palette.Entries.Count == 256)
        {
            for (int p = 0; p < GamePalette.PlayerCount; p++)
            {
                _ = palette.PlayerRamp(p);
            }
        }
    }

    private static void Fnt(byte[] data)
    {
        FntFont font = FntFont.Parse(data);
        for (int c = 0; c < 256; c++)
        {
            int glyph = font.Charmap[c];
            int width = Math.Min(font.Widths[c], font.CellWidth);
            for (int y = 0; y < font.CellHeight; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    _ = font.GlyphPixel(glyph, x, y);
                }
            }
        }
    }

    private static void Hpfs(byte[] data)
    {
        HpfsArchive archive = HpfsArchive.Open(data);
        foreach (HpfsEntry entry in archive.ReadAll())
        {
            if (entry.IsNestedArchive)
            {
                Hpfs(entry.Data);
            }
        }
    }
}
