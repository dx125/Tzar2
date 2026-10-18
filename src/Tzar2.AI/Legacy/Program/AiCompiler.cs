using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.AI.Legacy.Loading;
using Tzar2.AI.Legacy.Syntax;
using Tzar2.Core.Hashing;

namespace Tzar2.AI.Legacy.Program;

/// <summary>
/// Loads a strategy script with its includes and compiles it to an <see cref="AiProgram"/> (ADR 0050).
/// <list type="bullet">
/// <item><c>include</c> is textual: the included file's items are spliced where the include stands, inside the
/// current block (so a top-level <c>critical</c> rule of an included module gates the rest of the including block,
/// which is why the corpus wraps some includes in <c>{ }</c>).</item>
/// <item>A missing include is a warning and is skipped (Appendix D.6); a cyclic include is skipped with a warning; the
/// include depth and the program size are bounded (<see cref="AiParseLimits"/>).</item>
/// <item>Each file is parsed once and its messages are reported once, however often it is included.</item>
/// </list>
/// </summary>
public static class AiCompiler
{
    /// <summary>Compiles <paramref name="root"/> from <paramref name="source"/>.</summary>
    /// <param name="root">The strategy script name (any spelling, normalised).</param>
    /// <param name="source">Where scripts come from.</param>
    /// <param name="limits">Limits (null = defaults).</param>
    /// <returns>The program; check <see cref="AiProgram.Diagnostics"/>. A missing root gives an empty program with an error.</returns>
    public static AiProgram Compile(string root, IAiScriptSource source, AiParseLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(source);
        limits ??= AiParseLimits.Default;
        limits.Validate();
        var state = new State(source, limits);
        string name = AiScriptNames.Normalize(root);
        if (!AiScriptNames.IsSafe(name))
        {
            state.Diagnostics.Error(AiDiagnosticCode.IncludeNotFound, new SourceLocation(name, 1, 1), $"'{root}' is not a script name inside the AI folder.");
        }
        else if (state.Load(name) is { } syntax)
        {
            var block = new List<int>();
            state.Stack.Add(name);
            state.CompileItems(syntax.Items, block, includeDepth: 0);
            state.Stack.RemoveAt(state.Stack.Count - 1);
            state.Close(block);
        }
        else
        {
            state.Diagnostics.Error(AiDiagnosticCode.IncludeNotFound, new SourceLocation(name, 1, 1), $"The AI script '{name}' does not exist.");
        }

        return state.Build(name);
    }

    /// <summary>Compiles a single script text (no includes are resolvable unless <paramref name="others"/> has them).</summary>
    public static AiProgram CompileText(string name, string text, IAiScriptSource? others = null, AiParseLimits? limits = null)
    {
        var memory = new MemoryAiScriptSource().Add(name, text);
        return Compile(name, others is null ? memory : new LayeredSource(memory, others), limits);
    }

    private sealed class LayeredSource(IAiScriptSource first, IAiScriptSource second) : IAiScriptSource
    {
        public bool TryRead(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
            => first.TryRead(name, out text) || second.TryRead(name, out text);
    }

    private struct MutableRule
    {
        public int BlockEnd;
        public int FirstCondition;
        public int ConditionCount;
        public int FirstAction;
        public int ActionCount;
        public int Name;
        public bool Critical;
    }

    private sealed class State(IAiScriptSource source, AiParseLimits limits)
    {
        private readonly Dictionary<string, AiScriptSyntax?> _parsed = new(StringComparer.Ordinal);
        private readonly List<string> _files = [];
        private readonly List<MutableRule> _rules = [];
        private readonly List<SourceLocation> _ruleLocations = [];
        private readonly List<AiConditionCode> _conditions = [];
        private readonly List<AiActionCode> _actions = [];
        private readonly Interner _symbols = new();
        private readonly List<SourceLocation> _symbolLocations = [];
        private readonly Interner _flags = new();
        private readonly Interner _ruleNames = new();
        private readonly List<List<int>> _rulesByName = [];
        private readonly Interner _texts = new(caseSensitive: true);
        private readonly Interner _civilizations = new();
        private readonly List<AiExpression?> _externs = [];
        private readonly List<int> _externCosts = [];
        private int _items;
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private bool _tooLarge;
        private int _includes;

        public AiDiagnosticBag Diagnostics { get; } = new(limits.MaxDiagnostics);

        public List<string> Stack { get; } = [];

        public AiScriptSyntax? Load(string name)
        {
            if (_parsed.TryGetValue(name, out AiScriptSyntax? cached))
            {
                return cached;
            }

            AiScriptSyntax? syntax = null;
            if (source.TryRead(name, out string? text))
            {
                syntax = AiParser.Parse(name, text, limits);
                foreach (AiDiagnostic d in syntax.Diagnostics)
                {
                    Diagnostics.Add(d);
                }

                _files.Add(name);
            }

            _parsed[name] = syntax;
            return syntax;
        }

