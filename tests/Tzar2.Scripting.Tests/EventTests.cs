using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Xunit;
using static Tzar2.Scripting.Tests.ScenarioBuilder;

namespace Tzar2.Scripting.Tests;

/// <summary>The eight events of Appendix C.5, one synthetic scenario each (invented positions and counts).</summary>
public class EventTests
{
    // A rule that copies event "e" into $v on every pass.
    private static void Watch(ScenarioBuilder b, string eventName = "e")
        => b.Rule("1", b.Action(new KeepRuleAction("keep")), b.Action(new ExpressionAction("watch", b.Expr($"$v = {eventName}"))));

    private static int Tiles(int t) => (t * Tile) + (Tile / 2);

    [Fact]
    public void Object_in_area_named_all_and_any()
    {
        var b = new ScenarioBuilder();
        int a = b.Unit("euro.peon", 0, 3, 3);
        int c = b.Unit("euro.peon", 0, 3, 5);
        b.Event(new ObjectInAreaEvent("e", All(a, c), Rect(10, 2, 14, 8)));
        b.Event(new ObjectInAreaEvent("f", Any(a, c), Rect(10, 2, 14, 8)));
        Watch(b);
        b.Rule("1", b.Action(new KeepRuleAction("keep2")), b.Action(new ExpressionAction("watch2", b.Expr("$w = f"))));
        using TriggerDriver d = b.Start();
        d.Seconds(1);
        Assert.Equal(0, d.Variable("v"));
        Assert.Equal(0, d.Variable("w"));
        d.Order(0, CommandKind.Move, [d.Object(a)], Tiles(12), Tiles(4));
        d.Seconds(8);
        Assert.Equal(0, d.Variable("v"));
        Assert.Equal(1, d.Variable("w"));
        d.Order(0, CommandKind.Move, [d.Object(c)], Tiles(12), Tiles(6));
        d.Seconds(8);
        Assert.Equal(1, d.Variable("v"));
    }

