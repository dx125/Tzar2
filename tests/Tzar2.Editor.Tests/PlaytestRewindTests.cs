using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Editing;
using Tzar2.Game.Session;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>Rewind and the fast speeds of a playtest (ADR 0285).</summary>
public class PlaytestRewindTests
{
    private const int Tile = MapData.UnitsPerTile;

    // A kingdom against a computer kingdom, with a trigger clock: the AI, the triggers and the units all have state.
    private static MapDocument Mission()
    {
        MapDocument doc = MapDocument.CreateEmpty("Rewind", 40, 24, Fixtures.RulesFor(null));
        doc.Apply("Objects", s => s
            .Add(new MapObject("euro.footman", 0, (4 * Tile) + 1024, (10 * Tile) + 1024), null, out _)
            .Add(new MapObject("euro.peon", 0, (6 * Tile) + 1024, (12 * Tile) + 1024), null, out _)
            .Add(new MapObject("euro.peon", 1, (30 * Tile) + 1024, (10 * Tile) + 1024), null, out _));
        doc.Apply("Scenario", s => s.WithScenario(m => m
            .WithKingdom(ScenarioModel.DefaultKingdom(1) with { StartTileX = 4, StartTileY = 10 })
            .WithKingdom(ScenarioModel.DefaultKingdom(2) with { StartTileX = 30, StartTileY = 10 })
            .AddAction(new SetTimerAction("Clock", ScenarioModel.Source("30000"), true), out _)
            .AddAction(new ExpressionAction("Mark", ScenarioModel.Source("$Done = 1")), out _)
            .AddRule(new RuleDef("start", ScenarioModel.Source("1"), new[] { 0 }), out _)
            .AddRule(new RuleDef("ring", ScenarioModel.Source("Clock == 0 && $Done == 0"), new[] { 1 }), out _)));
        return doc;
    }

    // The state hash after each tick, by the tick that ran.
    private static Dictionary<int, ulong> Play(GameSession session, int ticks)
    {
        var hashes = new Dictionary<int, ulong>();
        void Record(GameSession s) => hashes[s.LastTickResult.Tick.Value] = s.LastTickResult.StateHash;
        session.AfterTick += Record;
        session.Step(ticks);
        session.AfterTick -= Record;
        return hashes;
    }

    private static string Ticks(MatchRewind rewind) => string.Join(' ', rewind.Points);

    [Fact]
    public void A_rewound_playtest_plays_on_exactly_as_it_did_from_that_point()
    {
        using GameSession first = Playtest.Start(Mission(), Fixtures.Classic, new PlaytestOptions());
        using var rewind = new MatchRewind(first);
        Assert.Equal(200, rewind.IntervalTicks); // 10 s of 50 ms ticks
        Dictionary<int, ulong> played = Play(first, 1000);
        Assert.Equal("0 200 400 600 800 1000", Ticks(rewind));
        ulong at1000 = first.Simulation.ComputeFullHash();

        using GameSession second = rewind.RewindTo(450)!;
        Assert.Same(second, rewind.Session);
        Assert.Equal(1, rewind.Generation);
        Assert.Equal(400, rewind.Tick);
        Assert.Equal(400, second.Simulation.World.Tick.Value);
        Assert.Equal("0 200 400", Ticks(rewind)); // the abandoned future's points are gone

        Dictionary<int, ulong> replayed = Play(second, 600);
        Assert.Equal(1000, rewind.Tick);
        for (int tick = 400; tick < 1000; tick++)
        {
            Assert.Equal(played[tick], replayed[tick]);
        }

        Assert.Equal(at1000, second.Simulation.ComputeFullHash());
        Assert.Equal("0 200 400 600 800 1000", Ticks(rewind));
    }

    [Fact]
    public void Back_goes_to_the_previous_point_and_back_by_a_minute_stops_at_the_start()
    {
        using GameSession session = Playtest.Start(Mission(), Fixtures.Classic, new PlaytestOptions());
        using var rewind = new MatchRewind(session);
        Assert.False(rewind.CanGoBack);
        Assert.Null(rewind.Back());
        session.Step(450);
        Assert.True(rewind.CanGoBack);

        using GameSession a = rewind.Back()!;
        Assert.Equal(400, rewind.Tick);

        // Just after a point, Back skips it: pressed again it goes further back.
        using GameSession b = rewind.Back()!;
        Assert.Equal(200, rewind.Tick);
        b.Step(10);

        // A minute back from 0:10.5 is before the start: the start.
        using GameSession c = rewind.BackBy(60)!;
        Assert.Equal(0, rewind.Tick);
        Assert.Null(rewind.BackBy(60));
        Assert.Equal(3, rewind.Generation);
    }