        public void CompileItems(ImmutableArray<AiItem> items, List<int> block, int includeDepth)
        {
            foreach (AiItem item in items)
            {
                if (_tooLarge)
                {
                    return;
                }

                if (++_items > limits.MaxExpandedItems)
                {
                    _tooLarge = true;
                    Diagnostics.Error(AiDiagnosticCode.ProgramTooLarge, item.Location, $"The program expands to more than {limits.MaxExpandedItems} rules, blocks and includes; the rest is ignored.");
                    return;
                }

                switch (item)
                {
                    case AiRuleItem rule:
                        int added = AddRule(rule);
                        if (added >= 0)
                        {
                            block.Add(added);
                        }

                        break;

                    case AiBlockItem inner:
                        var innerRules = new List<int>();
                        CompileItems(inner.Items, innerRules, includeDepth);
                        Close(innerRules);
                        break;

                    case AiIncludeItem include:
                        Include(include, block, includeDepth);
                        break;
                }
            }
        }

        // The rules directly in a block jump to the block's end when a critical one fails.
        public void Close(List<int> block)
        {
            int end = _rules.Count;
            foreach (int index in block)
            {
                MutableRule r = _rules[index];
                r.BlockEnd = end;
                _rules[index] = r;
            }
        }

        public AiProgram Build(string root)
        {
            var rules = ImmutableArray.CreateBuilder<AiRuleCode>(_rules.Count);
            foreach (MutableRule r in _rules)
            {
                rules.Add(new AiRuleCode(r.BlockEnd, r.FirstCondition, r.ConditionCount, r.FirstAction, r.ActionCount, r.Name, r.Critical));
            }

            var byName = ImmutableArray.CreateBuilder<ImmutableArray<int>>(_rulesByName.Count);
            foreach (List<int> list in _rulesByName)
            {
                byName.Add([.. list]);
            }

            ImmutableArray<AiRuleCode> code = rules.MoveToImmutable();
            ulong hash = Hash(code);
            return new AiProgram(
                root,
                code,
                [.. _ruleLocations],
                [.. _conditions],
                [.. _actions],
                _symbols.ToImmutable(),
                _flags.ToImmutable(),
                _ruleNames.ToImmutable(),
                byName.MoveToImmutable(),
                _texts.ToImmutable(),
                _civilizations.ToImmutable(),
                [.. _externs],
                [.. _externCosts],
                [.. _symbolLocations],
                [.. _files],
                [.. Diagnostics.Items],
                hash);
        }

        private void Include(AiIncludeItem include, List<int> block, int includeDepth)
        {
            string name = AiScriptNames.Normalize(include.Path);
            if (!AiScriptNames.IsSafe(name))
            {
                Diagnostics.Error(AiDiagnosticCode.IncludeNotFound, include.Location, $"'{include.Path}' is not a script name inside the AI folder ('..' and absolute paths are not allowed); it is skipped.");
                return;
            }

            if (Stack.Contains(name))
            {
                if (_reported.Add("cycle:" + name))
                {
                    Diagnostics.Warning(AiDiagnosticCode.IncludeCycle, include.Location, $"'{name}' includes itself ({string.Join(" → ", Stack)} → {name}); this include is skipped.");
                }

                return;
            }

            if (includeDepth + 1 > limits.MaxIncludeDepth)
            {
                Diagnostics.Error(AiDiagnosticCode.IncludeTooDeep, include.Location, $"Includes are nested deeper than {limits.MaxIncludeDepth} levels; '{name}' is skipped.");
                return;
            }

            if (++_includes > limits.MaxIncludes)
            {
                _tooLarge = true;
                Diagnostics.Error(AiDiagnosticCode.ProgramTooLarge, include.Location, $"The program expands more than {limits.MaxIncludes} includes; the rest is ignored.");
                return;
            }

            AiScriptSyntax? syntax = Load(name);
            if (syntax is null)
            {
                if (_reported.Add("missing:" + name))
                {
                    Diagnostics.Warning(AiDiagnosticCode.IncludeNotFound, include.Location, $"The included script '{name}' does not exist; the rest of the script still loads.");
                }

                return;
            }

            Stack.Add(name);
            CompileItems(syntax.Items, block, includeDepth + 1);
            Stack.RemoveAt(Stack.Count - 1);
        }

