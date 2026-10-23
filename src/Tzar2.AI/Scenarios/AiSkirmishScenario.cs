using System;
using System.Collections.Generic;
using System.Globalization;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Tzar2.Sim.Scenarios;

namespace Tzar2.AI.Scenarios;

/// <summary>Parameters of <see cref="AiSkirmishScenario"/>.</summary>
public sealed record AiSkirmishOptions
{
    /// <summary>Map side in tiles.</summary>
    public int MapSize { get; init; } = 128;

    /// <summary>AI kingdoms (bases on a ring around the center, 1-8).</summary>
    public int Players { get; init; } = 2;

    /// <summary>Peasants per kingdom at the start.</summary>
    public int WorkersPerPlayer { get; init; } = 6;

    /// <summary>Trees in each base's forest.</summary>
    public int TreesPerPlayer { get; init; } = 160;

    /// <summary>Seed of the layout and the match.</summary>
    public ulong Seed { get; init; } = 0xA1_2026UL;

    /// <summary>Difficulty of every AI.</summary>
    public AiDifficulty Difficulty { get; init; } = AiDifficulty.Hard;

    /// <summary>Civilization of every AI (a ruleset key).</summary>
    public string Civilization { get; init; } = "euro";

    /// <summary>Starting-resource level index of the ruleset (-1 = default).</summary>
    public int StartingResources { get; init; } = 1;

    /// <summary>Population limit.</summary>
    public int PopulationLimit { get; init; } = 200;

    /// <summary>Teams (<see cref="SimulationConfig.Teams"/>: 4 bits per slot; equal non-zero teams are allies).</summary>
    public ulong Teams { get; init; }

    /// <summary>AI host options.</summary>
    public AiOptions Ai { get; init; } = AiOptions.Default;
}

/// <summary>
/// AI against AI on a synthetic map (M3 exit "skirmish vs. Hard AI"; benchmark of TDD §21 "8 Hard AIs"): every kingdom
/// starts with a castle, a few peasants, a forest and a gold and a stone rock; nothing but the AI issues commands. The
/// ruleset and the script are passed in (the classic ruleset from the synthetic tables and the engine-default script in
/// public CI; REQ-LEGAL-03). Progress metrics per kingdom come from the sim events.
/// </summary>
public sealed class AiSkirmishScenario : IBenchScenario
{
    private readonly CommandList _none = new();
    private readonly long[] _delivered;
    private readonly long[] _built;
    private readonly long[] _trained;
    private readonly long[] _kills;
    private readonly long[] _damage;

    /// <summary>Creates the match at tick 0 with the AI attached.</summary>
    public AiSkirmishScenario(Ruleset ruleset, AiProgram program, AiSkirmishOptions options, int workerCount = 1)
        : this(ruleset, program, CreateMap(ruleset, options ?? throw new ArgumentNullException(nameof(options))), options, workerCount)
    {
    }

