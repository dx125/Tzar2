using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Snapshots;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>GAME-09 (ADR 0093): castles, outposts, ships and carpets holding units.</summary>
public class HolderTests
{
    private static readonly string[] Strait =
    [
        "........~~~~~~~~........",
        "........~~~~~~~~........",
        "........~~~~~~~~........",
        "........~~~~~~~~........",
        "........~~~~~~~~........",
        "........~~~~~~~~........",
    ];

    [Fact]
    [Trait("Rule", "HOLD-003")]
    public void Units_enter_a_castle_leave_the_map_and_come_out_next_to_it()
    {
        using var d = new SimDriver(TestMap(32, 16, [Structure("euro.castle", 0, 12, 5), .. Enumerable.Range(0, 3).Select(i => Unit("euro.footman", 0, 6, 5 + i))]), ruleset: Classic);
        var render = new RenderSnapshotExchange();
        d.Sim.RenderOutput = render;
        EntityId castle = OfType(d.World, "euro.castle").Single();
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        d.Order(0, CommandKind.Board, footmen, target: castle);
        d.RunUntil(() => d.World.Holders.Get(castle).Count == 3, 400);
        Assert.Equal(3, d.World.Holders.Get(castle).Count);
        Assert.Equal(3, d.Count(SimEventKind.Boarded));
        Assert.All(footmen, f => Assert.True(d.World.IsHeld(f)));
        Assert.All(footmen, f => Assert.False(d.World.Motions.Has(f)));
        Assert.DoesNotContain(render.AcquireLatest()!.Entities.ToArray(), e => footmen.Contains(e.Id));

        // Units inside take no orders.
        d.MoveToTile(0, footmen, 2, 2);
        d.Step();
        Assert.Contains(CommandRejection.NoValidEntities, d.Rejections());

        d.Order(0, CommandKind.Unload, [castle], 6 * Tile, 6 * Tile);
        d.Step();
        Assert.Equal(0, d.World.Holders.Get(castle).Count);
        Assert.All(footmen, f => Assert.False(d.World.IsHeld(f)));
        Assert.All(footmen, f => Assert.True(d.World.Map.IsPassableAt(Locomotor.Foot, d.PositionOf(f).X, d.PositionOf(f).Y)));
        d.RunUntil(() => !d.AnyMoving(footmen), 400);
        Assert.All(footmen, f => Assert.InRange(d.PositionOf(f).X / Tile, 4, 8));
    }

    [Fact]
    [Trait("Rule", "ORD-015")]
    public void Emerge_lets_only_the_picked_unit_out_and_sends_it_to_the_place()
    {
        using var d = new SimDriver(TestMap(32, 16, [Structure("euro.castle", 0, 12, 5), .. Enumerable.Range(0, 3).Select(i => Unit("euro.footman", 0, 6, 5 + i))]), ruleset: Classic);
        EntityId castle = OfType(d.World, "euro.castle").Single();
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        d.Order(0, CommandKind.Board, footmen, target: castle);
        d.RunUntil(() => d.World.Holders.Get(castle).Count == 3, 400);

        d.Order(0, CommandKind.Unload, [castle], 22 * Tile, 6 * Tile, target: footmen[1]);
        d.Step();
        Assert.Equal(2, d.World.Holders.Get(castle).Count);
        Assert.False(d.World.IsHeld(footmen[1]));
        Assert.True(d.World.IsHeld(footmen[0]) && d.World.IsHeld(footmen[2]));
        d.RunUntil(() => !d.AnyMoving([footmen[1]]), 400);
        Assert.InRange(d.PositionOf(footmen[1]).X / Tile, 20, 24);

        // A unit that is not inside that holder: nothing comes out.
        d.Order(0, CommandKind.Unload, [castle], 6 * Tile, 6 * Tile, target: footmen[1]);
        d.Step();
        Assert.Contains(CommandRejection.NoValidEntities, d.Rejections());
        Assert.Equal(2, d.World.Holders.Get(castle).Count);
    }

