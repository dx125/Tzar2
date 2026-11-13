using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Editing;
using Tzar2.Game.Scenario;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>Tzar2's own actions: patrol routes, reinforcement waves and camera paths (ADR 0293).</summary>
public class OwnActionTests
{
    private const int Tile = MapData.UnitsPerTile;

    // A footman of kingdom 1 at (5, 10) and a peasant of kingdom 2 far off; the actions start at once.
    private static MapDocument Map(params ActionDef[] actions)
    {
        MapDocument doc = MapDocument.CreateEmpty("Own", 48, 24, Fixtures.RulesFor(null));
        doc.Apply("Objects", s => s
            .Add(new MapObject("euro.footman", 0, (5 * Tile) + 1024, (10 * Tile) + 1024), null, out _)
            .Add(new MapObject("euro.peon", 1, (44 * Tile) + 1024, (20 * Tile) + 1024), null, out _));
        doc.Apply("Scenario", s => s.WithScenario(m =>
        {
            m = m.WithKingdom(ScenarioModel.DefaultKingdom(1) with { StartTileX = 5, StartTileY = 10 })
                .WithKingdom(ScenarioModel.DefaultKingdom(2) with { StartTileX = 44, StartTileY = 20, EconomyAi = false, WarAi = false });
            var indices = new List<int>();
            foreach (ActionDef a in actions)
            {
                m = m.AddAction(a, out int i);
                indices.Add(i);
            }

            return m.AddRule(new RuleDef("go", ScenarioModel.Source("1"), indices.ToArray()), out _);
        }));
        return doc;
    }

    private static EquatableArray<AreaSpec> Points(params (int X, int Y)[] tiles) => EquatableArray<AreaSpec>.From(tiles.Select(t => (AreaSpec)new RectArea(t.X, t.Y, t.X, t.Y)));

    private static int TileX(GameSession s, int objectIndex) => s.Simulation.World.Positions.Get(s.Scenario!.EntityOf(objectIndex)).X / Tile;

    private static NamedObjects Footman => new(EquatableArray<int>.From([1]), Quantifier.All);

    [Fact]
    public void A_patrol_walks_its_route_again_and_again()
    {
        MapDocument doc = Map(new PatrolRouteAction("Walk", Footman, Points((5, 10), (24, 10)), Loop: true));
        int footman = doc.State.Objects[0].Id;
        Assert.Equal(1, footman);
        using GameSession session = Playtest.Start(doc, Fixtures.Classic, new PlaytestOptions(Kingdom: 1, ComputerAi: false));
        int farthest = 0, back = int.MaxValue;
        for (int t = 0; t < 120; t++)
        {
            session.Step(20);
            int x = TileX(session, 0);
            farthest = Math.Max(farthest, x);
            if (farthest >= 22)
            {
                back = Math.Min(back, x);
            }
        }

        Assert.True(farthest >= 22, $"it never reached the far point (got to {farthest})");
        Assert.True(back <= 7, $"it never came back to the first point (closest {back})");
    }

    [Fact]
    public void A_patrol_without_a_loop_stops_at_its_last_point()
    {
        using GameSession session = Playtest.Start(Map(new PatrolRouteAction("Walk", Footman, Points((24, 10)), Loop: false)), Fixtures.Classic, new PlaytestOptions(Kingdom: 1, ComputerAi: false));
        session.Step(1600);
        Assert.InRange(TileX(session, 0), 22, 26);
    }

    [Fact]
    public void Reinforcement_waves_come_on_time_and_march_to_their_target()
    {
        var waves = new ReinforcementWavesAction("Help", "euro.footman", 2, 3, new RectArea(40, 4, 43, 7), 3, 5_000, new RectArea(30, 5, 30, 5));
        using GameSession session = Playtest.Start(Map(waves), Fixtures.Classic, new PlaytestOptions(Kingdom: 1, ComputerAi: false));
        int Footmen() => session.Simulation.World.Identities.View.ToArray().Count(i => i.Owner == 1 && Fixtures.Classic.EntityTypes[i.TypeId].Key == "euro.footman");
        session.Step(30);
        Assert.Equal(3, Footmen());
        session.Step(100); // 6.5 s
        Assert.Equal(6, Footmen());
        session.Step(100);
        Assert.Equal(9, Footmen());
        session.Step(300);
        Assert.Equal(9, Footmen()); // three waves only

        // They marched west towards (30, 5).
        World w = session.Simulation.World;
        int marched = w.Identities.View.ToArray().Select((info, i) => (info, i)).Count(p => p.info.Owner == 1 && Fixtures.Classic.EntityTypes[p.info.TypeId].Key == "euro.footman" && w.Positions.Get(w.Identities.Entities[p.i]).X / Tile <= 34);
        Assert.True(marched >= 3, $"only {marched} footmen marched");
    }