    /// <summary>Creates the match on a given map (e.g. a random map, GAME-18) with one AI per kingdom of <paramref name="options"/>.</summary>
    public AiSkirmishScenario(Ruleset ruleset, AiProgram program, MapData map, AiSkirmishOptions options, int workerCount = 1)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Players, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Players, 8);
        Options = options;
        Program = program;
        Map = map;
        Config = new SimulationConfig
        {
            MatchSeed = options.Seed,
            PlayerCount = options.Players,
            StartingResources = options.StartingResources,
            PopulationLimit = options.PopulationLimit,
            Teams = options.Teams,
        };
        Simulation = new Simulation(ruleset, Map, Config, workerCount);
        if (System.Linq.Enumerable.Contains(ruleset.Civilizations, options.Civilization, StringComparer.Ordinal))
        {
            for (int p = 0; p < options.Players; p++)
            {
                Simulation.World.SetCivilization(p, options.Civilization);
            }
        }
        Ai = LegacyAiSystem.Attach(Simulation, PlayerSettings(program, options), options.Ai);
        _delivered = new long[options.Players];
        _built = new long[options.Players];
        _trained = new long[options.Players];
        _kills = new long[options.Players];
        _damage = new long[options.Players];
    }

    private AiSkirmishScenario(Simulation simulation, LegacyAiSystem ai, AiSkirmishScenario from)
    {
        Options = from.Options;
        Program = from.Program;
        Map = from.Map;
        Config = from.Config;
        Simulation = simulation;
        Ai = ai;
        _delivered = (long[])from._delivered.Clone();
        _built = (long[])from._built.Clone();
        _trained = (long[])from._trained.Clone();
        _kills = (long[])from._kills.Clone();
        _damage = (long[])from._damage.Clone();
    }

    /// <summary>The options.</summary>
    public AiSkirmishOptions Options { get; }

    /// <summary>The script every AI runs.</summary>
    public AiProgram Program { get; }

    /// <summary>The map.</summary>
    public MapData Map { get; }

    /// <summary>The match settings.</summary>
    public SimulationConfig Config { get; }

    /// <inheritdoc />
    public Simulation Simulation { get; }

    /// <summary>The AI host.</summary>
    public LegacyAiSystem Ai { get; }

    /// <inheritdoc />
    public CommandList LastCommands => _none;

    /// <summary>Resource units delivered by a kingdom.</summary>
    public long Delivered(int player) => _delivered[player];

    /// <summary>Buildings a kingdom completed.</summary>
    public long Built(int player) => _built[player];

    /// <summary>Units a kingdom trained.</summary>
    public long Trained(int player) => _trained[player];

    /// <summary>Enemies a kingdom killed.</summary>
    public long Kills(int player) => _kills[player];

    /// <summary>Damage a kingdom dealt to other kingdoms.</summary>
    public long DamageDealt(int player) => _damage[player];

    /// <summary>The AI settings of every kingdom.</summary>
    public static IReadOnlyList<AiPlayerSettings> PlayerSettings(AiProgram program, AiSkirmishOptions options)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(options);
        var players = new List<AiPlayerSettings>();
        for (int p = 0; p < options.Players; p++)
        {
            players.Add(new AiPlayerSettings { Slot = (byte)p, Program = program, Civilization = options.Civilization, Difficulty = options.Difficulty });
        }

        return players;
    }

    /// <summary>Restores this scenario's match from a snapshot of it and re-attaches the AI (the metrics carry over).</summary>
    public AiSkirmishScenario Restore(byte[] snapshot, int workerCount = 1)
    {
        Simulation simulation = Simulation.Restore(snapshot, Simulation.World.Ruleset, Map, workerCount);
        LegacyAiSystem ai = LegacyAiSystem.Attach(simulation, PlayerSettings(Program, Options), Options.Ai);
        return new AiSkirmishScenario(simulation, ai, this);
    }

    /// <summary>The map: open grass, per kingdom a castle, peasants, a forest and a gold and a stone rock.</summary>
    public static MapData CreateMap(Ruleset ruleset, AiSkirmishOptions options)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(options);
        int size = options.MapSize;
        var objects = new List<MapObject>();
        var used = new bool[size * size];
        var rng = Xoshiro256StarStar.FromSeed(options.Seed ^ 0x51D3UL);
        string civ = options.Civilization;
        int center = size / 2;
        int radius = (size / 2) - 18;
        for (int p = 0; p < options.Players; p++)
        {
            (int ox, int oy) = Directions[p % Directions.Length];
            int bx = center + (ox * radius / 100) - 2;
            int by = center + (oy * radius / 100) - 2;
            Place(objects, used, size, ruleset, ruleset.EntityTypes[ruleset.SeatTypeId(civ)].Key, p, bx, by);
            Place(objects, used, size, ruleset, "comm.res_gold", MapObject.Nature, bx + 9, by - 2);
            Place(objects, used, size, ruleset, "comm.res_stone", MapObject.Nature, bx - 4, by + 9);

            // The forest lies behind the base, away from the center.
            int fx = bx + (ox > 0 ? 9 : ox < 0 ? -15 : -4);
            int fy = by + (oy > 0 ? 9 : oy < 0 ? -15 : -4);
            for (int t = 0, attempts = 0; t < options.TreesPerPlayer && attempts < options.TreesPerPlayer * 8; attempts++)
            {
                if (Place(objects, used, size, ruleset, "comm.tree", MapObject.Nature, fx + rng.NextInt(12), fy + rng.NextInt(12)))
                {
                    t++;
                }
            }

            for (int w = 0; w < options.WorkersPerPlayer; w++)
            {
                objects.Add(new MapObject(ruleset.EntityTypes[ruleset.StartingWorkerTypeId(civ)].Key, (byte)p, Center(bx + 6 + (w % 3), size), Center(by + 1 + (w / 3), size)));
            }
        }

        MapData terrain = ScenarioMaps.Build($"ai-skirmish-{size}", size, size, new byte[size * size], []);
        return ScenarioMaps.WithObjects(terrain, objects);

        static int Center(int tile, int size) => (Math.Clamp(tile, 0, size - 1) * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2);
    }

    /// <inheritdoc />
    public void GenerateCommands() => _none.Clear();

    /// <inheritdoc />
    public TickResult RunTick()
    {
        GenerateCommands();
        TickResult result = Simulation.Tick(_none);
        AfterTick();
        return result;
    }

    /// <inheritdoc />
    public void AfterTick()
    {
        World world = Simulation.World;
        foreach (SimEvent e in world.Events.Events)
        {
            switch (e.Kind)
            {
                case SimEventKind.ResourceDelivered when e.Player < _delivered.Length:
                    _delivered[e.Player] += e.Arg;
                    break;
                case SimEventKind.ConstructionCompleted when e.Player < _built.Length:
                    _built[e.Player]++;
                    break;
                case SimEventKind.UnitTrained when e.Player < _trained.Length:
                    _trained[e.Player]++;
                    break;
                case SimEventKind.Killed:
                    if (OwnerOfIndex(world, e.X) is >= 0 and var killer && killer < _kills.Length && killer != e.Player)
                    {
                        _kills[killer]++;
                    }

                    break;
                case SimEventKind.Damaged:
                    if (OwnerOfIndex(world, e.X) is >= 0 and var attacker && attacker < _damage.Length && world.Identities.IndexOf(e.Entity) is >= 0 and var t
                        && world.Identities.At(t).Owner != attacker)
                    {
                        _damage[attacker] += e.Arg;
                    }

                    break;
            }
        }
    }

    /// <inheritdoc />
    public string Describe()
    {
        var parts = new List<string>();
        for (int p = 0; p < Options.Players; p++)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"p{p}: delivered {_delivered[p]}, built {_built[p]}, trained {_trained[p]}, damage {_damage[p]}, kills {_kills[p]}, waves {Ai.Players[p].Waves}, ships {Ships(p)}, landings {Ai.Players[p].Landings}"));
        }

        return string.Create(CultureInfo.InvariantCulture, $"entities {Simulation.World.Entities.AliveCount}; ") + string.Join("; ", parts);
    }

    /// <summary>Ships <paramref name="player"/> has now (naval maps, ADR 0144).</summary>
    public int Ships(int player)
    {
        World world = Simulation.World;
        int n = 0;
        for (int i = 0; i < world.Identities.Count; i++)
        {
            if (world.Identities.View[i].Owner == player && world.IsShip(world.Identities.Entities[i]))
            {
                n++;
            }
        }

        return n;
    }

    /// <inheritdoc />
    public void Dispose() => Simulation.Dispose();

    private static int OwnerOfIndex(World world, int index)
    {
        if ((uint)index > EntityId.MaxIndex)
        {
            return -1;
        }

        EntityId e = world.Entities.AtIndex(index);
        return !e.IsNone && world.Identities.IndexOf(e) is >= 0 and var i ? world.Identities.At(i).Owner : -1;
    }

    private static readonly (int X, int Y)[] Directions = [(-100, -100), (100, 100), (100, -100), (-100, 100), (0, -100), (0, 100), (-100, 0), (100, 0)];

    private static bool Place(List<MapObject> objects, bool[] used, int size, Ruleset ruleset, string key, int owner, int tx, int ty)
    {
        int id = ruleset.EntityTypeId(key);
        if (id < 0)
        {
            throw new ArgumentException($"The ruleset has no '{key}'.", nameof(ruleset));
        }

        EntityTypeDefinition type = ruleset.EntityTypes[id];
        if (tx < 1 || ty < 1 || tx + type.FootprintWidth >= size || ty + type.FootprintHeight >= size)
        {
            return false;
        }

        // Buildings and rocks keep a free ring around them; trees may touch each other.
        int ring = string.Equals(key, "comm.tree", StringComparison.Ordinal) ? 0 : 1;
        for (int y = ty - ring; y < ty + type.FootprintHeight + ring; y++)
        {
            for (int x = tx - ring; x < tx + type.FootprintWidth + ring; x++)
            {
                if (used[(y * size) + x])
                {
                    return false;
                }
            }
        }

        for (int y = ty; y < ty + type.FootprintHeight; y++)
        {
            for (int x = tx; x < tx + type.FootprintWidth; x++)
            {
                used[(y * size) + x] = true;
            }
        }

        objects.Add(MapObject.AtTile(key, (byte)owner, tx, ty));
        return true;
    }
}
