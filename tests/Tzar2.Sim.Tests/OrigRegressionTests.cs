using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Scenarios;
using Xunit;
using Xunit.Abstractions;
using static Tzar2.Sim.Tests.SimDriver;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Regression tests for the movement defects of the original (TDD §5 ORIG-01/02/03, §9.2 acceptance tests 1-4 and
/// 6, §22 item 9). They were written before the pathfinding stack and fail on the M2-A straight-line mover.
/// </summary>
public class OrigRegressionTests(ITestOutputHelper output)
{
    private const int TicksPerSecond = 20;

    /// <summary>A west/east map split by a river with a <paramref name="bridgeWidth"/>-tile bridge at rows y0...</summary>
    private static MapData RiverMap(int width, int height, int riverX0, int riverX1, int bridgeY0, int bridgeWidth)
    {
        var rows = new string[height];
        for (int y = 0; y < height; y++)
        {
            char[] row = new char[width];
            for (int x = 0; x < width; x++)
            {
                bool river = x >= riverX0 && x <= riverX1;
                bool bridge = y >= bridgeY0 && y < bridgeY0 + bridgeWidth;
                row[x] = river && !bridge ? '~' : '.';
            }

            rows[y] = new string(row);
        }

        return ScenarioMaps.FromAscii("river", rows);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // ORIG-01: corpses must never block terrain (§9.2 acceptance 1: kill 500 units on a 10×10-tile area that includes
    // a bridge, then move 100 units across it: no added cost and no stuck units).
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ORIG_01_mass_death_on_a_bridge_adds_no_cost_and_strands_no_unit()
    {
        MapData map = RiverMap(64, 40, 30, 33, bridgeY0: 19, bridgeWidth: 2);
        (List<Position[]> withCorpses, SimDriver a) = RunBridgeTraffic(map, killFirst: 500);
        (List<Position[]> clean, SimDriver b) = RunBridgeTraffic(map, killFirst: 0);
        using (a)
        using (b)
        {
            Assert.Equal(500, a.World.Decals.Count);
            Assert.Equal(b.World.Map.ObstacleVersion, a.World.Map.ObstacleVersion);
            Assert.True(a.World.Map.Blockers.SequenceEqual(b.World.Map.Blockers));

            // No added cost: the 100 movers behave identically, tick by tick, with and without 500 corpses.
            Assert.Equal(clean.Count, withCorpses.Count);
            for (int t = 0; t < clean.Count; t++)
            {
                Assert.Equal(clean[t], withCorpses[t]);
            }

            // No stuck units: everyone crossed and stopped; nobody gave up or was declared unreachable.
            Position[] final = withCorpses[^1];
            Assert.All(final, p => Assert.True(p.X > 34 * Tile, $"unit at tile ({p.X / Tile}, {p.Y / Tile}) did not cross"));
            Assert.Equal(0, a.Count(SimEventKind.Blocked));
            Assert.Equal(0, a.Count(SimEventKind.GoalUnreachable));
            output.WriteLine($"100 units crossed in {withCorpses.Count} ticks over 500 corpses");
        }
    }

    private static (List<Position[]> Trace, SimDriver Driver) RunBridgeTraffic(MapData map, int killFirst)
    {
        var d = new SimDriver(map, players: 2);
        var movers = new List<EntityId>();
        for (int i = 0; i < 100; i++)
        {
            movers.Add(d.Spawn(SyntheticScenario.Swordsman, 0, 6 + (i % 10), 14 + (i / 10)));
        }

        // The dead: 500 units of player 1 packed onto the 10×10 tiles around the bridge (x 27-36, y 15-24), land only.
        var victims = new List<EntityId>();
        for (int i = 0; victims.Count < killFirst; i++)
        {
            int tx = 27 + (i % 10);
            int ty = 15 + ((i / 10) % 10);
            if (d.World.Map.IsPassable(Locomotor.Foot, tx, ty))
            {
                victims.Add(d.Spawn(SyntheticScenario.Swordsman, 1, tx, ty, (i * 97 % 1500) - 750, (i * 61 % 1500) - 750));
            }
        }

        // They die through the debug kill command (combat calls the same World.Kill), in two batches.
        d.Order(1, CommandKind.DebugKill, victims.Take(250).ToArray());
        d.Step();
        d.Order(1, CommandKind.DebugKill, victims.Skip(250).ToArray());
        d.Step();

        d.MoveToTile(0, movers.ToArray(), 50, 20);
        var trace = new List<Position[]>();
        for (int t = 0; t < 120 * TicksPerSecond; t++)
        {
            d.Step();
            trace.Add(movers.Select(d.PositionOf).ToArray());
            if (!d.AnyMoving(movers))
            {
                break;
            }
        }

        return (trace, d);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // ORIG-02: group congestion. §9.2 acceptance 2 (200 mixed units to one point in open terrain: every unit stops
    // within 6 s of the first arrival, none still moving after 10 s) and 3 (150 units each way across a 2-cell-wide
    // bridge: no deadlock, throughput ≥ 80% of single-file capacity).
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ORIG_02_two_hundred_mixed_units_arrive_together_and_never_orbit()
    {
        MapData map = ScenarioMaps.FromAscii("open", Enumerable.Repeat(new string('.', 96), 96).ToArray());
        using var d = new SimDriver(map);
        var units = new List<EntityId>();
        for (int i = 0; i < 200; i++)
        {
            ushort type = i % 10 < 6 ? SyntheticScenario.Swordsman : i % 10 < 9 ? SyntheticScenario.Knight : SyntheticScenario.Catapult;
            // A loose blob, 14 columns × 15 rows at 0.75-tile spacing, centred on tile (20, 48).
            units.Add(d.World.Spawn(type, 0, (15 * Tile) + ((i % 14) * Tile * 3 / 4), (42 * Tile) + ((i / 14) * Tile * 3 / 4)));
        }

        d.MoveToTile(0, units.ToArray(), 70, 48);
        int firstArrival = -1;
        var lastMovingTick = new Dictionary<EntityId, int>();
        for (int t = 0; t < 180 * TicksPerSecond; t++)
        {
            d.Step();
            int tick = d.World.Tick.Value;
            if (firstArrival < 0 && d.Events.Any(e => e.Event.Kind == SimEventKind.Arrived))
            {
                firstArrival = tick;
            }

            foreach (EntityId u in units)
            {
                if (d.IsMoving(u))
                {
                    lastMovingTick[u] = tick;
                }
            }

            if (firstArrival >= 0 && tick > firstArrival + (10 * TicksPerSecond))
            {
                break;
            }
        }

        Assert.True(firstArrival > 0, "no unit arrived");
        int latestStop = lastMovingTick.Values.Max();
        output.WriteLine($"first arrival tick {firstArrival}, last unit stopped at tick {latestStop}");
        Assert.True(latestStop <= firstArrival + (6 * TicksPerSecond), $"a unit was still moving {latestStop - firstArrival} ticks after the first arrival");
        Assert.False(d.AnyMoving(units));

        // Everyone is near the goal and nobody stands on top of another unit.
        Position[] final = units.Select(d.PositionOf).ToArray();
        Assert.All(final, p => Assert.InRange(Math.Abs(p.X - TileCenter(70)) + Math.Abs(p.Y - TileCenter(48)), 0, 16 * Tile));
        int overlapping = 0;
        for (int i = 0; i < final.Length; i++)
        {
            for (int j = i + 1; j < final.Length; j++)
            {
                long dx = final[i].X - final[j].X;
                long dy = final[i].Y - final[j].Y;
                overlapping += (dx * dx) + (dy * dy) < 256L * 256 ? 1 : 0;
            }
        }

        Assert.Equal(0, overlapping);
        Assert.Equal(0, d.Count(SimEventKind.Blocked));
    }

    [Fact]
    public void ORIG_02_opposing_columns_cross_a_narrow_bridge_without_deadlock()
    {
        // Bridge: one tile (= 2 occupancy cells) wide, 6 tiles long.
        const int BridgeY = 20;
        MapData map = RiverMap(72, 40, 33, 38, bridgeY0: BridgeY, bridgeWidth: 1);
        using var d = new SimDriver(map);
        var west = new List<EntityId>();
        var east = new List<EntityId>();
        for (int i = 0; i < 150; i++)
        {
            west.Add(d.World.Spawn(SyntheticScenario.Swordsman, 0, (8 * Tile) + ((i % 10) * Tile * 3 / 4), (14 * Tile) + ((i / 10) * Tile * 3 / 4)));
            east.Add(d.World.Spawn(SyntheticScenario.Swordsman, 0, (56 * Tile) + ((i % 10) * Tile * 3 / 4), (14 * Tile) + ((i / 10) * Tile * 3 / 4)));
        }

        d.MoveToTile(0, west.ToArray(), 60, 20);
        d.MoveToTile(0, east.ToArray(), 12, 20);
        int midX = 36 * Tile;
        var crossed = new Dictionary<EntityId, int>();
        bool Crossed(EntityId u, bool fromWest) => fromWest ? d.PositionOf(u).X > midX : d.PositionOf(u).X < midX;
        int ticks = d.RunUntil(
            () =>
            {
                foreach (EntityId u in west)
                {
                    if (!crossed.ContainsKey(u) && Crossed(u, true))
                    {
                        crossed[u] = d.World.Tick.Value;
                    }
                }

                foreach (EntityId u in east)
                {
                    if (!crossed.ContainsKey(u) && Crossed(u, false))
                    {
                        crossed[u] = d.World.Tick.Value;
                    }
                }

                return crossed.Count == 300;
            },
            600 * TicksPerSecond);

        output.WriteLine($"{crossed.Count}/300 crossed after {ticks} ticks");
        Assert.Equal(300, crossed.Count);

        // Single-file capacity of the bridge: one unit per body length (2 × radius) at walking speed.
        int speedPerTick = d.World.SpeedPerTick(SyntheticScenario.Swordsman);
        int bodyLength = 2 * d.World.Ruleset.EntityTypes[SyntheticScenario.Swordsman].Radius;
        double singleFilePerTick = (double)speedPerTick / bodyLength;
        int first = crossed.Values.Min();
        int last = crossed.Values.Max();
        double throughput = 299.0 / Math.Max(1, last - first);
        output.WriteLine($"throughput {throughput:F3} units/tick vs single-file {singleFilePerTick:F3} ({throughput / singleFilePerTick:P0})");
        Assert.True(throughput >= 0.8 * singleFilePerTick, $"throughput {throughput:F3} < 80% of {singleFilePerTick:F3}");
        Assert.Equal(0, d.Count(SimEventKind.Blocked));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // ORIG-03: depth-limited pathfinding. §9.2 acceptance 4 (goal enclosed by walls: "unreachable" in ≤ 1 tick and the
    // nearest point chosen) and 6 (a long path across a maze-like map is found: no depth limit).
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ORIG_03_a_unit_crosses_a_maze_along_its_full_length()
    {
        (MapData map, (int X, int Y) first, (int X, int Y) last) = ScenarioMaps.Maze(128, corridor: 2, wall: 1, seed: 42);
        using var d = new SimDriver(map);
        EntityId knight = d.Spawn(SyntheticScenario.Knight, 0, first.X, first.Y);
        d.MoveToTile(0, [knight], last.X, last.Y);
        int ticks = d.RunUntil(() => !d.IsMoving(knight), 2000 * TicksPerSecond);
        Position p = d.PositionOf(knight);
        output.WriteLine($"maze crossed in {ticks} ticks");
        Assert.Equal(last.X, p.X / Tile);
        Assert.Equal(last.Y, p.Y / Tile);
        Assert.Equal(0, d.Count(SimEventKind.Blocked));
        Assert.Equal(0, d.Count(SimEventKind.GoalUnreachable));
    }

    [Fact]
    public void ORIG_03_an_enclosed_goal_is_detected_at_once_and_the_nearest_point_is_used()
    {
        // A closed ring of cliffs around tile (40, 40), outer radius 5 tiles; the unit starts at tile (10, 40).
        string[] rows = new string[64];
        for (int y = 0; y < 64; y++)
        {
            char[] row = new char[64];
            for (int x = 0; x < 64; x++)
            {
                int r2 = ((x - 40) * (x - 40)) + ((y - 40) * (y - 40));
                row[x] = r2 is >= 16 and <= 25 ? '#' : '.';
            }

            rows[y] = new string(row);
        }

        using var d = new SimDriver(ScenarioMaps.FromAscii("ring", rows));
        EntityId unit = d.Spawn(SyntheticScenario.Swordsman, 0, 10, 40);
        d.MoveToTile(0, [unit], 40, 40);
        d.Step();

        // Detected in the tick the order executes.
        (int Tick, SimEvent Event)[] unreachable = d.Events.Where(e => e.Event.Kind == SimEventKind.GoalUnreachable).ToArray();
        Assert.Single(unreachable);
        Assert.Equal(0, unreachable[0].Tick);
        d.RunUntil(() => !d.IsMoving(unit), 120 * TicksPerSecond);

        // It ends on the reachable tile nearest to the goal: just outside the ring (r² = 26 tiles², e.g. (39, 35)).
        Position p = d.PositionOf(unit);
        Assert.Equal(MotionState.Idle, d.MotionOf(unit).State);
        int dx = (p.X / Tile) - 40;
        int dy = (p.Y / Tile) - 40;
        Assert.Equal(26, (dx * dx) + (dy * dy));
        Assert.Equal(0, d.Count(SimEventKind.Blocked));
    }
}
