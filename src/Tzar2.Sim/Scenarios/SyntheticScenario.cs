using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Scenarios;

/// <summary>Parameters of <see cref="SyntheticScenario"/>.</summary>
public sealed record SyntheticScenarioOptions
{
    /// <summary>Map side in tiles.</summary>
    public int MapSize { get; init; } = 512;

    /// <summary>Units spawned at tick 0.</summary>
    public int EntityCount { get; init; } = 4000;

    /// <summary>Players (kingdoms).</summary>
    public int Players { get; init; } = 8;

    /// <summary>Scenario seed (map, spawns, bot orders) and match seed.</summary>
    public ulong Seed { get; init; } = 0x7A42_2026UL;

    /// <summary>Units each player orders to a new random point per tick.</summary>
    public int OrdersPerPlayerPerTick { get; init; } = 16;

    /// <summary>Units each player deletes and respawns per tick (mass-death churn, B4-like).</summary>
    public int ChurnPerPlayerPerTick { get; init; }

    /// <summary>Path budget per tick (node expansions, REQ-PATH-03).</summary>
    public int PathBudgetPerTick { get; init; } = SimulationConfig.DefaultPathBudgetPerTick;

    /// <summary>Probability per tile corner, in basis points, of a water lake seed.</summary>
    public int LakeSeedBasisPoints { get; init; } = 4;
}

/// <summary>
/// A deterministic synthetic match for benchmarks and determinism CI (TDD §21 B3/B4-like, §22.2): a generated
/// map with grass, lakes and cliffs, units spread across it, and "bot clients" that issue random move orders (and
/// optionally delete/respawn churn) every tick. The bot reads the world like an AI would; its commands go through
/// <see cref="CommandCodec"/> and slot stamping exactly like lockstep commands.
/// </summary>
public sealed class SyntheticScenario : IBenchScenario
{
    /// <summary>Terrain ids of <see cref="CreateRuleset"/>.</summary>
    public const byte Grass = 0;

    /// <summary>Water terrain id.</summary>
    public const byte Water = 1;

    /// <summary>Entity type ids of <see cref="CreateRuleset"/>.</summary>
    public const ushort Swordsman = 0;

    /// <summary>Mounted unit type id.</summary>
    public const ushort Knight = 1;

    /// <summary>Large (siege) unit type id.</summary>
    public const ushort Catapult = 2;

    /// <summary>3×3 building with free corners.</summary>
    public const ushort House = 3;

    /// <summary>1×1 resource object.</summary>
    public const ushort Tree = 4;

    /// <summary>Ship type id.</summary>
    public const ushort Boat = 5;

    /// <summary>Flying unit type id.</summary>
    public const ushort Dragon = 6;

    /// <summary>The mobile land types the bot spawns and orders (ids 0 .. MobileTypeCount-1).</summary>
    public const int MobileTypeCount = 3;

    private readonly SyntheticScenarioOptions _options;
    private readonly CommandList _batch = new();
    private readonly CommandList _tickSet = new();
    private readonly BinaryBufferWriter _wire = new(4096);
    private readonly EntityId[][] _owned;
    private readonly int[] _ownedCount;
    private readonly EntityId[] _selection;
    private Xoshiro256StarStar _bot;

    /// <summary>Creates the scenario and its simulation at tick 0.</summary>
    public SyntheticScenario(SyntheticScenarioOptions options, int workerCount = 1)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Ruleset = CreateRuleset();
        Map = CreateMap(options.MapSize, options.Seed, options.LakeSeedBasisPoints);
        Simulation = new Simulation(
            Ruleset,
            Map,
            new SimulationConfig { MatchSeed = options.Seed, PlayerCount = options.Players, CheatsAllowed = true, PathBudgetPerTick = options.PathBudgetPerTick },
            workerCount);
        _bot = Xoshiro256StarStar.FromSeed(options.Seed ^ 0xB07B07UL);
        _owned = new EntityId[options.Players][];
        _ownedCount = new int[options.Players];
        for (int p = 0; p < options.Players; p++)
        {
            _owned[p] = new EntityId[64];
        }

