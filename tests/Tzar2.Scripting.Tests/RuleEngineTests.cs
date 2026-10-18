using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim.Events;
using Xunit;
using Tzar2.Tests.Scenario;
using static Tzar2.Tests.Scenario.ScenarioBuilder;

namespace Tzar2.Scripting.Tests;

/// <summary>The rule loop, the expression VM and timers (Appendix C.3, C.6; ADR 0130).</summary>
public class RuleEngineTests
{
    private static int Value(string expression, string variable = "r", int ticks = 1)
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Rule("1", b.Action(new ExpressionAction("calc", b.Expr($"${variable} = {expression}"))));
        using TriggerDriver d = b.Start();
        d.Run(ticks);
        return d.Variable(variable);
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(5*3 - 8)/12*7 >= (40/10 + 5)", 0)]
    [InlineData("17 / 5", 3)]
    [InlineData("-17 / 5", -3)]
    [InlineData("-17 % 5", -2)]
    [InlineData("7 / 0", 0)]
    [InlineData("7 % 0", 0)]
    [InlineData("2147483647 + 1", int.MaxValue)]
    [InlineData("-2147483647 - 5", int.MinValue)]
    [InlineData("65536 * 65536", int.MaxValue)]
    [InlineData("!0 + !5", 1)]
    [InlineData("3 < 4 == 1", 1)]
    [InlineData("0 && $never = 9", 0)]
    [InlineData("1 || ($never = 9)", 1)]
    [InlineData("nosuchname + 4", 4)]
    [InlineData("$health + $gold", 500)] // no current object: $health 0, $gold the human player's (500 synthetic start)
    public void Expressions_use_saturating_32_bit_integers(string expression, int expected)
        => Assert.Equal(expected, Value(expression));

