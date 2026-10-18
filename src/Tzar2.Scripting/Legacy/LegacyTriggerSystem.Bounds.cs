using System.Collections.Generic;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Scripting.Legacy;

/// <summary>
/// The cost bound of one rule (ADR 0130): a scenario is untrusted input (community maps), so a single rule must not be
/// able to blow the tick budget. At attach, rules keep at most <see cref="MaxActionsPerRule"/> actions and expressions at
/// most <see cref="MaxExpressionNodes"/> nodes and <see cref="MaxExpressionDepth"/> levels; anything larger is truncated
/// (actions) or treated as empty (expressions), with a diagnostic. At run time a tick stops a rule that has spent
/// <see cref="TriggerOptions.HardStepCapPerTick"/> steps (a <see cref="ScenarioFault.RuleCut"/> fault): the worst case per
/// tick is the step budget plus that cap.
/// </summary>
public sealed partial class LegacyTriggerSystem
{
    /// <summary>Most actions one rule keeps.</summary>
    public const int MaxActionsPerRule = 256;

    /// <summary>Most nodes one expression may have.</summary>
    public const int MaxExpressionNodes = 4096;

    /// <summary>Deepest nesting of one expression (the evaluator recurses).</summary>
    public const int MaxExpressionDepth = 256;

    private readonly HashSet<ExpressionDef> _rejected = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> _diagnostics = [];
    private int[][] _ruleActions = [];

    /// <summary>What attach had to cut from the scenario to bound its cost (empty for sane scenarios).</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>Steps spent in the last tick (tests, the trigger debugger).</summary>
    internal int StepsLastTick => _steps;

    // Whether this tick's rule ran over the hard cap: everything stops until the rule ends.
    private bool OverCap => _steps > _options.HardStepCapPerTick;

    private void Bound(ScenarioData scenario)
    {
        _ruleActions = new int[scenario.Rules.Length][];
        for (int r = 0; r < scenario.Rules.Length; r++)
        {
            RuleDef rule = scenario.Rules[r];
            int n = rule.ActionIndices.Length;
            if (n > MaxActionsPerRule)
            {
                _diagnostics.Add($"rule {r} '{rule.Name}': {n} actions, only the first {MaxActionsPerRule} are kept");
                n = MaxActionsPerRule;
            }

            _ruleActions[r] = new int[n];
            for (int k = 0; k < n; k++)
            {
                _ruleActions[r][k] = rule.ActionIndices[k];
            }

            Check(rule.Condition, $"rule {r} '{rule.Name}' condition");
        }

        for (int a = 0; a < scenario.Actions.Length; a++)
        {
            switch (scenario.Actions[a])
            {
                case ExpressionAction x:
                    Check(x.Expression, $"action {a} '{x.Name}'");
                    break;
                case SetObjectPropertiesAction x:
                    Check(x.Expression, $"action {a} '{x.Name}'");
                    break;
                case SetTimerAction x:
                    Check(x.Initial, $"action {a} '{x.Name}'");
                    break;
            }
        }

        for (int e = 0; e < scenario.Events.Length; e++)
        {
            if (scenario.Events[e] is ObjectExpressionEvent x)
            {
                Check(x.Expression, $"event {e} '{x.Name}'");
            }
        }

        for (int c = 0; c < scenario.Conversations.Length; c++)
        {
            foreach (PhraseDef p in scenario.Conversations[c].Phrases)
            {
                Check(p.Condition, $"conversation {c} phrase '{p.Name}' condition");
                Check(p.Result, $"conversation {c} phrase '{p.Name}' result");
            }
        }
    }

    // An expression over the size or depth bound is treated as empty.
    private void Check(ExpressionDef expression, string where)
    {
        if (expression.Root is not { } root)
        {
            return;
        }

        int nodes = 0;
        var stack = new Stack<(Expr Node, int Depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            (Expr node, int depth) = stack.Pop();
            if (++nodes > MaxExpressionNodes || depth > MaxExpressionDepth)
            {
                _rejected.Add(expression);
                _diagnostics.Add($"{where}: the expression is larger than {MaxExpressionNodes} nodes or deeper than {MaxExpressionDepth} levels and is ignored");
                return;
            }

            switch (node)
            {
                case UnaryExpr u:
                    stack.Push((u.Operand, depth + 1));
                    break;
                case BinaryExpr b:
                    stack.Push((b.Left, depth + 1));
                    stack.Push((b.Right, depth + 1));
                    break;
                case AssignExpr a:
                    stack.Push((a.Target, depth + 1));
                    stack.Push((a.Value, depth + 1));
                    break;
                case CallExpr c:
                    foreach (Expr arg in c.Arguments)
                    {
                        stack.Push((arg, depth + 1));
                    }

                    break;
                case SequenceExpr q:
                    foreach (Expr statement in q.Statements)
                    {
                        stack.Push((statement, depth + 1));
                    }

                    break;
            }
        }
    }
}