        private int AddRule(AiRuleItem rule)
        {
            if (_rules.Count >= limits.MaxRules)
            {
                _tooLarge = true;
                Diagnostics.Error(AiDiagnosticCode.ProgramTooLarge, rule.Location, $"The program has more than {limits.MaxRules} rules after expanding includes; the rest is ignored.");
                return -1;
            }

            int index = _rules.Count;
            var r = new MutableRule { Critical = rule.Critical, Name = -1, FirstCondition = _conditions.Count, FirstAction = _actions.Count };
            if (rule.Name is { } ruleName)
            {
                int nameId = InternRuleName(ruleName);
                if (_rulesByName[nameId].Count < limits.MaxRulesPerName)
                {
                    r.Name = nameId;
                    _rulesByName[nameId].Add(index);
                }
                else if (_reported.Add("name:" + ruleName.ToLowerInvariant()))
                {
                    Diagnostics.Error(AiDiagnosticCode.ProgramTooLarge, rule.Location, $"More than {limits.MaxRulesPerName} rules are named '{ruleName}'; the further ones keep no name ('forget' cannot reach them).");
                }
            }

            foreach (AiClause clause in rule.Clauses)
            {
                switch (clause)
                {
                    case AiWhenClause whenClause:
                        foreach (AiCondition c in whenClause.Conditions)
                        {
                            _conditions.Add(Compile(c));
                        }

                        break;

                    case AiHaveClause have:
                        foreach (AiGoalSyntax g in have.Goals)
                        {
                            if (g.Name.Length > 0)
                            {
                                _actions.Add(new AiActionCode(AiActionOp.Goal, Symbol(g.Name, g.Location), g.Number ?? -1, g.Percent));
                            }
                        }

                        break;

                    case AiTriggerClause trigger:
                        foreach (AiFlagChange change in trigger.Changes)
                        {
                            _actions.Add(new AiActionCode(change.Clear ? AiActionOp.ClearFlag : AiActionOp.SetFlag, _flags.Intern(change.Flag), -1, false));
                        }

                        break;

                    case AiForgetClause forget:
                        _actions.Add(forget.RuleName is { } target
                            ? new AiActionCode(AiActionOp.Forget, InternRuleName(target), -1, false)
                            : new AiActionCode(AiActionOp.ForgetThis, -1, -1, false));
                        break;

                    case AiSayClause say:
                        _actions.Add(new AiActionCode(AiActionOp.Say, _texts.Intern(say.Text), -1, false));
                        break;
                }
            }

            r.ConditionCount = _conditions.Count - r.FirstCondition;
            r.ActionCount = _actions.Count - r.FirstAction;
            _rules.Add(r);
            _ruleLocations.Add(rule.Location);
            return index;
        }

        private AiConditionCode Compile(AiCondition c)
        {
            switch (c.Kind)
            {
                case AiConditionKind.Flag:
                    return new AiConditionCode(AiConditionOp.Flag, c.Negated, false, c.Name.Length > 0 ? _flags.Intern(c.Name) : -1, 0);

                case AiConditionKind.Random:
                    return new AiConditionCode(AiConditionOp.Random, c.Negated, c.Percent, 0, c.Number ?? 0);

                case AiConditionKind.Time:
                    return new AiConditionCode(AiConditionOp.Time, c.Negated, false, 0, c.Number ?? 0);

                case AiConditionKind.Difficulty:
                    int level = c.Name.ToLowerInvariant() switch { "easy" => 0, "normal" => 1, "hard" => 2, _ => -1 };
                    return new AiConditionCode(AiConditionOp.Difficulty, c.Negated, false, level, 0);

                case AiConditionKind.Civilization:
                    return new AiConditionCode(AiConditionOp.Civilization, c.Negated, false, _civilizations.Intern(c.Name), 0);

                case AiConditionKind.Extern:
                    AiExpression? expression = c.Expression;
                    int nodes = 0;
                    if (expression is not null)
                    {
                        (nodes, int depth) = Measure(expression, 0, limits.MaxExpressionNodes);
                        if (nodes > limits.MaxExpressionNodes || depth > AiExpressionLimits.MaxDepth)
                        {
                            Diagnostics.Error(AiDiagnosticCode.InvalidExpression, c.Location, $"The extern expression is too large (at most {limits.MaxExpressionNodes} values and operators, {AiExpressionLimits.MaxDepth} levels); the condition is false.");
                            expression = null;
                            nodes = 0;
                        }
                    }

                    _externs.Add(expression);
                    _externCosts.Add(1 + (nodes / 8));
                    return new AiConditionCode(AiConditionOp.Extern, c.Negated, false, _externs.Count - 1, 0);

                default:
                    int symbol = c.Name.Length > 0 ? Symbol(c.Name, c.Location) : -1;
                    return new AiConditionCode(c.Enemy ? AiConditionOp.EnemyCount : AiConditionOp.Count, c.Negated, c.Percent, symbol, c.Number ?? 1);
            }
        }

