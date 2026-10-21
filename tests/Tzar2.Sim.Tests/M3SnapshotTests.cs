using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Snapshots of the M3 state (ADRs 0040-0044): research and upgrade queues, technologies, capture progress, gates, fire,
/// neglect, mana, lifetimes, levels, bridges and turned footprints round-trip and continue identically; corrupt values
/// are rejected (ADR 0019: snapshots are untrusted).
/// </summary>
public class M3SnapshotTests
{
    private static readonly string[] River =
    [
        "................................",
        "................................",
        "..............~~~...............",
        "..............~~~...............",
        "..............~~~...............",
        "..............~~~...............",
        "..............~~~...............",
        "..............~~~...............",
        "................................",
        "................................",
        "................................",
        "................................",
    ];

    private static MapData Busy() => AsciiMap(
        River,
        Structure("euro.castle", 0, 0, 0),
        Structure("euro.blacksmith", 0, 6, 0),
        Structure("euro.house", 0, 0, 8),
        Structure("euro.house", 1, 24, 2),
        Structure("comm.field", 0, 4, 8),
        Structure("comm.tower_wood", 0, 10, 0),
        Structure("comm.bridge", 0, 13, 4),
        Unit("euro.peon", 0, 9, 8),
        Unit("euro.peon", 0, 22, 4),
        Unit("euro.peon", 0, 21, 5),
        Unit("euro.mage", 0, 8, 10),
        Unit("comm.cow", 0, 10, 10));

    [Fact]
    public void A_busy_m3_world_round_trips_and_continues_identically()
    {
        using var d = new SimDriver(Busy(), ruleset: Classic);
        World w = d.World;
        EntityId[] peons = [.. OfType(w, "euro.peon")];
        w.Healths.Get(OfType(w, "euro.house", 0).Single()).Current = 100; // burns
        w.Manas.Get(OfType(w, "euro.mage").Single()).MilliMana = 1234;
        d.Order(0, CommandKind.Research, [OfType(w, "euro.blacksmith").Single()], arg0: Classic.TechnologyId("te_temperiron"));
        d.Order(0, CommandKind.Upgrade, [OfType(w, "comm.tower_wood").Single()], arg0: Classic.UpgradeId("to_wtower2"));
        d.Order(0, CommandKind.Capture, [peons[1], peons[2]], target: OfType(w, "euro.house", 1).Single());
        d.Order(0, CommandKind.Build, [peons[0]], 8 * Tile, 4 * Tile, arg0: Id("comm.gate_wood"), arg1: 1);
        d.Order(0, CommandKind.Attack, [peons[0]], target: OfType(w, "comm.cow").Single());
        ResearchTests.Grant(d, 0, Classic, "te_fields");
        d.RunUntil(() => false, 120);
        Assert.Equal((1, 1, 1), (w.Burnings.Count, w.Neglects.Count, w.Manas.Count));
        Assert.Contains(w.Map.Bridges.ToArray(), b => b != 0);

        byte[] snapshot = d.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, Classic, w.Map.Data);
        Assert.Equal(snapshot, restored.CaptureSnapshot());
        var empty = new CommandList();
        for (int t = 0; t < 400; t++)
        {
            Assert.Equal(d.Step().StateHash, restored.Tick(empty).StateHash);
        }

        Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
    }

    public static TheoryData<string> Corruptions() =>
    [
        "unknown technology bit", "civilization", "gate open 2", "capture by the owner", "fire on a field", "upgrade bit", "turned 2",
        "lifetime expired", "research item at the wrong building", "regrowth timer", "construction carry",
        "repair state on a cow", "mana above the pool", "phantom bridge deck", "paid modifier on an empty slot",
    ];

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void Invalid_m3_state_is_rejected(string corruption)
    {
        var world = new World(Classic, Busy(), new SimulationConfig { PlayerCount = 2 });
        world.LoadMap();
        EntityId castle = OfType(world, "euro.castle").Single();
        EntityId enemyHouse = OfType(world, "euro.house", 1).Single();
        EntityId field = OfType(world, "comm.field").Single();
        EntityId tower = OfType(world, "comm.tower_wood").Single();
        EntityId cow = OfType(world, "comm.cow").Single();
        switch (corruption)
        {
            case "unknown technology bit": world.Players[0].AddTechnology(200); break;
            case "civilization": world.Players[1].Civilization = 9; break;
            case "gate open 2":
                EntityId gate = world.PlaceStructure(Id("comm.gate_wood"), 0, 20, 9);
                world.Gates.Get(gate).Open = 2;
                break;
            case "capture by the owner": world.CaptureStates.Set(enemyHouse, new CaptureState { Player = 1 }); break;
            case "fire on a field": world.Burnings.Set(field, default); break;
            case "upgrade bit": world.UpgradeLevels.Set(tower, new UpgradeLevels { Mask = 1u << 30 }); break;
            case "turned 2": world.Footprints.Get(tower).Rotated = 2; break;
            case "lifetime expired": world.Lifetimes.Set(cow, new Lifetime { ExpiresTick = 0 }); break;
            case "research item at the wrong building":
                world.Productions.Get(castle).Count = 1;
                world.Productions.Get(castle).Queue[0] = new QueueRun { Item = QueueItems.Research(Classic.TechnologyId("te_temperiron")), Count = 1 };
                break;
            case "regrowth timer": world.ResourceStocks.Get(cow).RegenMs = 999_999; break;
            case "repair state on a cow": world.RepairProgresses.Set(cow, default); break;
            case "mana above the pool": world.Manas.Get(OfType(world, "euro.mage").Single()).MilliMana = 1_000_000; break;
            case "phantom bridge deck": world.Map.AddBridge(3, 11); break;
            case "paid modifier on an empty slot": world.Productions.Get(castle).Queue[5] = new QueueRun { Paid = new PaidModifier { Add = 3 } }; break;
            case "construction carry":
                EntityId site = world.PlaceStructure(Id("euro.house"), 0, 26, 9, underConstruction: true);
                world.Constructions.Get(site).CarryBasisPoints = 10_000;
                break;
        }

        byte[] snapshot = Snapshots.WorldSnapshot.Capture(world);
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, Classic, world.Map.Data));
    }
}
