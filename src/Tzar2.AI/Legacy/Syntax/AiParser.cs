using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Tzar2.AI.Legacy.Syntax;

/// <summary>
/// Parser of the legacy AI DSL (Appendix D.3, ADR 0050) into <see cref="AiScriptSyntax"/>. Keywords are
/// case-insensitive (the corpus writes <c>CRITICAL</c>, <c>THIS</c> and <c>this</c>). The parser never throws on
/// malformed input: it reports a diagnostic with the position and resynchronises at the next <c>end</c>, <c>rule</c>,
/// <c>{</c> or <c>}</c>. Work is linear in the input; block nesting is bounded by
/// <see cref="AiParseLimits.MaxNestingDepth"/> (deeper blocks are reported and skipped without recursion).
/// </summary>
public static class AiParser
{
    /// <summary>Parses one script file (includes are not resolved here, see <c>AiScriptLoader</c>).</summary>
    /// <param name="file">The normalised script name used in locations.</param>
    /// <param name="text">The script text.</param>
    /// <param name="limits">Limits (null = <see cref="AiParseLimits.Default"/>).</param>
    public static AiScriptSyntax Parse(string file, string text, AiParseLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);
        limits ??= AiParseLimits.Default;
        limits.Validate();
        var diagnostics = new AiDiagnosticBag(limits.MaxDiagnostics);
        if (text.Length > limits.MaxSourceLength)
        {
            diagnostics.Error(AiDiagnosticCode.SourceTooLarge, new SourceLocation(file, 1, 1), $"The script has {text.Length} characters; at most {limits.MaxSourceLength} are allowed.");
            return new AiScriptSyntax(file, [], [.. diagnostics.Items]);
        }