    [Fact]
    public void Short_circuit_skips_the_assignment_and_division_by_zero_is_reported()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Rule("1", b.Action(new ExpressionAction("calc", b.Expr("$a = 0 && ($b = 5)"))));
        b.Rule("1", b.Action(new ExpressionAction("div", b.Expr("$d = 4 / 0"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(0, d.Variable("b"));
        Assert.Contains(d.Of(SimEventKind.ScenarioFault), e => e.Arg == (int)ScenarioFault.DivisionByZero && e.X == 1);
    }

    [Fact]
    public void Rnd_stays_within_its_inclusive_bounds_and_is_deterministic()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Rule("1", b.Action(new KeepRuleAction("keep")), b.Action(new ExpressionAction("roll", b.Expr("$r = @rnd(3, 5)"))), b.Action(new ExpressionAction("fixed", b.Expr("$s = rnd(9, 9)"))));
        int[] Rolls()
        {
            using TriggerDriver d = b.Start();
            var rolls = new int[40];
            for (int i = 0; i < rolls.Length; i++)
            {
                d.Seconds(1);
                rolls[i] = d.Variable("r");
                Assert.Equal(9, d.Variable("s"));
            }

            return rolls;
        }

        int[] first = Rolls();
        Assert.All(first, r => Assert.InRange(r, 3, 5));
        Assert.Equal(3, first.Distinct().Count());
        Assert.Equal(first, Rolls());
    }

    [Fact]
    public void Resource_functions_read_a_kingdom_by_its_legacy_number()
    {
        Assert.Equal(1, Value("@gold(1) == @gold(2)"));
        Assert.Equal(0, Value("@wood(9) + @food(0)")); // no such kingdoms
    }

    [Fact]
    public void A_rule_fires_once_unless_it_keeps_itself_and_rules_run_about_every_second()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Rule("1", b.Action(new ExpressionAction("once", b.Expr("$once = $once + 1"))));
        b.Rule("1", b.Action(new ExpressionAction("kept", b.Expr("$kept = $kept + 1"))), b.Action(new KeepRuleAction("keep")));
        b.Rule("", b.Action(new ExpressionAction("never", b.Expr("$empty = 1")))); // an empty condition is false
        b.Rule("1 +", b.Action(new ExpressionAction("broken", b.Expr("$broken = 1")))); // unparsable: false
        using TriggerDriver d = b.Start();
        d.Step(); // the first pass runs in the first tick
        Assert.Equal(1, d.Variable("once"));
        Assert.Equal(1, d.Variable("kept"));
        d.Run(19);
        Assert.Equal(1, d.Variable("kept")); // 20 ticks of 50 ms per pass
        d.Step();
        Assert.Equal(2, d.Variable("kept"));
        d.Seconds(5);
        Assert.Equal(1, d.Variable("once"));
        Assert.Equal(7, d.Variable("kept"));
        Assert.Equal(0, d.Variable("empty"));
        Assert.Equal(0, d.Variable("broken"));
        Assert.Equal([true, false, false, false], d.State.RuleDisabled);
    }

    [Fact]
    public void Rules_run_in_order_and_see_each_others_assignments_within_a_pass()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Rule("$step == 1", b.Action(new ExpressionAction("second", b.Expr("$step = 2"))), b.Action(new ExpressionAction("second2", b.Expr("$order = $order * 10 + 2"))));
        b.Rule("$step == 0", b.Action(new ExpressionAction("first", b.Expr("$step = 1"))), b.Action(new ExpressionAction("first2", b.Expr("$order = $order * 10 + 1"))));
        b.Rule("$step == 1", b.Action(new ExpressionAction("third", b.Expr("$order = $order * 10 + 3"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(13, d.Variable("order")); // rule 0 saw step 0 in this pass; rule 2 saw the new value
        d.Seconds(1);
        Assert.Equal(132, d.Variable("order"));
    }

    [Fact]
    public void A_pass_over_its_budget_continues_in_the_next_tick()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        for (int i = 0; i < 30; i++)
        {
            b.Rule("1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1+1", b.Action(new ExpressionAction("count" + i, b.Expr("$n = $n + 1"))));
        }

        using TriggerDriver d = b.Start(options: TriggerOptions.Default with { StepBudgetPerTick = 400 });
        d.Step();
        int first = d.Variable("n");
        Assert.InRange(first, 1, 29);
        Assert.True(d.State.PassRule > 0);
        Assert.Contains(d.Of(SimEventKind.ScenarioFault), e => e.Arg == (int)ScenarioFault.BudgetDeferred);
        d.Run(10);
        Assert.Equal(30, d.Variable("n"));
        Assert.Equal(-1, d.State.PassRule);
    }

    [Fact]
    public void Timers_count_up_or_down_by_game_time_and_stop()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        int up = b.Action(new SetTimerAction("UpTimer", b.Expr("250"), CountDown: false));
        int down = b.Action(new SetTimerAction("DownTimer", b.Expr("1500"), CountDown: true));
        int stop = b.Action(new StopTimerAction("halt", up));
        b.Rule("1", up, down);
        b.Rule("UpTimer >= 2250", stop);
        b.Rule("DownTimer == 0", b.Action(new ExpressionAction("rang", b.Expr("$rang = 1"))));
        b.Rule("1", b.Action(new KeepRuleAction("keep")), b.Action(new ExpressionAction("watch", b.Expr("$up = UpTimer"))), b.Action(new ExpressionAction("watch2", b.Expr("$down = DownTimer"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(250, d.Variable("up")); // set in this pass, read before it advanced
        d.Seconds(1);
        Assert.Equal(1250, d.Variable("up"));
        Assert.Equal(500, d.Variable("down"));
        d.Seconds(1);
        Assert.Equal(2250, d.Variable("up"));
        Assert.Equal(0, d.Variable("down"));
        Assert.Equal(1, d.Variable("rang"));
        d.Seconds(2);
        Assert.Equal(2250, d.Variable("up")); // stopped
        Assert.False(d.State.Timers[up].Running);
    }

    [Fact]
    public void Assigning_a_timer_sets_its_value_and_presets_one_not_started_yet()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        int clock = b.Action(new SetTimerAction("Clock", b.Expr("0"), CountDown: false));
        b.Action(new SetTimerAction("Spare", b.Expr("0"), CountDown: false));
        b.Rule("1", clock);
        b.Rule("Clock >= 1000", b.Action(new KeepRuleAction("keep")), b.Action(new ExpressionAction("reset", b.Expr("Clock = 0, $resets = $resets + 1"))));
        b.Rule("$resets == 2", b.Action(new ExpressionAction("spare", b.Expr("Spare = 500"))));
        using TriggerDriver d = b.Start();
        d.Seconds(3);
        Assert.Equal(2, d.Variable("resets"));
        d.Seconds(2);
        Assert.True(d.State.Timers[1].Started);
        Assert.False(d.State.Timers[1].Running); // preset: it holds the value until its Set Timer action runs
        Assert.Equal(500, d.State.Timers[1].ValueMs);
    }

    [Fact]
    public void Attaching_rejects_a_scenario_for_another_map_or_a_started_match()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        var other = new ScenarioBuilder();
        other.Unit("euro.peon", 0, 3, 3);
        other.Unit("euro.peon", 0, 4, 3);
        using var sim = new Tzar2.Sim.Simulation(Classic, b.Map(), new Tzar2.Sim.SimulationConfig { PlayerCount = 2 });
        Assert.Throws<System.ArgumentException>(() => LegacyTriggerSystem.Attach(sim, other.Scenario()));
        sim.Tick(new Tzar2.Sim.Commands.CommandList());
        Assert.Throws<System.InvalidOperationException>(() => LegacyTriggerSystem.Attach(sim, b.Scenario()));
    }
}