        _selection = new EntityId[Math.Max(1, Math.Max(options.OrdersPerPlayerPerTick, options.ChurnPerPlayerPerTick))];
        SpawnInitialUnits();
    }

    /// <summary>The ruleset.</summary>
    public Ruleset Ruleset { get; }

    /// <summary>The map.</summary>
    public MapData Map { get; }

    /// <summary>The simulation.</summary>
    public Simulation Simulation { get; }

    /// <summary>The command set of the last generated tick.</summary>
    public CommandList LastCommands => _tickSet;

    /// <summary>
    /// The scenario's ruleset: grass (land) and water terrain; foot, mounted and large land units, a 3×3 house, a
    /// tree, a boat and a dragon.
    /// </summary>
    public static Ruleset CreateRuleset() => new(
        "synthetic",
        [
            new TerrainTypeDefinition(Grass, "grass", LocomotorMask.Land | LocomotorMask.Air, isWater: false),
            new TerrainTypeDefinition(Water, "water", LocomotorMask.Ship | LocomotorMask.Air, isWater: true),
        ],
        [
            new EntityTypeDefinition(Swordsman, "swordsman", Locomotor.Foot, speedUnitsPerSecond: 64 * 48, maxHealth: 100, radius: 7 * 64),
            new EntityTypeDefinition(Knight, "knight", Locomotor.Mounted, speedUnitsPerSecond: 64 * 80, maxHealth: 180, radius: 10 * 64),
            new EntityTypeDefinition(Catapult, "catapult", Locomotor.Large, speedUnitsPerSecond: 64 * 24, maxHealth: 250, radius: 13 * 64),
            new EntityTypeDefinition(House, "house", Locomotor.Foot, 0, 400, footprintWidth: 3, footprintHeight: 3, footprint: [0, 1, 0, 1, 1, 1, 0, 1, 0], leavesCorpse: false),
            new EntityTypeDefinition(Tree, "tree", Locomotor.Foot, 0, 50, footprintWidth: 1, footprintHeight: 1, leavesCorpse: false),
            new EntityTypeDefinition(Boat, "boat", Locomotor.Ship, speedUnitsPerSecond: 64 * 40, maxHealth: 150, radius: 14 * 64),
            new EntityTypeDefinition(Dragon, "dragon", Locomotor.Air, speedUnitsPerSecond: 64 * 64, maxHealth: 300, radius: 12 * 64),
        ],
        maxPlayers: 16);

    /// <summary>Generates a map: grass with lakes grown from random seeds and scattered cliff tiles.</summary>
    public static MapData CreateMap(int size, ulong seed, int lakeSeedBasisPoints = 4)
    {
        var rng = Xoshiro256StarStar.FromSeed(seed ^ 0x3A9UL);
        int stride = size + 1;
        byte[] corners = new byte[stride * stride];
        int lakes = (int)((long)corners.Length * lakeSeedBasisPoints / 10_000);
        for (int l = 0; l < lakes; l++)
        {
            int cx = rng.NextInt(stride);
            int cy = rng.NextInt(stride);
            int radius = rng.NextInt(2, 9);
            for (int y = Math.Max(0, cy - radius); y <= Math.Min(size, cy + radius); y++)
            {
                for (int x = Math.Max(0, cx - radius); x <= Math.Min(size, cx + radius); x++)
                {
                    if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= radius * radius)
                    {
                        corners[(y * stride) + x] = Water;
                    }
                }
            }
        }

        byte[] cliffs = new byte[size * size];
        for (int i = 0; i < cliffs.Length; i++)
        {
            cliffs[i] = rng.NextChance(30) ? (byte)1 : (byte)0;
        }

        return new MapData($"synthetic-{size}", size, size, corners, [], cliffs);
    }

    /// <inheritdoc />
    public TickResult RunTick()
    {
        GenerateCommands();
        return Simulation.Tick(_tickSet);
    }

    /// <inheritdoc />
    public void GenerateCommands()
    {
        World world = Simulation.World;
        CollectOwnedUnits(world);
        _tickSet.Clear();
        int worldSize = world.Map.WorldWidth;
        for (int p = 0; p < _options.Players; p++)
        {
            _batch.Clear();
            int owned = _ownedCount[p];
            if (owned > 0 && _options.OrdersPerPlayerPerTick > 0)
            {
                int n = Pick(p, _options.OrdersPerPlayerPerTick);
                _batch.Add(CommandKind.Move, _selection.AsSpan(0, n), _bot.NextInt(worldSize), _bot.NextInt(worldSize));
            }

            if (owned > 0 && _options.ChurnPerPlayerPerTick > 0)
            {
                int n = Pick(p, _options.ChurnPerPlayerPerTick);
                _batch.Add(CommandKind.Delete, _selection.AsSpan(0, n));
                (int x, int y) = RandomLandPoint(world.Map);
                _batch.Add(CommandKind.DebugSpawn, default, x, y, arg0: _bot.NextInt(MobileTypeCount), arg1: n);
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
    }

    /// <inheritdoc />
    public string Describe() => string.Empty;

    /// <inheritdoc />
    public void Dispose() => Simulation.Dispose();

    private void SpawnInitialUnits()
    {
        World world = Simulation.World;
        for (int i = 0; i < _options.EntityCount; i++)
        {
            (int x, int y) = RandomLandPoint(world.Map);
            ushort type = (ushort)_bot.NextInt(MobileTypeCount);
            world.Spawn(type, i % _options.Players, x, y);
        }
    }

    private (int X, int Y) RandomLandPoint(MapState map)
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            int x = _bot.NextInt(map.WorldWidth);
            int y = _bot.NextInt(map.WorldHeight);
            if (map.IsPassableAt(Locomotor.Large, x, y))
            {
                return (x, y);
            }
        }

        return (map.WorldWidth / 2, map.WorldHeight / 2);
    }

    private int Pick(int player, int count)
    {
        int owned = _ownedCount[player];
        int n = Math.Min(count, owned);
        for (int i = 0; i < n; i++)
        {
            _selection[i] = _owned[player][_bot.NextInt(owned)];
        }

        return n;
    }

    private void CollectOwnedUnits(World world)
    {
        Array.Clear(_ownedCount);
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            int p = identities[i].Owner;
            if (p >= _owned.Length)
            {
                continue;
            }

            if (_ownedCount[p] == _owned[p].Length)
            {
                Array.Resize(ref _owned[p], _owned[p].Length * 2);
            }

            _owned[p][_ownedCount[p]++] = ids[i];
        }
    }
}