    [Fact]
    public void A_match_with_patrols_and_waves_rewinds_and_plays_on_exactly()
    {
        MapDocument doc = Map(
            new PatrolRouteAction("Walk", Footman, Points((5, 10), (20, 10)), Loop: true),
            new ReinforcementWavesAction("Help", "euro.footman", 2, 2, new RectArea(40, 4, 42, 6), 4, 7_000, new RectArea(20, 6, 20, 6)));
        using GameSession first = Playtest.Start(doc, Fixtures.Classic, new PlaytestOptions(Kingdom: 1, ComputerAi: false));
        using var rewind = new MatchRewind(first);
        var played = new Dictionary<int, ulong>();
        first.AfterTick += s => played[s.LastTickResult.Tick.Value] = s.LastTickResult.StateHash;
        first.Step(800);

        // The point at 0:20 holds the patrol and the waves still to come (trigger state version 5).
        using GameSession second = rewind.RewindTo(400)!;
        var replayed = new Dictionary<int, ulong>();
        second.AfterTick += s => replayed[s.LastTickResult.Tick.Value] = s.LastTickResult.StateHash;
        second.Step(400);
        for (int tick = 400; tick < 800; tick++)
        {
            Assert.Equal(played[tick], replayed[tick]);
        }
    }

    [Fact]
    public void A_camera_path_tells_the_presentation_its_points_in_order()
    {
        var camera = new CameraPathAction("Show", EquatableArray<AreaSpec>.From([new RectArea(20, 8, 20, 8), new ObjectArea(1, 0)]), 2_500);
        using GameSession session = Playtest.Start(Map(camera), Fixtures.Classic, new PlaytestOptions(Kingdom: 1, ComputerAi: false));
        session.Step(30);
        var notices = new List<ScenarioNotice>();
        session.DrainNotices(notices);
        ScenarioNotice[] points = [.. notices.Where(n => n.Kind == ScenarioNoticeKind.CameraPoint)];
        Assert.Equal(2, points.Length);
        Assert.Equal(((20 * Tile) + (Tile / 2), (8 * Tile) + (Tile / 2), 2_500), (points[0].X, points[0].Y, points[0].DurationMs));
        Assert.Equal(5, points[1].X / Tile); // the footman where it stands
    }

    [Fact]
    public void The_own_actions_save_reopen_and_are_marked_as_not_the_originals()
    {
        MapDocument doc = Map(
            new PatrolRouteAction("Walk", Footman, Points((5, 10), (20, 10)), Loop: true),
            new ReinforcementWavesAction("Help", "euro.nobody", 2, 2, new RectArea(40, 4, 42, 6), 4, 7_000, null),
            new CameraPathAction("Show", EquatableArray<AreaSpec>.Empty, 1_000));
        MapDocument back = MapPackage.Read(MapPackage.Write(doc), Fixtures.RulesFor, "own.t2map");
        Assert.Equal(doc.State.Scenario.Actions, back.State.Scenario.Actions);
        Assert.Equal(doc.BuildScenario().Actions, back.BuildScenario().Actions);

        ValidationResult v = MapValidator.Validate(doc, new ValidationOptions { Ruleset = Fixtures.Classic });
        Assert.Equal(3, v.Issues.Count(i => i.Code == "action.own" && i.Severity == ValidationSeverity.Info));
        Assert.Contains(v.Issues, i => i.Code == "action.type" && i.Message.Contains("euro.nobody", StringComparison.Ordinal));
        Assert.Contains(v.Issues, i => i.Code == "action.route" && i.Message.Contains("Show", StringComparison.Ordinal));
        Assert.All(doc.State.Scenario.Actions.Items, a => Assert.EndsWith(TriggerSchema.OwnMark, TriggerSchema.Of(a).Title, StringComparison.Ordinal));

        // Deleting the patrolling object takes it out of the route's objects.
        doc.Apply("Delete", s => s.Remove([1]));
        Assert.Empty(((NamedObjects)((PatrolRouteAction)doc.State.Scenario.Actions[0]).Objects).ObjectIndices.Items);
    }
}
