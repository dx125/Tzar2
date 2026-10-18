using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Tzar2.AI.Legacy.Syntax;

/// <summary>Operators of the trigger expression language (Appendix C.6).</summary>
public enum AiExpressionOperator : byte
{
    /// <summary><c>||</c> (short-circuit, 0/1).</summary>
    Or = 1,

    /// <summary><c>&amp;&amp;</c> (short-circuit, 0/1).</summary>
    And,

    /// <summary><c>==</c>.</summary>
    Equal,

    /// <summary><c>!=</c>.</summary>
    NotEqual,

    /// <summary><c>&lt;</c>.</summary>
    Less,

    /// <summary><c>&lt;=</c>.</summary>
    LessOrEqual,

    /// <summary><c>&gt;</c>.</summary>
    Greater,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterOrEqual,

    /// <summary><c>+</c>.</summary>
    Add,

    /// <summary><c>-</c>.</summary>
    Subtract,

    /// <summary><c>*</c>.</summary>
    Multiply,

    /// <summary><c>/</c> (truncating; by zero gives 0, UNVERIFIED UNK-41).</summary>
    Divide,

    /// <summary><c>%</c> (by zero gives 0).</summary>
    Remainder,

    /// <summary>Unary <c>!</c>.</summary>
    Not,

    /// <summary>Unary <c>-</c>.</summary>
    Negate,
}

/// <summary>Bounds of <c>extern</c> expressions shared by the compiler and the evaluator.</summary>
public static class AiExpressionLimits
{
    /// <summary>Deepest expression tree the compiler accepts and the evaluator evaluates (operator chains count).</summary>
    public const int MaxDepth = 200;
}

/// <summary>A node of an <c>extern</c> expression (integers only).</summary>
public abstract record AiExpression;

/// <summary>An integer literal.</summary>
/// <param name="Value">The value.</param>
public sealed record AiNumberExpression(int Value) : AiExpression;

/// <summary><c>$name</c>.</summary>
/// <param name="Name">The name without <c>$</c>, as written.</param>
public sealed record AiVariableExpression(string Name) : AiExpression;

/// <summary><c>@name(args)</c> (or <c>name(args)</c>, as the manual also writes <c>rnd(...)</c>).</summary>
/// <param name="Name">The function name without <c>@</c>, as written.</param>
/// <param name="Arguments">The arguments.</param>
public sealed record AiCallExpression(string Name, ImmutableArray<AiExpression> Arguments) : AiExpression;

/// <summary>A unary operation.</summary>
/// <param name="Operator"><see cref="AiExpressionOperator.Not"/> or <see cref="AiExpressionOperator.Negate"/>.</param>
/// <param name="Operand">The operand.</param>
public sealed record AiUnaryExpression(AiExpressionOperator Operator, AiExpression Operand) : AiExpression;

/// <summary>A binary operation.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Left">Left operand.</param>
/// <param name="Right">Right operand.</param>
public sealed record AiBinaryExpression(AiExpressionOperator Operator, AiExpression Left, AiExpression Right) : AiExpression;

/// <summary>
/// Parser of the integer expression language of the legacy triggers (Appendix C.6), as used inside <c>extern "…"</c>
/// conditions of AI scripts. Recursion is bounded by <see cref="AiParseLimits.MaxNestingDepth"/>; malformed input
/// gives an error message, never an exception. Assignments are not allowed in a condition.
/// </summary>
public static class AiExpressionParser
{
    /// <summary>Parses <paramref name="text"/>.</summary>
    /// <param name="text">The expression.</param>
    /// <param name="maxDepth">Deepest nesting allowed.</param>
    /// <param name="expression">The expression, or null on error.</param>
    /// <param name="error">The error message with its 0-based character offset, or null.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string text, int maxDepth, out AiExpression? expression, out (int Offset, string Message)? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text, maxDepth);
        expression = parser.ParseAll();
        error = parser.Error;
        if (error is not null)
        {
            expression = null;
        }

