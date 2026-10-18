using System.Collections.Generic;
using Tzar2.Formats.Text;

namespace Tzar2.Import.Maps;

/// <summary>Result of code page detection.</summary>
/// <param name="CodePage">The chosen code page.</param>
/// <param name="Ambiguous">Too little evidence to be sure.</param>
/// <param name="Language">Guessed source language tag.</param>
/// <param name="HighBytes">How many bytes ≥ 0x80 the text has (0 = pure ASCII).</param>
public sealed record CodePageGuess(CodePage CodePage, bool Ambiguous, string Language, int HighBytes);

/// <summary>
/// Heuristic code page detection for map text (REQ-EDT-07): CP1251 when the words with non-ASCII bytes are mostly
/// written in the upper half (Cyrillic words are; a Spanish or German word has a few accented letters among ASCII ones,
/// even two in a row as in "Peñón"), CP1250 when letters specific to Central European languages appear, else CP1252.
/// The language tag is a guess from the same evidence. Deterministic.
/// </summary>
public static class CodePageDetector
{
    // Bytes that are letters in CP1250 but punctuation/rare in CP1252 (ś ź Ś Ź ł Ł ą Ą ż Ż ő ű).
    private static readonly HashSet<byte> CentralEuropean = [0x9C, 0x9F, 0x8C, 0x8F, 0xB3, 0xA3, 0xB9, 0xA5, 0xBF, 0xAF, 0xF5, 0xFB, 0xE6, 0xC6, 0xEA, 0xCA, 0xF1];

    // Spanish-specific letters/marks in CP1252 (ñ Ñ á í ó ú ¿ ¡).
    private static readonly HashSet<byte> Spanish = [0xF1, 0xD1, 0xE1, 0xED, 0xF3, 0xFA, 0xBF, 0xA1];

    // German-specific letters in CP1252 (ä ö ü ß Ä Ö Ü).
    private static readonly HashSet<byte> German = [0xE4, 0xF6, 0xFC, 0xDF, 0xC4, 0xD6, 0xDC];

    /// <summary>Guesses the code page of <paramref name="samples"/>.</summary>
    public static CodePageGuess Detect(IEnumerable<byte[]> samples)
    {
        int high = 0, upperWords = 0, mixedWords = 0, central = 0, spanish = 0, german = 0;
        foreach (byte[] s in samples)
        {
            int wordLength = 0, wordHigh = 0;
            for (int i = 0; i <= s.Length; i++)
            {
                byte b = i < s.Length ? s[i] : (byte)' ';
                bool letter = b >= 0x80 || (b | 0x20) is >= (byte)'a' and <= (byte)'z';
                if (letter)
                {
                    wordLength++;
                    wordHigh += b >= 0x80 ? 1 : 0;
                }
                else
                {
                    // A word written mostly in the upper half reads as Cyrillic; one with a few such bytes as Latin.
                    if (wordHigh > 0 && wordLength >= 2)
                    {
                        _ = wordHigh * 10 >= wordLength * 6 ? upperWords++ : mixedWords++;
                    }

                    wordLength = wordHigh = 0;
                }

                if (b < 0x80)
                {
                    continue;
                }

                high++;
                central += CentralEuropean.Contains(b) ? 1 : 0;
                spanish += Spanish.Contains(b) ? 1 : 0;
                german += German.Contains(b) ? 1 : 0;
            }
        }

        if (high == 0)
        {
            return new CodePageGuess(CodePage.Windows1252, false, "en", 0);
        }

        bool fewBytes = high < 8;
        if (upperWords > mixedWords)
        {
            return new CodePageGuess(CodePage.Windows1251, fewBytes, "ru", high);
        }

        int cp1250Only = central - spanish;
        if (cp1250Only > spanish && cp1250Only > german)
        {
            return new CodePageGuess(CodePage.Windows1250, true, "pl", high);
        }

        string language = spanish >= german ? (spanish > 0 ? "es" : "en") : "de";
        return new CodePageGuess(CodePage.Windows1252, fewBytes, language, high);
    }
}
