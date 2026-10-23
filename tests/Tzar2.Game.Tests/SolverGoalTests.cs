using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Rules;
using Tzar2.Game.Campaigns;
using Tzar2.Tests.Scenario;
using Xunit;
using Xunit.Abstractions;
using static Tzar2.Tests.Scenario.ScenarioBuilder;

namespace Tzar2.Game.Tests;

/// <summary>The solver's goal discovery (ADR 0135) on invented scenarios.</summary>
public sealed class SolverGoalTests(ITestOutputHelper output)
{
    private static readonly Ruleset Rules = SyntheticLegacyContent.CompileRuleset();

    private static CampaignReport Run(ScenarioBuilder b, AutoplayOptions options)
    {
        var campaign = new CampaignData("solver", "Solver", "", new[] { new CampaignMap("only", "Only", "only", "", true) }, EquatableArray<CampaignEdge>.Empty);
        var source = new InMemoryCampaignSource(campaign, new Dictionary<string, MissionContent> { ["only"] = new MissionContent(b.Map(), b.Scenario()) });
        return CampaignAutoplay.Run(source, Rules, options);
    }

    [Fact]
    public void A_rule_gated_on_a_variable_first_pursues_the_conversation_that_sets_it()
    {
        // The gate is near; the stone whose conversation sets $told is far. Walking to the gate first would be wasted:
        // the win rule needs $told, so the solver walks to the stone, the talk sets it, then the gate wins.
        var b = new ScenarioBuilder();
        int hero = b.Unit("euro.footman", 0, 4, 8);
        b.Unit("euro.peon", 1, 44, 28);
        int stone = b.Unit("euro.peon", 0, 40, 8);
        int talk = b.Conversation(Phrase(b, "rune", true, "An invented rune glows.", result: "$told = 1", source: stone));
        b.Event(new ObjectInAreaEvent("AtGate", All(hero), Rect(12, 7, 14, 9)));
        b.Event(new ObjectInAreaEvent("AtStone", All(hero), new ObjectArea(stone, RadiusTiles: 2)));
        b.Rule("$told && AtGate", b.Action(new PlayerWinAction("win", PlayerMasks.Of(1), null)));
        b.Rule("AtStone", b.Action(new ConversationAction("read", talk)));

        CampaignReport report = Run(b, new AutoplayOptions { ComputerAi = false, CheatAssist = false, SingleMission = true, MaxTicksPerMission = 6000 });
        output.WriteLine(report.Missions[0].Trace);
        Assert.True(report.Missions[0].Completed, $"{report.Missions[0].Result} {report.Missions[0].Ticks} {report.Missions[0].Trace}");
        Assert.Equal("play", report.Missions[0].Assist);
    }

    [Fact]
    public void A_walled_in_goal_is_breached()
    {
        // The goal area is a pocket closed by an enemy wall ring: the solver attacks the wall and walks in.
        var b = new ScenarioBuilder();
        int hero = b.Unit("euro.knight", 0, 4, 8);
        for (int i = 0; i < 8; i++)
        {
            b.Unit("euro.knight", 0, 6 + (i % 2), 4 + i); // the army helps
        }

        for (int x = 18; x <= 24; x++)
        {
            b.Structure("comm.wall_stone", 1, x, 5);
            b.Structure("comm.wall_stone", 1, x, 11);
        }

        for (int y = 6; y <= 10; y++)
        {
            b.Structure("comm.wall_stone", 1, 18, y);
            b.Structure("comm.wall_stone", 1, 24, y);
        }

        b.Unit("euro.peon", 1, 44, 28);
        b.Event(new ObjectInAreaEvent("Inside", All(hero), Rect(20, 7, 22, 9)));
        b.Rule("Inside", b.Action(new PlayerWinAction("win", PlayerMasks.Of(1), null)));

        CampaignReport report = Run(b, new AutoplayOptions { ComputerAi = false, CheatAssist = false, SingleMission = true, MaxTicksPerMission = 24000 });
        output.WriteLine(report.Missions[0].Trace);
        Assert.True(report.Missions[0].Completed, $"{report.Missions[0].Result} {report.Missions[0].Ticks} {report.Missions[0].Trace}");
        Assert.Contains("breach", report.Missions[0].Trace, System.StringComparison.Ordinal);
    }

    [Fact]
    public void An_enemy_a_pending_rule_needs_alive_is_not_cleared_before_that_rule_fires()
    {
        // v_02: the High Priest talks only while the mages live ("AtPriest && !Killed"), and the win needs both the talk
        // and the mages dead. Cheat assistance clears the enemies early; it must spare the mage until the talk is done.
        var b = new ScenarioBuilder { Width = 128 };
        int hero = b.Unit("euro.footman", 0, 4, 8);
        int mage = b.Unit("euro.peon", 1, 60, 28);
        b.Event(new ObjectInAreaEvent("AtPriest", All(hero), Rect(118, 7, 120, 9))); // a long walk: the clearing comes first
        b.Event(new ObjectDestroyedEvent("Killed", All(mage)));
        b.Rule("1", b.Action(new ChangePlayerStatusAction("stays", 2, CannotWin: false, CannotLose: true))); // no win by conquest
        b.Rule("AtPriest && !Killed", b.Action(new ExpressionAction("talk", b.Expr("$talked = 1"))));
        b.Rule("$talked && Killed", b.Action(new PlayerWinAction("win", PlayerMasks.Of(1), null)));

        CampaignReport report = Run(b, new AutoplayOptions { ComputerAi = false, SingleMission = true, ClearAfterTicks = 40, ReinforceAfterTicks = 1_000_000, MaxTicksPerMission = 12000 });
        output.WriteLine(report.Missions[0].Trace);
        Assert.True(report.Missions[0].Completed, $"{report.Missions[0].Result} {report.Missions[0].Ticks} {report.Missions[0].Trace}");
        Assert.Contains("talked=1", report.Missions[0].Trace, System.StringComparison.Ordinal);
    }
}
