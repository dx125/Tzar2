using System;
using System.Collections.Generic;
using Tzar2.Formats.Text;

namespace Tzar2.Formats.Tsv;

/// <summary>One line of a TSV table.</summary>
/// <param name="Line">1-based source line.</param>
/// <param name="Cells">The cells as written (ragged: lengths vary), with one pair of surrounding quotes removed.</param>
public sealed record TsvRow(int Line, IReadOnlyList<string> Cells)
{
    /// <summary>Whether every cell is empty (an empty line or a line of tabs): a group separator.</summary>
    public bool IsBlank
    {
        get
        {
            foreach (string cell in Cells)
            {
                if (cell.Length != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>The cell at <paramref name="column"/>, or an empty string beyond the row's end (pads ragged rows).</summary>
    public string this[int column] => column >= 0 && column < Cells.Count ? Cells[column] : string.Empty;
}

/// <summary>
/// Reader for the tab-separated unit and building tables (docs/formats/ini-tsv.md §3): first line is the header, rows are
/// ragged, blank lines separate groups, cells containing commas may be quoted. Map columns by position, not by name.
/// Parsing never fails.
/// </summary>
public sealed class TsvTable
{
    private TsvTable(TsvRow? header, List<TsvRow> rows)
    {
        Header = header;
        Rows = rows;
    }

    /// <summary>The first line, or null for an empty file.</summary>
    public TsvRow? Header { get; }

    /// <summary>All lines after the header in order, including blank separator rows.</summary>
    public IReadOnlyList<TsvRow> Rows { get; }

    /// <summary>Parses raw bytes in the edition's code page.</summary>
    public static TsvTable Parse(ReadOnlySpan<byte> data, CodePage codePage) => Parse(LegacyText.Decode(data, codePage));

    /// <summary>Parses decoded text.</summary>
    public static TsvTable Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> lines = TextLines.Split(text);
        TsvRow? header = null;
        var rows = new List<TsvRow>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            string[] cells = lines[i].Split('\t');
            for (int c = 0; c < cells.Length; c++)
            {
                string cell = cells[c];
                cells[c] = cell.Length >= 2 && cell[0] == '"' && cell[^1] == '"' ? cell[1..^1] : cell;
            }

            var row = new TsvRow(i + 1, cells);
            if (header is null)
            {
                header = row;
            }
            else
            {
                rows.Add(row);
            }
        }

        return new TsvTable(header, rows);
    }
}
