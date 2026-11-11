using System;
using System.IO;
using System.Runtime.InteropServices;
using Tzar2.Content.Compiled;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Components;
using Tzar2.Sim.Ecs;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Map;

namespace Tzar2.Sim;

/// <summary>
/// The world-system state of M3 stream F (ADRs 0100-0106): diplomacy, per-kingdom state (statistics, loans, cheats,
/// outcome), visibility, trade routes and Nature homes, plus the day clock and weather (pure functions of the tick).
/// </summary>
public sealed partial class World
{
    private KingdomState[] _kingdoms = [];
    private System.Collections.Generic.Dictionary<string, ushort[]>? _barracksPools;
    private byte[] _gateBars = [];

    /// <summary>Stances and sharing between the kingdoms (GAME-13).</summary>
    public Diplomacy Diplomacy { get; private set; } = null!;

    /// <summary>Per-kingdom visibility (GAME-12).</summary>
    public Visibility Visibility { get; private set; } = null!;

    /// <summary>Per-kingdom world state by slot: statistics, loan, cheats, outcome (GAME-13/14/17/19).</summary>
    public Span<KingdomState> Kingdoms => _kingdoms;

    /// <summary>Per-kingdom world state by slot, read-only (the AI and other readers; ADR 0213).</summary>
    public ReadOnlySpan<KingdomState> KingdomsView => _kingdoms;

    /// <summary>Trade routes of trading units (hot; GAME-14).</summary>
    public ComponentStore<TradeRoute> TradeRoutes { get; private set; } = null!;

    /// <summary>Home of Nature's wandering and guarding units (hot; GAME-15).</summary>
    public ComponentStore<NatureHome> NatureHomes { get; private set; } = null!;

    /// <summary>The minute of the 24-hour day clock after the last completed tick (GAME-16; noon when the cycle is off).</summary>
    public int DayMinute => MinuteAt(Tick.Value);

    /// <summary>Whether it is night now (GAME-16).</summary>
    public bool IsNight => IsNightAt(MinuteAt(Tick.Value));

    /// <summary>The weather now (GAME-16): the terrain set's precipitation in periods that have weather, else clear.</summary>
    public WeatherKind Weather => WeatherAt(Tick.Value);

    /// <summary>The day clock at <paramref name="tick"/>.</summary>
    public int MinuteAt(int tick)
    {
        WorldSettings w = Ruleset.World;
        if (!Config.DayNight || w.DayLengthMs <= 0)
        {
            return 720;
        }

        long ms = (long)tick * Config.TickLengthMilliseconds % w.DayLengthMs;
        int shift = Scenario?.DayClockShiftMinutes ?? 0; // @settime (SCN-011)
        return (int)((w.DayStartMinute + (ms * 1440 / w.DayLengthMs) + shift) % 1440);
    }

    /// <summary>Whether <paramref name="minute"/> of the day clock is night.</summary>
    public bool IsNightAt(int minute)
    {
        WorldSettings w = Ruleset.World;
        if (!Config.DayNight || w.DayLengthMs <= 0)
        {
            return false;
        }

        return w.NightStartMinute > w.NightEndMinute
            ? minute >= w.NightStartMinute || minute < w.NightEndMinute
            : minute >= w.NightStartMinute && minute < w.NightEndMinute;
    }

    /// <summary>The weather at <paramref name="tick"/>: hashed from the match seed per weather period (no state).</summary>
    public WeatherKind WeatherAt(int tick)
    {
        WorldSettings w = Ruleset.World;
        WeatherKind kind = w.WeatherOf(Map.Data.TerrainSet);
        if (!Config.Weather || kind == WeatherKind.Clear)
        {
            return WeatherKind.Clear;
        }

        long period = (long)tick * Config.TickLengthMilliseconds / w.WeatherPeriodMs;
        ulong roll = SplitMix64.Mix(Config.MatchSeed ^ 0x57EA_7E35_0000_0000UL ^ (ulong)period);
        return (int)(roll % 10_000) < w.WeatherChanceBasisPoints ? kind : WeatherKind.Clear;
    }

    /// <summary>Whether <paramref name="player"/> may command <paramref name="entity"/> (own, or shared unit control; GAME-13).</summary>
    public bool MayCommand(int player, EntityId entity)
        => Entities.IsAlive(entity) && Identities.IndexOf(entity) is >= 0 and var i && Diplomacy.MayCommand(player, Identities.At(i).Owner);

