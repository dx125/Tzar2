using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Tzar2.Sim.Scenarios;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Passability churn for the allocation gate (M2-B review leftover): every 10 ticks a construction site appears at one
/// of eight spots and is cancelled 5 ticks later (blockers added and removed, navigation updated incrementally), while
/// 120 units keep moving across the map.
/// </summary>
internal sealed class PassabilityChurnScenario : IBenchScenario
{
    private readonly CommandList _tickSet = new();
    private readonly CommandList _batch = new();
    private readonly List<EntityId> _units = [];
    private readonly EntityId[] _one = new EntityId[1];
    private readonly ushort _house;
    private int _spot;

    public PassabilityChurnScenario(int workers)
    {
        var objects = new List<MapObject> { Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 9, 9) };
        for (int i = 0; i < 120; i++)
        {
            objects.Add(Unit("euro.footman", 0, 4 + (i % 12), 20 + (i / 12)));
        }

        Simulation = new Simulation(Classic, TestMap(64, 64, [.. objects]), new SimulationConfig { MatchSeed = 3, PlayerCount = 1, StartingResources = 2 }, workers);
        _house = Id("euro.house");
        foreach (EntityId e in Simulation.World.Identities.Entities)
        {
            if (Simulation.World.Identities.Get(e).TypeId == Id("euro.footman"))
            {
                _units.Add(e);
            }
        }
    }

    public Simulation Simulation { get; }

    public CommandList LastCommands => _tickSet;

    public void GenerateCommands()
    {
        World world = Simulation.World;
        int tick = world.Tick.Value;
        _batch.Clear();
        if (tick % 10 == 0)
        {
            foreach (EntityId e in world.Workers.Entities)
            {
                _one[0] = e;
            }

            int x = 20 + ((_spot % 4) * 8);
            int y = 10 + ((_spot / 4 % 2) * 30);
            _spot++;
            _batch.Add(CommandKind.Build, _one, x * MapState.UnitsPerTile, y * MapState.UnitsPerTile, arg0: _house);
        }
        else if (tick % 10 == 5)
        {
            foreach (EntityId e in world.Constructions.Entities)
            {
                _one[0] = e;
                _batch.Add(CommandKind.Cancel, _one);
            }
        }

        if (tick % 40 == 0)
        {
            int target = (tick / 40) % 2 == 0 ? 56 : 8;
            _batch.Add(CommandKind.Move, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_units), target * MapState.UnitsPerTile, 30 * MapState.UnitsPerTile);
        }

        _tickSet.Clear();
        _tickSet.AppendStamped(0, _batch);
    }

    public void AfterTick()
    {
    }

    public TickResult RunTick()
    {
        GenerateCommands();
        return Simulation.Tick(_tickSet);
    }

    public string Describe() => string.Empty;

    public void Dispose() => Simulation.Dispose();
}