    [Fact]
    [Trait("Rule", "HOLD-001")]
    public void A_full_holder_takes_no_more_and_Landing_Tactics_adds_room()
    {
        Ruleset rules = ClassicWith("comm.galleon: { Holder: { capacity: 2 } }");
        using var d = new SimDriver(AsciiMap(Strait, [Unit("comm.galleon", 0, 9, 2), .. Enumerable.Range(0, 4).Select(i => Unit("euro.footman", 0, 6, 1 + i))]), ruleset: rules);
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        d.Order(0, CommandKind.Board, footmen, target: ship);
        d.RunUntil(() => !d.World.Errands.Entities.ToArray().Intersect(footmen).Any(), 600);
        Assert.Equal(2, d.World.Holders.Get(ship).Count); // the others found it full when they arrived: no overflow
        d.Order(0, CommandKind.Board, [footmen.First(f => !d.World.IsHeld(f))], target: ship);
        d.Step();
        Assert.Contains(CommandRejection.HolderFull, d.Rejections());

        Grant(d.World, 0, "te_landingtactics"); // +4 (UNVERIFIED)
        Assert.Equal(6, d.World.HolderCapacity(ship));
        d.Order(0, CommandKind.Board, [.. footmen.Where(f => !d.World.IsHeld(f))], target: ship);
        d.RunUntil(() => d.World.Holders.Get(ship).Count == 4, 600);
        Assert.Equal(4, d.World.Holders.Get(ship).Count);
    }

