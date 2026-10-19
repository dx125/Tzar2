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

    /// <summary>
    /// Taverns for kingdoms 0 and 1 (of three or more), who hire there (ADR 0108). Off by default: the benchmark workloads
    /// must not change (ADR 0080); the gameplay golden turns it on.
    /// </summary>
    public bool Taverns { get; init; }
}

/// <summary>
/// Economy and combat at scale (§21 sim budget, §25 M2 "basic gathering/building/production/combat"; ADR 0039): every
/// kingdom has a castle, barracks, lumber mill, storage and farm, a forest and a gold rock; its peasants harvest and
/// build houses, its barracks train footmen, and its soldiers move-advance to the map center where all kingdoms meet.
/// A deterministic bot issues the orders through <see cref="CommandCodec"/> and slot stamping, like lockstep clients.
/// The ruleset is passed in (the compiled <c>classic</c> ruleset; Sim never compiles rules, REQ-ARCH-01).
/// </summary>
public sealed partial class GameplayScenario : IBenchScenario
{
    private readonly GameplayScenarioOptions _options;
    private readonly CommandList _batch = new();
    private readonly CommandList _tickSet = new();
    private readonly BinaryBufferWriter _wire = new(4096);
    private readonly List<EntityId> _idleWorkers = [];
    private readonly List<EntityId> _allWorkers = [];
    private readonly List<EntityId> _idleSoldiers = [];
    private readonly List<EntityId> _selection = [];
    private readonly ushort _peon;
    private readonly ushort _footman;
    private readonly ushort _house;
    private readonly ushort _barrack;
    private readonly ushort _castle;
    private readonly int _gate;
    private readonly int _wall;
    private readonly int _bridge;
    private readonly bool[] _bridged;
    private Xoshiro256StarStar _bot;
    private long _delivered;
    private long _kills;
    private long _trained;
    private long _built;
    private long _researched;
    private long _captured;
    private long _fires;
    private long _repaired;
    private long _claimed;
    private long _sales;
    private long _tributes;
    private long _diplomacy;
    private long _hired;
    private readonly int _tavern;
    private readonly int _market;

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
        _castle = TypeId(ruleset, "euro.castle");
        _gate = ruleset.EntityTypeId("comm.gate_wood");
        _wall = ruleset.EntityTypeId("comm.wall_wood");
        _bridge = ruleset.EntityTypeId("comm.bridge");
        _market = ruleset.EntityTypeId("euro.market");
        _tavern = ruleset.EntityTypeId("euro.tavern");
        _bridged = new bool[options.Players];
        Map = CreateMap(ruleset, options);
        Simulation = new Simulation(
            ruleset,
            Map,
            new SimulationConfig
            {
                MatchSeed = options.Seed,
                PlayerCount = options.Players,
                StartingResources = 2,
                PopulationLimit = 1000,
                Victory = VictoryCondition.Standard, // GAME-17: the victory evaluation runs (nobody is wiped out in a bench run)
                CheatsAllowed = true, // GAME-19: hmnotech lets kingdoms 0 and 1 trade without researching Trade Routes
            },
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

    /// <summary>Buildings captured, fires started and repairs completed so far (GAME-03/04).</summary>
    public (long Captured, long Fires, long Repaired) M3Events => (_captured, _fires, _repaired);

    /// <summary>World-system events (M3 stream F, ADR 0107): gateways claimed, trade sales, tributes, diplomacy changes.</summary>
    public (long Claimed, long Sales, long Tributes, long Diplomacy) WorldEvents => (_claimed, _sales, _tributes, _diplomacy);

    /// <summary>Tavern hires (mercenary groups and heroes; ADR 0108).</summary>
    public long Hired => _hired;

    private int BridgeTiles()
    {
        int n = 0;
        foreach (byte b in Simulation.World.Map.Bridges)
        {
            n += b != 0 ? 1 : 0;
        }

        return n;
    }

    /// <summary>Technologies researched so far (GAME-06).</summary>
    public long Researched => _researched;

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
        var water = new byte[size * size];
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

            // M3 stream F (ADR 0107): two markets 14 tiles apart and a caravan per kingdom (kingdoms 0 and 1 trade).
            if (ruleset.EntityTypeId("euro.market") >= 0 && ruleset.EntityTypeId("euro.trader") >= 0
                && Place(objects, used, size, ruleset, "euro.market", p, bx + 4, by - 4) && Place(objects, used, size, ruleset, "euro.market", p, bx - 10, by - 4))
            {
                objects.Add(new MapObject("euro.trader", (byte)p, Center(bx + 5, size), Center(by - 5, size)));
            }

            // A tavern (ADR 0108) for kingdoms 0 and 1 of three or more (as their alliance and trade): they hire a mercenary
            // group and a hero; two-kingdom runs (the allocation gate) keep their map.
            if (options.Taverns && ruleset.EntityTypeId("euro.tavern") >= 0 && p < 2 && options.Players >= 3)
            {
                Place(objects, used, size, ruleset, "euro.tavern", p, bx - 6, by - 8);
            }

            // Keep the bot's gate, wall and bridge spots free of trees.
            Reserve(used, size, bx - 3, by + 1, 1, 3);
            Reserve(used, size, bx - 6, by - 3, 6, 1);
            Reserve(used, size, bx + RiverLeft - 1, by + RiverTop + 3, 5, 2);

            // A short river beside the base (columns +2..+4, rows +12..+19) for the bot's bridge (M3, ADR 0045).
            for (int ry = by + RiverTop; ry < by + RiverTop + 8; ry++)
            {
                for (int rx = bx + RiverLeft; rx < bx + RiverLeft + 3; rx++)
                {
                    if ((uint)rx < (uint)size && (uint)ry < (uint)size)
                    {
                        water[(ry * size) + rx] = 1;
                        used[(ry * size) + rx] = true;
                    }
                }
            }
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

            AddCombatMagicObjects(objects, used, size, ruleset, p, bx, by); // GAME-07…11 (ADR 0096)

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

        // Nature in the middle (GAME-15): a magic gateway the armies meet at, bears and wild cows around it.
        if (ruleset.EntityTypeId("comm.magicgateway") >= 0)
        {
            Place(objects, used, size, ruleset, "comm.magicgateway", MapObject.Nature, center - 1, center - 1);
        }

        for (int a = 0; a < 4; a++)
        {
            (int ox, int oy) = Directions[a];
            if (ruleset.EntityTypeId("comm.bear") >= 0 && a < 2)
            {
                objects.Add(new MapObject("comm.bear", MapObject.Nature, Center(center + (ox * 6 / 100), size), Center(center + (oy * 6 / 100), size)));
            }

            if (ruleset.EntityTypeId("comm.cow") >= 0)
            {
                objects.Add(new MapObject("comm.cow", MapObject.Nature, Center(center + (oy * 9 / 100), size), Center(center - (ox * 9 / 100), size)));
            }
        }

        MapData terrain = ScenarioMaps.Build($"gameplay-{size}", size, size, water, new byte[size * size]);
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
                case SimEventKind.ResearchCompleted: _researched++; break;
                case SimEventKind.Captured: _captured++; break;
                case SimEventKind.FireStarted: _fires++; break;
                case SimEventKind.Repaired: _repaired++; break;
                case SimEventKind.Claimed: _claimed++; break;
                case SimEventKind.TradeDelivered: _sales++; break;
                case SimEventKind.Hired: _hired++; break;
                case SimEventKind.TributeSent: _tributes++; break;
                case SimEventKind.DiplomacyChanged: _diplomacy++; break;
                default: CountCombatMagic(e.Kind); break;
            }
        }
    }

    /// <inheritdoc />
    public string Describe()
        => string.Create(CultureInfo.InvariantCulture, $"entities {Simulation.World.Entities.AliveCount}, delivered {_delivered}, kills {_kills}, trained {_trained}, built {_built}, researched {_researched}, captured {_captured}, fires {_fires}, repaired {_repaired}, claimed {_claimed}, sales {_sales}, tributes {_tributes}, diplomacy {_diplomacy}, hired {_hired}, gates {Simulation.World.Gates.Count}, bridge tiles {BridgeTiles()}, corpses {Simulation.World.Decals.Count}, spells {_spells}, projectiles {_projectiles}, boarded {_boarded}, items {_picked}, level-ups {_levels}");

    /// <inheritdoc />
    public void Dispose() => Simulation.Dispose();

    // One bot pass for a kingdom: idle peasants harvest, a house now and then, footmen from the barracks, idle soldiers
    // move-advance to the center.
    private void Think(World world, byte player, int tick)
    {
        _idleWorkers.Clear();
        _allWorkers.Clear();
        _idleSoldiers.Clear();
        EntityId barracks = EntityId.None;
        EntityId castle = EntityId.None;
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
            else if (type == _castle && castle.IsNone && world.Productions.Has(e))
            {
                castle = e;
            }
            else if (world.Workers.IndexOf(e) is >= 0 and var w)
            {
                _allWorkers.Add(e);
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

        WorldOrders(world, player, tick);

        // M3 (ADR 0045): the bot also captures, repairs, attacks houses (fire), builds a gate, drags a wall and bridges
        // its river, so the cross-OS golden and hash streams cover those systems.
        int first = 0;
        if (!castle.IsNone)
        {
            first = M3Orders(world, player, tick, castle);
            CombatMagicOrders(world, player, tick, castle); // GAME-07…11: holders, items, spells, stances (ADR 0096)
        }

        // Peasants: two build a house every 400 ticks; the rest harvest the node nearest to them.
        if (tick % 400 < _options.BotIntervalTicks && _idleWorkers.Count >= first + 2)
        {
            Position at = world.Positions.Get(_idleWorkers[first]);
            int tx = (at.X / MapState.UnitsPerTile) + _bot.NextInt(-6, 7);
            int ty = (at.Y / MapState.UnitsPerTile) + _bot.NextInt(-6, 7);
            _selection.Clear();
            _selection.Add(_idleWorkers[first]);
            _selection.Add(_idleWorkers[first + 1]);
            _batch.Add(CommandKind.Build, Span(_selection), Math.Max(0, tx) * MapState.UnitsPerTile, Math.Max(0, ty) * MapState.UnitsPerTile, arg0: _house);
            first += 2;
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

        // GAME-06: the castle researches its technologies one after another (cheaper peasants, faster building...).
        if (!castle.IsNone && tick % 600 < _options.BotIntervalTicks && world.TypeOf(castle).Traits.Producer is { } producer)
        {
            foreach (ushort tech in producer.Researches)
            {
                if (!world.Players[player].HasTechnology(tech))
                {
                    _selection.Clear();
                    _selection.Add(castle);
                    _batch.Add(CommandKind.Research, Span(_selection), arg0: tech);
                    break;
                }
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

    // M3 stream F (ADR 0107): kingdoms 0 and 1 ally (shared vision, resources, victory) and their caravans trade between
    // their two markets; everyone sends a small tribute now and then; kingdom 2 turns neutral towards 3 later on.
    private void WorldOrders(World world, byte player, int tick)
    {
        int window = _options.BotIntervalTicks;
        int players = _options.Players;
        if (player < 2 && players >= 3) // two-kingdom runs (allocation gate) keep their battle
        {
            byte other = (byte)(1 - player);
            if (tick < window)
            {
                _batch.Add(CommandKind.Cheat, arg0: (int)Systems.CheatCode.NoTech);
            }

            if (tick >= 40 && tick < 40 + window)
            {
                _batch.Add(CommandKind.SetDiplomacy, x: other, arg0: (int)Kingdoms.Stance.Ally,
                    arg1: (int)(Kingdoms.Sharing.Vision | Kingdoms.Sharing.Resources | Kingdoms.Sharing.Victory));
            }

            if (tick >= 120 && tick < 120 + window && _market >= 0 && Market(world, player, 0) is { IsNone: false } own && Market(world, player, 1) is { IsNone: false } theirs)
            {
                _selection.Clear();
                ReadOnlySpan<EntityId> traders = world.Motions.Entities;
                foreach (EntityId t in traders)
                {
                    if (world.Identities.Get(t).Owner == player && world.TypeOf(t).Traits.World.Trader is not null)
                    {
                        _selection.Add(t);
                    }
                }

                Position b = world.Positions.Get(theirs);
                _batch.Add(CommandKind.Trade, Span(_selection), b.X, b.Y, target: own, arg0: 1 | (3 << 8), arg1: 50);
            }

            if (tick >= 600 && tick < 600 + window && _tavern >= 0 && Tavern(world, player) is { IsNone: false } tavern)
            {
                _selection.Clear();
                _selection.Add(tavern);
                _batch.Add(CommandKind.Hire, Span(_selection), arg0: 0);
                _batch.Add(CommandKind.Hire, Span(_selection), arg0: world.Ruleset.World.MercenaryOffers); // the hero
            }
        }

        if (tick % 400 < window && players >= 2)
        {
            _batch.Add(CommandKind.Tribute, x: (player + 1) % players, arg0: 0, arg1: 10);
        }

        if (player == 2 && players >= 4 && tick >= 800 && tick < 800 + window)
        {
            _batch.Add(CommandKind.SetDiplomacy, x: 3, arg0: (int)Kingdoms.Stance.Neutral);
        }
    }

    private EntityId Tavern(World world, byte owner)
    {
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            Identity identity = world.Identities.Get(structures[i]);
            if (identity.TypeId == _tavern && identity.Owner == owner && !world.Constructions.Has(structures[i]))
            {
                return structures[i];
            }
        }

        return EntityId.None;
    }

    private EntityId Market(World world, byte owner, int nth)
    {
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            Identity identity = world.Identities.Get(structures[i]);
            if (identity.TypeId == _market && identity.Owner == owner && !world.Constructions.Has(structures[i]) && nth-- == 0)
            {
                return structures[i];
            }
        }

        return EntityId.None;
    }

    private const int RiverLeft = 2;
    private const int RiverTop = 12;

    // Uses the first idle workers for one M3 order per window; returns how many it used. Soldiers: every 1,000 ticks the
    // first ten idle ones attack the nearest enemy house.
    private int M3Orders(World world, byte player, int tick, EntityId castle)
    {
        Footprint f = world.Footprints.Get(castle);
        int bx = f.TileX;
        int by = f.TileY;
        int window = _options.BotIntervalTicks;
        int used = 0;
        EntityId Take()
        {
            return used < _idleWorkers.Count ? _idleWorkers[used++] : EntityId.None;
        }

        if (tick % 1000 < window && _idleSoldiers.Count >= 10 && NearestEnemy(world, player, _house, world.Positions.Get(castle)) is { IsNone: false } victim)
        {
            _batch.Add(CommandKind.Attack, Span(_idleSoldiers)[..10], target: victim);
            _idleSoldiers.RemoveRange(0, 10);
        }

        if (tick % 900 < window && _idleWorkers.Count >= used + 2 && NearestEnemy(world, player, _house, world.Positions.Get(castle)) is { IsNone: false } house)
        {
            _selection.Clear();
            _selection.Add(Take());
            _selection.Add(Take());
            _batch.Add(CommandKind.Capture, Span(_selection), target: house);
        }

        // The busy worker nearest to the damaged building repairs it: idle workers became rare once units walked at
        // SPEED x 10 px/s, and the one sent from far away arrived too late to finish (ADR 0171).
        if (tick % 300 < window && DamagedOwn(world, player) is { IsNone: false } damaged
            && NearestBusyWorker(world, world.Positions.Get(damaged)) is { IsNone: false } repairer)
        {
            _selection.Clear();
            _selection.Add(repairer);
            _batch.Add(CommandKind.Repair, Span(_selection), target: damaged);
        }

        // Three busy workers (from the end of the list, so the idle passes above keep theirs) get the gate, the wall and
        // the bridge.
        EntityId WorkerFromEnd(int k) => _allWorkers.Count > k ? _allWorkers[^(k + 1)] : EntityId.None;
        EntityId builder = WorkerFromEnd(0);
        if (tick >= 200 && tick < 200 + window && !builder.IsNone && _gate >= 0)
        {
            _selection.Clear();
            _selection.Add(builder);
            _batch.Add(CommandKind.Build, Span(_selection), (bx - 3) * MapState.UnitsPerTile, (by + 1) * MapState.UnitsPerTile, arg0: _gate, arg1: 1);
        }

        if (tick >= 400 && tick < 400 + window && !WorkerFromEnd(1).IsNone && _wall >= 0)
        {
            _selection.Clear();
            _selection.Add(WorkerFromEnd(1));
            _batch.Add(CommandKind.BuildWall, Span(_selection), (bx - 6) * MapState.UnitsPerTile, (by - 3) * MapState.UnitsPerTile, arg0: _wall, arg1: ((by - 3) << 16) | (bx - 1));
        }

        if (tick % 350 < window && !WorkerFromEnd(2).IsNone && _bridge >= 0 && !_bridged[player])
        {
            _selection.Clear();
            _selection.Add(WorkerFromEnd(2));
            _batch.Add(CommandKind.Build, Span(_selection), (bx + RiverLeft - 1) * MapState.UnitsPerTile, (by + RiverTop + 3) * MapState.UnitsPerTile, arg0: _bridge);
            if (world.Players[player].HasTechnology(world.Ruleset.TechnologyId("te_architecture")))
            {
                _bridged[player] = true; // one attempt once the bridge is allowed
            }
        }

        return used;
    }

    private static void Reserve(bool[] used, int size, int x0, int y0, int w, int h)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                if ((uint)x < (uint)size && (uint)y < (uint)size)
                {
                    used[(y * size) + x] = true;
                }
            }
        }
    }

    private static EntityId NearestEnemy(World world, byte player, ushort type, Position from)
    {
        EntityId best = EntityId.None;
        long bestDistance = long.MaxValue;
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            Identity identity = world.Identities.Get(structures[i]);
            if (identity.TypeId != type || identity.Owner == player || !world.HasPlayer(identity.Owner) || world.Constructions.Has(structures[i]))
            {
                continue;
            }

            Position p = world.Positions.Get(structures[i]);
            long d = Core.Numerics.IntMath.DistanceSquared(from.X, from.Y, p.X, p.Y);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = structures[i];
            }
        }

        return best;
    }

    /// <summary>The working (not idle) worker nearest to a point, leaving out the three that build the gate, wall and bridge.</summary>
    private EntityId NearestBusyWorker(World world, Position to)
    {
        EntityId best = EntityId.None;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < _allWorkers.Count - 3; i++)
        {
            EntityId w = _allWorkers[i];
            if (_idleWorkers.Contains(w))
            {
                continue;
            }

            Position at = world.Positions.Get(w);
            long d = Core.Numerics.IntMath.DistanceSquared(at.X, at.Y, to.X, to.Y);
            if (d < bestDistance)
            {
                (best, bestDistance) = (w, d);
            }
        }

        return best;
    }

    /// <summary>
    /// The own building that is not on fire with the fewest hit points missing. The first damaged one in the list was
    /// usually a burning house that lost hit points faster than one repairer restores them, so no repair completed once
    /// units walked at SPEED x 10 px/s (ADR 0171).
    /// </summary>
    private static EntityId DamagedOwn(World world, byte player)
    {
        EntityId best = EntityId.None;
        int bestMissing = int.MaxValue;
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            EntityId s = structures[i];
            int missing = world.Healths.Get(s).Max - world.Healths.Get(s).Current;
            if (world.Identities.Get(s).Owner == player && !world.Constructions.Has(s) && world.TypeOf(s).Traits.IsRepairable
                && !world.Burnings.Has(s) && missing > 0 && missing < bestMissing)
            {
                (best, bestMissing) = (s, missing);
            }
        }

        return best;
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
