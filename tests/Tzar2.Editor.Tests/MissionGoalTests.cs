using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Editing;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The mission goal wizard (ADR 0290): each goal's rules work, as the acceptance's bot shows.</summary>
public class MissionGoalTests
{
    private const int Tile = MapData.UnitsPerTile;

    // A hero (object 0) and a peon of the player, an enemy peon; no automatic victory, so only the goal wins.
    private static MapDocument Map(int gold = 0)
    {
        MapDocument doc = MapDocument.CreateEmpty("Goals", 32, 20, Fixtures.RulesFor(null));
        doc.Apply("Objects", s => s
            .Add(new MapObject("euro.footman", 0, (4 * Tile) + 1024, (10 * Tile) + 1024), new ObjectInfo("Hero", "", ObjectFlags.None), out _)
            .Add(new MapObject("euro.peon", 0, (5 * Tile) + 1024, (12 * Tile) + 1024), null, out _)
            .Add(new MapObject("euro.peon", 1, (26 * Tile) + 1024, (10 * Tile) + 1024), null, out _));
        doc.Apply("Scenario", s => s.WithScenario(m => (m with { Properties = m.Properties with { Victory = Tzar2.Content.Compiled.Scenario.VictoryCondition.NotSpecified } })
            .WithKingdom(ScenarioModel.DefaultKingdom(1) with { StartTileX = 4, StartTileY = 10, Gold = gold })
            .WithKingdom(ScenarioModel.DefaultKingdom(2) with { StartTileX = 26, StartTileY = 10 })));
        return doc;
    }

    // The hero's id in the document (goals name objects by id; the scenario numbers them when it is built).
    private static int Hero(MapDocument doc) => doc.State.Objects.First(o => o.Info.Name == "Hero").Id;

    private static MapDocument With(MapDocument doc, MissionGoal goal)
    {
        doc.Apply("Goal", s => s.WithScenario(m => MissionGoals.Add(m, goal)));
        return doc;
    }

    private static PlayabilityRow Played(MapDocument doc)
    {
        Assert.DoesNotContain(MapValidator.Validate(doc, new ValidationOptions { Ruleset = Fixtures.Classic }).Issues, i => i.Severity >= ValidationSeverity.Error);
        return PlayabilityCheck.Map(doc, Fixtures.Classic, maxMinutesPerMission: 5).Rows[0];
    }

    [Fact]
    public void A_destroy_the_enemies_goal_is_won_by_play()
    {
        MapDocument doc = With(Map(), new MissionGoal(GoalKind.DestroyEnemies, "Drive the raiders out."));
        ScenarioData s = doc.BuildScenario();
        Assert.Contains(s.Rules, r => r.Name == "Goal 1 quest");
        Assert.Contains(s.Rules, r => r.Name == "Goal 1 done");
        PlayabilityRow row = Played(doc);
        Assert.True(row.Completed && row.ByPlay, row.How + " " + row.Trace);
    }

    [Fact]
    public void A_reach_the_area_goal_is_won_when_the_hero_walks_there()
    {
        MapDocument map = Map();
        MapDocument doc = With(map, new MissionGoal(GoalKind.ReachArea, "Bring the hero to the ford.", ObjectId: Hero(map), Area: new RectArea(14, 9, 16, 11)));
        PlayabilityRow row = Played(doc);
        Assert.True(row.Completed && row.ByPlay, row.How + " " + row.Trace);
    }

    [Fact]
    public void A_hold_out_goal_is_won_when_its_time_runs_out_and_numbers_follow_on()
    {
        MapDocument doc = With(Map(), new MissionGoal(GoalKind.HoldOut, "Hold the bridge for a minute.", Minutes: 1));
        ScenarioData s = With(Map(), new MissionGoal(GoalKind.HoldOut, "x")).BuildScenario();
        Assert.Contains(MissionGoals.Add(doc.State.Scenario, new MissionGoal(GoalKind.Gather, "Gather 300 gold.", Resource: "gold", Amount: 300)).Rules, r => r.Name == "Goal 2 done" && r.Condition.Source == "@gold(1) >= 300");
        PlayabilityRow row = Played(doc);
        Assert.True(row.Completed && row.ByPlay, row.How + " " + row.Trace);
        Assert.InRange(row.GameSeconds, 55, 70);
    }

    [Fact]
    public void A_gather_goal_is_won_when_the_stock_is_there()
    {
        PlayabilityRow row = Played(With(Map(gold: 500), new MissionGoal(GoalKind.Gather, string.Empty, Resource: "gold", Amount: 300)));
        Assert.True(row.Completed && row.ByPlay, row.How + " " + row.Trace);
        Assert.True(row.GameSeconds <= 3);
    }

    [Fact]
    public void A_keep_alive_goal_loses_when_the_hero_dies()
    {
        MapDocument map = Map();
        MapDocument doc = With(map, new MissionGoal(GoalKind.KeepAlive, "Keep the hero alive.", ObjectId: Hero(map)));
        using GameSession session = Playtest.Start(doc, Fixtures.Classic, new PlaytestOptions());
        session.Step(30);
        var kill = new CommandList();
        kill.Add(CommandKind.DebugKill, [session.Scenario!.EntityOf(0)]);
        session.Submit(kill);
        session.Step(60);
        Assert.True((session.Simulation.World.Players[0].Flags & PlayerFlags.Defeated) != 0);
    }

    [Fact]
    public void A_goal_without_what_it_needs_is_refused()
    {
        Assert.Throws<ArgumentException>(() => MissionGoals.Add(Map().State.Scenario, new MissionGoal(GoalKind.ReachArea, "x")));
        Assert.Throws<ArgumentException>(() => MissionGoals.Add(Map().State.Scenario, new MissionGoal(GoalKind.Gather, "x", Resource: "silver")));
        Assert.Throws<ArgumentException>(() => MissionGoals.Add(Map().State.Scenario, new MissionGoal(GoalKind.HoldOut, "x", Minutes: 0)));
    }
}
