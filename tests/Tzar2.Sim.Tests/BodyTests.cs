using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Units have the original's body (WORLD-015; batch K, capture-20261022-110601): a footman's IMAGES.INI object box is
/// 32 × 32, its body 14 px, so two never come within 28 px; at rest a group stands one to a tile, and a compact group
/// keeps its formation. Bodies do not jam opposing columns on a bridge (ORIG-02).
/// </summary>
public class BodyTests
{
    private const int Body = 14 * 64;

    [Fact]
    [Trait("Rule", "WORLD-015")]
    public void Nine_footmen_crowded_together_and_sent_to_one_point_stand_a_body_apart()
    {
        // Spawned a quarter tile apart (bodies over bodies), then all sent to one point.
        var units = new List<Tzar2.Content.Compiled.MapObject>();
        for (int i = 0; i < 9; i++)
        {
            units.Add(new Tzar2.Content.Compiled.MapObject("euro.footman", 0, (10 * Tile) + ((i % 3) * Tile / 4), (10 * Tile) + ((i / 3) * Tile / 4)));
        }

        using var d = new SimDriver(TestMap(40, 24, [.. units]), ruleset: Classic);
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        d.MoveToTile(0, footmen, 26, 12);
        d.RunUntil(() => footmen.All(f => !d.IsMoving(f)), 1200);
        d.RunUntil(() => false, 40); // settled
        Assert.All(footmen, f => Assert.False(d.IsMoving(f)));
        long nearest = MinDistance(d, footmen);
        Assert.True(nearest >= (2 * Body) - 128, $"two footmen stand {nearest / 64.0:F1} px apart");
        Assert.True(footmen.All(f => IntMath.ISqrt(IntMath.DistanceSquared(d.PositionOf(f).X, d.PositionOf(f).Y, (26 * Tile) + (Tile / 2), (12 * Tile) + (Tile / 2))) < 3 * Tile), "they gathered at the point");
    }

    [Fact]
    [Trait("Rule", "WORLD-015")]
    public void A_compact_group_keeps_its_formation_and_walkers_never_overlap()
    {
        // Batch K: nine footmen two tiles apart arrive as the same 3 x 3 block.
        var units = new List<Tzar2.Content.Compiled.MapObject>();
        for (int i = 0; i < 9; i++)
        {
            units.Add(Unit("euro.footman", 0, 6 + ((i % 3) * 2), 8 + ((i / 3) * 2)));
        }

        using var d = new SimDriver(TestMap(48, 24, [.. units]), ruleset: Classic);
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        Position first = d.PositionOf(footmen[0]);
        d.MoveToTile(0, footmen, 30, 12);
        d.RunUntil(() => footmen.Any(f => d.IsMoving(f)), 40);
        long closest = long.MaxValue;
        for (int t = 0; t < 1200 && !footmen.All(f => !d.IsMoving(f)); t++)
        {
            d.Step();
            closest = Math.Min(closest, MinDistance(d, footmen));
        }

        Assert.True(closest >= (2 * Body) - 256, $"walking, two footmen came {closest / 64.0:F1} px apart");
        int dx = d.PositionOf(footmen[0]).X - first.X;
        Assert.True(Math.Abs(MinDistance(d, footmen) - (2 * Tile)) <= Tile / 4, $"at rest {MinDistance(d, footmen) / 64.0:F1} px apart, not the formation's 64");
        Assert.True(dx > 20 * Tile, "the block moved");
    }

    [Fact]
    [Trait("Rule", "WORLD-015")]
    public void Opposing_columns_of_footmen_cross_a_two_tile_bridge()
    {
        // ORIG-02 with the original's bodies: twenty footmen of one kingdom each way over a bridge two tiles wide.
        var rows = new string[24];
        for (int y = 0; y < rows.Length; y++)
        {
            char[] row = new char[56];
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = x >= 26 && x <= 30 && y is not (11 or 12) ? '~' : '.';
            }

            rows[y] = new string(row);
        }

        var units = new List<Tzar2.Content.Compiled.MapObject>();
        for (int i = 0; i < 20; i++)
        {
            units.Add(Unit("euro.footman", 0, 6 + (i % 5), 8 + (i / 5)));
            units.Add(Unit("euro.footman", 0, 46 + (i % 5), 8 + (i / 5)));
        }

        using var d = new SimDriver(AsciiMap(rows, [.. units]), ruleset: Classic);
        EntityId[] all = [.. OfType(d.World, "euro.footman", 0)];
        EntityId[] west = [.. all.Where(u => d.PositionOf(u).X < 20 * Tile)];
        EntityId[] east = [.. all.Where(u => d.PositionOf(u).X > 40 * Tile)];
        d.MoveToTile(0, west, 50, 10);
        d.MoveToTile(0, east, 8, 10);
        int mid = 28 * Tile;
        d.RunUntil(() => west.All(u => d.PositionOf(u).X > mid + (3 * Tile)) && east.All(u => d.PositionOf(u).X < mid - (3 * Tile)), 120 * 20);
        Assert.True(west.All(u => d.PositionOf(u).X > mid + (3 * Tile)), $"{west.Count(u => d.PositionOf(u).X > mid + (3 * Tile))}/20 crossed east; west at {string.Join(" ", west.Select(u => $"{d.PositionOf(u).X / Tile},{d.PositionOf(u).Y / Tile}"))} rejections {string.Join(",", d.Rejections())}");
        Assert.True(east.All(u => d.PositionOf(u).X < mid - (3 * Tile)), $"{east.Count(u => d.PositionOf(u).X < mid - (3 * Tile))}/20 crossed west");
    }

    private static long MinDistance(SimDriver d, EntityId[] units)
    {
        long best = long.MaxValue;
        for (int i = 0; i < units.Length; i++)
        {
            for (int j = i + 1; j < units.Length; j++)
            {
                Position a = d.PositionOf(units[i]);
                Position b = d.PositionOf(units[j]);
                best = Math.Min(best, IntMath.ISqrt(IntMath.DistanceSquared(a.X, a.Y, b.X, b.Y)));
            }
        }

        return best;
    }
}
