using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace Tzar2.Content.Compiled.Scenario.Expressions;

/// <summary>Resolves the names an expression uses to indices of the scenario tables (ADR 0120).</summary>
public interface IExpressionScope
{
    /// <summary>Index of user variable <paramref name="name"/> (without <c>$</c>), registering it if new; −1 if not allowed.</summary>
    int UserVariable(string name);

    /// <summary>Index of the event named <paramref name="name"/>, or −1.</summary>
    int Event(string name);

    /// <summary>Index of the Set Timer action named <paramref name="name"/>, or −1.</summary>
    int Timer(string name);
}

/// <summary>
/// Parser of the legacy expression language (Appendix C.6): integer literals, <c>$variables</c>, bare event and timer
/// names, <c>@function(…)</c> (the <c>@</c> is optional, as the manual's <c>rnd(…)</c> example shows), the operators
/// <c>= || &amp;&amp; == != &lt; &lt;= &gt; &gt;= + - * / % ! -</c> and parentheses. Precedence from loosest to tightest:
/// assignment (right-associative), <c>||</c>, <c>&amp;&amp;</c>, equality, relational, additive, multiplicative, unary.
/// Names resolve case-insensitively through an <see cref="IExpressionScope"/>; an unresolved name becomes
/// <see cref="UnknownRef"/>, never an error. Deterministic and allocation-light; no I/O.
/// </summary>
public static class ExpressionParser
{
    private static readonly Dictionary<string, ReservedVariable> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        ["health"] = ReservedVariable.Health,
        ["maxhealth"] = ReservedVariable.MaxHealth,
        ["attack"] = ReservedVariable.Attack,
        ["defense"] = ReservedVariable.Defense,
        ["defence"] = ReservedVariable.Defense,
        ["level"] = ReservedVariable.Level,
        ["manna"] = ReservedVariable.Manna,
        ["mana"] = ReservedVariable.Manna,
        ["maxmanna"] = ReservedVariable.MaxManna,
        ["maxmana"] = ReservedVariable.MaxManna,
        ["kingdom"] = ReservedVariable.Kingdom,
        ["x"] = ReservedVariable.X,
        ["y"] = ReservedVariable.Y,
        ["gold"] = ReservedVariable.Gold,
        ["stone"] = ReservedVariable.Stone,
        ["food"] = ReservedVariable.Food,
        ["wood"] = ReservedVariable.Wood,
    };

    private static readonly Dictionary<string, CallKind> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gold"] = CallKind.Gold,
        ["stone"] = CallKind.Stone,
        ["food"] = CallKind.Food,
        ["wood"] = CallKind.Wood,
        ["rnd"] = CallKind.Rnd,
    };

    /// <summary>Whether <paramref name="name"/> (without <c>$</c>) is a reserved variable.</summary>
    public static bool IsReserved(string name) => Reserved.ContainsKey(name);

    /// <summary>The canonical spelling of a reserved variable, e.g. <c>$maxhealth</c>.</summary>
    public static string Spelling(ReservedVariable variable) => "$" + variable.ToString().ToLowerInvariant();

    /// <summary>Parses <paramref name="source"/>. Never throws for bad input: errors go to <see cref="ExpressionDef.Error"/>.</summary>
    public static ExpressionDef Parse(string source, IExpressionScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        if (source.AsSpan().Trim().Length == 0)
        {
            return new ExpressionDef(source, null);
        }

        try
        {
            Expr root = new Parser(source, scope).ParseAll();
            return new ExpressionDef(source, root);
        }
        catch (FormatException e)
        {
            return new ExpressionDef(source, null, e.Message);
        }
    }

    private enum Kind
    {
        End,
        Number,
        Variable,
        Identifier,
        Function,
        Op,
    }

    private readonly record struct Token(Kind Kind, string Text, int Position);

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly IExpressionScope _scope;
        private int _index;

        public Parser(string source, IExpressionScope scope)
        {
            _tokens = Tokenize(source);
            _scope = scope;
        }

        public Expr ParseAll()
        {
            Expr e = Assignment();
            if (Peek.Kind != Kind.End)
            {
                throw Error($"unexpected '{Peek.Text}'");
            }

            return e;
        }

        private Token Peek => _tokens[_index];

        private Token Next() => _tokens[_index++];

        private bool IsOp(string op) => Peek.Kind == Kind.Op && Peek.Text == op;

        private FormatException Error(string message) => new($"{message} at position {Peek.Position + 1}");

        private Expr Assignment()
        {
            Expr left = Or();
            if (!IsOp("="))
            {
                return left;
            }

            if (left is not (UserVar or ReservedVar or UnknownRef))
            {
                throw Error("only a variable can be assigned");
            }

            Next();
            return new AssignExpr(left, Assignment());
        }

        private Expr Or() => LeftAssociative(And, ("||", BinaryOp.Or));

        private Expr And() => LeftAssociative(Equality, ("&&", BinaryOp.And));

        private Expr Equality() => LeftAssociative(Relational, ("==", BinaryOp.Equal), ("!=", BinaryOp.NotEqual));

        private Expr Relational() => LeftAssociative(
            Additive,
            ("<", BinaryOp.Less),
            ("<=", BinaryOp.LessOrEqual),
            (">", BinaryOp.Greater),
            (">=", BinaryOp.GreaterOrEqual));

        private Expr Additive() => LeftAssociative(Term, ("+", BinaryOp.Add), ("-", BinaryOp.Subtract));

        private Expr Term() => LeftAssociative(Unary, ("*", BinaryOp.Multiply), ("/", BinaryOp.Divide), ("%", BinaryOp.Modulo));

        private Expr LeftAssociative(Func<Expr> operand, params (string Op, BinaryOp Kind)[] ops)
        {
            Expr left = operand();
            while (true)
            {
                BinaryOp? found = null;
                foreach ((string op, BinaryOp kind) in ops)
                {
                    if (IsOp(op))
                    {
                        found = kind;
                        break;
                    }
                }

                if (found is null)
                {
                    return left;
                }

                Next();
                left = new BinaryExpr(found.Value, left, operand());
            }
        }

        private Expr Unary()
        {
            if (IsOp("!"))
            {
                Next();
                return new UnaryExpr(UnaryOp.Not, Unary());
            }

            if (IsOp("-"))
            {
                Next();
                Expr operand = Unary();
                return operand is IntLiteral { Value: not int.MinValue } literal ? new IntLiteral(-literal.Value) : new UnaryExpr(UnaryOp.Negate, operand);
            }

            if (IsOp("+"))
            {
                Next();
                return Unary();
            }

            return Primary();
        }

        private Expr Primary()
        {
            Token t = Next();
            switch (t.Kind)
            {
                case Kind.Number:
                    if (!int.TryParse(t.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                    {
                        _index--;
                        throw Error($"number '{t.Text}' is out of range");
                    }

                    return new IntLiteral(value);
                case Kind.Variable:
                    return Variable(t.Text);
                case Kind.Identifier:
                    return Name(t.Text);
                case Kind.Function:
                    return Call(t);
                case Kind.Op when t.Text == "(":
                    Expr inner = Assignment();
                    Expect(")");
                    return inner;
                default:
                    _index--;
                    throw Error(t.Kind == Kind.End ? "unexpected end of expression" : $"unexpected '{t.Text}'");
            }
        }

        private Expr Variable(string name)
        {
            if (Reserved.TryGetValue(name, out ReservedVariable reserved))
            {
                return new ReservedVar(reserved);
            }

            int index = _scope.UserVariable(name);
            return index >= 0 ? new UserVar(index) : new UnknownRef("$" + name);
        }

        private Expr Name(string name)
        {
            int index = _scope.Event(name);
            if (index >= 0)
            {
                return new EventRef(index);
            }

            index = _scope.Timer(name);
            return index >= 0 ? new TimerRef(index) : new UnknownRef(name);
        }

        private Expr Call(Token t)
        {
            string name = t.Text.TrimStart('@');
            if (!Functions.TryGetValue(name, out CallKind kind))
            {
                _index--;
                throw Error($"unknown function '{t.Text}'");
            }

            Expect("(");
            var args = ImmutableArray.CreateBuilder<Expr>();
            if (!IsOp(")"))
            {
                args.Add(Assignment());
                while (IsOp(","))
                {
                    Next();
                    args.Add(Assignment());
                }
            }

            Expect(")");
            int expected = kind == CallKind.Rnd ? 2 : 1;
            if (args.Count != expected)
            {
                throw Error($"'{name}' takes {expected} argument(s), got {args.Count}");
            }

            return new CallExpr(kind, args.ToImmutable());
        }

        private void Expect(string op)
        {
            if (!IsOp(op))
            {
                throw Error(Peek.Kind == Kind.End ? $"missing '{op}'" : $"expected '{op}' but found '{Peek.Text}'");
            }

            Next();
        }

        private static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                int start = i;
                if (IsDigit(c))
                {
                    while (i < s.Length && IsDigit(s[i]))
                    {
                        i++;
                    }

                    tokens.Add(new Token(Kind.Number, s[start..i], start));
                }
                else if (c == '$' || c == '@' || IsIdentifierStart(c))
                {
                    i++;
                    while (i < s.Length && IsIdentifierPart(s[i]))
                    {
                        i++;
                    }

                    string text = s[start..i];
                    if (text.Length == 1 && c is '$' or '@')
                    {
                        throw new FormatException($"'{c}' without a name at position {start + 1}");
                    }

                    int j = i;
                    while (j < s.Length && char.IsWhiteSpace(s[j]))
                    {
                        j++;
                    }

                    bool call = c == '@' || (j < s.Length && s[j] == '(' && Functions.ContainsKey(text));
                    Kind kind = c == '$' ? Kind.Variable : call ? Kind.Function : Kind.Identifier;
                    tokens.Add(new Token(kind, c == '$' ? text[1..] : text, start));
                }
                else
                {
                    string two = i + 1 < s.Length ? s.Substring(i, 2) : string.Empty;
                    if (two is "||" or "&&" or "==" or "!=" or "<=" or ">=")
                    {
                        tokens.Add(new Token(Kind.Op, two, start));
                        i += 2;
                    }
                    else if (c is '+' or '-' or '*' or '/' or '%' or '<' or '>' or '!' or '=' or '(' or ')' or ',')
                    {
                        tokens.Add(new Token(Kind.Op, c.ToString(), start));
                        i++;
                    }
                    else if (c is '–' or '−')
                    {
                        // En dash / minus sign, as typed in the manual's examples.
                        tokens.Add(new Token(Kind.Op, "-", start));
                        i++;
                    }
                    else
                    {
                        throw new FormatException($"unexpected character '{c}' at position {start + 1}");
                    }
                }
            }

            tokens.Add(new Token(Kind.End, string.Empty, s.Length));
            return tokens;
        }

        private static bool IsDigit(char c) => c is >= '0' and <= '9';

        private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);

        private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
    }
}
