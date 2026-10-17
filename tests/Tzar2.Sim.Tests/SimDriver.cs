using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Tests;

/// <summary>Drives a <see cref="Simulation"/> tick by tick with per-player orders and records the events.</summary>
internal sealed class SimDriver : IDisposable
{
    public const int Tile = MapState.UnitsPerTile;

    private readonly CommandList[] _pending;
    private readonly CommandList _tickSet = new();

    public SimDriver(MapData map, int players = 2, int workers = 1, Ruleset? ruleset = null, int? pathBudget = null, Func<SimulationConfig, SimulationConfig>? configure = null)
    {
        var config = new SimulationConfig { MatchSeed = 7, PlayerCount = players, CheatsAllowed = true };
        if (pathBudget is { } budget)
        {
            config = config with { PathBudgetPerTick = budget };
        }

        config = configure?.Invoke(config) ?? config;

        Sim = new Simulation(ruleset ?? TestWorlds.Ruleset, map, config, workers);
        _pending = new CommandList[players];
        for (int i = 0; i < players; i++)
        {
            _pending[i] = new CommandList();
        }
    }

    public Simulation Sim { get; }

    public World World => Sim.World;

    public List<(int Tick, SimEvent Event)> Events { get; } = [];

    public static int TileCenter(int tile) => (tile * Tile) + (Tile / 2);

    public EntityId Spawn(ushort type, int player, int tileX, int tileY, int dx = 0, int dy = 0)
        => World.Spawn(type, player, TileCenter(tileX) + dx, TileCenter(tileY) + dy);

    public void Order(int player, CommandKind kind, ReadOnlySpan<EntityId> units, int x = 0, int y = 0, EntityId target = default, int arg0 = 0, int arg1 = 0, CommandFlags flags = CommandFlags.None)
        => _pending[player].Add(kind, units, x, y, target, arg0, arg1, flags);

    /// <summary>The rejection reasons of the commands rejected so far, in order.</summary>
    public List<CommandRejection> Rejections()
    {
        var list = new List<CommandRejection>();
        foreach ((int _, SimEvent e) in Events)
        {
            if (e.Kind == SimEventKind.CommandRejected)
            {
                list.Add((CommandRejection)e.Arg);
            }
        }

        return list;
    }

    public void MoveToTile(int player, ReadOnlySpan<EntityId> units, int tileX, int tileY)
        => Order(player, CommandKind.Move, units, TileCenter(tileX), TileCenter(tileY));

    public TickResult Step()
    {
        _tickSet.Clear();
        for (int p = 0; p < _pending.Length; p++)
        {
            _tickSet.AppendStamped((byte)p, _pending[p]);
            _pending[p].Clear();
        }

        int tick = World.Tick.Value;
        TickResult result = Sim.Tick(_tickSet);
        foreach (SimEvent e in World.Events.Events)
        {
            Events.Add((tick, e));
        }

        return result;
    }

    /// <summary>Steps until <paramref name="done"/> holds after a tick, or <paramref name="maxTicks"/> ran.</summary>
    /// <returns>Ticks run.</returns>
    public int RunUntil(Func<bool> done, int maxTicks)
    {
        for (int t = 0; t < maxTicks; t++)
        {
            Step();
            if (done())
            {
                return t + 1;
            }
        }

        return maxTicks;
    }

    public Position PositionOf(EntityId e) => World.Positions.Get(e);

    public Motion MotionOf(EntityId e) => World.Motions.Get(e);

    public bool IsMoving(EntityId e) => World.Motions.Get(e).State == MotionState.Moving;

    public bool AnyMoving(IEnumerable<EntityId> units)
    {
        foreach (EntityId e in units)
        {
            if (World.Motions.Get(e).State == MotionState.Moving)
            {
                return true;
            }
        }

        return false;
    }

    public int Count(SimEventKind kind)
    {
        int n = 0;
        foreach ((int _, SimEvent e) in Events)
        {
            n += e.Kind == kind ? 1 : 0;
        }

        return n;
    }

    public void Dispose() => Sim.Dispose();
}
