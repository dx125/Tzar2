using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Map;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// GAME-12 (ADR 0101, ORIG-10): per-kingdom visibility, exploration, last-seen structures, No Fog / Explore, shared
/// vision, temporary reveals, and the incremental grid matching a from-scratch computation (property test).
/// </summary>
public class VisibilityTests
{
    [Fact]
    public void A_unit_reveals_its_sight_explored_tiles_stay_explored_and_enemy_units_hide_beyond_sight()
    {
        using var d = new SimDriver(TestMap(64, 16, Unit("euro.peon", 0, 5, 8), Unit("euro.footman", 1, 40, 8)), ruleset: Classic);
        World w = d.World;
        EntityId peon = OfType(w, "euro.peon").Single();
        EntityId footman = OfType(w, "euro.footman").Single();
        d.Step();

        // Synthetic peasant sight 7 tiles.
        Assert.Equal(TileVisibility.Visible, w.Visibility.StateOf(0, 5, 8));
        Assert.Equal(TileVisibility.Visible, w.Visibility.StateOf(0, 12, 8));
        Assert.Equal(TileVisibility.Unexplored, w.Visibility.StateOf(0, 20, 8));
        Assert.Equal(TileVisibility.Unexplored, w.Visibility.StateOf(1, 5, 8));
        Assert.False(w.Visibility.CanSee(w, 0, footman));
        Assert.True(w.Visibility.CanSee(w, 0, peon));

        d.MoveToTile(0, [peon], 30, 8);
        d.RunUntil(() => !d.IsMoving(peon) && d.World.Tick.Value > 5, 600);
        Assert.Equal(TileVisibility.Explored, w.Visibility.StateOf(0, 5, 8)); // remembered, not in sight
        Assert.Equal(TileVisibility.Visible, w.Visibility.StateOf(0, 30, 8));
        Assert.False(w.Visibility.CanSee(w, 0, footman)); // 10 tiles away, beyond the peasant's 7
        Span<byte> states = new byte[64 * 16];
        w.Visibility.WriteStates(0, states);
        Assert.Equal((byte)TileVisibility.Explored, states[(8 * 64) + 5]);
        Assert.Equal((byte)TileVisibility.Visible, states[(8 * 64) + 30]);
    }

    [Fact]
    public void Enemy_buildings_are_remembered_as_last_seen_until_the_tile_is_seen_again()
    {
        using var d = new SimDriver(TestMap(64, 16, Unit("euro.peon", 0, 14, 5), Structure("euro.house", 1, 20, 4)), ruleset: Classic);
        World w = d.World;
        EntityId peon = OfType(w, "euro.peon").Single();
        EntityId house = OfType(w, "euro.house").Single();
        d.Step();
        d.Step();
        d.Step();
        d.Step();
        Assert.Equal(house.Value, w.Visibility.MemoryOf(0, house.Index).Entity);

        d.MoveToTile(0, [peon], 1, 5);
        d.RunUntil(() => !d.IsMoving(peon) && d.World.Tick.Value > 5, 600);
        Assert.False(w.Visibility.CanSee(w, 0, house));
        d.Order(0, CommandKind.DebugKill, [house]);
        d.Step();
        Assert.False(w.Entities.IsAlive(house));
        for (int i = 0; i < 8; i++)
        {
            d.Step();
        }

        // Destroyed out of sight: the kingdom still believes it is there.
        Ghost ghost = w.Visibility.MemoryOf(0, house.Index);
        Assert.Equal(house.Value, ghost.Entity);
        Assert.Equal((ushort)20, ghost.TileX);
        Assert.Equal(1, ghost.Owner);

        d.MoveToTile(0, [peon], 16, 5);
        d.RunUntil(() => !d.IsMoving(peon) && d.World.Tick.Value > 5, 600);
        d.RunUntil(() => w.Visibility.MemoryOf(0, house.Index).Entity == 0, 16);
        Assert.Equal(0u, w.Visibility.MemoryOf(0, house.Index).Entity); // seen again: it is gone
    }

