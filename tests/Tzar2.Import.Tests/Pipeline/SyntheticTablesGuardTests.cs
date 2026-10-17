using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Tzar2.Formats.Text;
using Tzar2.Formats.Tsv;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Import.Tests.Pipeline;

/// <summary>
/// REQ-LEGAL-03 guard (PR #14 review B3): the synthetic tables CI compiles <c>classic</c> from must not reproduce
/// original data. With <c>TZAR2_ORIGIN</c> set, every numeric cell of every synthetic row differs from the original row
/// of the same class (empty cells excepted), and so do the population values.
/// </summary>
public class SyntheticTablesGuardTests
{
    [OriginFact]
    public void Synthetic_rows_differ_from_the_original_in_every_numeric_column()
    {
        string origin = OriginPaths.RequireOrigin();
        MemorySource synthetic = SyntheticLegacyContent.CreateTables();
        var checkedCells = 0;
        foreach (string path in synthetic.Paths.Where(p => p.EndsWith(" units.txt.json", StringComparison.Ordinal) || p.EndsWith(" buildings.txt.json", StringComparison.Ordinal)))
        {
            string file = Path.GetFileName(path)[..^".json".Length];
            string? originalPath = Directory.EnumerateFiles(Path.Combine(origin, "DATA")).FirstOrDefault(f => string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(originalPath);
            TsvTable original = TsvTable.Parse(File.ReadAllBytes(originalPath!), CodePage.Windows1252);
            bool units = file.EndsWith(" units.txt", StringComparison.Ordinal);
            int[] columns = units ? [2, 3, 4, 5, 6, 7, 8, 9, 10] : [2, 3, 4, 5, 6, 7];
            using JsonDocument doc = JsonDocument.Parse(synthetic.ReadAllBytes(path));
            foreach (JsonElement row in doc.RootElement.GetProperty("rows").EnumerateArray())
            {
                string[] cells = [.. row.EnumerateArray().Select(c => c.GetString() ?? string.Empty)];
                if (cells.Length < 2)
                {
                    continue;
                }

                TsvRow? match = original.Rows.FirstOrDefault(r => !r.IsBlank && r[1].Trim() == cells[1]);
                if (match is null)
                {
                    continue;
                }

                foreach (int c in columns)
                {
                    string mine = Normalize(c < cells.Length ? cells[c] : string.Empty);
                    string theirs = Normalize(match[c]);
                    if (mine.Length == 0 || mine == "*" || theirs.Length == 0 || theirs == "*")
                    {
                        continue;
                    }

                    Assert.True(mine != theirs, $"{file} {cells[1]} column {c}: synthetic '{mine}' equals the original");
                    checkedCells++;
                }
            }
        }

        Assert.True(checkedCells > 50, $"only {checkedCells} cells compared");
    }

    private static string Normalize(string cell) => cell.Trim().Trim('"').Replace(" ", string.Empty, StringComparison.Ordinal);
}