    [Fact]
    public void A_rewind_keeps_the_speed_the_pause_and_the_view()
    {
        using GameSession session = Playtest.Start(Mission(), Fixtures.Classic, new PlaytestOptions(RevealMap: true));
        session.SpeedIndex = 99;
        Assert.Equal(300, session.SpeedPercent); // the original's fastest without the testing tools

        using var rewind = new MatchRewind(session);
        Assert.True(session.FastSpeeds);
        session.SpeedIndex = 99;
        Assert.Equal(2000, session.SpeedPercent);
        session.Paused = true;
        session.View(1);
        session.Step(250);

        using GameSession next = rewind.Back()!;
        Assert.True(next.FastSpeeds);
        Assert.Equal(2000, next.SpeedPercent);
        Assert.True(next.Paused);
        Assert.Equal(1, next.ViewIndex);
        Assert.Equal(session.FogOn, next.FogOn);
        next.FastSpeeds = false;
        Assert.Equal(300, next.SpeedPercent);
    }

    [Fact]
    public void Older_points_thin_out_to_a_minute_apart_and_to_the_budget()
    {
        using GameSession session = Playtest.Start(Mission(), Fixtures.Classic, new PlaytestOptions(ComputerAi: false));
        using var rewind = new MatchRewind(session);
        session.Step(6000); // 5 minutes

        // The latest 3 minutes every 10 s (from 2:00 = tick 2400), before that every minute, and the start.
        string dense = string.Join(' ', Enumerable.Range(12, 19).Select(i => i * 200));
        Assert.Equal("0 1200 " + dense, Ticks(rewind));
        Assert.True(rewind.Bytes > 0);

        using GameSession small = Playtest.Start(Mission(), Fixtures.Classic, new PlaytestOptions(ComputerAi: false));
        using var tight = new MatchRewind(small, budgetBytes: 1);
        small.Step(6000);
        Assert.Equal("0 " + dense, Ticks(tight)); // over the budget only the start and the latest window stay
    }

    [Fact]
    public void Rewind_needs_a_single_player_session_that_has_not_started()
    {
        using GameSession session = Playtest.Start(Mission(), Fixtures.Classic, new PlaytestOptions());
        session.Start();
        try
        {
            Assert.Throws<InvalidOperationException>(() => new MatchRewind(session));
        }
        finally
        {
            session.Stop();
        }
    }

    [Fact]
    public void The_trigger_debugger_follows_a_rewind()
    {
        using GameSession session = Playtest.Start(Mission(), Fixtures.Classic, new PlaytestOptions());
        using var rewind = new MatchRewind(session);
        using var debugger = new TriggerDebugger(session);
        debugger.BreakOnRule(1, true);
        rewind.Rewound += debugger.MoveTo;
        session.Step(650);
        Assert.Contains(debugger.Log, l => l.Tick >= 400);

        using GameSession next = rewind.RewindTo(400)!;
        Assert.Same(next, debugger.Session);
        Assert.True(debugger.HasRuleBreak(1));
        Assert.Equal(400, debugger.Watch.Tick);
        Assert.DoesNotContain(debugger.Log, l => l.Tick > 400);
        Assert.Contains(debugger.Log, l => l.Text == "rewound to tick 400");
        Assert.All(debugger.Watch.Rules, r => Assert.Null(r.LastValue));
        Assert.True(next.Paused); // the breakpoint had paused the match; the rewind keeps it paused
        debugger.Continue();

        // It watches the new session: the clock (30 s) runs out at 0:30 and the breakpoint pauses it.
        for (int i = 0; i < 400 && !next.Paused; i++)
        {
            next.Step(1);
        }

        Assert.True(next.Paused);
        Assert.Contains("rule 'ring' fired", debugger.Hit, StringComparison.Ordinal);
    }

    [Fact]
    public void The_trigger_history_records_what_fired_and_a_rewind_to_it_plays_it_again()
    {
        using GameSession session = Playtest.Start(Mission(), Fixtures.Classic, new PlaytestOptions());
        using var rewind = new MatchRewind(session);
        using var debugger = new TriggerDebugger(session);
        rewind.Rewound += debugger.MoveTo;
        session.Step(700);
        DebugFiring ring = Assert.Single(debugger.History, f => !f.IsAction && f.Name == "ring");
        Assert.Contains(debugger.History, f => f.IsAction && f.Name == "Clock");
        Assert.InRange(ring.Tick, 590, 640); // the 30 s clock

        // The history's double-click: back to the point before it; what came after is gone and plays again.
        using GameSession back = rewind.RewindTo(ring.Tick - 1)!;
        Assert.DoesNotContain(debugger.History, f => f.Tick >= rewind.Tick);
        back.Step(ring.Tick - rewind.Tick + 25);
        Assert.Equal(ring.Tick, Assert.Single(debugger.History, f => f.Name == "ring").Tick);
    }
}
