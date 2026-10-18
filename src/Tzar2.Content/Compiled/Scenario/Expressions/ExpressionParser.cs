using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace Tzar2.Content.Compiled.Scenario.Expressions;

/// <summary>Resolves the names an expression uses to indices of the scenario tables (ADR 0120).</summary>
public interface IExpressionScope
{
    /// <summary>
    /// Index of user variable <paramref name="name"/> (without <c>$</c>; <c>#</c> names keep their <c>#</c>),
    /// registering it if new; −1 if not allowed.
    /// </summary>
    int UserVariable(string name);

    /// <summary>Index of the event named <paramref name="name"/>, or −1.</summary>
    int Event(string name);

    /// <summary>Index of the Set Timer action named <paramref name="name"/>, or −1.</summary>
    int Timer(string name);
}

/// <summary>
/// Parser of the legacy expression language (Appendix C.6 plus the further names of ADR 0120): integer literals,
/// <c>$variables</c> and <c>#variables</c>, bare event and timer names, <c>@function(…)</c> (the <c>@</c> is optional
/// for known functions, as the manual's <c>rnd(…)</c> example shows), the operators
/// <c>= || &amp;&amp; == != &lt; &lt;= &gt; &gt;= + - * / % ! -</c>, parentheses, and several statements separated by
/// <c>;</c>, a top-level <c>,</c> or a line break (a line break inside parentheses or next to an operator is
/// whitespace). Precedence from loosest to tightest: assignment (right-associative), <c>||</c>,
/// <c>&amp;&amp;</c>, equality, relational, additive, multiplicative, unary. Names resolve case-insensitively through
/// an <see cref="IExpressionScope"/>; an unresolved name (or an unknown <c>@name</c> without arguments) becomes
/// <see cref="UnknownRef"/>, never an error. Function arity is not checked. Deterministic; no I/O.
/// </summary>
public static class ExpressionParser
{
    /// <summary>Deepest nesting of operators, parentheses and calls a parse accepts (untrusted community maps).</summary>
    public const int MaxDepth = 200;

    // Canonical spellings, as the original game spells them (docs/formats/wmp.md §9); not derived from enum names.
    private static readonly Dictionary<ReservedVariable, string> ReservedSpelling = new()
    {
        [ReservedVariable.Health] = "$health",
        [ReservedVariable.MaxHealth] = "$maxhealth",
        [ReservedVariable.Attack] = "$attack",
        [ReservedVariable.Defense] = "$defence",
        [ReservedVariable.Level] = "$level",
        [ReservedVariable.Manna] = "$manna",
        [ReservedVariable.MaxManna] = "$maxmanna",
        [ReservedVariable.Kingdom] = "$kingdom",
        [ReservedVariable.X] = "$x",
        [ReservedVariable.Y] = "$y",
        [ReservedVariable.Gold] = "$gold",
        [ReservedVariable.Stone] = "$stone",
        [ReservedVariable.Food] = "$food",
        [ReservedVariable.Wood] = "$wood",
        [ReservedVariable.Difficulty] = "$difficulty",
        [ReservedVariable.UnderFog] = "$underfog",
        [ReservedVariable.Flags] = "$flags",
        [ReservedVariable.Program] = "$program",
        [ReservedVariable.InHolder] = "$inholder",
        [ReservedVariable.IsSelected] = "$isselected",
        [ReservedVariable.Cargo] = "$cargo",
        [ReservedVariable.Race] = "$race",
        [ReservedVariable.CanUseDiplomacy] = "$canusediplomacy",
        [ReservedVariable.CanDelObjects] = "$candelobjects",
    };

    private static readonly Dictionary<string, ReservedVariable> Reserved = BuildReserved();

    private static readonly Dictionary<string, CallKind> Functions = BuildFunctions();

    /// <summary>Whether <paramref name="name"/> (without <c>$</c>) is a reserved variable.</summary>
    public static bool IsReserved(string name) => Reserved.ContainsKey(name);

    /// <summary>The canonical spelling of a reserved variable, e.g. <c>$maxhealth</c> (the game's <c>$defence</c>).</summary>
    public static string Spelling(ReservedVariable variable) => ReservedSpelling[variable];

    /// <summary>The canonical spelling of a function, e.g. <c>@kingdomcanrescue</c>.</summary>
    public static string Spelling(CallKind function) => "@" + function.ToString().ToLowerInvariant();

    /// <summary>Parses <paramref name="source"/>. Never throws for bad input: errors go to <see cref="ExpressionDef.Error"/>.</summary>
    public static ExpressionDef Parse(string source, IExpressionScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        try
        {
            Expr? root = new Parser(source, scope).ParseAll();
            return new ExpressionDef(source, root);
        }
        catch (FormatException e)
        {
            return new ExpressionDef(source, null, e.Message);
        }
    }

    private static Dictionary<string, ReservedVariable> BuildReserved()
    {
        var map = new Dictionary<string, ReservedVariable>(StringComparer.OrdinalIgnoreCase);
        foreach (ReservedVariable v in Enum.GetValues<ReservedVariable>())
        {
            map[ReservedSpelling[v][1..]] = v;
        }

        map["defense"] = ReservedVariable.Defense;
        map["mana"] = ReservedVariable.Manna;
        map["maxmana"] = ReservedVariable.MaxManna;
        return map;
    }

    private static Dictionary<string, CallKind> BuildFunctions()
    {
        var map = new Dictionary<string, CallKind>(StringComparer.OrdinalIgnoreCase);
        foreach (CallKind k in Enum.GetValues<CallKind>())
        {
            map[k.ToString()] = k;
        }

        return map;
    }

    private static bool TakesText(CallKind kind) => kind is CallKind.Str or CallKind.PlayMovie;