    [Fact]
    public void No_fog_shows_explored_areas_and_explore_starts_with_the_whole_map_known()
    {
        MapData map = TestMap(64, 16, Unit("euro.peon", 0, 5, 8), Unit("euro.footman", 1, 40, 8), Structure("euro.house", 1, 50, 4));
        using (var noFog = new SimDriver(map, ruleset: Classic, configure: c => c with { NoFog = true }))
        {
            EntityId peon = OfType(noFog.World, "euro.peon").Single();
            noFog.Step();
            noFog.MoveToTile(0, [peon], 30, 8);
            noFog.RunUntil(() => !noFog.IsMoving(peon) && noFog.World.Tick.Value > 5, 600);
            Assert.Equal(TileVisibility.Visible, noFog.World.Visibility.StateOf(0, 5, 8)); // explored = in sight
            Assert.Equal(TileVisibility.Unexplored, noFog.World.Visibility.StateOf(0, 60, 8));
        }

        using var explore = new SimDriver(map, ruleset: Classic, configure: c => c with { Explored = true });
        explore.Step();
        World w = explore.World;
        Assert.Equal(TileVisibility.Explored, w.Visibility.StateOf(0, 60, 15));
        Assert.Equal(TileVisibility.Visible, w.Visibility.StateOf(0, 5, 8));
        EntityId house = OfType(w, "euro.house").Single();
        Assert.Equal(house.Value, w.Visibility.MemoryOf(0, house.Index).Entity); // the enemy house is known
        Assert.False(w.Visibility.CanSee(w, 0, OfType(w, "euro.footman").Single())); // units still need sight
    }

    [Fact]
    public void Shared_vision_needs_both_kingdoms_to_be_allies_and_to_offer_it()
    {
        using var d = new SimDriver(TestMap(64, 16, Unit("euro.peon", 0, 5, 8), Unit("euro.footman", 1, 40, 8)), ruleset: Classic, configure: c => c with { });
        World w = d.World;
        int allyVision = (int)Sharing.Vision;
        d.Order(0, CommandKind.SetDiplomacy, [], x: 1, arg0: (int)Stance.Ally, arg1: allyVision);
        d.Step();
        Assert.False(w.Visibility.IsVisible(0, 40, 8)); // only one side offers

        d.Order(1, CommandKind.SetDiplomacy, [], x: 0, arg0: (int)Stance.Ally, arg1: allyVision);
        d.Step();
        Assert.True(w.Visibility.IsVisible(0, 40, 8));
        Assert.True(w.Visibility.IsVisible(1, 5, 8));
        Assert.True(w.Visibility.CanSee(w, 0, OfType(w, "euro.footman").Single()));

        // Ending the alliance (after the anti flip-flop interval) ends the shared vision; what was seen stays explored.
        for (int i = 0; i < 200; i++)
        {
            d.Step();
        }

        d.Order(1, CommandKind.SetDiplomacy, [], x: 0, arg0: (int)Stance.Enemy, arg1: 0);
        d.Step();
        Assert.False(w.Visibility.IsVisible(0, 40, 8));
        Assert.Equal(TileVisibility.Explored, w.Visibility.StateOf(0, 40, 8));
    }

    [Fact]
    public void A_temporary_reveal_shows_an_area_until_it_expires()
    {
        using var d = new SimDriver(TestMap(64, 16, Unit("euro.peon", 0, 5, 8)), ruleset: Classic);
        World w = d.World;
        d.Step();
        w.IsLocked = false;
        w.Visibility.Reveal(w, 0, 50, 8, 3, w.Tick.Value + 10);
        w.IsLocked = true;
        d.Step();
        Assert.True(w.Visibility.IsVisible(0, 50, 8));
        Assert.True(w.Visibility.IsVisible(0, 52, 9));
        Assert.Single(w.Visibility.TemporaryReveals);
        byte[] snapshot = d.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, w.Ruleset, w.Map.Data);
        Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
        for (int i = 0; i < 12; i++)
        {
            d.Step();
        }

