using System;
using System.Collections.Generic;
using System.Text.Json;
using Tzar2.Content.Assets;
using Tzar2.Formats.Fnt;
using Tzar2.Formats.Ini;
using Tzar2.Formats.Text;
using Tzar2.Formats.Tsv;

namespace Tzar2.Import.Pipeline;

/// <summary>Converters for text-based legacy files: INI and TSV tables to JSON (as data, order and duplicates kept), fonts.</summary>
public static class TextConverters
{
    /// <summary>
    /// <c>{"source","codePage","sections":[{"name","line","entries":[[key|null,value],…]}…]}</c>; the implicit leading
    /// section has name null.
    /// </summary>
    public static byte[] IniToJson(IniDocument ini, string source, CodePage codePage)
    {
        ArgumentNullException.ThrowIfNull(ini);
        return JsonOutput.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("source", source);
            w.WriteNumber("codePage", (int)codePage);
            w.WriteStartArray("sections");
            foreach (IniSection section in ini.Sections)
            {
                w.WriteStartObject();
                if (section.Name is null)
                {
                    w.WriteNull("name");
                }
                else
                {
                    w.WriteString("name", section.Name);
                }

                w.WriteNumber("line", section.Line);
                w.WriteStartArray("entries");
                foreach (IniEntry entry in section.Entries)
                {
                    w.WriteStartArray();
                    if (entry.Key is null)
                    {
                        w.WriteNullValue();
                    }
                    else
                    {
                        w.WriteStringValue(entry.Key);
                    }

                    w.WriteStringValue(entry.Value);
                    w.WriteEndArray();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary><c>{"source","codePage","header":[…],"rows":[[…],…]}</c>; blank separator rows are empty arrays, ragged rows kept as written.</summary>
    public static byte[] TsvToJson(TsvTable table, string source, CodePage codePage)
    {
        ArgumentNullException.ThrowIfNull(table);
        return JsonOutput.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("source", source);
            w.WriteNumber("codePage", (int)codePage);
            w.WriteStartArray("header");
            if (table.Header is not null)
            {
                foreach (string cell in table.Header.Cells)
                {
                    w.WriteStringValue(cell);
                }
            }

            w.WriteEndArray();
            w.WriteStartArray("rows");
            foreach (TsvRow row in table.Rows)
            {
                w.WriteStartArray();
                if (!row.IsBlank)
                {
                    foreach (string cell in row.Cells)
                    {
                        w.WriteStringValue(cell);
                    }
                }

                w.WriteEndArray();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>The glyph strip of a font as an image: Indexed8 when the embedded bitmap has a palette (FIXSHAD), raw R8 coverage otherwise.</summary>
    public static ImageAsset FontStrip(FntFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        bool indexed = font.Strip.Palette.Count > 0;
        return new ImageAsset
        {
            Format = indexed ? PixelFormat.Indexed8 : PixelFormat.R8,
            Width = font.Strip.Width,
            Height = font.Strip.Height,
            Palette = indexed ? ImageConverter.IndexedPalette(font.Strip.Palette, greenKey: true) : [],
            Pixels = (byte[])font.Strip.Pixels.Clone(),
        };
    }

    /// <summary>
    /// Font metrics: face, cell size, glyph count, the raw metric words, and one <c>[code, unicode, glyph, width]</c>
    /// row per character with a non-zero width (Unicode from the edition's code page), so text can be laid out in UTF-16.
    /// </summary>
    public static byte[] FontToJson(FntFont font, string source, string strip, CodePage codePage, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(warnings);
        if (font.DeclaredGlyphCount != font.GlyphCount)
        {
            warnings.Add($"header declares {font.DeclaredGlyphCount} glyphs, the strip holds {font.GlyphCount}");
        }

        return JsonOutput.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("source", source);
            w.WriteString("strip", strip);
            w.WriteString("face", font.Face);
            w.WriteNumber("codePage", (int)codePage);
            w.WriteNumber("cellWidth", font.CellWidth);
            w.WriteNumber("cellHeight", font.CellHeight);
            w.WriteNumber("glyphCount", font.GlyphCount);
            w.WriteNumber("declaredGlyphCount", font.DeclaredGlyphCount);
            w.WriteStartArray("metrics");
            foreach (ushort m in font.Metrics)
            {
                w.WriteNumberValue(m);
            }

            w.WriteEndArray();
            w.WriteStartArray("characters");
            for (int code = 0; code < 256; code++)
            {
                int width = font.Widths[code];
                if (width == 0)
                {
                    continue;
                }

                if (width > font.CellWidth)
                {
                    warnings.Add($"character {code} is {width} px wide, wider than the {font.CellWidth} px cell");
                }

                w.WriteStartArray();
                w.WriteNumberValue(code);
                w.WriteNumberValue(LegacyText.DecodeByte((byte)code, codePage));
                w.WriteNumberValue(font.Charmap[code]);
                w.WriteNumberValue(width);
                w.WriteEndArray();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    internal static void WriteStringArray(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (string v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }
}