    private enum Kind
    {
        End,
        Number,
        Variable,
        Identifier,
        Function,
        Text,
        Op,
        Break,
    }

    private readonly record struct Token(Kind Kind, string Text, int Position);

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly IExpressionScope _scope;
        private int _index;
        private int _depth;

        public Parser(string source, IExpressionScope scope)
        {
            _tokens = Tokenize(source);
            _scope = scope;
        }

        public Expr? ParseAll()
        {
            var statements = ImmutableArray.CreateBuilder<Expr>();
            while (true)
            {
                while (IsOp(";") || IsOp(","))
                {
                    Next();
                }

                if (Peek.Kind == Kind.End)
                {
                    break;
                }

                statements.Add(Assignment());
                if (Peek.Kind != Kind.End && !IsOp(";") && !IsOp(","))
                {
                    throw Error($"unexpected '{Peek.Text}'");
                }
            }

            return statements.Count switch
            {
                0 => null,
                1 => statements[0],
                _ => new SequenceExpr(statements.ToImmutable()),
            };
        }

        private Token Peek => _tokens[_index];

        private Token Next() => _tokens[_index++];

        private bool IsOp(string op) => Peek.Kind == Kind.Op && Peek.Text == op;

        private FormatException Error(string message) => new($"{message} at position {Peek.Position + 1}");

        private Expr Assignment()
        {
            Enter();
            try
            {
                return AssignmentBody();
            }
            finally
            {
                _depth--;
            }
        }

        private void Enter()
        {
            if (++_depth > MaxDepth)
            {
                throw Error($"expression nested deeper than {MaxDepth}");
            }
        }

        private Expr AssignmentBody()
        {
            Expr left = Or();
            if (!IsOp("="))
            {
                return left;
            }

            if (left is not (UserVar or ReservedVar or TimerRef or UnknownRef))
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
            Enter();
            try
            {
                return UnaryBody();
            }
            finally
            {
                _depth--;
            }
        }

        private Expr UnaryBody()
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
            // '$' names may be reserved; '#' names are always user variables and keep their '#' (ADR 0120).
            if (name[0] == '$' && Reserved.TryGetValue(name[1..], out ReservedVariable reserved))
            {
                return new ReservedVar(reserved);
            }

            int index = _scope.UserVariable(name[0] == '$' ? name[1..] : name);
            return index >= 0 ? new UserVar(index) : new UnknownRef(name);
        }

        private Expr Name(string name)
        {
            if (name[0] == '@')
            {
                return new UnknownRef(name);
            }

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

            if (Peek.Kind == Kind.Text)
            {
                return new CallExpr(kind, ImmutableArray.Create<Expr>(new TextArg(Next().Text)));
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
                if (c is ';')
                {
                    tokens.Add(new Token(Kind.Op, ";", i));
                    i++;
                    continue;
                }

                if (c is '\r' or '\n')
                {
                    tokens.Add(new Token(Kind.Break, string.Empty, i));
                    i++;
                    continue;
                }

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
                else if (c is '$' or '@' or '#' || IsIdentifierStart(c))
                {
                    i++;
                    while (i < s.Length && IsIdentifierPart(s[i]))
                    {
                        i++;
                    }

                    string text = s[start..i];
                    if (text.Length == 1 && c is '$' or '@' or '#')
                    {
                        throw new FormatException($"'{c}' without a name at position {start + 1}");
                    }

                    int j = i;
                    while (j < s.Length && s[j] is ' ' or '\t')
                    {
                        j++;
                    }

                    bool paren = j < s.Length && s[j] == '(';
                    bool known = Functions.TryGetValue(text.TrimStart('@'), out CallKind kind);
                    Kind tokenKind = c is '$' or '#' ? Kind.Variable : (paren && (c == '@' || known)) ? Kind.Function : Kind.Identifier;
                    tokens.Add(new Token(tokenKind, text, start));
                    if (tokenKind == Kind.Function && known && TakesText(kind))
                    {
                        int close = s.IndexOf(')', j);
                        if (close < 0)
                        {
                            throw new FormatException($"missing ')' at position {s.Length + 1}");
                        }

                        tokens.Add(new Token(Kind.Text, s[(j + 1)..close].Trim(), j + 1));
                        i = close + 1;
                    }
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
            return Breaks(tokens);
        }

        // A line break separates statements only at parenthesis depth 0, after a complete operand and before a token
        // that cannot continue an expression; otherwise it is whitespace (the original's maps break long conditions
        // after "&&").
        private static List<Token> Breaks(List<Token> tokens)
        {
            var result = new List<Token>(tokens.Count);
            int depth = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                Token t = tokens[i];
                if (t.Kind == Kind.Op && t.Text == "(")
                {
                    depth++;
                }
                else if (t.Kind == Kind.Op && t.Text == ")")
                {
                    depth--;
                }

                if (t.Kind != Kind.Break)
                {
                    result.Add(t);
                    continue;
                }

                int next = i + 1;
                while (tokens[next].Kind == Kind.Break)
                {
                    next++;
                }

                bool afterOperand = result.Count > 0 && (result[^1].Kind is Kind.Number or Kind.Variable or Kind.Identifier or Kind.Text
                    || result[^1] is { Kind: Kind.Op, Text: ")" });
                bool continues = tokens[next].Kind == Kind.Op && tokens[next].Text is "||" or "&&" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "+" or "*" or "/" or "%" or "=" or ")" or ",";
                if (depth <= 0 && afterOperand && !continues)
                {
                    result.Add(new Token(Kind.Op, ";", t.Position));
                }

                i = next - 1;
            }

            return result;
        }

        private static bool IsDigit(char c) => c is >= '0' and <= '9';

        private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);

        private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
    }
}