        Assert.False(w.Visibility.IsVisible(0, 50, 8));
        Assert.Equal(TileVisibility.Explored, w.Visibility.StateOf(0, 50, 8));
        Assert.Empty(w.Visibility.TemporaryReveals);
    }

    [Theory]
    [InlineData(1, 11)]
    [InlineData(4, 12)]
    public void The_incremental_grid_always_equals_a_from_scratch_computation(int workers, ulong seed)
    {
        // Property test (ORIG-10): random walks, deaths and new units of three kingdoms; after every tick each
        // kingdom's visible set equals the union of its revealers' disks computed from scratch, and explored ⊇ visible.
        var objects = new List<MapObject>();
        var rng = Xoshiro256StarStar.FromSeed(seed);
        string[] types = ["euro.peon", "euro.footman", "euro.archer"];
        for (int i = 0; i < 30; i++)
        {
            objects.Add(Unit(types[i % 3], i % 3, 2 + rng.NextInt(44), 2 + rng.NextInt(44)));
        }

        objects.Add(Structure("euro.house", 1, 30, 30));
        using var d = new SimDriver(TestMap(48, 48, [.. objects]), players: 3, workers: workers, ruleset: Classic);
        World w = d.World;
        for (int t = 0; t < 150; t++)
        {
            EntityId[] units = [.. w.Motions.Entities.ToArray()];
            if (t % 5 == 0 && units.Length > 0)
            {
                EntityId u = units[rng.NextInt(units.Length)];
                d.MoveToTile(w.Identities.Get(u).Owner, [u], rng.NextInt(48), rng.NextInt(48));
            }

            if (t % 37 == 36 && units.Length > 0)
            {
                d.Order(0, CommandKind.DebugKill, [units[rng.NextInt(units.Length)]]);
            }

            if (t % 41 == 40)
            {
                d.Order(rng.NextInt(3), CommandKind.DebugSpawn, [], SimDriver.TileCenter(rng.NextInt(48)), SimDriver.TileCenter(rng.NextInt(48)), arg0: Id("euro.peon"), arg1: 1);
            }

            d.Step();
            for (int p = 0; p < 3; p++)
            {
                bool[] expected = FromScratch(w, p);
                for (int ty = 0; ty < 48; ty++)
                {
                    for (int tx = 0; tx < 48; tx++)
                    {
                        Assert.True(expected[(ty * 48) + tx] == w.Visibility.IsVisible(p, tx, ty), $"tick {t} player {p} tile ({tx}, {ty})");
                        Assert.True(!expected[(ty * 48) + tx] || w.Visibility.IsExplored(p, tx, ty));
                    }
                }
            }
        }
    }

    // The definition (ADR 0101): every unit or building of the kingdom reveals the tiles whose offset (dx, dy) from its tile
    // has dx² + dy² ≤ r² + r, r = round((sight + half its footprint) / tile).
    private static bool[] FromScratch(World w, int player)
    {
        var visible = new bool[w.Map.Width * w.Map.Height];
        ReadOnlySpan<EntityId> ids = w.Identities.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Identity identity = w.Identities.Components[i];
            EntityTypeDefinition type = w.Ruleset.EntityTypes[identity.TypeId];
            if (identity.Owner != player || type.Traits.Category is not (EntityCategory.Unit or EntityCategory.Building))
            {
                continue;
            }

            int sight = type.Traits.SightUnits > 0 ? w.Stats.Apply(player, ModifierStat.Sight, type.Id, type.Traits.SightUnits) : w.Ruleset.World.DefaultSightTiles * MapState.UnitsPerTile;
            int r = Math.Clamp((sight + (Math.Max(type.FootprintWidth, type.FootprintHeight) * MapState.UnitsPerTile / 2) + (MapState.UnitsPerTile / 2)) / MapState.UnitsPerTile, 0, 64);
            Position p = w.Positions.Get(ids[i]);
            int cx = p.X / MapState.UnitsPerTile;
            int cy = p.Y / MapState.UnitsPerTile;
            for (int y = Math.Max(0, cy - r); y <= Math.Min(w.Map.Height - 1, cy + r); y++)
            {
                for (int x = Math.Max(0, cx - r); x <= Math.Min(w.Map.Width - 1, cx + r); x++)
                {
                    if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= (r * r) + r)
                    {
                        visible[(y * w.Map.Width) + x] = true;
                    }
                }
            }
        }

        return visible;
    }
}
