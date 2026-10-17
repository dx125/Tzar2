using System;
using System.Collections.Generic;
using System.Globalization;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Scenarios;

/// <summary>Parameters of <see cref="GameplayScenario"/>.</summary>
public sealed record GameplayScenarioOptions
{
    /// <summary>Map side in tiles.</summary>
    public int MapSize { get; init; } = 192;

    /// <summary>Kingdoms (bases on a ring around the center, 1-8).</summary>
    public int Players { get; init; } = 4;

    /// <summary>Peasants per kingdom at the start.</summary>
    public int WorkersPerPlayer { get; init; } = 60;

    /// <summary>Soldiers per kingdom at the start.</summary>
    public int SoldiersPerPlayer { get; init; } = 300;

    /// <summary>Trees in each base's forest.</summary>
    public int TreesPerPlayer { get; init; } = 300;

    /// <summary>Seed of the map layout, the bot and the match.</summary>
    public ulong Seed { get; init; } = 0x6A3E_2026UL;

    /// <summary>Ticks between two bot passes of one kingdom.</summary>
    public int BotIntervalTicks { get; init; } = 40;
}

/// <summary>
/// Economy and combat at scale (§21 sim budget, §25 M2 "basic gathering/building/production/combat"; ADR 0039): every
/// kingdom has a castle, barracks, lumber mill, storage and farm, a forest and a gold rock; its peasants harvest and
/// build houses, its barracks train footmen, and its soldiers move-advance to the map center where all kingdoms meet.
/// A deterministic bot issues the orders through <see cref="CommandCodec"/> and slot stamping, like lockstep clients.
/// The ruleset is passed in (the compiled <c>classic</c> ruleset; Sim never compiles rules, REQ-ARCH-01).
/// </summary>
public sealed class GameplayScenario : IBenchScenario
{
    private readonly GameplayScenarioOptions _options;
    private readonly CommandList _batch = new();
    private readonly CommandList _tickSet = new();
    private readonly BinaryBufferWriter _wire = new(4096);
    private readonly List<EntityId> _idleWorkers = [];
    private readonly List<EntityId> _idleSoldiers = [];
    private readonly List<EntityId> _selection = [];
    private readonly ushort _peon;
    private readonly ushort _footman;
    private readonly ushort _house;
    private readonly ushort _barrack;
    private Xoshiro256StarStar _bot;
    private long _delivered;
    private long _kills;
    private long _trained;
    private long _built;

