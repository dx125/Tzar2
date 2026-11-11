using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Serialization;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Tests.Scenario;
using Xunit;
using static Tzar2.Tests.Scenario.ScenarioBuilder;

namespace Tzar2.Scripting.Tests;

/// <summary>The reserved variables a scenario reads or sets about the game itself (ADR 0287).</summary>
public class ScriptSwitchTests
{
    private static int Index(TriggerDriver d, string variable) => d.ScenarioData.Variables.Items.IndexOf(variable);

    [Fact]
    [Trait("Rule", "SCN-005")]
    public void Difficulty_reads_the_games_difficulty_and_a_write_changes_nothing()
    {
        var b = new ScenarioBuilder();
        int unit = b.Unit("euro.footman", 0, 6, 5);
        b.Event(new ObjectExpressionEvent("hard", All(unit), b.Expr("$difficulty == 2")));
        b.Rule("1", b.Action(new ExpressionAction("read", b.Expr("$d = $difficulty; $difficulty = 0; $after = $difficulty; $wave = (6 - $difficulty) * 60000; $h = hard"))));
        using TriggerDriver d = b.Start(options: TriggerOptions.Default with { Difficulty = 2 });
        d.Step();
        Assert.Equal((2, 2, 240_000, 1), (d.Variable("d"), d.Variable("after"), d.Variable("wave"), d.Variable("h")));
        Assert.Empty(d.Of(SimEventKind.ScenarioFault));

        Assert.Equal(1, TriggerOptions.Default.Difficulty);
        Assert.Equal(TriggerOptions.Default.Fingerprint(), (TriggerOptions.Default with { Difficulty = 1 }).Fingerprint());
        Assert.NotEqual(TriggerOptions.Default.Fingerprint(), (TriggerOptions.Default with { Difficulty = 2 }).Fingerprint());
    }

    [Fact]
    [Trait("Rule", "SCN-006")]
    public void Candelobjects_at_zero_forbids_the_human_kingdoms_to_delete_until_it_is_set_again()
    {
        var b = new ScenarioBuilder();
        int mine = b.Unit("euro.peon", 0, 3, 3);
        int theirs = b.Unit("euro.peon", 1, 30, 20);
        b.Rule("1", b.Action(new ExpressionAction("lock", b.Expr("$candelobjects = 0; $read = $candelobjects"))));
        b.Rule("$open", b.Action(new ExpressionAction("unlock", b.Expr("$candelobjects = 1"))));
        using TriggerDriver d = b.Start(options: TriggerOptions.Default with { ResponderMask = 1 });
        d.Step();
        Assert.True(d.State.NoDelete);
        Assert.Equal(0, d.Variable("read")); // written only: it reads 0

        d.Order(0, CommandKind.Delete, [d.Object(mine)]);
        d.Order(1, CommandKind.Delete, [d.Object(theirs)]); // a computer kingdom is not bound
        d.Step();
        SimEvent refused = Assert.Single(d.Of(SimEventKind.CommandRejected), e => e.Arg == (int)CommandRejection.ScenarioForbids);
        Assert.Equal(0, refused.Player);

        using (TriggerDriver restored = d.Restore())
        {
            Assert.True(restored.State.NoDelete);
        }

        d.State.Variables[Index(d, "open")] = 1;
        d.Seconds(2);
        Assert.False(d.State.NoDelete);
        d.Order(0, CommandKind.Delete, [d.Object(mine)]);
        d.Step();
        Assert.Single(d.Of(SimEventKind.CommandRejected), e => e.Arg == (int)CommandRejection.ScenarioForbids);
    }

    [Fact]
    [Trait("Rule", "SCN-007")]
    public void Canusediplomacy_at_zero_forbids_the_human_kingdoms_diplomacy_and_tribute()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Unit("euro.peon", 1, 30, 20);
        b.Rule("1", b.Action(new ExpressionAction("close", b.Expr("$canusediplomacy = 0"))));
        using TriggerDriver d = b.Start(options: TriggerOptions.Default with { ResponderMask = 1 });
        d.Step();
        Assert.True(d.State.NoDiplomacy);
        d.Order(0, CommandKind.SetDiplomacy, [], x: 1, arg0: (int)Stance.Ally);
        d.Order(0, CommandKind.Tribute, [], x: 1, arg0: d.World.Ruleset.ResourceId("gold"), arg1: 10);
        d.Order(1, CommandKind.SetDiplomacy, [], x: 0, arg0: (int)Stance.Ally);
        d.Step();
        Assert.Equal(2, d.Of(SimEventKind.CommandRejected).Count(e => e.Arg == (int)CommandRejection.ScenarioForbids && e.Player == 0));
        Assert.DoesNotContain(d.Of(SimEventKind.CommandRejected), e => e.Arg == (int)CommandRejection.ScenarioForbids && e.Player == 1);
    }

    [Fact]
    [Trait("Rule", "SCN-008")]
    public void Isselected_reads_whether_a_human_kingdom_has_the_object_selected()
    {
        var b = new ScenarioBuilder();
        int hero = b.Unit("euro.footman", 0, 6, 5);
        int peon = b.Unit("euro.peon", 0, 8, 5);
        b.Event(new ObjectExpressionEvent("chosen", All(hero), b.Expr("$isselected == 1")));
        b.Event(new ObjectExpressionEvent("flagged", All(hero), b.Expr("$flags == 1")));
        b.Rule("1", b.Action(new KeepRuleAction("k")), b.Action(new ExpressionAction("w", b.Expr("$chosen = chosen; $flagged = flagged"))));
        using TriggerDriver d = b.Start(options: TriggerOptions.Default with { ResponderMask = 1 });
        d.Step();
        Assert.Equal(0, d.Variable("chosen"));

        d.Order(0, CommandKind.ScenarioSelect, [d.Object(peon), d.Object(hero), d.Object(hero)]);
        d.Seconds(2);
        Assert.Equal((1, 1), (d.Variable("chosen"), d.Variable("flagged")));
        Assert.Equal(new[] { d.Object(hero).Value, d.Object(peon).Value }.Order().ToArray(), d.State.Selections[0].ToArray()); // once each, ascending

        using (TriggerDriver restored = d.Restore())
        {
            Assert.Equal(d.State.Selections[0], restored.State.Selections[0]);
        }

        d.Order(1, CommandKind.ScenarioSelect, [d.Object(hero)]); // a computer kingdom has no say
        d.Order(0, CommandKind.ScenarioSelect, []);
        d.Seconds(2);
        Assert.Equal(0, d.Variable("chosen"));
        Assert.Contains(d.Of(SimEventKind.CommandRejected), e => e.Arg == (int)CommandRejection.NoScenarioPrompt && e.Player == 1);
    }

    [Fact]
    public void A_state_without_switches_keeps_the_older_format_byte_for_byte()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Rule("$go", b.Action(new ExpressionAction("lock", b.Expr("$candelobjects = 0"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(2, Written(d.State)[0]);

        d.State.Variables[Index(d, "go")] = 1;
        d.Seconds(2);
        byte[] bytes = Written(d.State);
        Assert.Equal(3, bytes[0]);
        TriggerState back = TriggerState.ReadFrom(bytes, d.State.Shape);
        Assert.True(back.NoDelete);
        Assert.False(back.NoDiplomacy);
        Assert.Equal(bytes, Written(back));
    }

    private static byte[] Written(TriggerState state)
    {
        var w = new BinaryBufferWriter(256);
        state.WriteTo(w);
        return w.ToArray();
    }
}
