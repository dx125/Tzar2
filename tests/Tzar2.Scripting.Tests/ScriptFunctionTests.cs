using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Tests.Scenario;
using Xunit;
using static Tzar2.Tests.Scenario.ScenarioBuilder;

namespace Tzar2.Scripting.Tests;

/// <summary>The functions and actions the original's campaigns call, as ReTzar reads the original (ADR 0288).</summary>
public class ScriptFunctionTests
{
    private const int Tile = Tzar2.Content.Compiled.MapData.UnitsPerTile;

    private static int Index(TriggerDriver d, string variable) => d.ScenarioData.Variables.Items.IndexOf(variable);

    [Fact]
    [Trait("Rule", "SCN-009")]
    public void Setai_gives_a_computer_kingdom_none_economy_war_or_its_whole_ai()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Unit("euro.peon", 1, 30, 20);
        b.Rule("1", b.Action(new ExpressionAction("ai", b.Expr("$war = @setai(2, 2); $mine = @setai(1, 0); $bad = @setai(9, 1) + @setai(2, 7)"))));
        b.Rule("$whole", b.Action(new ExpressionAction("full", b.Expr("@setai(2, 3)"))));
        using TriggerDriver d = b.Start(options: TriggerOptions.Default with { ResponderMask = 1 });
        d.Step();
        const PlayerFlags Halves = PlayerFlags.AiHalves | PlayerFlags.AssistEconomy | PlayerFlags.AssistWar;
        Assert.Equal(PlayerFlags.AiHalves | PlayerFlags.AssistWar, d.World.Players[1].Flags & Halves);
        Assert.Equal(PlayerFlags.None, d.World.Players[0].Flags & Halves); // the human's own computer is left alone
        Assert.Equal((1, 1, 0), (d.Variable("war"), d.Variable("mine"), d.Variable("bad")));

        d.State.Variables[Index(d, "whole")] = 1;
        d.Seconds(2);
        Assert.Equal(PlayerFlags.None, d.World.Players[1].Flags & Halves);
    }

    [Fact]
    [Trait("Rule", "SCN-011")]
    public void Settime_sets_the_day_clock_to_the_hour_and_it_runs_on()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Rule("$night", b.Action(new ExpressionAction("night", b.Expr("$ok = @settime(22); $hour = @gettime(); $bad = @settime(30)"))));
        using TriggerDriver d = b.Start(configure: c => c with { DayNight = true });
        d.Seconds(3);
        int before = d.World.MinuteAt(d.World.Tick.Value);
        d.State.Variables[Index(d, "night")] = 1;
        d.Seconds(2);
        Assert.Equal((1, 22, 0), (d.Variable("ok"), d.Variable("hour"), d.Variable("bad")));
        int now = d.World.MinuteAt(d.World.Tick.Value);
        Assert.InRange(now, 22 * 60, (22 * 60) + 60);
        Assert.NotEqual(before / 60, now / 60);

        using TriggerDriver restored = d.Restore();
        Assert.Equal(now, restored.World.MinuteAt(restored.World.Tick.Value));
    }

    [Fact]
    [Trait("Rule", "SCN-012")]
    public void God_keeps_an_object_from_harm_and_build_finishes_a_site()
    {
        var b = new ScenarioBuilder();
        int hero = b.Unit("euro.footman", 0, 10, 10);
        int foe = b.Unit("euro.knight", 1, 11, 10);
        int peon = b.Unit("euro.peon", 1, 30, 20);
        b.Rule("1", b.Action(new SetObjectPropertiesAction("immortal", All(hero), b.Expr("$ok = @god(1)"))), b.Action(new ExpressionAction("none", b.Expr("$noobject = @god(1)"))));
        b.Rule("$finish", b.Action(new SetObjectPropertiesAction("finish", new CategoryObjects(ObjectCategory.Buildings, PlayerMasks.Of(2), CountComparison.AtLeast, 1, Quantifier.Any), b.Expr("@build()"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal((1, 0), (d.Variable("ok"), d.Variable("noobject")));
        Assert.True(d.Triggers.IsGod(d.Object(hero)));
        int health = d.World.Healths.Get(d.Object(hero)).Current;
        d.Order(1, CommandKind.Attack, [d.Object(foe)], target: d.Object(hero));
        d.Seconds(10);
        Assert.Equal(health, d.World.Healths.Get(d.Object(hero)).Current);
        Assert.True(HurtWithoutGod()); // the same fight hurts a hero without it

        int house = d.World.Ruleset.EntityTypeId("euro.house");
        d.Order(1, CommandKind.Build, [d.Object(peon)], 34 * Tile, 20 * Tile, arg0: house);
        d.Step();
        Assert.Equal(1, d.World.Constructions.Count);
        d.State.Variables[Index(d, "finish")] = 1;
        d.Seconds(2);
        Assert.Equal(0, d.World.Constructions.Count);
        Assert.Contains(d.Of(SimEventKind.ConstructionCompleted), e => e.Player == 1);
    }

    // The fight of the god test without @god: the hero takes blows.
    private static bool HurtWithoutGod()
    {
        var b = new ScenarioBuilder();
        int hero = b.Unit("euro.footman", 0, 10, 10);
        int foe = b.Unit("euro.knight", 1, 11, 10);
        using TriggerDriver d = b.Start();
        d.Step();
        int health = d.World.Healths.Get(d.Object(hero)).Current;
        d.Order(1, CommandKind.Attack, [d.Object(foe)], target: d.Object(hero));
        d.Seconds(10);
        return !d.World.Entities.IsAlive(d.Object(hero)) || d.World.Healths.Get(d.Object(hero)).Current < health;
    }

    [Fact]
    [Trait("Rule", "SCN-013")]
    public void Rnd_with_reversed_bounds_gives_the_first_and_the_small_functions_give_what_the_original_gives()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Unit("euro.peon", 1, 30, 20);
        b.Rule("1", b.Action(new ExpressionAction("f", b.Expr("$r = @rnd(9, 2); $s = @str(7); $k = @kingdomcanrescue(2, 0); $nok = @kingdomcanrescue(9, 0); $dip = @getdiplomacy(1, 2)"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal((9, 1, 1, 0, 0x10), (d.Variable("r"), d.Variable("s"), d.Variable("k"), d.Variable("nok"), d.Variable("dip")));
        Assert.True((d.World.Players[1].Flags & PlayerFlags.NoRescue) != 0);
    }

    [Fact]
    [Trait("Rule", "SCN-014")]
    public void Explore_area_of_no_time_shows_the_area_four_seconds()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 2, 2);
        b.Rule("1", b.Action(new ExploreAreaAction("glance", 0, true, Rect(36, 20, 42, 26))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.True(d.World.Visibility.IsVisible(0, 39, 23));
        d.Seconds(3);
        Assert.True(d.World.Visibility.IsVisible(0, 39, 23));
        d.Seconds(2);
        Assert.False(d.World.Visibility.IsVisible(0, 39, 23));
    }
}