        List<AiToken> tokens = AiLexer.Tokenize(file, text, limits, diagnostics);
        var state = new Parser(file, text, tokens, limits, diagnostics);
        ImmutableArray<AiItem> items = state.ParseFile();
        return new AiScriptSyntax(file, items, [.. diagnostics.Items]);
    }

    private sealed class Parser(string file, string text, List<AiToken> tokens, AiParseLimits limits, AiDiagnosticBag diagnostics)
    {
        private int _index;

        private AiToken Current => _index < tokens.Count ? tokens[_index] : EndToken;

        private AiToken Next => _index + 1 < tokens.Count ? tokens[_index + 1] : EndToken;

        private AiToken EndToken => tokens.Count > 0
            ? new AiToken(AiTokenKind.End, text.Length, 0, tokens[^1].Line, tokens[^1].Column + tokens[^1].Length, 0)
            : new AiToken(AiTokenKind.End, 0, 0, 1, 1, 0);

        public ImmutableArray<AiItem> ParseFile() => ParseItems(depth: 0, open: null);

        private ImmutableArray<AiItem> ParseItems(int depth, AiToken? open)
        {
            var items = ImmutableArray.CreateBuilder<AiItem>();
            while (true)
            {
                AiToken t = Current;
                switch (t.Kind)
                {
                    case AiTokenKind.End:
                        if (open is { } o)
                        {
                            diagnostics.Error(AiDiagnosticCode.MissingCloseBrace, Location(o), "This '{' has no matching '}'.");
                        }

                        return items.ToImmutable();

                    case AiTokenKind.CloseBrace:
                        _index++;
                        if (open is not null)
                        {
                            return items.ToImmutable();
                        }

                        diagnostics.Error(AiDiagnosticCode.UnmatchedCloseBrace, Location(t), "This '}' has no matching '{'.");
                        continue;

                    case AiTokenKind.OpenBrace:
                        _index++;
                        if (depth + 1 > limits.MaxNestingDepth)
                        {
                            diagnostics.Error(AiDiagnosticCode.NestingTooDeep, Location(t), $"Blocks are nested deeper than {limits.MaxNestingDepth} levels; this block is skipped.");
                            SkipBlock();
                            continue;
                        }

                        ImmutableArray<AiItem> inner = ParseItems(depth + 1, t);
                        items.Add(new AiBlockItem(inner, Location(t)));
                        continue;

                    case AiTokenKind.Identifier when Is(t, "include"):
                        _index++;
                        if (Current.Kind == AiTokenKind.String)
                        {
                            items.Add(new AiIncludeItem(TextOf(Current), Location(t)));
                            _index++;
                        }
                        else
                        {
                            diagnostics.Error(AiDiagnosticCode.MissingOperand, Location(Current), "'include' needs a file name in quotes.");
                        }

                        continue;

                    case AiTokenKind.Identifier when Is(t, "critical"):
                        _index++;
                        if (Current.Kind == AiTokenKind.Identifier && Is(Current, "rule"))
                        {
                            items.Add(ParseRule(critical: true, Location(t)));
                        }
                        else
                        {
                            diagnostics.Error(AiDiagnosticCode.UnexpectedToken, Location(Current), $"'critical' must be followed by 'rule', not {Describe(Current)}.");
                        }

                        continue;

                    case AiTokenKind.Identifier when Is(t, "rule"):
                        items.Add(ParseRule(critical: false, Location(t)));
                        continue;

                    default:
                        diagnostics.Error(AiDiagnosticCode.UnexpectedToken, Location(t), $"Expected 'rule', 'critical rule', 'include', '{{' or '}}', not {Describe(t)}.");
                        SkipToItemStart();
                        continue;
                }
            }
        }

        private AiRuleItem ParseRule(bool critical, SourceLocation location)
        {
            AiToken ruleToken = Current;
            _index++; // 'rule'
            string? name = null;
            if (Current.Kind == AiTokenKind.Identifier && !IsKeyword(Current))
            {
                name = TextOf(Current);
                _index++;
            }

            var clauses = ImmutableArray.CreateBuilder<AiClause>();
            int parts = 0;
            while (true)
            {
                AiToken t = Current;
                if (t.Kind == AiTokenKind.Identifier)
                {
                    if (Is(t, "end"))
                    {
                        _index++;
                        return new AiRuleItem(critical, name, clauses.ToImmutable(), location);
                    }

                    if (Is(t, "when"))
                    {
                        _index++;
                        var conditions = ImmutableArray.CreateBuilder<AiCondition>();
                        ParseList(t, "condition", () =>
                        {
                            AiCondition? c = ParseCondition();
                            if (c is not null && Count(ref parts))
                            {
                                conditions.Add(c);
                            }

                            return c is not null;
                        });
                        clauses.Add(new AiWhenClause(conditions.ToImmutable(), Location(t)));
                        continue;
                    }

                    if (Is(t, "have"))
                    {
                        _index++;
                        var goals = ImmutableArray.CreateBuilder<AiGoalSyntax>();
                        ParseList(t, "goal", () =>
                        {
                            AiGoalSyntax? g = ParseGoal();
                            if (g is not null && Count(ref parts))
                            {
                                goals.Add(g);
                            }

                            return g is not null;
                        });
                        clauses.Add(new AiHaveClause(goals.ToImmutable(), Location(t)));
                        continue;
                    }

                    if (Is(t, "trigger"))
                    {
                        _index++;
                        var changes = ImmutableArray.CreateBuilder<AiFlagChange>();
                        ParseList(t, "flag", () =>
                        {
                            AiToken at = Current;
                            bool clear = false;
                            if (at.Kind == AiTokenKind.Bang)
                            {
                                clear = true;
                                _index++;
                            }

                            if (Current.Kind != AiTokenKind.Identifier || IsKeyword(Current))
                            {
                                return false;
                            }

                            if (Count(ref parts))
                            {
                                changes.Add(new AiFlagChange(clear, TextOf(Current), Location(at)));
                            }

                            _index++;
                            return true;
                        });
                        clauses.Add(new AiTriggerClause(changes.ToImmutable(), Location(t)));
                        continue;
                    }

                    if (Is(t, "forget"))
                    {
                        _index++;
                        if (Current.Kind == AiTokenKind.Identifier && (!IsKeyword(Current) || Is(Current, "this")))
                        {
                            if (Count(ref parts))
                            {
                                clauses.Add(new AiForgetClause(Is(Current, "this") ? null : TextOf(Current), Location(t)));
                            }

                            _index++;
                        }
                        else
                        {
                            diagnostics.Error(AiDiagnosticCode.MissingOperand, Location(Current), "'forget' needs a rule name or THIS.");
                        }

                        continue;
                    }

                    if (Is(t, "say"))
                    {
                        _index++;
                        if (Current.Kind == AiTokenKind.String)
                        {
                            if (Count(ref parts))
                            {
                                clauses.Add(new AiSayClause(TextOf(Current), Location(t)));
                            }

                            _index++;
                        }
                        else
                        {
                            diagnostics.Error(AiDiagnosticCode.MissingOperand, Location(Current), "'say' needs a message in quotes.");
                        }

                        continue;
                    }
                }

                if (t.Kind is AiTokenKind.End or AiTokenKind.OpenBrace or AiTokenKind.CloseBrace
                    || (t.Kind == AiTokenKind.Identifier && (Is(t, "rule") || Is(t, "critical") || Is(t, "include"))))
                {
                    diagnostics.Error(AiDiagnosticCode.MissingEnd, Location(ruleToken), $"This rule has no 'end' (found {Describe(t)} at line {t.Line}).");
                    return new AiRuleItem(critical, name, clauses.ToImmutable(), location);
                }

                diagnostics.Error(AiDiagnosticCode.UnexpectedToken, Location(t), $"Expected 'when', 'have', 'trigger', 'forget', 'say' or 'end', not {Describe(t)}.");
                _index++;
            }
        }

        // item (',' item)*; reports a missing item once and stops at the first token that cannot continue the list.
        private void ParseList(AiToken keyword, string what, Func<bool> item)
        {
            if (!item())
            {
                diagnostics.Error(AiDiagnosticCode.MissingOperand, Location(Current), $"'{TextOf(keyword).ToLowerInvariant()}' needs a {what}, not {Describe(Current)}.");
                ConsumeJunk();
                return;
            }

            while (Current.Kind == AiTokenKind.Comma)
            {
                _index++;
                if (!item())
                {
                    diagnostics.Error(AiDiagnosticCode.MissingOperand, Location(Current), $"Expected a {what} after ',', not {Describe(Current)}.");
                    ConsumeJunk();
                    return;
                }
            }
        }

        private AiCondition? ParseCondition()
        {
            AiToken start = Current;
            bool negated = false;
            if (start.Kind == AiTokenKind.Identifier && Is(start, "not"))
            {
                negated = true;
                _index++;
            }

            AiToken t = Current;
            if (t.Kind != AiTokenKind.Identifier || (IsKeyword(t) && !IsConditionKeyword(t)))
            {
                return null;
            }

            SourceLocation location = Location(start);
            if (Is(t, "trigger"))
            {
                _index++;
                if (Current.Kind != AiTokenKind.Identifier || IsKeyword(Current))
                {
                    diagnostics.Error(AiDiagnosticCode.MissingOperand, Location(Current), $"'trigger' in a condition needs a flag name, not {Describe(Current)}.");
                    return new AiCondition(negated, AiConditionKind.Flag, string.Empty, null, false, false, null, location);
                }

                string flag = TextOf(Current);
                _index++;
                return new AiCondition(negated, AiConditionKind.Flag, flag, null, false, false, null, location);
            }

            if (Is(t, "random") || Is(t, "time"))
            {
                bool random = Is(t, "random");
                _index++;
                if (Current.Kind != AiTokenKind.Number)
                {
                    diagnostics.Error(AiDiagnosticCode.MissingOperand, Location(Current), $"'{(random ? "random" : "time")}' needs a number, not {Describe(Current)}.");
                    return new AiCondition(negated, random ? AiConditionKind.Random : AiConditionKind.Time, string.Empty, 0, false, false, null, location);
                }

                int number = Current.Number;
                _index++;
                bool percent = false;
                if (Current.Kind == AiTokenKind.Percent)
                {
                    percent = true;
                    _index++;
                    if (!random)
                    {
                        diagnostics.Error(AiDiagnosticCode.InvalidArgument, Location(t), "'time' takes milliseconds, not a percentage.");
                    }
                }

                if (random && number > 100)
                {
                    diagnostics.Warning(AiDiagnosticCode.InvalidArgument, Location(t), $"'random {number}' is more than 100 %; it is always true.");
                }

                return new AiCondition(negated, random ? AiConditionKind.Random : AiConditionKind.Time, string.Empty, number, percent, false, null, location);
            }

            if (Is(t, "difficulty") || Is(t, "civ"))
            {
                bool difficulty = Is(t, "difficulty");
                _index++;
                if (Current.Kind != AiTokenKind.Identifier || IsKeyword(Current))
                {
                    diagnostics.Error(AiDiagnosticCode.MissingOperand, Location(Current), $"'{(difficulty ? "difficulty" : "civ")}' needs a value, not {Describe(Current)}.");
                    return new AiCondition(negated, difficulty ? AiConditionKind.Difficulty : AiConditionKind.Civilization, string.Empty, null, false, false, null, location);
                }

                string value = TextOf(Current);
                _index++;
                if (difficulty && !IsOneOf(value, "easy", "normal", "hard"))
                {
                    diagnostics.Error(AiDiagnosticCode.InvalidArgument, Location(t), $"Unknown difficulty '{value}' (easy, normal or hard); the condition is false.");
                }
                else if (!difficulty && !IsOneOf(value, "european", "asian", "arabian"))
                {
                    diagnostics.Warning(AiDiagnosticCode.InvalidArgument, Location(t), $"Unknown civilization '{value}' (european, asian or arabian); it is matched against the ruleset's civilization keys.");
                }

                return new AiCondition(negated, difficulty ? AiConditionKind.Difficulty : AiConditionKind.Civilization, value, null, false, false, null, location);
            }

            if (Is(t, "extern"))
            {
                _index++;
                if (Current.Kind != AiTokenKind.String)
                {
                    diagnostics.Error(AiDiagnosticCode.MissingOperand, Location(Current), $"'extern' needs an expression in quotes, not {Describe(Current)}.");
                    return new AiCondition(negated, AiConditionKind.Extern, string.Empty, null, false, false, null, location);
                }

                AiToken s = Current;
                string source = TextOf(s);
                _index++;
                AiExpression? expression = null;
                if (!AiExpressionParser.TryParse(source, limits.MaxNestingDepth, out expression, out (int Offset, string Message)? error) && error is { } e)
                {
                    diagnostics.Error(AiDiagnosticCode.InvalidExpression, new SourceLocation(file, s.Line, s.Column + 1 + e.Offset), $"Invalid extern expression: {e.Message}.");
                }

                return new AiCondition(negated, AiConditionKind.Extern, source, null, false, false, expression, location);
            }

            bool enemy = false;
            if (Is(t, "enemy"))
            {
                enemy = true;
                _index++;
                t = Current;
                if (t.Kind != AiTokenKind.Identifier || IsKeyword(t))
                {
                    diagnostics.Error(AiDiagnosticCode.MissingOperand, Location(t), $"'enemy' needs an object name, not {Describe(t)}.");
                    return new AiCondition(negated, AiConditionKind.Count, string.Empty, null, false, true, null, location);
                }
            }

            string name = TextOf(t);
            _index++;
            int? count = null;
            bool share = false;
            if (Current.Kind == AiTokenKind.Number)
            {
                count = Current.Number;
                _index++;
                if (Current.Kind == AiTokenKind.Percent)
                {
                    share = true;
                    _index++;
                }
            }

            return new AiCondition(negated, AiConditionKind.Count, name, count, share, enemy, null, location);
        }

        private AiGoalSyntax? ParseGoal()
        {
            AiToken start = Current;
            int? number = null;
            bool percent = false;
            if (start.Kind == AiTokenKind.Number)
            {
                number = start.Number;
                _index++;
                if (Current.Kind == AiTokenKind.Percent)
                {
                    percent = true;
                    _index++;
                }
            }

            if (Current.Kind != AiTokenKind.Identifier || IsKeyword(Current))
            {
                if (number is not null)
                {
                    diagnostics.Error(AiDiagnosticCode.MissingOperand, Location(Current), $"Expected what to have after {number}{(percent ? "%" : string.Empty)}, not {Describe(Current)}.");
                    return new AiGoalSyntax(number, percent, string.Empty, Location(start));
                }

                return null;
            }

            string name = TextOf(Current);
            _index++;
            return new AiGoalSyntax(number, percent, name, Location(start));
        }

        // Counts one more condition, goal, flag change, forget or say of the rule; false (and one message) beyond the limit.
        private bool Count(ref int parts)
        {
            parts++;
            if (parts == limits.MaxClausesPerRule + 1)
            {
                diagnostics.Error(AiDiagnosticCode.RuleTooLarge, Location(Current), $"A rule may have at most {limits.MaxClausesPerRule} conditions, goals and flags; the rest are ignored.");
            }

            return parts <= limits.MaxClausesPerRule;
        }

        // After a malformed list item: drop numbers, strings, '%', '!' and ',' so the clause loop can continue.
        private void ConsumeJunk()
        {
            while (Current.Kind is AiTokenKind.Number or AiTokenKind.String or AiTokenKind.Percent or AiTokenKind.Bang or AiTokenKind.Comma)
            {
                _index++;
            }
        }

        private void SkipToItemStart()
        {
            _index++;
            while (Current.Kind != AiTokenKind.End && Current.Kind is not (AiTokenKind.OpenBrace or AiTokenKind.CloseBrace)
                && !(Current.Kind == AiTokenKind.Identifier && (Is(Current, "rule") || Is(Current, "critical") || Is(Current, "include"))))
            {
                _index++;
            }
        }

        // Skips a block whose '{' was consumed, counting braces (no recursion).
        private void SkipBlock()
        {
            int level = 1;
            while (Current.Kind != AiTokenKind.End && level > 0)
            {
                level += Current.Kind switch { AiTokenKind.OpenBrace => 1, AiTokenKind.CloseBrace => -1, _ => 0 };
                _index++;
            }
        }

        private bool Is(AiToken token, string keyword)
            => token.Kind == AiTokenKind.Identifier && token.Length == keyword.Length
                && string.Compare(text, token.Start, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0;

        private bool IsKeyword(AiToken t)
            => Is(t, "rule") || Is(t, "critical") || Is(t, "include") || Is(t, "end") || Is(t, "when") || Is(t, "have")
                || Is(t, "trigger") || Is(t, "forget") || Is(t, "say") || Is(t, "not") || Is(t, "this");

        // Keywords that may start a condition after 'when' or ','.
        private bool IsConditionKeyword(AiToken t) => Is(t, "trigger");

        private static bool IsOneOf(string value, string a, string b, string c)
            => string.Equals(value, a, StringComparison.OrdinalIgnoreCase) || string.Equals(value, b, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, c, StringComparison.OrdinalIgnoreCase);

        private string TextOf(AiToken token) => text.Substring(token.Start, token.Length);

        private SourceLocation Location(AiToken token) => new(file, token.Line, token.Column);

        private string Describe(AiToken token) => token.Kind switch
        {
            AiTokenKind.End => "the end of the file",
            AiTokenKind.Identifier => $"'{Clip(TextOf(token))}'",
            AiTokenKind.Number => $"the number {token.Number}",
            AiTokenKind.String => "a string",
            AiTokenKind.OpenBrace => "'{'",
            AiTokenKind.CloseBrace => "'}'",
            AiTokenKind.Comma => "','",
            AiTokenKind.Percent => "'%'",
            AiTokenKind.Bang => "'!'",
            _ => "a token",
        };

        private static string Clip(string s) => s.Length <= 32 ? s : s[..32] + "…";
    }
}