    [Fact]
    [Trait("Rule", "HOLD-007")]
    public void A_holder_takes_only_the_units_it_accepts()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("comm.carpet", 0, 8, 4), Unit("comm.catapult", 0, 5, 4), Unit("comm.galleon", 0, 0, 0) with { TypeKey = "euro.footman", X = 5 * Tile, Y = 8 * Tile }), ruleset: Classic);
        EntityId carpet = OfType(d.World, "comm.carpet").Single();
        d.Order(0, CommandKind.Board, [.. OfType(d.World, "comm.catapult")], target: carpet);
        d.Step();
        Assert.Contains(CommandRejection.NotAccepted, d.Rejections()); // [accept] infantryunits, mountedunits
        d.Order(0, CommandKind.Board, [.. OfType(d.World, "euro.footman")], target: carpet);
        d.RunUntil(() => d.World.Holders.Get(carpet).Count == 1, 400);
        Assert.Equal(1, d.World.Holders.Get(carpet).Count);
    }

    [Fact]
    [Trait("Rule", "HOLD-003")]
    public void Units_heal_inside_a_castle()
    {
        // castle: 120 hit points per minute (UNVERIFIED, UNK-24): 15 s heal 30.
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 12, 5), Unit("euro.footman", 0, 10, 7)), ruleset: Classic);
        EntityId castle = OfType(d.World, "euro.castle").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.World.Healths.Get(footman).Current = 30;
        d.Order(0, CommandKind.Board, [footman], target: castle);
        d.RunUntil(() => d.World.IsHeld(footman), 400);
        int start = Hp(d, footman);
        d.RunUntil(() => false, 300);
        Assert.Equal(start + 30, Hp(d, footman));
        d.RunUntil(() => false, 400);
        Assert.Equal(70, Hp(d, footman));
    }

    [Fact]
    [Trait("Rule", "BLD-009")]
    [Trait("Rule", "HOLD-006")]
    public void Units_leave_a_destroyed_building_but_go_down_with_a_ship_at_sea()
    {
        using var d = new SimDriver(
            AsciiMap(["................~~~~~~~~~~~~~~~~", "................~~~~~~~~~~~~~~~~", "................~~~~~~~~~~~~~~~~", "................~~~~~~~~~~~~~~~~", "................~~~~~~~~~~~~~~~~", "................~~~~~~~~~~~~~~~~", "................~~~~~~~~~~~~~~~~", "................~~~~~~~~~~~~~~~~"],
                [Structure("euro.castle", 0, 2, 1), Unit("euro.footman", 0, 8, 2), Unit("euro.footman", 0, 8, 4), Unit("comm.galleon", 0, 17, 3), Unit("euro.peon", 0, 14, 3), Unit("euro.peon", 0, 14, 5)]),
            ruleset: Classic);
        EntityId castle = OfType(d.World, "euro.castle").Single();
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        d.Order(0, CommandKind.Board, footmen, target: castle);
        d.Order(0, CommandKind.Board, peons, target: ship);
        d.RunUntil(() => d.World.Holders.Get(castle).Count == 2 && d.World.Holders.Get(ship).Count == 2, 600);
        d.Order(0, CommandKind.Move, [ship], 29 * Tile, 4 * Tile);
        d.RunUntil(() => !d.IsMoving(ship), 1200);
        Assert.Equal(d.PositionOf(ship), d.PositionOf(peons[0])); // they sail with it
        d.Order(0, CommandKind.DebugKill, [castle, ship]);
        d.Step();
        Assert.All(footmen, f => Assert.True(d.World.Entities.IsAlive(f) && !d.World.IsHeld(f)));
        Assert.All(peons, p => Assert.False(d.World.Entities.IsAlive(p)));
        Assert.Equal(2, Of(d, SimEventKind.Killed).Count(e => peons.Contains(e.Entity)));
    }

    [Fact]
    [Trait("Rule", "HOLD-002")]
    public void A_galleon_carries_soldiers_across_the_water()
    {
        using var d = new SimDriver(AsciiMap(Strait, [Unit("comm.galleon", 0, 9, 2), .. Enumerable.Range(0, 3).Select(i => Unit("euro.footman", 0, 5, 1 + i))]), ruleset: Classic);
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        d.Order(0, CommandKind.Board, footmen, target: ship);
        d.RunUntil(() => d.World.Holders.Get(ship).Count == 3, 600);
        d.Order(0, CommandKind.Move, [ship], (14 * Tile) + 1024, (2 * Tile) + 1024);
        d.RunUntil(() => !d.IsMoving(ship), 1200);
        d.Order(0, CommandKind.Unload, [ship], 20 * Tile, 3 * Tile);
        d.Step();
        Assert.Equal(0, d.World.Holders.Get(ship).Count);
        d.RunUntil(() => !d.AnyMoving(footmen), 600);
        Assert.All(footmen, f => Assert.True(d.PositionOf(f).X / Tile >= 16, $"footman at tile {d.PositionOf(f).X / Tile}"));
    }

    [Fact]
    [Trait("Rule", "HOLD-009")]
    public void A_garrisoned_soldier_keeps_its_items_across_a_snapshot()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 12, 5), Unit("euro.footman", 0, 8, 7), Unit("comm.item", MapObject.Nature, 9, 7)), ruleset: Classic);
        EntityId castle = OfType(d.World, "euro.castle").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId sword = OfType(d.World, "comm.item").Single();
        d.Order(0, CommandKind.PickItem, [footman], target: sword);
        d.RunUntil(() => d.World.IsHeld(sword), 300);
        d.Order(0, CommandKind.Board, [footman], target: castle);
        d.RunUntil(() => d.World.IsHeld(footman), 300);
        byte[] snapshot = d.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, Classic, d.World.Map.Data);
        Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
        Assert.Equal(footman.Value, restored.World.Helds.Get(sword).Holder);
    }

    [Fact]
    [Trait("Rule", "HOLD-005")]
    public void Units_inside_cannot_be_attacked()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 12, 5), Unit("euro.footman", 0, 10, 7), Unit("euro.footman", 1, 6, 7)), ruleset: Classic);
        EntityId castle = OfType(d.World, "euro.castle").Single();
        EntityId inside = OfType(d.World, "euro.footman", 0).Single();
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.Board, [inside], target: castle);
        d.RunUntil(() => d.World.IsHeld(inside), 400);
        d.Order(1, CommandKind.Attack, [enemy], target: inside);
        d.Step();
        Assert.Contains(CommandRejection.InvalidArgument, d.Rejections());
    }
}