    /// <summary>
    /// Whether units of <paramref name="attackerOwner"/> may attack <paramref name="target"/> (GAME-13 stances; ambient
    /// birds and unclaimed gateways or wild cows never; cheats: <c>hmgod</c> kingdoms are still targets but take no damage).
    /// </summary>
    public bool MayAttack(int attackerOwner, EntityId target)
    {
        int i = Identities.IndexOf(target);
        if (i < 0)
        {
            return false;
        }

        Identity identity = Identities.At(i);
        WorldTraits w = Ruleset.EntityTypes[identity.TypeId].Traits.World;
        if (w.IsAmbient || w.Indestructible || (w.Claim is not null && !HasPlayer(identity.Owner)))
        {
            return false;
        }

        if (attackerOwner != identity.Owner && IsRescue(identity.Owner))
        {
            return false; // NAT-008 (ReTzar's reading, the relation R[x][9] = 0x40): nobody attacks the rescue kingdom
        }

        return Diplomacy.MayAttack(attackerOwner, identity.Owner, w.IsMilitary);
    }

    /// <summary>Whether <paramref name="owner"/> is the map's rescue pseudo-kingdom (NAT-008).</summary>
    public bool IsRescue(int owner) => HasPlayer(owner) && (Players[owner].Flags & PlayerFlags.Rescue) != 0;

    /// <summary>
    /// Whether <paramref name="owner"/> is a kingdom that may free prisoners and take magic gateways (NAT-008, NAT-001;
    /// ReTzar 0x5028a0): in the match, not defeated, not the rescue kingdom, and not barred by <c>@kingdomcanrescue</c>.
    /// </summary>
    public bool MayRescue(int owner)
        => HasPlayer(owner) && (Players[owner].Flags & (PlayerFlags.Active | PlayerFlags.Defeated | PlayerFlags.Rescue | PlayerFlags.NoRescue)) == PlayerFlags.Active;

    /// <summary>Whether a kingdom's units and buildings take no damage (<c>hmgod</c>, GAME-19).</summary>
    public bool IsInvulnerable(int owner) => HasPlayer(owner) && (_kingdoms[owner].Cheats & CheatFlags.God) != 0;

    /// <summary>Whether a kingdom ignores technology and building requirements (<c>hmnotech</c>, GAME-19).</summary>
    public bool IgnoresRequirements(int player) => HasPlayer(player) && (_kingdoms[player].Cheats & CheatFlags.NoTech) != 0;

    /// <summary>Whether a kingdom builds, trains and researches at once (<c>hmbuildozer</c>, GAME-19).</summary>
    public bool BuildsInstantly(int player) => HasPlayer(player) && (_kingdoms[player].Cheats & CheatFlags.Buildozer) != 0;

    /// <summary>
    /// Whether an open gate's passage at (<paramref name="toX"/>, <paramref name="toY"/>) bars a unit of
    /// <paramref name="owner"/> coming from (<paramref name="fromX"/>, <paramref name="fromY"/>): gates let only their owner and
    /// the kingdoms it treats as allies through (TDD §9.1, ADR 0102). A unit already on the passage may leave it.
    /// </summary>
    internal bool GateBars(byte owner, int fromX, int fromY, int toX, int toY)
    {
        if (_gateBars.Length == 0)
        {
            return false;
        }

        int to = ((toY / MapState.UnitsPerTile) * Map.Width) + (toX / MapState.UnitsPerTile);
        int from = ((fromY / MapState.UnitsPerTile) * Map.Width) + (fromX / MapState.UnitsPerTile);
        if (to == from || (uint)to >= (uint)_gateBars.Length || _gateBars[to] == 0)
        {
            return false;
        }

        return !Diplomacy.OpensGateFor(_gateBars[to] - 1, owner);
    }