    [Fact]
    public void Object_in_area_typed_counts_per_kingdom_and_an_object_area_follows_its_object()
    {
        var b = new ScenarioBuilder();
        int flag = b.Unit("euro.footman", 1, 20, 10);
        for (int i = 0; i < 3; i++)
        {
            b.Unit("euro.peon", 0, 19 + i, 12);
        }

        b.Event(new ObjectInAreaEvent("e", Typed("euro.peon", 1, CountComparison.AtLeast, 3), new ObjectArea(flag, RadiusTiles: 3)));
        b.Event(new ObjectInAreaEvent("few", Typed("euro.peon", 1, CountComparison.AtMost, 1), Rect(18, 11, 22, 13)));
        Watch(b);
        b.Rule("1", b.Action(new KeepRuleAction("keep2")), b.Action(new ExpressionAction("watch2", b.Expr("$w = few"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(1, d.Variable("v"));
        Assert.Equal(0, d.Variable("w"));
        d.Order(1, CommandKind.Move, [d.Object(flag)], Tiles(40), Tiles(25)); // the area moves away with the footman
        d.Seconds(12);
        Assert.Equal(0, d.Variable("v"));
    }

    [Fact]
    public void Object_destroyed_named_and_typed()
    {
        var b = new ScenarioBuilder();
        int a = b.Unit("euro.peon", 1, 3, 3);
        int c = b.Unit("euro.peon", 1, 5, 3);
        b.Event(new ObjectDestroyedEvent("e", All(a, c)));
        b.Event(new ObjectDestroyedEvent("one", Any(a, c)));
        b.Event(new ObjectDestroyedEvent("two", Typed("euro.peon", 2, CountComparison.AtLeast, 2)));
        Watch(b);
        b.Rule("1", b.Action(new KeepRuleAction("k1")), b.Action(new ExpressionAction("w1", b.Expr("$one = one"))));
        b.Rule("1", b.Action(new KeepRuleAction("k2")), b.Action(new ExpressionAction("w2", b.Expr("$two = two"))));
        using TriggerDriver d = b.Start();
        d.Seconds(1);
        Assert.Equal(0, d.Variable("one"));
        d.Order(0, CommandKind.DebugKill, [d.Object(a)]);
        d.Seconds(1);
        Assert.Equal(1, d.Variable("one"));
        Assert.Equal(0, d.Variable("v"));
        Assert.Equal(0, d.Variable("two"));
        d.Order(0, CommandKind.DebugKill, [d.Object(c)]);
        d.Seconds(1);
        Assert.Equal(1, d.Variable("v"));
        Assert.Equal(1, d.Variable("two"));
        Assert.Equal(2, d.State.EventMemory[2][1]);
    }

    [Fact]
    public void Object_exist_named_and_typed()
    {
        var b = new ScenarioBuilder();
        int a = b.Unit("euro.footman", 0, 3, 3);
        b.Unit("euro.footman", 0, 4, 3);
        b.Event(new ObjectExistEvent("e", All(a)));
        b.Event(new ObjectExistEvent("many", Typed("euro.footman", 1, CountComparison.AtLeast, 2)));
        Watch(b);
        b.Rule("1", b.Action(new KeepRuleAction("k1")), b.Action(new ExpressionAction("w1", b.Expr("$many = many"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(1, d.Variable("v"));
        Assert.Equal(1, d.Variable("many"));
        d.Order(0, CommandKind.DebugKill, [d.Object(a)]);
        d.Seconds(1);
        Assert.Equal(0, d.Variable("v"));
        Assert.Equal(0, d.Variable("many"));
    }

    [Fact]
    public void Objects_enter_and_leave_a_holder()
    {
        var b = new ScenarioBuilder();
        int castle = b.Structure("euro.castle", 0, 12, 5);
        int f1 = b.Unit("euro.footman", 0, 6, 5);
        int f2 = b.Unit("euro.footman", 0, 6, 6);
        b.Event(new ObjectsEnterHolderEvent("e", All(f1, f2), castle));
        b.Event(new ObjectLeaveHolderEvent("out", Any(f1, f2), castle));
        b.Event(new ObjectLeaveHolderEvent("outTyped", Typed("euro.footman", 1, CountComparison.AtLeast, 2), castle));
        Watch(b);
        b.Rule("1", b.Action(new KeepRuleAction("k1")), b.Action(new ExpressionAction("w1", b.Expr("$out = out"))));
        b.Rule("1", b.Action(new KeepRuleAction("k2")), b.Action(new ExpressionAction("w2", b.Expr("$outTyped = outTyped"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(0, d.Variable("v"));
        Assert.Equal(0, d.Variable("out")); // never inside yet
        d.Order(0, CommandKind.Board, [d.Object(f1), d.Object(f2)], target: d.Object(castle));
        d.Seconds(15);
        Assert.Equal(1, d.Variable("v"));
        Assert.Equal(0, d.Variable("out"));
        d.Order(0, CommandKind.Unload, [d.Object(castle)], Tiles(6), Tiles(6));
        d.Seconds(1);
        Assert.Equal(0, d.Variable("v"));
        Assert.Equal(1, d.Variable("out"));
        Assert.Equal(1, d.Variable("outTyped"));
    }

    [Fact]
    public void A_typed_holder_matches_any_holder_of_the_type()
    {
        var b = new ScenarioBuilder();
        b.Structure("euro.castle", 0, 12, 5);
        int f1 = b.Unit("euro.footman", 0, 6, 5);
        b.Event(new ObjectsEnterHolderEvent("e", All(f1), -1, Typed("euro.castle", 1, CountComparison.AtLeast, 1)));
        Watch(b);
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(0, d.Variable("v"));
        d.Order(0, CommandKind.Board, [d.Object(f1)], target: d.Object(0));
        d.Seconds(12);
        Assert.Equal(1, d.Variable("v"));
    }

    [Fact]
    public void Player_defeated_and_player_won()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Unit("euro.peon", 1, 20, 3);
        b.Event(new PlayerDefeatedEvent("e", PlayerMasks.Of(2)));
        b.Event(new PlayerWonEvent("won", PlayerMasks.Of(1)));
        Watch(b);
        b.Rule("1", b.Action(new KeepRuleAction("k1")), b.Action(new ExpressionAction("w1", b.Expr("$won = won"))));
        b.Rule("$t == 1", b.Action(new PlayerWinAction("win", PlayerMasks.Of(1), null)));
        b.Rule("1", b.Action(new ExpressionAction("arm", b.Expr("$t = 1"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(0, d.Variable("v"));
        d.Seconds(2);
        Assert.Equal(1, d.Variable("v"));
        Assert.Equal(1, d.Variable("won"));
    }

    [Fact]
    public void Object_expression_evaluates_reserved_variables_per_object()
    {
        var b = new ScenarioBuilder();
        int a = b.Unit("euro.footman", 0, 3, 3);
        int c = b.Unit("euro.footman", 0, 5, 3);
        b.Event(new ObjectExpressionEvent("e", Any(a, c), b.Expr("$health < $maxhealth / 2")));
        b.Event(new ObjectExpressionEvent("rich", Typed("euro.footman", 1, CountComparison.AtLeast, 2), b.Expr("$gold >= 100 && $kingdom == 1 && $x >= 3")));
        Watch(b);
        b.Rule("1", b.Action(new KeepRuleAction("k1")), b.Action(new ExpressionAction("w1", b.Expr("$rich = rich"))));
        b.Rule("$hurt == 1", b.Action(new SetObjectPropertiesAction("hurt", All(c), b.Expr("$health = 10"))));
        b.Rule("1", b.Action(new ExpressionAction("arm", b.Expr("$hurt = 1"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(0, d.Variable("v"));
        Assert.Equal(1, d.Variable("rich"));
        d.Seconds(2);
        Assert.Equal(10, d.World.Healths.Get(d.Object(c)).Current);
        Assert.Equal(1, d.Variable("v"));
    }

    [Fact]
    public void Unknown_events_are_false_and_hidden_objects_are_neither_destroyed_nor_existing()
    {
        var b = new ScenarioBuilder();
        int hidden = b.Unit("euro.peon", 0, 3, 3, new ObjectInfo("ghost", "", ObjectFlags.NotAppear));
        b.Event(new UnknownEvent("e", 99, new byte[] { 1, 2 }));
        b.Event(new ObjectDestroyedEvent("gone", All(hidden)));
        b.Event(new ObjectExistEvent("there", All(hidden)));
        Watch(b);
        b.Rule("1", b.Action(new KeepRuleAction("k1")), b.Action(new ExpressionAction("w1", b.Expr("$gone = gone + there"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(0, d.Variable("v"));
        Assert.Equal(0, d.Variable("gone"));
        Assert.True(d.State.Hidden[hidden]);
        Assert.Equal(EntityId.None.Value, d.State.MapEntities[hidden]);
    }
}