    /// <summary>Creates the scenario and its simulation at tick 0.</summary>
    /// <param name="ruleset">The compiled classic ruleset (needs the <c>euro.*</c> and <c>comm.*</c> types).</param>
    /// <param name="options">Parameters.</param>
    /// <param name="workerCount">Sim worker threads (never changes results).</param>
    public GameplayScenario(Ruleset ruleset, GameplayScenarioOptions options, int workerCount = 1)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Players, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Players, 8);
        _options = options;
        Ruleset = ruleset;
        _peon = TypeId(ruleset, "euro.peon");
        _footman = TypeId(ruleset, "euro.footman");
        _house = TypeId(ruleset, "euro.house");
        _barrack = TypeId(ruleset, "euro.barrack");
        Map = CreateMap(ruleset, options);
        Simulation = new Simulation(
            ruleset,
            Map,
            new SimulationConfig { MatchSeed = options.Seed, PlayerCount = options.Players, StartingResources = 2, PopulationLimit = 1000 },
            workerCount);
        _bot = Xoshiro256StarStar.FromSeed(options.Seed ^ 0xB07UL);
    }

    /// <summary>The ruleset.</summary>
    public Ruleset Ruleset { get; }

    /// <summary>The generated map (terrain and placed objects).</summary>
    public MapData Map { get; }

    /// <inheritdoc />
    public Simulation Simulation { get; }

    /// <inheritdoc />
    public CommandList LastCommands => _tickSet;

    /// <summary>Resource units delivered so far.</summary>
    public long Delivered => _delivered;

    /// <summary>Entities killed in combat so far.</summary>
    public long Kills => _kills;

    /// <summary>Units trained so far.</summary>
    public long Trained => _trained;

    /// <summary>Buildings completed so far.</summary>
    public long Built => _built;

    /// <summary>
    /// The map: open grass; per kingdom a base on a ring around the center (castle, barracks, lumber mill, storage,
    /// farm), a forest behind it, a gold rock, peasants at the castle and a block of soldiers in front of the base.
    /// </summary>
    public static MapData CreateMap(Ruleset ruleset, GameplayScenarioOptions options)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(options);
        int size = options.MapSize;
        var objects = new List<MapObject>();
        var rng = Xoshiro256StarStar.FromSeed(options.Seed ^ 0x3A9UL);
        var used = new bool[size * size];
        int center = size / 2;
        int radius = (size / 2) - 24;
        string[] soldierTypes = ["euro.footman", "euro.footman", "euro.archer", "euro.maceman"];
        for (int p = 0; p < options.Players; p++)
        {
            // Base corner on a ring (8 fixed directions; integer offsets keep the layout exact).
            (int ox, int oy) = Directions[p % Directions.Length];
            int bx = center + (ox * radius / 100) - 8;
            int by = center + (oy * radius / 100) - 8;
            Place(objects, used, size, ruleset, "euro.castle", p, bx, by);
            Place(objects, used, size, ruleset, "euro.barrack", p, bx + 7, by);
            Place(objects, used, size, ruleset, "euro.lumber", p, bx, by + 7);
            Place(objects, used, size, ruleset, "euro.storage", p, bx + 4, by + 7);
            Place(objects, used, size, ruleset, "euro.farm", p, bx + 8, by + 5);
            Place(objects, used, size, ruleset, "comm.res_gold", MapObject.Nature, bx + 12, by + 8);
            // Houses: enough population room (castle + 30 houses) that the barracks keep training.
            for (int h = 0; h < 30; h++)
            {
                Place(objects, used, size, ruleset, "euro.house", p, bx + 11 + ((h % 6) * 3), by + ((h / 6) * 3));
            }

            // Forest: away from the center.
            int fx = bx - (ox > 0 ? -16 : 12);
            int fy = by - (oy > 0 ? -16 : 12);
            for (int t = 0, attempts = 0; t < options.TreesPerPlayer && attempts < options.TreesPerPlayer * 8; attempts++)
            {
                int tx = fx + rng.NextInt(20);
                int ty = fy + rng.NextInt(20);
                if (Place(objects, used, size, ruleset, "comm.tree", MapObject.Nature, tx, ty))
                {
                    t++;
                }
            }

            for (int w = 0; w < options.WorkersPerPlayer; w++)
            {
                objects.Add(new MapObject("euro.peon", (byte)p, Center(bx - 1, size), Center(by + (w % 12), size) + ((w / 12) * 300)));
            }

            // Soldiers: a block between the base and the center.
            int sx = bx + (ox > 0 ? -14 : 16);
            int sy = by + (oy > 0 ? -14 : 16);
            int columns = 20;
            for (int s = 0; s < options.SoldiersPerPlayer; s++)
            {
                int x = Math.Clamp((sx * MapState.UnitsPerTile) + ((s % columns) * 1200), 1024, (size * MapState.UnitsPerTile) - 1024);
                int y = Math.Clamp((sy * MapState.UnitsPerTile) + ((s / columns) * 1200), 1024, (size * MapState.UnitsPerTile) - 1024);
                objects.Add(new MapObject(soldierTypes[s % soldierTypes.Length], (byte)p, x, y));
            }
        }

        MapData terrain = ScenarioMaps.Build($"gameplay-{size}", size, size, new byte[size * size], []);
        return ScenarioMaps.WithObjects(terrain, objects);

        static int Center(int tile, int size) => (Math.Clamp(tile, 0, size - 1) * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2);
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
    public void GenerateCommands()
    {
        World world = Simulation.World;
        int tick = world.Tick.Value;
        _tickSet.Clear();
        for (int p = 0; p < _options.Players; p++)
        {
            _batch.Clear();
            if ((tick + (p * 7)) % _options.BotIntervalTicks == 0)
            {
                Think(world, (byte)p, tick);
            }

            // Client -> relay -> peer: encode the batch, decode it, stamp the sender's slot.
            _wire.Clear();
            CommandCodec.WriteBatch(_wire, _batch);
            var reader = new BinaryBufferReader(_wire.WrittenSpan);
            CommandCodec.ReadBatch(ref reader, _batch);
            _tickSet.AppendStamped((byte)p, _batch);
        }
    }

    /// <inheritdoc />
    public void AfterTick()
    {
        foreach (SimEvent e in Simulation.World.Events.Events)
        {
            switch (e.Kind)
            {
                case SimEventKind.ResourceDelivered: _delivered += e.Arg; break;
                case SimEventKind.Killed: _kills++; break;
                case SimEventKind.UnitTrained: _trained++; break;
                case SimEventKind.ConstructionCompleted: _built++; break;
            }
        }
    }

    /// <inheritdoc />
    public string Describe()
        => string.Create(CultureInfo.InvariantCulture, $"entities {Simulation.World.Entities.AliveCount}, delivered {_delivered}, kills {_kills}, trained {_trained}, built {_built}, corpses {Simulation.World.Decals.Count}");

    /// <inheritdoc />
    public void Dispose() => Simulation.Dispose();

    // One bot pass for a kingdom: idle peasants harvest, a house now and then, footmen from the barracks, idle soldiers
    // move-advance to the center.
    private void Think(World world, byte player, int tick)
    {
        _idleWorkers.Clear();
        _idleSoldiers.Clear();
        EntityId barracks = EntityId.None;
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            if (identities[i].Owner != player)
            {
                continue;
            }

            EntityId e = ids[i];
            ushort type = identities[i].TypeId;
            if (type == _barrack && barracks.IsNone && world.Productions.Has(e))
            {
                barracks = e;
            }
            else if (world.Workers.IndexOf(e) is >= 0 and var w)
            {
                if (world.Workers.At(w).Task == WorkerTask.None && !IsMoving(world, e))
                {
                    _idleWorkers.Add(e);
                }
            }
            else if (world.Combats.IndexOf(e) is >= 0 and var c && world.Motions.Has(e))
            {
                Combat combat = world.Combats.At(c);
                if (combat.Target == 0 && combat.Mode == CombatMode.None && !IsMoving(world, e))
                {
                    _idleSoldiers.Add(e);
                }
            }
        }

        // Peasants: two build a house every 400 ticks; the rest harvest the node nearest to them.
        int first = 0;
        if (tick % 400 < _options.BotIntervalTicks && _idleWorkers.Count >= 2)
        {
            Position at = world.Positions.Get(_idleWorkers[0]);
            int tx = (at.X / MapState.UnitsPerTile) + _bot.NextInt(-6, 7);
            int ty = (at.Y / MapState.UnitsPerTile) + _bot.NextInt(-6, 7);
            _selection.Clear();
            _selection.Add(_idleWorkers[0]);
            _selection.Add(_idleWorkers[1]);
            _batch.Add(CommandKind.Build, Span(_selection), Math.Max(0, tx) * MapState.UnitsPerTile, Math.Max(0, ty) * MapState.UnitsPerTile, arg0: _house);
            first = 2;
        }

        for (int i = first; i < _idleWorkers.Count; i++)
        {
            EntityId node = NearestNode(world, world.Positions.Get(_idleWorkers[i]));
            if (!node.IsNone)
            {
                _selection.Clear();
                _selection.Add(_idleWorkers[i]);
                _batch.Add(CommandKind.Harvest, Span(_selection), target: node);
            }
        }

        if (!barracks.IsNone && tick % 200 < _options.BotIntervalTicks)
        {
            _selection.Clear();
            _selection.Add(barracks);
            _batch.Add(CommandKind.Train, Span(_selection), arg0: _footman, arg1: 3);
        }

        if (_idleSoldiers.Count > 0)
        {
            int c = world.Map.WorldWidth / 2;
            int spread = 8 * MapState.UnitsPerTile;
            for (int start = 0; start < _idleSoldiers.Count; start += 50)
            {
                int n = Math.Min(50, _idleSoldiers.Count - start);
                _batch.Add(CommandKind.Move, Span(_idleSoldiers).Slice(start, n), c + _bot.NextInt(-spread, spread), c + _bot.NextInt(-spread, spread), flags: CommandFlags.Ctrl);
            }
        }
    }

    private static EntityId NearestNode(World world, Position from)
    {
        EntityId best = EntityId.None;
        long bestDistance = long.MaxValue;
        ReadOnlySpan<EntityId> nodes = world.ResourceStocks.Entities;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (world.ResourceStocks.Components[i].Amount == 0)
            {
                continue;
            }

            Position p = world.Positions.Get(nodes[i]);
            long d = Core.Numerics.IntMath.DistanceSquared(from.X, from.Y, p.X, p.Y);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = nodes[i];
            }
        }

        return best;
    }

    private static bool IsMoving(World world, EntityId e) => world.Motions.IndexOf(e) is >= 0 and var m && world.Motions.At(m).State == MotionState.Moving;

    private static ReadOnlySpan<EntityId> Span(List<EntityId> list) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);

    private static readonly (int X, int Y)[] Directions = [(-100, -100), (100, 100), (100, -100), (-100, 100), (0, -100), (0, 100), (-100, 0), (100, 0)];

    private static ushort TypeId(Ruleset ruleset, string key)
        => ruleset.EntityTypeId(key) is >= 0 and var id ? (ushort)id : throw new ArgumentException($"The ruleset has no '{key}'.", nameof(ruleset));

    private static bool Place(List<MapObject> objects, bool[] used, int size, Ruleset ruleset, string key, int owner, int tx, int ty)
    {
        EntityTypeDefinition type = ruleset.EntityTypes[TypeId(ruleset, key)];
        if (tx < 1 || ty < 1 || tx + type.FootprintWidth >= size || ty + type.FootprintHeight >= size)
        {
            return false;
        }

        for (int y = ty; y < ty + type.FootprintHeight; y++)
        {
            for (int x = tx; x < tx + type.FootprintWidth; x++)
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
