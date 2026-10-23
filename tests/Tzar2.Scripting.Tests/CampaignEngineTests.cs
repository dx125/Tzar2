using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Tests.Scenario;
using Xunit;
using static Tzar2.Tests.Scenario.ScenarioBuilder;

namespace Tzar2.Scripting.Tests;

/// <summary>The trigger-engine rules ADR 0220 settled for the original campaigns (invented maps and positions).</summary>
public class CampaignEngineTests
{
    private static int Tiles(int t) => (t * Tile) + (Tile / 2);

    [Fact]
    public void Object_order_moves_the_units_of_a_defeated_kingdom()
    {
        // A kingdom that only holds a campaign's heroes may be beaten; the script still walks its heroes.
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        int hero = b.Unit("euro.footman", 1, 3, 8);
        b.Rule("1", b.Action(new PlayerLoseAction("lose", PlayerMasks.Of(2), null)));
        b.Rule("$t == 1", b.Action(new ObjectOrderAction("march", All(hero), Rect(20, 7, 22, 9), "move")));
        b.Rule("1", b.Action(new ExpressionAction("arm", b.Expr("$t = 1"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.True((d.World.Players[1].Flags & PlayerFlags.Defeated) != 0);
        d.Seconds(25);
        Assert.InRange(d.World.Positions.Get(d.Object(hero)).X / Tile, 18, 24);
    }

    [Fact]
    public void A_unit_inside_a_holder_is_in_an_area_where_its_holder_is_and_counts_as_its_cargo()
    {
        var b = new ScenarioBuilder();
        int castle = b.Structure("euro.castle", 0, 12, 5);
        int f1 = b.Unit("euro.footman", 0, 6, 5);
        int f2 = b.Unit("euro.footman", 0, 6, 6);
        b.Event(new ObjectInAreaEvent("near", All(f1, f2), Rect(11, 4, 15, 8)));
        b.Event(new ObjectExpressionEvent("loaded", All(castle), b.Expr("$cargo >= 2")));
        b.Rule("1", b.Action(new KeepRuleAction("k1")), b.Action(new ExpressionAction("w1", b.Expr("$near = near"))));
        b.Rule("1", b.Action(new KeepRuleAction("k2")), b.Action(new ExpressionAction("w2", b.Expr("$loaded = loaded"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(0, d.Variable("near"));
        Assert.Equal(0, d.Variable("loaded"));
        d.Order(0, CommandKind.Board, [d.Object(f1), d.Object(f2)], target: d.Object(castle));
        d.Seconds(15);
        Assert.True(d.World.IsHeld(d.Object(f1)) && d.World.IsHeld(d.Object(f2)));
        Assert.Equal(1, d.Variable("near"));
        Assert.Equal(1, d.Variable("loaded"));
        d.Order(0, CommandKind.Unload, [d.Object(castle)], Tiles(6), Tiles(6));
        d.Seconds(1);
        Assert.Equal(0, d.Variable("loaded"));
    }

    [Fact]
    [Trait("Rule", "ECO-024")]
    [Trait("Rule", "ECO-023")]
    public void A_peasant_given_to_another_kingdom_keeps_its_load_and_the_default_order_delivers_it_at_the_store()
    {
        // bigisland: Azim takes the gold for a kingdom without a store; "Azim changes kingdom back" and "Azim leaves gold"
        // (a default order on the village's storage) put it into the village's stockpile.
        var b = new ScenarioBuilder();
        b.Structure("euro.castle", 0, 2, 2);
        int storage = b.Structure("euro.storage", 0, 30, 2);
        int peon = b.Unit("euro.peon", 1, 8, 8);
        int gold = b.Unit("comm.resitem", MapObject.Nature, 9, 8);
        b.Event(new ObjectExistEvent("lies", All(gold)));
        b.Rule("!lies", b.Action(new ChangeObjectOwnerAction("back", All(peon), 1)), b.Action(new ObjectOrderAction("leave", All(peon), new ObjectArea(storage, 0), "default")));
        using TriggerDriver d = b.Start();
        int id = d.World.Ruleset.ResourceId("gold");
        int before = d.World.Players[0].Resources[id];
        EntityId azim = d.Object(peon);
        d.Order(1, CommandKind.PickItem, [azim], target: d.Object(gold));
        d.Run(1);
        int ticks = 0;
        while (d.World.Identities.Get(azim).Owner != 0 && ticks++ < 2000)
        {
            d.Step();
        }

        Assert.Equal(0, d.World.Identities.Get(azim).Owner);
        Assert.Equal(100, d.World.Workers.Get(azim).CarryAmount); // the load stays with it
        d.Seconds(30);
        Assert.Equal(0, d.World.Workers.Get(azim).CarryAmount);
        Assert.Equal(before + 100, d.World.Players[0].Resources[id]);
    }
}
