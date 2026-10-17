using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Tzar2.Content.Assets;

/// <summary>One character of an imported bitmap font.</summary>
/// <param name="Code">Code in the font's code page.</param>
/// <param name="Unicode">Unicode scalar value.</param>
/// <param name="Glyph">Glyph index in the strip.</param>
/// <param name="Width">Advance and drawn width in pixels.</param>
public readonly record struct FontCharacter(int Code, int Unicode, int Glyph, int Width);

/// <summary>A glyph placed by <see cref="FontAsset.Layout"/>: draw the left <paramref name="Width"/> columns of glyph <paramref name="Glyph"/> at (<paramref name="X"/>, <paramref name="Y"/>).</summary>
/// <param name="Glyph">Glyph index.</param>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
/// <param name="Width">Columns to draw.</param>
public readonly record struct PlacedGlyph(int Glyph, int X, int Y, int Width);

/// <summary>
/// An imported <c>.FNT</c> bitmap font (<c>fonts/&lt;name&gt;.json</c> + glyph strip <c>.t2img</c>, docs/formats/fnt.md,
/// t2pkg.md): glyph <c>g</c> is rows <c>g × CellHeight</c> … of the strip, character <c>c</c> draws the left
/// <c>width</c> columns of its glyph and advances by <c>width</c>.
/// </summary>
public sealed class FontAsset
{
    private readonly Dictionary<int, FontCharacter> _byUnicode = [];

    private FontAsset(string face, string strip, int cellWidth, int cellHeight, int glyphCount, IReadOnlyList<FontCharacter> characters)
    {
        Face = face;
        Strip = strip;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        GlyphCount = glyphCount;
        Characters = characters;
        foreach (FontCharacter c in characters)
        {
            if (c.Width > 0)
            {
                _byUnicode.TryAdd(c.Unicode, c);
            }
        }
    }

    /// <summary>Face name from the font header.</summary>
    public string Face { get; }

    /// <summary>Package path of the glyph strip.</summary>
    public string Strip { get; }

    /// <summary>Glyph cell width (strip width).</summary>
    public int CellWidth { get; }

    /// <summary>Glyph cell height.</summary>
    public int CellHeight { get; }

    /// <summary>Glyphs in the strip.</summary>
    public int GlyphCount { get; }

    /// <summary>All characters, in code order.</summary>
    public IReadOnlyList<FontCharacter> Characters { get; }

    /// <summary>The character for a Unicode scalar, if the font has a glyph for it.</summary>
    public bool TryGetCharacter(int unicode, out FontCharacter character) => _byUnicode.TryGetValue(unicode, out character);

    /// <summary>
    /// Lays out <paramref name="text"/> left to right; <c>\n</c> starts a new line <see cref="CellHeight"/> lower.
    /// Characters without a glyph are replaced by <c>?</c> (or skipped when the font has no <c>?</c>).
    /// </summary>
    public IReadOnlyList<PlacedGlyph> Layout(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var glyphs = new List<PlacedGlyph>();
        int x = 0, y = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                x = 0;
                y += CellHeight;
                continue;
            }

            int scalar = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text[i], text[++i]) : text[i];
            if (!TryGetCharacter(scalar, out FontCharacter c) && !TryGetCharacter('?', out c))
            {
                continue;
            }

            glyphs.Add(new PlacedGlyph(c.Glyph, x, y, Math.Min(c.Width, CellWidth)));
            x += c.Width;
        }

        return glyphs;
    }

    /// <summary>Width in pixels of the widest line of <paramref name="text"/>.</summary>
    public int Measure(string text)
    {
        int max = 0;
        foreach (PlacedGlyph g in Layout(text))
        {
            max = Math.Max(max, g.X + g.Width);
        }

        return max;
    }

    /// <summary>
    /// The glyph strip as RGBA8 in <paramref name="colour"/>. Pixel values are coverage (fnt.md): value <c>v</c> becomes
    /// alpha <c>v / max</c> (1-bit fonts max 1, anti-aliased fonts max 7), whether the strip was imported as R8 or, with
    /// its irrelevant embedded palette, as Indexed8 (<c>SMALL</c>, <c>AV10</c>). Only a strip whose palette keys a used
    /// index (<c>FIXSHAD</c>: white glyph, black drop shadow, green-keyed background) is drawn with its palette.
    /// </summary>
    public static byte[] StripToRgba(ImageAsset strip, Rgba32 colour)
    {
        ArgumentNullException.ThrowIfNull(strip);
        if (strip.Format == PixelFormat.Indexed8 && UsesKeyedIndex(strip))
        {
            return strip.ToRgba();
        }

        if (strip.Format is not (PixelFormat.R8 or PixelFormat.Indexed8))
        {
            return strip.ToRgba();
        }

        int max = 1;
        foreach (byte v in strip.Pixels)
        {
            max = Math.Max(max, v);
        }

        byte[] rgba = new byte[strip.Pixels.Length * 4];
        for (int i = 0; i < strip.Pixels.Length; i++)
        {
            rgba[4 * i] = colour.R;
            rgba[(4 * i) + 1] = colour.G;
            rgba[(4 * i) + 2] = colour.B;
            rgba[(4 * i) + 3] = (byte)(((Math.Min((int)strip.Pixels[i], max) * 255) + (max / 2)) / max);
        }

        return rgba;
    }

    private static bool UsesKeyedIndex(ImageAsset strip)
    {
        foreach (byte v in strip.Pixels)
        {
            if (strip.Palette[v].A == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses <c>fonts/&lt;name&gt;.json</c>.</summary>
    /// <exception cref="InvalidDataException">The JSON is malformed.</exception>
    public static FontAsset Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            JsonElement root = doc.RootElement;
            var characters = new List<FontCharacter>();
            foreach (JsonElement c in root.GetProperty("characters").EnumerateArray())
            {
                if (c.GetArrayLength() < 4)
                {
                    throw new InvalidDataException("font json: a character needs [code, unicode, glyph, width].");
                }

                characters.Add(new FontCharacter(c[0].GetInt32(), c[1].GetInt32(), c[2].GetInt32(), c[3].GetInt32()));
            }

            return new FontAsset(
                root.TryGetProperty("face", out JsonElement face) ? face.GetString() ?? string.Empty : string.Empty,
                root.GetProperty("strip").GetString() ?? string.Empty,
                root.GetProperty("cellWidth").GetInt32(),
                root.GetProperty("cellHeight").GetInt32(),
                root.TryGetProperty("glyphCount", out JsonElement count) ? count.GetInt32() : 0,
                characters);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("font json: " + e.Message, e);
        }
    }
}
