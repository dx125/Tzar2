using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Scripting.Tests.ScenarioBuilder;

namespace Tzar2.Scripting.Tests;

/// <summary>
/// The parts of the legacy language beyond Appendix C that the converter found in real maps (ADR 0120 update): statement
/// sequences, further functions and reserved variables, the Delay action, object categories, legacy owner codes.
/// </summary>
public class ExtendedLanguageTests
{
    private static TriggerDriver Once(ScenarioBuilder b, int players, params ActionDef[] actions)
    {
        b.Rule("1", actions.Select(b.Action).ToArray());
        TriggerDriver d = b.Start(players);
        d.Step();
        return d;
    }

    [Fact]
    public void Sequences_run_in_order_and_give_the_last_value()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        using TriggerDriver d = Once(b, 2, new ExpressionAction("seq", b.Expr("$a = 2; $b = $a * 5\r\n$c = @max(1, $b, 4) + @min(7, 3)")));
        Assert.Equal((2, 10, 13), (d.Variable("a"), d.Variable("b"), d.Variable("c")));
    }

    [Fact]
    public void Resource_setters_population_limit_and_diplomacy_queries()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        using TriggerDriver d = Once(
            b,
            3,
            new ChangeDiplomacyAction("pact", 1, 2, Stance.Ally, true, false, false),
            new ExpressionAction("q", b.Expr("@setgold(1, 777); @setwood(2, 5); @setpoplimit(1, 42); $ally = @isally(1, 2); $enemy = @isenemy(1, 3); $vision = @hassharedvision(2, 1); $res = @hassharedres(1, 2); $d = @getdiplomacy(1, 3); $bad = @isally(1, 9)")));
        int gold = d.World.Ruleset.ResourceId("gold");
        Assert.Equal(777, d.World.Players[0].Resources[gold]);
        Assert.Equal(5, d.World.Players[1].Resources[d.World.Ruleset.ResourceId("wood")]);
        Assert.Equal(42, d.World.Players[0].PopulationLimit);
        Assert.Equal((1, 1, 1, 0, 1, 0), (d.Variable("ally"), d.Variable("enemy"), d.Variable("vision"), d.Variable("res"), d.Variable("d"), d.Variable("bad")));
    }

    [Fact]
    public void Interface_functions_are_no_ops_and_unsupported_ones_report_a_fault()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        using TriggerDriver d = Once(b, 2, new ExpressionAction("ui", b.Expr("@clearselection(); @playmovie(movies\\end.ddv); $x1 = @str(hello) + 4; @VardalWon = 1")), new ExpressionAction("god", b.Expr("@god(1)")));
        Assert.Equal(4, d.Variable("x1"));
        Assert.Single(d.Of(SimEventKind.ScenarioFault), e => e.Arg == (int)ScenarioFault.UnsupportedFunction);
    }

    [Fact]
    public void Further_reserved_variables_read_the_current_object()
    {
        var b = new ScenarioBuilder();
        int castle = b.Structure("euro.castle", 0, 12, 5);
        int unit = b.Unit("euro.footman", 0, 6, 5);
        int far = b.Unit("euro.footman", 1, 40, 25);
        b.Event(new ObjectExpressionEvent("held", All(unit), b.Expr("$inholder == 1 && $race > 0")));
        b.Event(new ObjectExpressionEvent("fog", All(far), b.Expr("$underfog")));
        b.Rule("1", b.Action(new KeepRuleAction("k")), b.Action(new ExpressionAction("w", b.Expr("$held = held; $fog = fog"))));
        using TriggerDriver d = b.Start(options: TriggerOptions.Default with { ResponderMask = 1 });
        d.Step();
        Assert.Equal((0, 1), (d.Variable("held"), d.Variable("fog")));
        d.Order(0, CommandKind.Board, [d.Object(unit)], target: d.Object(castle));
        d.Seconds(10);
        Assert.Equal(1, d.Variable("held"));
    }

    [Fact]
    public void Delay_parks_the_rest_of_the_action_list()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Rule(
            "1",
            b.Action(new ExpressionAction("first", b.Expr("$step = 1"))),
            b.Action(new DelayAction("wait", 2000)),
            b.Action(new ExpressionAction("second", b.Expr("$step = 2"))),
            b.Action(new DelayAction("wait2", 500)),
            b.Action(new ExpressionAction("third", b.Expr("$step = 3"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(1, d.Variable("step"));
        Assert.True(d.State.RuleDisabled[0]); // no Keep Rule in the list
        d.Run(39);
        Assert.Equal(1, d.Variable("step"));
        d.Step(); // 2,000 ms later
        Assert.Equal(2, d.Variable("step"));

        using TriggerDriver restored = d.Restore();
        restored.Run(10);
        Assert.Equal(3, restored.Variable("step"));
        Assert.Empty(restored.State.Delayed);
    }

    [Fact]
    public void Categories_count_units_military_and_buildings()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.footman", 1, 3, 3);
        b.Unit("euro.footman", 1, 4, 3);
        b.Unit("euro.peon", 1, 5, 3);
        b.Structure("euro.house", 1, 10, 10);
        b.Event(new ObjectExistEvent("army", new CategoryObjects(ObjectCategory.MilitaryUnits, PlayerMasks.Of(2), CountComparison.AtLeast, 2, Quantifier.All)));
        b.Event(new ObjectExistEvent("units", new CategoryObjects(ObjectCategory.Units, PlayerMasks.Of(2), CountComparison.AtLeast, 4, Quantifier.All)));
        b.Event(new ObjectDestroyedEvent("lost", new CategoryObjects(ObjectCategory.Buildings, PlayerMasks.Of(2), CountComparison.AtLeast, 1, Quantifier.Any)));
        b.Rule("1", b.Action(new KeepRuleAction("k")), b.Action(new ExpressionAction("w", b.Expr("$army = army; $units = units; $lost = lost"))));
        b.Rule("$go", b.Action(new DestroyObjectAction("raze", new CategoryObjects(ObjectCategory.Buildings, PlayerMasks.Of(2), CountComparison.AtLeast, 1, Quantifier.Any))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal((1, 0, 0), (d.Variable("army"), d.Variable("units"), d.Variable("lost")));
        d.State.Variables[d.ScenarioData.Variables.Items.IndexOf("go")] = 1;
        d.Seconds(2);
        Assert.Equal(1, d.Variable("lost"));
    }

    [Fact]
    public void Spawn_owners_use_the_legacy_codes()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        using TriggerDriver d = Once(b, 2, new SpawnObjectAction("wolves", "euro.footman", 10, 2, Rect(20, 20, 21, 21)), new SpawnObjectAction("bad", "euro.footman", 7, 1, Rect(20, 20, 21, 21)));
        Assert.Equal(2, d.World.Identities.Components.ToArray().Count(i => i.Owner == Content.Compiled.MapObject.Nature && d.World.Ruleset.EntityTypes[i.TypeId].Key == "euro.footman"));
        Assert.Contains(d.Of(SimEventKind.ScenarioFault), e => e.Arg == (int)ScenarioFault.ActionFailed); // kingdom 7 is not in a 2-player match
    }
}
