using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>Naval state in snapshots (ADRs 0140-0144): dockings and explorers round-trip, and corrupt ones are rejected.</summary>
public class NavalSnapshotTests
{
    // A ship sailing to unload across the bay, one to meet boarders, a repair on its way, a boat fishing, explorers.
    private static SimDriver Run(int workers, int ticks)
    {
        var d = new SimDriver(
            AsciiMap(
                [.. Enumerable.Repeat("............" + new string('~', 40) + "............", 20)],
                Structure("euro.castle", 0, 0, 0),
                Structure("euro.harbor", 0, 9, 8),
                Unit("comm.galleon", 0, 13, 3),
                Unit("comm.hulk", 0, 30, 14),
                Unit("comm.xebec", 0, 40, 10),
                Unit("comm.fishing_boat", 0, 14, 9),
                new MapObject("comm.fishpatch", MapObject.Nature, SimDriver.TileCenter(20), SimDriver.TileCenter(9)),
                Unit("euro.footman", 0, 10, 2),
                Unit("euro.footman", 0, 10, 3),
                Unit("euro.footman", 0, 4, 16),
                Unit("euro.footman", 0, 5, 16),
                Unit("euro.peon", 0, 6, 12),
                Unit("comm.xebec", 0, 16, 18)),
            workers: workers,
            ruleset: Classic);
        World w = d.World;
        w.Players[0].Resources[Resource("wood")] = 5000;
        w.Players[0].Resources[Resource("food")] = 5000;
        EntityId galleon = OfType(w, "comm.galleon").Single();
        EntityId hulk = OfType(w, "comm.hulk").Single();
        EntityId xebec = OfType(w, "comm.xebec").First();
        EntityId[] footmen = [.. OfType(w, "euro.footman")];
        ref Health h = ref w.Healths.Get(xebec);
        h.Current = h.Max / 3;
        d.Order(0, CommandKind.Board, [footmen[0], footmen[1]], target: galleon);
        d.Order(0, CommandKind.Board, [footmen[2], footmen[3]], target: hulk);
        d.Order(0, CommandKind.Repair, [.. OfType(w, "euro.peon")], target: xebec);
        d.Order(0, CommandKind.Harvest, [.. OfType(w, "comm.fishing_boat")], target: OfType(w, "comm.fishpatch").Single());
        d.Order(0, CommandKind.Explore, [OfType(w, "comm.xebec").Last()]);
        for (int t = 0; t < ticks; t++)
        {
            if (t == 60)
            {
                d.Order(0, CommandKind.Unload, [galleon], 58 * Tile, 6 * Tile);
            }

            d.Step();
        }

        return d;
    }

    [Fact]
    public void A_naval_world_round_trips_and_continues_identically()
    {
        using SimDriver d = Run(workers: 1, ticks: 70);
        World w = d.World;
        Assert.True(w.Dockings.Count >= 2, $"dockings {w.Dockings.Count}");
        Assert.Equal(1, w.Explorers.Count);
        byte[] snapshot = d.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, Classic, w.Map.Data, workerCount: 3);
        Assert.Equal(snapshot, restored.CaptureSnapshot());
        var empty = new CommandList();
        for (int t = 0; t < 1200; t++)
        {
            Assert.Equal(d.Step().StateHash, restored.Tick(empty).StateHash);
        }

        Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
        Assert.True(OfType(d.World, "euro.footman").Count(f => !d.World.IsHeld(f) && d.PositionOf(f).X / Tile >= 52) == 2, "the galleon landed its soldiers across the sea");
    }

    [Fact]
    public void Ships_give_the_same_hashes_on_one_and_four_workers()
    {
        ulong Stream(int workers)
        {
            using SimDriver d = Run(workers, 0);
            var h = new StateHasher();
            for (int t = 0; t < 1500; t++)
            {
                TickResult r = d.Step();
                h.Add(r.StateHash).Add(r.FullHash ?? 0UL);
            }

            return h.GetCurrentHash();
        }

        Assert.Equal(Stream(1), Stream(4));
    }

    public static TheoryData<string> Corruptions() =>
    [
        "docking of a footman", "docking purpose 9", "docking off the map", "docking padding", "unloading without a point", "boarding with a limit",
        "docking retries", "docking of a held ship", "explorer goal beyond the map", "explorer of a building", "explorer far in the future",
    ];

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void Invalid_naval_state_is_rejected(string corruption)
    {
        using SimDriver d = Run(workers: 1, ticks: 70);
        World w = d.World;
        w.IsLocked = false;
        EntityId footman = OfType(w, "euro.footman").First(f => !w.IsHeld(f));
        EntityId ship = w.Dockings.Entities[0];
        ref Docking dock = ref w.Dockings.At(0);
        switch (corruption)
        {
            case "docking of a footman": w.Dockings.Set(footman, w.Dockings.At(0)); break;
            case "docking purpose 9": dock.Purpose = (DockPurpose)9; break;
            case "docking off the map": dock.WaterX = -1; break;
            case "docking padding": dock.Reserved = 1; break;
            case "unloading without a point":
                dock.Purpose = DockPurpose.Unload;
                dock.TowardX = -50;
                break;
            case "boarding with a limit":
                dock.Purpose = DockPurpose.Board;
                dock.TowardX = 0;
                dock.TowardY = 0;
                dock.Limit = 3;
                break;
            case "docking retries": dock.Retries = 50; break;
            case "docking of a held ship": w.Helds.Set(ship, new Held { Holder = OfType(w, "euro.castle").Single().Value }); break;
            case "explorer goal beyond the map": w.Explorers.At(0).GoalTile = 1_000_000; break;
            case "explorer of a building": w.Explorers.Set(OfType(w, "euro.castle").Single(), new Exploring { GoalTile = -1 }); break;
            case "explorer far in the future": w.Explorers.At(0).NextTick = w.Tick.Value + 10_000; break;
        }

        byte[] snapshot = Snapshots.WorldSnapshot.Capture(w);
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, Classic, w.Map.Data));
    }
}
