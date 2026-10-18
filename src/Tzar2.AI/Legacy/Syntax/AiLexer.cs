using System;
using System.Collections.Generic;

namespace Tzar2.AI.Legacy.Syntax;

/// <summary>Token kinds of the legacy AI DSL (Appendix D.2).</summary>
internal enum AiTokenKind : byte
{
    /// <summary>End of input.</summary>
    End = 0,

    /// <summary><c>[A-Za-z_][A-Za-z0-9_]*</c>; keywords are identifiers the parser recognises (case-insensitively).</summary>
    Identifier,

    /// <summary>Decimal integer.</summary>
    Number,

    /// <summary><c>"…"</c> on one line, no escapes.</summary>
    String,

    /// <summary><c>{</c>.</summary>
    OpenBrace,

    /// <summary><c>}</c>.</summary>
    CloseBrace,

    /// <summary><c>,</c>.</summary>
    Comma,

    /// <summary><c>%</c>.</summary>
    Percent,

    /// <summary><c>!</c>.</summary>
    Bang,
}

/// <summary>A token: kind, text range, 1-based position, and the value of a number.</summary>
internal readonly record struct AiToken(AiTokenKind Kind, int Start, int Length, int Line, int Column, int Number);

/// <summary>
/// Splits a script into tokens. Comments run from <c>;</c> to the end of the line; whitespace (spaces, tabs, CR, LF)
/// separates tokens and is otherwise insignificant. Invalid characters, unterminated strings and oversized numbers are
/// reported and skipped, so the parser always gets a well-formed token stream.
/// </summary>
internal static class AiLexer
{
    public static List<AiToken> Tokenize(string file, string text, AiParseLimits limits, AiDiagnosticBag diagnostics)
    {
        var tokens = new List<AiToken>();
        int line = 1;
        int lineStart = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n')
            {
                i++;
                line++;
                lineStart = i;
                continue;
            }

            if (c is ' ' or '\t' or '\r' or '\f' or '\v' || c == ' ' || c == '﻿')
            {
                i++;
                continue;
            }

            int column = i - lineStart + 1;
            if (c == ';')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new AiToken(AiTokenKind.Identifier, start, i - start, line, column, 0));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                int start = i;
                long value = 0;
                bool overflow = false;
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    if (!overflow)
                    {
                        value = (value * 10) + (text[i] - '0');
                        overflow = value > int.MaxValue;
                    }

                    i++;
                }

                // A number glued to letters ("10x") is one bad token, not a number and a name.
                if (i < text.Length && (char.IsAsciiLetter(text[i]) || text[i] == '_'))
                {
                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                    {
                        i++;
                    }

                    diagnostics.Error(AiDiagnosticCode.UnexpectedToken, new SourceLocation(file, line, column), $"'{Clip(text[start..i])}' is neither a number nor a name.");
                    continue;
                }

                if (overflow)
                {
                    diagnostics.Error(AiDiagnosticCode.NumberTooLarge, new SourceLocation(file, line, column), $"The number {Clip(text[start..i])} is too large (at most {int.MaxValue}).");
                    value = int.MaxValue;
                }

                tokens.Add(new AiToken(AiTokenKind.Number, start, i - start, line, column, (int)value));
                continue;
            }

            if (c == '"')
            {
                int start = i;
                i++;
                while (i < text.Length && text[i] != '"' && text[i] != '\n')
                {
                    i++;
                }

                int length = i - start - 1;
                if (i >= text.Length || text[i] != '"')
                {
                    diagnostics.Error(AiDiagnosticCode.UnterminatedString, new SourceLocation(file, line, column), "The string has no closing quote on its line.");
                }
                else
                {
                    i++;
                }

                if (length > limits.MaxStringLength)
                {
                    diagnostics.Error(AiDiagnosticCode.StringTooLong, new SourceLocation(file, line, column), $"The string is longer than {limits.MaxStringLength} characters.");
                    length = limits.MaxStringLength;
                }

                tokens.Add(new AiToken(AiTokenKind.String, start + 1, Math.Max(0, length), line, column, 0));
                continue;
            }

            AiTokenKind? kind = c switch
            {
                '{' => AiTokenKind.OpenBrace,
                '}' => AiTokenKind.CloseBrace,
                ',' => AiTokenKind.Comma,
                '%' => AiTokenKind.Percent,
                '!' => AiTokenKind.Bang,
                _ => null,
            };
            if (kind is { } k)
            {
                tokens.Add(new AiToken(k, i, 1, line, column, 0));
                i++;
                continue;
            }

            // Skip a run of invalid characters with one message.
            int bad = i;
            while (i < text.Length && IsInvalid(text[i]))
            {
                i++;
            }

            diagnostics.Error(AiDiagnosticCode.InvalidCharacter, new SourceLocation(file, line, column), $"Unexpected character {Describe(text[bad])}.");
        }

        return tokens;
    }

    private static bool IsInvalid(char c)
        => !(char.IsAsciiLetterOrDigit(c) || c is '_' or ';' or '"' or '{' or '}' or ',' or '%' or '!' or ' ' or '\t' or '\r' or '\n' or '\f' or '\v' or ' ' or '﻿');

    private static string Describe(char c)
        => c is >= ' ' and < '\u007F' ? $"'{c}'" : $"U+{(int)c:X4}";

    private static string Clip(string s) => s.Length <= 20 ? s : s[..20] + "…";
}

/// <summary>Collects diagnostics up to the limit; one summary entry replaces the overflow.</summary>
internal sealed class AiDiagnosticBag(int max)
{
    private readonly List<AiDiagnostic> _items = [];
    private bool _overflowed;

    public int ErrorCount { get; private set; }

    public IReadOnlyList<AiDiagnostic> Items => _items;

    public void Error(AiDiagnosticCode code, SourceLocation location, string message)
        => Add(new AiDiagnostic(AiDiagnosticSeverity.Error, code, location, message));

    public void Warning(AiDiagnosticCode code, SourceLocation location, string message)
        => Add(new AiDiagnostic(AiDiagnosticSeverity.Warning, code, location, message));

    public void Add(AiDiagnostic diagnostic)
    {
        if (diagnostic.Severity == AiDiagnosticSeverity.Error)
        {
            ErrorCount++;
        }

        if (_items.Count < max)
        {
            _items.Add(diagnostic);
        }
        else if (!_overflowed)
        {
            _overflowed = true;
            _items.Add(new AiDiagnostic(AiDiagnosticSeverity.Warning, AiDiagnosticCode.TooManyDiagnostics, diagnostic.Location, $"More than {max} messages; the rest are not shown."));
        }
    }
}