        // Node count and depth of an expression; stops counting past the limit (the depth is bounded by the parser).
        private static (int Nodes, int Depth) Measure(AiExpression e, int depth, int limit)
        {
            if (depth > AiExpressionLimits.MaxDepth)
            {
                return (limit + 1, depth);
            }

            switch (e)
            {
                case AiUnaryExpression u:
                {
                    (int n, int d) = Measure(u.Operand, depth + 1, limit);
                    return (n + 1, d);
                }

                case AiBinaryExpression b:
                {
                    (int ln, int ld) = Measure(b.Left, depth + 1, limit);
                    if (ln > limit)
                    {
                        return (ln, ld);
                    }

                    (int rn, int rd) = Measure(b.Right, depth + 1, limit);
                    return (ln + rn + 1, Math.Max(ld, rd));
                }

                case AiCallExpression call:
                {
                    int n = 1;
                    int d = depth;
                    foreach (AiExpression a in call.Arguments)
                    {
                        (int an, int ad) = Measure(a, depth + 1, limit);
                        n += an;
                        d = Math.Max(d, ad);
                        if (n > limit)
                        {
                            break;
                        }
                    }

                    return (n, d);
                }

                default:
                    return (1, depth);
            }
        }

        private int Symbol(string name, SourceLocation location)
        {
            int before = _symbols.Count;
            int id = _symbols.Intern(name);
            if (_symbols.Count > before)
            {
                _symbolLocations.Add(location);
            }

            return id;
        }

        private int InternRuleName(string name)
        {
            int id = _ruleNames.Intern(name);
            while (_rulesByName.Count <= id)
            {
                _rulesByName.Add([]);
            }

            return id;
        }

        private ulong Hash(ImmutableArray<AiRuleCode> rules)
        {
            var h = new StateHasher();
            h.Add(0x41495047).Add(rules.Length);
            foreach (AiRuleCode r in rules)
            {
                h.Add(r.BlockEnd).Add(r.FirstCondition).Add(r.ConditionCount).Add(r.FirstAction).Add(r.ActionCount).Add(r.Name).Add(r.Critical);
            }

            h.Add(_conditions.Count);
            foreach (AiConditionCode c in _conditions)
            {
                h.Add((byte)c.Op).Add(c.Negated).Add(c.Percent).Add(c.Operand).Add(c.Number);
            }

            h.Add(_actions.Count);
            foreach (AiActionCode a in _actions)
            {
                h.Add((byte)a.Op).Add(a.Operand).Add(a.Number).Add(a.Percent);
            }

            _symbols.AppendTo(h);
            _flags.AppendTo(h);
            _ruleNames.AppendTo(h);
            _texts.AppendTo(h);
            _civilizations.AppendTo(h);
            h.Add(_externs.Count);
            foreach (AiExpression? e in _externs)
            {
                AppendExpression(h, e);
            }

            return h.GetCurrentHash();
        }

        private static void AppendExpression(StateHasher h, AiExpression? e)
        {
            switch (e)
            {
                case null: h.Add((byte)0); break;
                case AiNumberExpression n: h.Add((byte)1).Add(n.Value); break;
                case AiVariableExpression v: h.Add((byte)2); AppendString(h, v.Name.ToLowerInvariant()); break;
                case AiCallExpression call:
                    h.Add((byte)3);
                    AppendString(h, call.Name.ToLowerInvariant());
                    h.Add(call.Arguments.Length);
                    foreach (AiExpression a in call.Arguments)
                    {
                        AppendExpression(h, a);
                    }

                    break;
                case AiUnaryExpression u: h.Add((byte)4).Add((byte)u.Operator); AppendExpression(h, u.Operand); break;
                case AiBinaryExpression b: h.Add((byte)5).Add((byte)b.Operator); AppendExpression(h, b.Left); AppendExpression(h, b.Right); break;
            }
        }

        internal static void AppendString(StateHasher h, string s)
        {
            h.Add(s.Length);
            foreach (char c in s)
            {
                h.Add((int)c);
            }
        }
    }

    // Interns names in order of first appearance; lookup is case-insensitive unless asked otherwise (keys lower-cased).
    private sealed class Interner(bool caseSensitive = false)
    {
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly List<string> _names = [];

        public int Count => _names.Count;

        public int Intern(string name)
        {
            string key = caseSensitive ? name : name.ToLowerInvariant();
            if (!_ids.TryGetValue(key, out int id))
            {
                id = _names.Count;
                _ids[key] = id;
                _names.Add(key);
            }

            return id;
        }

        public ImmutableArray<string> ToImmutable() => [.. _names];

        public void AppendTo(StateHasher h)
        {
            h.Add(_names.Count);
            foreach (string n in _names)
            {
                State.AppendString(h, n);
            }
        }
    }
}
