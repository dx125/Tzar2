using System;
using System.Collections.Generic;

namespace Tzar2.Formats.Text;

/// <summary>ANSI code pages of the known editions (docs/formats/ini-tsv.md §1).</summary>
public enum CodePage
{
    /// <summary>Central European (PL, CZ editions).</summary>
    Windows1250 = 1250,

    /// <summary>Cyrillic (RU, BG editions).</summary>
    Windows1251 = 1251,

    /// <summary>Western European (EN, DE, ES, PT, IT editions; the reference build).</summary>
    Windows1252 = 1252,
}

/// <summary>
/// Single-byte code page decoding with built-in tables, so the importer needs neither
/// <c>System.Text.Encoding.CodePages</c> nor the platform's code page support (AOT-safe, identical on every OS).
/// Bytes 0x00–0x7F are ASCII. Bytes a code page leaves undefined decode to the C1 control of the same value
/// (U+0080–U+009F), as Windows' own tables do, so decoding never fails and is reversible.
/// </summary>
public static class LegacyText
{
    /// <summary>Decodes <paramref name="data"/> to a string.</summary>
    public static string Decode(ReadOnlySpan<byte> data, CodePage codePage)
    {
        ReadOnlySpan<char> high = HighHalf(codePage);
        char[] chars = new char[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            chars[i] = b < 0x80 ? (char)b : high[b - 0x80];
        }

        return new string(chars);
    }

    /// <summary>The Unicode character for one byte.</summary>
    public static char DecodeByte(byte value, CodePage codePage)
        => value < 0x80 ? (char)value : HighHalf(codePage)[value - 0x80];

    /// <summary>Encodes text back to the code page; characters without a mapping become <c>?</c>.</summary>
    public static byte[] Encode(string text, CodePage codePage)
    {
        ArgumentNullException.ThrowIfNull(text);
        ReadOnlySpan<char> high = HighHalf(codePage);
        var reverse = new Dictionary<char, byte>(128);
        for (int i = 0; i < high.Length; i++)
        {
            reverse.TryAdd(high[i], (byte)(0x80 + i));
        }

        byte[] result = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            result[i] = c < 0x80 ? (byte)c : reverse.TryGetValue(c, out byte b) ? b : (byte)'?';
        }

        return result;
    }

    /// <summary>Whether <paramref name="value"/> is a supported code page number.</summary>
    public static bool IsSupported(int value) => value is 1250 or 1251 or 1252;

    private static ReadOnlySpan<char> HighHalf(CodePage codePage) => codePage switch
    {
        CodePage.Windows1250 => CodePageTables.Windows1250,
        CodePage.Windows1251 => CodePageTables.Windows1251,
        CodePage.Windows1252 => CodePageTables.Windows1252,
        _ => throw new ArgumentOutOfRangeException(nameof(codePage), codePage, "Unsupported code page."),
    };
}
