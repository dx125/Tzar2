using System;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Snapshots;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>Snapshots of gameplay state are untrusted input (ADR 0019): every gameplay component is validated on restore.</summary>
public class GameplaySnapshotTests
{
    private static MapData Base() => TestMap(
        32,
        16,
        Structure("euro.castle", 0, 2, 2),
        Structure("euro.house", 0, 10, 2),
        Tree(14, 6),
        Unit("euro.peon", 0, 9, 8),
        Unit("euro.footman", 1, 20, 8));

    [Fact]
    public void A_snapshot_with_workers_queues_and_sites_round_trips()
    {
        using var d = new SimDriver(Base(), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Build, [peon], 16 * Tile, 10 * Tile, arg0: Id("euro.house"));
        d.Order(0, CommandKind.Train, [OfType(d.World, "euro.castle").Single()], arg0: Id("euro.peon"), arg1: 2);
        d.RunUntil(() => false, 60);
        byte[] snapshot = d.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, Classic, d.World.Map.Data);
        Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
        Assert.Equal(snapshot, restored.CaptureSnapshot());
        Assert.Equal(d.World.Players[0].Resources[Resource("wood")], restored.World.Players[0].Resources[Resource("wood")]);
    }

    public static TheoryData<string> Corruptions() => ["worker task", "combat mode", "queue overflow", "queue item", "site progress", "stock on a unit", "combat without armament", "negative stockpile"];

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void Invalid_gameplay_state_is_rejected(string corruption)
    {
        var world = new World(Classic, Base(), new SimulationConfig { PlayerCount = 2 });
        world.LoadMap();
        EntityId peon = OfType(world, "euro.peon").Single();
        EntityId footman = OfType(world, "euro.footman").Single();
        EntityId castle = OfType(world, "euro.castle").Single();
        EntityId house = OfType(world, "euro.house").Single();
        switch (corruption)
        {
            case "worker task": world.Workers.Get(peon).Task = (WorkerTask)9; break;
            case "combat mode": world.Combats.Get(footman).Mode = (CombatMode)7; break;
            case "queue overflow": world.Productions.Get(castle).Count = ProducerDefinition.MaxQueueCapacity + 1; break;
            case "queue item":
                world.Productions.Get(castle).Count = 1;
                world.Productions.Get(castle).Queue[0] = new QueueRun { Item = Id("euro.footman"), Count = 1 }; // the castle does not train footmen
                break;
            case "site progress":
                world.Set(house, new Construction { ProgressMs = 99_999, TotalMs = 15_000 });
                world.Remove<Production>(house);
                break;
            case "stock on a unit": world.Set(footman, new ResourceStock { Amount = 5 }); break;
            case "combat without armament": world.Set(house, new Combat { HitInMs = -1, ChaseTick = -1 }); break;
            case "negative stockpile": world.Players[1].Resources[0] = -5; break;
        }

        byte[] snapshot = WorldSnapshot.Capture(world);
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, Classic, world.Map.Data));
    }
}
