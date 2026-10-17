using System;
using System.Globalization;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Scenarios;

/// <summary>Parameters of <see cref="MassDeathScenario"/>.</summary>
public sealed record MassDeathScenarioOptions
{
    /// <summary>Map side in tiles.</summary>
    public int MapSize { get; init; } = 256;

    /// <summary>Units killed around the bridge.</summary>
    public int Kills { get; init; } = 2000;

    /// <summary>Ticks over which they die (60 s at 20 Hz).</summary>
    public int KillTicks { get; init; } = 1200;

    /// <summary>Units patrolling across the bridge afterwards (half from each bank).</summary>
    public int Traffic { get; init; } = 400;

    /// <summary>Idle units elsewhere (load; 2,000 + 400 + 1,600 = the 4,000-unit budget scenario of §21).</summary>
    public int Bystanders { get; init; } = 1600;

    /// <summary>Seed.</summary>
    public ulong Seed { get; init; } = 0xB4_2026UL;

    /// <summary>Path budget per tick.</summary>
    public int PathBudgetPerTick { get; init; } = SimulationConfig.DefaultPathBudgetPerTick;
}

/// <summary>
/// TDD §21 benchmark <b>B4</b> "mass death: 2,000 kills in 60 s then traffic through the area" (ORIG-01): a river with
/// one 4-tile bridge; 2,000 units packed around the bridge die over 60 s, leaving corpses; then 400 units patrol across
/// the bridge from both banks. Budget: the tick p99 stays within the 12 ms sim budget, the corpses never change
/// passability (obstacle version unchanged) and nobody gets stuck.
/// </summary>
public sealed class MassDeathScenario : IBenchScenario
{
    /// <summary>Left column of the river.</summary>
    public const int RiverX0 = 124;

    /// <summary>Right column of the river.</summary>
    public const int RiverX1 = 131;

    /// <summary>Top row of the bridge.</summary>
    public const int BridgeY = 126;

    /// <summary>Bridge width in tiles.</summary>
    public const int BridgeWidth = 4;

    private readonly MassDeathScenarioOptions _options;
    private readonly CommandList _batch = new();
    private readonly CommandList _tickSet = new();
    private readonly EntityId[] _victims;
    private readonly EntityId[] _traffic;
    private readonly bool[] _east;
    private int _killed;

    /// <summary>Creates the map, the units and the simulation at tick 0.</summary>
    public MassDeathScenario(MassDeathScenarioOptions options, int workerCount = 1)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Ruleset = SyntheticScenario.CreateRuleset();
        int size = options.MapSize;
        byte[] water = new byte[size * size];
        for (int y = 0; y < size; y++)
        {
            if (y >= BridgeY && y < BridgeY + BridgeWidth)
            {
                continue;
            }

            for (int x = RiverX0; x <= RiverX1; x++)
            {
                water[(y * size) + x] = 1;
            }
        }

        Map = ScenarioMaps.Build($"mass-death-{size}", size, size, water, []);
        Simulation = new Simulation(
            Ruleset,
            Map,
            new SimulationConfig { MatchSeed = options.Seed, PlayerCount = 3, CheatsAllowed = true, PathBudgetPerTick = options.PathBudgetPerTick },
            workerCount);
        World world = Simulation.World;
        var rng = Xoshiro256StarStar.FromSeed(options.Seed);
        _victims = new EntityId[options.Kills];
        for (int i = 0; i < _victims.Length; i++)
        {
            (int x, int y) = LandPoint(world.Map, ref rng, 108, 108, 40, 40);
            _victims[i] = world.Spawn(SyntheticScenario.Swordsman, 1, x, y);
        }

        _traffic = new EntityId[options.Traffic];
        _east = new bool[options.Traffic];
        for (int i = 0; i < _traffic.Length; i++)
        {
            bool east = i % 2 == 1;
            (int x, int y) = LandPoint(world.Map, ref rng, east ? 200 : 20, 100, 36, 56);
            _traffic[i] = world.Spawn(SyntheticScenario.Swordsman, 0, x, y);
            _east[i] = east;
        }