    /// <summary>Records the passage tiles of open gates and their owners (movement, step 8; derived each tick, not state).</summary>
    internal void RefreshGateBars()
    {
        if (Gates.Count == 0)
        {
            if (_gateBars.Length != 0)
            {
                Array.Clear(_gateBars);
            }

            return;
        }

        if (_gateBars.Length != Map.Width * Map.Height)
        {
            _gateBars = new byte[Map.Width * Map.Height];
        }
        else
        {
            Array.Clear(_gateBars);
        }

        ReadOnlySpan<EntityId> gates = Gates.Entities;
        Span<GateState> states = Gates.Components;
        for (int g = 0; g < gates.Length; g++)
        {
            byte owner = Identities.Get(gates[g]).Owner;
            if (states[g].Open == 0 || !HasPlayer(owner))
            {
                continue;
            }

            Footprint f = Footprints.Get(gates[g]);
            EntityTypeDefinition type = TypeOf(gates[g]);
            int w = f.Width(type);
            int h = f.Height(type);
            ReadOnlySpan<byte> door = f.Passage(type);
            for (int dy = 0; dy < h; dy++)
            {
                for (int dx = 0; dx < w; dx++)
                {
                    if (door[(dy * w) + dx] != 0)
                    {
                        _gateBars[((f.TileY + dy) * Map.Width) + f.TileX + dx] = (byte)(owner + 1);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The unit types a civilization's barracks train (TRADE-009; the original draws a tavern offer from the buttons of a
    /// random civilization's barracks, 0x4d2030), ascending by type id; derived from the ruleset, not state.
    /// </summary>
    internal ReadOnlySpan<ushort> BarracksPool(string civilization)
    {
        _barracksPools ??= [];
        if (!_barracksPools.TryGetValue(civilization, out ushort[]? pool))
        {
            var list = new System.Collections.Generic.List<ushort>();
            if (Ruleset.EntityTypeId(civilization + ".barrack") is >= 0 and var b && Ruleset.EntityTypes[b].Traits.Producer is { } producer)
            {
                foreach (ushort id in producer.Produces)
                {
                    EntityTypeDefinition t = Ruleset.EntityTypes[id];
                    if (t.Traits.Category == EntityCategory.Unit && t.Locomotor is Locomotor.Foot or Locomotor.Mounted or Locomotor.Climber)
                    {
                        list.Add(id);
                    }
                }
            }

            list.Sort();
            pool = [.. list];
            _barracksPools[civilization] = pool;
        }

        return pool;
    }

    /// <summary>Adds to a stockpile up to <see cref="WorldSettings.StockpileCap"/> (incomes of the world systems: overflow guard).</summary>
    internal void Credit(int player, int resource, long amount)
    {
        ref int stock = ref _players[player].Resources[resource];
        stock = (int)Math.Clamp(stock + amount, 0, Ruleset.World.StockpileCap);
    }

    // Units inside holders (GAME-09: World.IsHeld, stream E) reveal nothing; cloaked units (GAME-11 invisibility and ninja
    // cloak: World.IsCloaked) are concealed from kingdoms without a detector near.
    internal bool IsConcealed(EntityId entity) => IsCloaked(entity);

    // Called once at the end of the constructor: the world-system state and its stores (appended after the others).
    private void InitializeKingdoms()
    {
        TradeRoutes = Components.RegisterHot<TradeRoute>("world.trade");
        NatureHomes = Components.RegisterHot<NatureHome>("world.home");
        Diplomacy = new Diplomacy(_players.Length);
        Diplomacy.ApplyTeams(Config);
        Visibility = new Visibility(Map.Width, Map.Height, _players.Length);
        _kingdoms = new KingdomState[_players.Length];
        for (int p = 0; p < _kingdoms.Length; p++)
        {
            _kingdoms[p] = new KingdomState
            {
                LastTributeTick = int.MinValue / 2,
                LoanResource = KingdomState.NoLoan,
                LoanLimit = Ruleset.World.LoanStartAmount,
                LoanRaiseTick = -1,
                OutcomeTick = -1,
            };
        }
    }

    // Match start (after the map's objects): Deathmatch resources, Nature homes, the first sight.
    internal void StartKingdoms()
    {
        GameplaySettings g = Ruleset.Gameplay;
        string deathmatch = Ruleset.World.DeathmatchStartingResources;
        if (Config.GameType == GameType.Deathmatch && deathmatch.Length > 0)
        {
            foreach (StartingResources level in g.StartingResources)
            {
                if (level.Key != deathmatch)
                {
                    continue;
                }

                for (int p = 0; p < _players.Length; p++)
                {
                    for (int r = 0; r < level.Amounts.Length; r++)
                    {
                        _players[p].Resources[r] = level.Amounts[r];
                    }
                }
            }
        }

        ReadOnlySpan<EntityId> ids = Identities.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Identity identity = Identities.At(i);
            WorldTraits w = Ruleset.EntityTypes[identity.TypeId].Traits.World;
            if (identity.Owner == MapObject.Nature && (w.Wanders || w.Guards) && Positions.IndexOf(ids[i]) is >= 0 and var p)
            {
                Position at = Positions.At(p);
                NatureHomes.Set(ids[i], new NatureHome { X = at.X, Y = at.Y, NextMoveTick = (int)(SplitMix64.Mix(Config.MatchSeed ^ ids[i].Value) % 400) });
            }
        }

        Visibility.Start(this, null);
    }

    internal void AppendKingdomsTo(StateHasher hasher)
    {
        hasher.AddSpan<KingdomState>(_kingdoms);
        Diplomacy.AppendTo(hasher);
    }

    internal void WriteKingdomsTo(BinaryBufferWriter writer)
    {
        writer.WriteBytes(MemoryMarshal.AsBytes(_kingdoms.AsSpan()));
        Diplomacy.WriteTo(writer);
        Visibility.WriteTo(writer);
    }

    // Restore (before the stores are read: the visibility rebuild only reads the registry and the ruleset). Formats before
    // 23 had no tavern exchange: 16 bytes fewer at the end of each kingdom (ADR 0271).
    internal void ReadKingdomsFrom(ref BinaryBufferReader reader, int version = Snapshots.WorldSnapshot.FormatVersion)
    {
        int size = Marshal.SizeOf<KingdomState>();
        int stored = version < 23 ? size - 16 : size;
        ReadOnlySpan<byte> bytes = reader.ReadBytes(_kingdoms.Length * stored);
        Span<byte> into = MemoryMarshal.AsBytes(_kingdoms.AsSpan());
        into.Clear();
        for (int i = 0; i < _kingdoms.Length; i++)
        {
            bytes.Slice(i * stored, stored).CopyTo(into.Slice(i * size, stored));
        }

        int resources = Ruleset.ResourceTypes.Count;
        foreach (KingdomState k in _kingdoms)
        {
            bool loan = k.LoanResource != KingdomState.NoLoan;
            if (k.GatheredTotal < 0 || k.Score < 0 || k.BuildValue < 0 || k.TradeIncome < 0 || k.TributeSent < 0 || k.TributeReceived < 0
                || k.UnitsTrained < 0 || k.UnitsLost < 0 || k.UnitsKilled < 0 || k.BuildingsBuilt < 0 || k.BuildingsLost < 0
                || k.BuildingsDestroyed < 0 || k.BuildingsCaptured < 0 || k.TechnologiesResearched < 0 || k.LastTributeTick > Tick.Value
                || (loan && (k.LoanResource < 0 || k.LoanResource >= resources || k.LoanDue <= 0)) || (!loan && k.LoanDue != 0)
                || k.LoanLimit < 0 || k.LoanRaiseTick < -1 || k.OutcomeTick < -1 || k.OutcomeTick > Tick.Value
                || (k.Cheats & ~(CheatFlags.NoFog | CheatFlags.NoTech | CheatFlags.God | CheatFlags.Buildozer)) != 0
                || (k.Cheats != CheatFlags.None && !Config.CheatsAllowed)
                || k.ExchangeLeft < 0 || k.ExchangeLeft > k.ExchangeTotal || (k.ExchangeLeft > 0 && (k.ExchangeGive >= resources || k.ExchangeReceive >= resources))
                || k.TavernPeriod != 0 || k.TavernTaken != 0)
            {
                throw new InvalidDataException("Snapshot violates a world invariant: invalid kingdom state.");
            }

            for (int r = 0; r < Ruleset.MaxResourceTypes; r++)
            {
                if (k.Gathered[r] < 0 || (r >= resources && k.Gathered[r] != 0))
                {
                    throw new InvalidDataException("Snapshot violates a world invariant: invalid statistics.");
                }
            }
        }

        Diplomacy.ReadFrom(ref reader, Tick.Value);
        Visibility.ReadFrom(ref reader, this);
    }

    // The trade and Nature stores (after the stores are read): each entity has the matching trait and valid fields.
    internal void ValidateKingdomComponents()
    {
        RequireCore(TradeRoutes.Entities, mobile: true, structure: false);
        RequireCore(NatureHomes.Entities, mobile: false, structure: false);
        ReadOnlySpan<EntityId> ids = TradeRoutes.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            TradeRoute r = TradeRoutes.Components[i];
            TraderDefinition? trader = TypeOf(ids[i]).Traits.World.Trader;
            int resources = Ruleset.ResourceTypes.Count;
            if (trader is null || r.DockA == 0 || r.DockB == 0 || r.DockA == r.DockB || r.GoodsA >= resources || r.GoodsB >= resources
                || r.Amount < 1 || r.Amount > trader.Capacity || r.Carry < 0 || r.Carry > trader.Capacity || r.TimerMs < 0
                || r.Phase is < TradePhase.ToA or > TradePhase.ToB || r.LoadedAt > 2 || (r.Carry > 0) != (r.LoadedAt != 0)
                || (r.Carry > 0 && r.CarryResource >= resources) || r.Reserved != 0 || r.Reserved1 != 0 || !Map.ContainsPoint(r.LoadX, r.LoadY))
            {
                throw new InvalidDataException("Snapshot violates a world invariant: invalid trade route.");
            }
        }

        ids = NatureHomes.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            NatureHome h = NatureHomes.Components[i];
            WorldTraits w = TypeOf(ids[i]).Traits.World;
            bool grazes = w.Grazes;
            if ((!w.Wanders && !w.Guards && !grazes) || !Map.ContainsPoint(h.X, h.Y) || h.Returning > 1
                || (!grazes && (h.Pasture != 0 || h.Cycles != 0 || h.Graze != 0))
                || h.Pasture > 2 || h.Cycles > Systems.NatureSystem.GrazeCycles || (h.Graze & 7) > 4 || (h.Graze >> 3) > 2)
            {
                throw new InvalidDataException("Snapshot violates a world invariant: invalid Nature home.");
            }
        }
    }
}