        return expression is not null;
    }

    private sealed class Parser(string text, int maxDepth)
    {
        private int _position;
        private int _depth;

        public (int Offset, string Message)? Error { get; private set; }

        public AiExpression? ParseAll()
        {
            AiExpression? e = ParseOr();
            SkipSpace();
            if (Error is null && _position < text.Length)
            {
                Fail($"unexpected '{text[_position]}'");
            }

            return Error is null ? e : null;
        }

        private AiExpression? ParseOr() => ParseLeft(ParseAnd, static (p) => p.Match("||") ? AiExpressionOperator.Or : null);

        private AiExpression? ParseAnd() => ParseLeft(ParseEquality, static (p) => p.Match("&&") ? AiExpressionOperator.And : null);

        private AiExpression? ParseEquality() => ParseLeft(ParseRelational, static (p) =>
            p.Match("==") ? AiExpressionOperator.Equal : p.Match("!=") ? AiExpressionOperator.NotEqual : null);

        private AiExpression? ParseRelational() => ParseLeft(ParseAdditive, static (p) =>
            p.Match("<=") ? AiExpressionOperator.LessOrEqual
            : p.Match(">=") ? AiExpressionOperator.GreaterOrEqual
            : p.MatchSingle('<', '=') ? AiExpressionOperator.Less
            : p.MatchSingle('>', '=') ? AiExpressionOperator.Greater
            : null);

        private AiExpression? ParseAdditive() => ParseLeft(ParseTerm, static (p) =>
            p.MatchSingle('+', '\0') ? AiExpressionOperator.Add : p.MatchSingle('-', '\0') ? AiExpressionOperator.Subtract : null);

        private AiExpression? ParseTerm() => ParseLeft(ParseUnary, static (p) =>
            p.MatchSingle('*', '\0') ? AiExpressionOperator.Multiply
            : p.MatchSingle('/', '\0') ? AiExpressionOperator.Divide
            : p.MatchSingle('%', '\0') ? AiExpressionOperator.Remainder
            : null);

        private AiExpression? ParseLeft(Func<AiExpression?> operand, Func<Parser, AiExpressionOperator?> op)
        {
            AiExpression? left = operand();
            while (Error is null)
            {
                SkipSpace();
                AiExpressionOperator? o = op(this);
                if (o is null)
                {
                    break;
                }

                AiExpression? right = operand();
                if (right is null || left is null)
                {
                    return null;
                }

                left = new AiBinaryExpression(o.Value, left, right);
            }

            return Error is null ? left : null;
        }

        private AiExpression? ParseUnary()
        {
            if (!Enter())
            {
                return null;
            }

            try
            {
                SkipSpace();
                if (MatchSingle('!', '='))
                {
                    AiExpression? operand = ParseUnary();
                    return operand is null ? null : new AiUnaryExpression(AiExpressionOperator.Not, operand);
                }

                if (MatchSingle('-', '\0'))
                {
                    AiExpression? operand = ParseUnary();
                    return operand is null ? null : new AiUnaryExpression(AiExpressionOperator.Negate, operand);
                }

                return ParsePrimary();
            }
            finally
            {
                _depth--;
            }
        }

        private AiExpression? ParsePrimary()
        {
            SkipSpace();
            if (_position >= text.Length)
            {
                Fail("expected a value");
                return null;
            }

            char c = text[_position];
            if (c == '(')
            {
                _position++;
                AiExpression? inner = ParseOr();
                SkipSpace();
                if (Error is null && !MatchSingle(')', '\0'))
                {
                    Fail("expected ')'");
                }

                return Error is null ? inner : null;
            }

            if (char.IsAsciiDigit(c))
            {
                long value = 0;
                int start = _position;
                while (_position < text.Length && char.IsAsciiDigit(text[_position]))
                {
                    value = (value * 10) + (text[_position] - '0');
                    if (value > int.MaxValue)
                    {
                        Fail("number too large", start);
                        return null;
                    }

                    _position++;
                }

                return new AiNumberExpression((int)value);
            }

            if (c == '$')
            {
                _position++;
                string? name = Identifier();
                return name is null ? null : new AiVariableExpression(name);
            }

            bool at = c == '@';
            if (at)
            {
                _position++;
            }

            string? function = Identifier();
            if (function is null)
            {
                return null;
            }

            SkipSpace();
            if (!MatchSingle('(', '\0'))
            {
                Fail(at ? "expected '(' after the function name" : $"unknown name '{function}' (variables start with '$')");
                return null;
            }

            var arguments = new List<AiExpression>();
            SkipSpace();
            if (!MatchSingle(')', '\0'))
            {
                while (true)
                {
                    AiExpression? argument = ParseOr();
                    if (argument is null)
                    {
                        return null;
                    }

                    arguments.Add(argument);
                    SkipSpace();
                    if (MatchSingle(')', '\0'))
                    {
                        break;
                    }

                    if (!MatchSingle(',', '\0'))
                    {
                        Fail("expected ',' or ')'");
                        return null;
                    }

                    if (arguments.Count >= 8)
                    {
                        Fail("too many arguments");
                        return null;
                    }
                }
            }

            return new AiCallExpression(function, [.. arguments]);
        }

        private string? Identifier()
        {
            int start = _position;
            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
            {
                _position++;
            }

            if (_position == start || char.IsAsciiDigit(text[start]))
            {
                Fail("expected a name");
                return null;
            }

            return text[start.._position];
        }

        private bool Enter()
        {
            if (++_depth > maxDepth)
            {
                _depth--;
                Fail("expression nested too deeply");
                return false;
            }

            return true;
        }

        private bool Match(string op)
        {
            if (string.CompareOrdinal(text, _position, op, 0, op.Length) == 0)
            {
                _position += op.Length;
                return true;
            }

            return false;
        }

        // Matches c unless it is followed by notFollowedBy (so '<' does not eat "<=", '!' does not eat "!=").
        private bool MatchSingle(char c, char notFollowedBy)
        {
            if (_position < text.Length && text[_position] == c
                && (notFollowedBy == '\0' || _position + 1 >= text.Length || text[_position + 1] != notFollowedBy))
            {
                _position++;
                return true;
            }

            return false;
        }

        private void SkipSpace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        private void Fail(string message, int? at = null) => Error ??= (at ?? _position, message);
    }
}