        for (int i = 0; i < options.Bystanders; i++)
        {
            (int x, int y) = LandPoint(world.Map, ref rng, 0, i % 2 == 0 ? 0 : 170, size, size - 170);
            world.Spawn(SyntheticScenario.Swordsman, 2, x, y);
        }
    }

    /// <summary>The ruleset.</summary>
    public Ruleset Ruleset { get; }

    /// <summary>The map.</summary>
    public MapData Map { get; }

    /// <inheritdoc />
    public Simulation Simulation { get; }

    /// <inheritdoc />
    public CommandList LastCommands => _tickSet;

    /// <summary>River crossings by the traffic so far.</summary>
    public int Crossings { get; private set; }

    /// <summary>Most corpses alive at once.</summary>
    public int PeakCorpses { get; private set; }

    /// <summary><see cref="SimEventKind.Blocked"/> events so far.</summary>
    public int Blocked { get; private set; }

    /// <inheritdoc />
    public void GenerateCommands()
    {
        World world = Simulation.World;
        int tick = world.Tick.Value;
        _tickSet.Clear();
        _batch.Clear();
        if (tick < _options.KillTicks)
        {
            int due = (int)((long)_victims.Length * (tick + 1) / _options.KillTicks);
            if (due > _killed)
            {
                _batch.Add(CommandKind.DebugKill, _victims.AsSpan(_killed, due - _killed));
                _killed = due;
            }

            _tickSet.AppendStamped(1, _batch);
        }
        else if (tick == _options.KillTicks)
        {
            int half = (_traffic.Length + 1) / 2;
            EntityId[] west = new EntityId[half];
            EntityId[] east = new EntityId[_traffic.Length - half];
            int w = 0;
            int e = 0;
            for (int i = 0; i < _traffic.Length; i++)
            {
                if (_east[i])
                {
                    east[e++] = _traffic[i];
                }
                else
                {
                    west[w++] = _traffic[i];
                }
            }

            int mid = (BridgeY * MapState.UnitsPerTile) + (BridgeWidth * MapState.UnitsPerTile / 2);
            _batch.Add(CommandKind.Patrol, west.AsSpan(0, w), 230 * MapState.UnitsPerTile, mid);
            _batch.Add(CommandKind.Patrol, east.AsSpan(0, e), 25 * MapState.UnitsPerTile, mid);
            _tickSet.AppendStamped(0, _batch);
        }
    }

    /// <inheritdoc />
    public TickResult RunTick()
    {
        GenerateCommands();
        TickResult result = Simulation.Tick(_tickSet);
        AfterTick();
        return result;
    }

    /// <inheritdoc />
    public void AfterTick()
    {
        World world = Simulation.World;
        int midX = ((RiverX0 + RiverX1 + 1) / 2) * MapState.UnitsPerTile;
        for (int i = 0; i < _traffic.Length; i++)
        {
            bool east = world.Positions.Get(_traffic[i]).X >= midX;
            if (east != _east[i])
            {
                _east[i] = east;
                Crossings++;
            }
        }

        PeakCorpses = Math.Max(PeakCorpses, world.Decals.Count);
        foreach (SimEvent e in world.Events.Events)
        {
            Blocked += e.Kind == SimEventKind.Blocked ? 1 : 0;
        }
    }

    /// <inheritdoc />
    public string Describe()
    {
        World world = Simulation.World;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"B4 mass death {_options.MapSize}²: {_killed} killed over {_options.KillTicks} ticks (peak {PeakCorpses} corpses, {world.Decals.Count} left), obstacle version {world.Map.ObstacleVersion}; {_traffic.Length} patrolling units made {Crossings} bridge crossings, {Blocked} blocked");
    }

    /// <inheritdoc />
    public void Dispose() => Simulation.Dispose();

    private static (int X, int Y) LandPoint(MapState map, ref Xoshiro256StarStar rng, int x0, int y0, int w, int h)
    {
        while (true)
        {
            int x = (x0 * MapState.UnitsPerTile) + rng.NextInt(w * MapState.UnitsPerTile);
            int y = (y0 * MapState.UnitsPerTile) + rng.NextInt(h * MapState.UnitsPerTile);
            if (map.IsPassableAt(Locomotor.Foot, x, y))
            {
                return (x, y);
            }
        }
    }
}
