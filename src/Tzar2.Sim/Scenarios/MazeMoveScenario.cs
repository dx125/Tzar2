using System;
using System.Globalization;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Scenarios;

/// <summary>Parameters of <see cref="MazeMoveScenario"/>.</summary>
public sealed record MazeMoveScenarioOptions
{
    /// <summary>Map side in tiles.</summary>
    public int MapSize { get; init; } = 512;

    /// <summary>Corridor width in tiles.</summary>
    public int Corridor { get; init; } = 3;

    /// <summary>Wall thickness in tiles.</summary>
    public int Wall { get; init; } = 1;

    /// <summary>Units ordered at tick 0.</summary>
    public int Units { get; init; } = 500;

    /// <summary>Units per move order (1 = 500 individual orders; ≥ 6 = groups with shared flow fields).</summary>
    public int GroupSize { get; init; } = 1;

    /// <summary>Idle units elsewhere on the map (load for movement and avoidance).</summary>
    public int Bystanders { get; init; }

    /// <summary>Seed of the maze, the spawns and the goals.</summary>
    public ulong Seed { get; init; } = 0xB3_2026UL;

    /// <summary>Path budget per tick.</summary>
    public int PathBudgetPerTick { get; init; } = SimulationConfig.DefaultPathBudgetPerTick;
}

/// <summary>
/// TDD §21 benchmark <b>B3</b> "500 units mass move on 512² maze": a perfect maze of cliff walls (every corridor is
/// reachable along one long winding route), units on random corridor tiles, and at tick 0 move orders to random
/// corridor tiles — individually or in groups. Budget (§21, REQ-PATH-12): all 500 orders fully planned within 3 ticks
/// on the desktop reference; the tick p99 stays within the 12 ms sim budget.
/// </summary>
public sealed class MazeMoveScenario : IBenchScenario
{
    private readonly MazeMoveScenarioOptions _options;
    private readonly CommandList _tickSet = new();
    private readonly EntityId[] _units;
    private readonly (int X, int Y)[] _goals;
    private readonly bool[] _planned;
    private bool _ordered;

    /// <summary>Creates the maze, the units and the simulation at tick 0.</summary>
    public MazeMoveScenario(MazeMoveScenarioOptions options, int workerCount = 1)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Ruleset = SyntheticScenario.CreateRuleset();
        (Map, _, _) = ScenarioMaps.Maze(options.MapSize, options.Corridor, options.Wall, options.Seed);
        Simulation = new Simulation(
            Ruleset,
            Map,
            new SimulationConfig { MatchSeed = options.Seed, PlayerCount = 2, CheatsAllowed = true, PathBudgetPerTick = options.PathBudgetPerTick },
            workerCount);
        var rng = Xoshiro256StarStar.FromSeed(options.Seed ^ 0xB3UL);
        World world = Simulation.World;
        _units = new EntityId[options.Units];
        _planned = new bool[options.Units];
        for (int i = 0; i < _units.Length; i++)
        {
            (int x, int y) = RandomCorridorPoint(world.Map, ref rng);
            _units[i] = world.Spawn(SyntheticScenario.Swordsman, 0, x, y);
        }

        for (int i = 0; i < options.Bystanders; i++)
        {
            (int x, int y) = RandomCorridorPoint(world.Map, ref rng);
            world.Spawn(SyntheticScenario.Swordsman, 1, x, y);
        }

        int orders = (_units.Length + options.GroupSize - 1) / Math.Max(1, options.GroupSize);
        _goals = new (int, int)[orders];
        for (int i = 0; i < orders; i++)
        {
            _goals[i] = RandomCorridorPoint(world.Map, ref rng);
        }
    }

    /// <summary>The ruleset.</summary>
    public Ruleset Ruleset { get; }

    /// <summary>The maze.</summary>
    public MapData Map { get; }

    /// <inheritdoc />
    public Simulation Simulation { get; }

    /// <inheritdoc />
    public CommandList LastCommands => _tickSet;

    /// <summary>The tick after which every ordered unit had received its first plan, or -1.</summary>
    public int PlannedAfterTick { get; private set; } = -1;

    /// <summary>Ordered units that reached their goal (stopped without giving up).</summary>
    public int Arrived { get; private set; }

    /// <inheritdoc />
    public void GenerateCommands()
    {
        _tickSet.Clear();
        if (_ordered)
        {
            return;
        }

        _ordered = true;
        var batch = new CommandList();
        int size = Math.Max(1, _options.GroupSize);
        for (int g = 0; g < _goals.Length; g++)
        {
            int start = g * size;
            int count = Math.Min(size, _units.Length - start);
            batch.Add(CommandKind.Move, _units.AsSpan(start, count), _goals[g].X, _goals[g].Y);
        }

        _tickSet.AppendStamped(0, batch);
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
        int waiting = 0;
        int arrived = 0;
        for (int i = 0; i < _units.Length; i++)
        {
            Motion m = world.Motions.Get(_units[i]);
            _planned[i] |= m.Mode != MotionMode.Waiting;
            waiting += _planned[i] ? 0 : 1;
            arrived += m.State == MotionState.Idle ? 1 : 0;
        }

        if (PlannedAfterTick < 0 && waiting == 0 && _ordered)
        {
            PlannedAfterTick = world.Tick.Value - 1;
        }

        Arrived = arrived;
    }

    /// <inheritdoc />
    public string Describe()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"B3 maze {_options.MapSize}² (corridor {_options.Corridor}, wall {_options.Wall}): {_units.Length} units in orders of {_options.GroupSize}, all planned after tick {PlannedAfterTick} (budget: within 3 ticks), {Arrived} arrived");

    /// <inheritdoc />
    public void Dispose() => Simulation.Dispose();

    private static (int X, int Y) RandomCorridorPoint(MapState map, ref Xoshiro256StarStar rng)
    {
        while (true)
        {
            int tx = rng.NextInt(map.Width);
            int ty = rng.NextInt(map.Height);
            if (map.IsPassable(Locomotor.Foot, tx, ty))
            {
                return ((tx * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2), (ty * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2));
            }
        }
    }
}
