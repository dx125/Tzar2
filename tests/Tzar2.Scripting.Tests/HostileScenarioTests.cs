using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Scripting.Tests.ScenarioBuilder;

namespace Tzar2.Scripting.Tests;

/// <summary>
/// Hostile scenarios (community maps are untrusted; ADR 0130): a single rule cannot exceed the per-rule bounds checked at
/// attach, and a tick never spends more than the hard step cap.
/// </summary>
public class HostileScenarioTests
{
    // A balanced sum of 2^levels ones (2^(levels+1) - 1 nodes, depth levels + 1).
    private static Expr Balanced(int levels) => levels == 0 ? new IntLiteral(1) : new BinaryExpr(BinaryOp.Add, Balanced(levels - 1), Balanced(levels - 1));

    // A left-leaning chain of n additions (depth n + 1).
    private static Expr Chain(int n)
    {
        Expr e = new IntLiteral(1);
        for (int i = 0; i < n; i++)
        {
            e = new BinaryExpr(BinaryOp.Add, e, new IntLiteral(1));
        }

        return e;
    }

    [Fact]
    public void A_rule_keeps_at_most_the_action_cap()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        int add = b.Action(new ExpressionAction("inc", b.Expr("$n = $n + 1")));
        b.Rule("1", Enumerable.Repeat(add, 10_000).ToArray());
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(LegacyTriggerSystem.MaxActionsPerRule, d.Variable("n"));
        Assert.Contains(d.Triggers.Diagnostics, m => m.Contains("only the first", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Oversized_or_overdeep_expressions_are_ignored_without_recursion_trouble()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        int v = b.UserVariable("big");
        int w = b.UserVariable("deep");
        b.Rule("1", b.Action(new ExpressionAction("big", new ExpressionDef("(huge)", new AssignExpr(new UserVar(v), Balanced(13))))));
        b.Rule("1", b.Action(new ExpressionAction("deep", new ExpressionDef("(deep)", new AssignExpr(new UserVar(w), Chain(1000))))));
        b.Rule("1", b.Action(new ExpressionAction("fine", b.Expr("$ok = 1"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal((0, 0, 1), (d.Variable("big"), d.Variable("deep"), d.Variable("ok")));
        Assert.Equal(2, d.Triggers.Diagnostics.Count);
    }

    [Fact]
    public void A_tick_stops_a_rule_at_the_hard_step_cap()
    {
        var b = new ScenarioBuilder();
        for (int i = 0; i < 40; i++)
        {
            b.Unit("euro.peon", 0, 2 + i, 3);
        }

        // Every action evaluates a 4,095-node expression on every peon: about 40 x 4,095 steps per action.
        int heavy = b.Action(new SetObjectPropertiesAction("heavy", Typed("euro.peon", 1, CountComparison.AtLeast, 0), new ExpressionDef("(heavy)", Balanced(11))));
        b.Rule("1", Enumerable.Repeat(heavy, 200).ToArray());
        b.Rule("1", b.Action(new ExpressionAction("after", b.Expr("$after = 1"))));
        var options = TriggerOptions.Default with { StepBudgetPerTick = 1000, HardStepCapPerTick = 50_000 };
        using TriggerDriver d = b.Start(options: options);
        d.Step();
        Assert.InRange(d.Triggers.StepsLastTick, 50_000, 50_000 + 100); // the cut rule only unwinds
        Assert.Contains(d.Of(SimEventKind.ScenarioFault), e => e.Arg == (int)ScenarioFault.RuleCut);
        Assert.Equal(0, d.Variable("after"));
        Assert.True(d.State.RuleDisabled[0]);
        d.Step(); // the pass continues with the next rule
        Assert.Equal(1, d.Variable("after"));
    }
}
