using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 10 (status effects, TDD §8.5) for structures and casters (GAME-01/03; ADR 0043): automatic gates open
/// while one of their owner's units is near and close when none is (never on a unit); badly damaged buildings burn and
/// lose hit points until repaired above the threshold or burned down; casters regain mana.
/// </summary>
public sealed class StructureSystem : ISimSystem
{
    private readonly List<EntityId> _burnedDown = [];
    private readonly List<(EntityId Gate, bool Open)> _gateChanges = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Spells;

    /// <inheritdoc />
    public string Name => "structures";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        int tickMs = world.Config.TickLengthMilliseconds;
        UpdateGates(world);
        Burn(world, tickMs);
        RegainMana(world, tickMs);
    }

    /// <summary>Whether a unit stands on a gate's passage tiles (a gate never closes on one).</summary>
    internal static bool PassageOccupied(World world, EntityId gate)
    {
        Footprint f = world.Footprints.Get(gate);
        EntityTypeDefinition type = world.TypeOf(gate);
        (int tx, int ty, int w, int h) = world.FootprintRect(gate);
        MapState map = world.Map;
        ReadOnlySpan<byte> door = f.Passage(type);
        for (int dy = 0; dy < h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                if (door[(dy * w) + dx] == 0)
                {
                    continue;
                }

                int cx0 = (tx + dx) * map.CellsPerTileAxis;
                int cy0 = (ty + dy) * map.CellsPerTileAxis;
                for (int cy = cy0; cy < cy0 + map.CellsPerTileAxis; cy++)
                {
                    for (int cx = cx0; cx < cx0 + map.CellsPerTileAxis; cx++)
                    {
                        if (map.Occupancy[(cy * map.CellWidth) + cx] != 0)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    // Gates (ADR 0043). Automatic gates (gate_subai_on, the default) want to be open while one of the owner's units is
    // within gateOpenReachPx of the footprint and closed once none is within gateCloseReachPx (hysteresis); the units are
    // found through a coarse grid rebuilt this tick, so the work is bounded by the units near gates. Then every gate whose
    // wanted state differs is changed, at most maxGateChangesPerTick per tick (ascending entity order, the rest wait),
    // never sooner than gateChangeIntervalMs after its last change, and never closed on a unit standing in it. Every
    // kingdom's units pass an open gate (per-player portals are a pathfinding follow-up).
    private void UpdateGates(World world)
    {
        if (world.Gates.Count == 0)
        {
            return;
        }

        GameplaySettings g = world.Ruleset.Gameplay;
        ReadOnlySpan<EntityId> gates = world.Gates.Entities;
        Span<GateState> states = world.Gates.Components;
        bool anyAutomatic = false;
        foreach (GateState s in states)
        {
            anyAutomatic |= s.Mode == GateMode.Automatic;
        }

        if (anyAutomatic)
        {
            BuildMoverGrid(world);
            for (int i = 0; i < gates.Length; i++)
            {
                ref GateState state = ref states[i];
                if (state.Mode != GateMode.Automatic)
                {
                    continue;
                }

                byte owner = world.Identities.Get(gates[i]).Owner;
                (int X0, int Y0, int X1, int Y1) rect = Interaction.Rect(world, gates[i]);
                long nearest = NearestOwnMover(world, owner, rect, g.GateCloseReachUnits);
                if (state.Open == 0 && nearest <= g.GateOpenReachUnits)
                {
                    state.Desired = 1;
                }
                else if (state.Open != 0 && nearest > g.GateCloseReachUnits)
                {
                    state.Desired = 0;
                }
            }
        }

        _gateChanges.Clear();
        int interval = GameplayCommands.GateIntervalTicks(world);
        for (int i = 0; i < gates.Length && _gateChanges.Count < g.MaxGateChangesPerTick; i++)
        {
            GateState state = states[i];
            if (state.Desired != state.Open && (long)world.Tick.Value - state.LastChangeTick >= interval
                && (state.Desired != 0 || !PassageOccupied(world, gates[i])))
            {
                _gateChanges.Add((gates[i], state.Desired != 0));
            }
        }

        foreach ((EntityId gate, bool open) in _gateChanges)
        {
            world.SetGateOpen(gate, open);
        }
    }

    // Player-owned movers bucketed by 8-tile cells (stable counting sort, ascending entity order per cell).
    private void BuildMoverGrid(World world)
    {
        _gridW = (world.Map.WorldWidth + GridCell - 1) / GridCell;
        _gridH = (world.Map.WorldHeight + GridCell - 1) / GridCell;
        int cells = _gridW * _gridH;
        if (_cellStart.Length < cells + 1)
        {
            _cellStart = new int[cells + 1];
            _cursor = new int[cells];
        }

        Array.Clear(_cellStart, 0, cells + 1);
        ReadOnlySpan<EntityId> movers = world.Motions.Entities;
        if (_cellOf.Length < movers.Length)
        {
            _cellOf = new int[Math.Max(movers.Length, _cellOf.Length * 2)];
            _items = new int[_cellOf.Length];
        }

        for (int m = 0; m < movers.Length; m++)
        {
            Position p = world.Positions.Get(movers[m]);
            int cell = (Math.Clamp(p.Y / GridCell, 0, _gridH - 1) * _gridW) + Math.Clamp(p.X / GridCell, 0, _gridW - 1);
            _cellOf[m] = cell;
            _cellStart[cell + 1]++;
        }

        for (int c = 0; c < cells; c++)
        {
            _cellStart[c + 1] += _cellStart[c];
        }

        Array.Copy(_cellStart, _cursor, cells);
        for (int m = 0; m < movers.Length; m++)
        {
            _items[_cursor[_cellOf[m]]++] = m;
        }
    }

    // The edge distance from a gate's footprint to the nearest own mover within `limit` (beyond it: long.MaxValue).
    private long NearestOwnMover(World world, byte owner, (int X0, int Y0, int X1, int Y1) rect, long limit)
    {
        long reach = limit + EntityTypeDefinition.MaxRadius;
        int cx0 = (int)Math.Max(0, (rect.X0 - reach) / GridCell);
        int cy0 = (int)Math.Max(0, (rect.Y0 - reach) / GridCell);
        int cx1 = (int)Math.Min(_gridW - 1, (rect.X1 + reach) / GridCell);
        int cy1 = (int)Math.Min(_gridH - 1, (rect.Y1 + reach) / GridCell);
        long best = long.MaxValue;
        ReadOnlySpan<EntityId> movers = world.Motions.Entities;
        Span<Motion> motions = world.Motions.Components;
        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int cell = (cy * _gridW) + cx;
                for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                {
                    int m = _items[k];
                    if (!world.Diplomacy.OpensGateFor(owner, world.Identities.Get(movers[m]).Owner))
                    {
                        continue;
                    }

                    Position p = world.Positions.Get(movers[m]);
                    long d = IntMath.ISqrt(Interaction.DistanceSquaredToRect(p.X, p.Y, rect)) - motions[m].Radius;
                    best = Math.Min(best, Math.Max(0, d));
                }
            }
        }

        return best;
    }

    private const int GridCell = 8 * MapState.UnitsPerTile;
    private int[] _cellStart = [];
    private int[] _cursor = [];
    private int[] _cellOf = [];
    private int[] _items = [];
    private int _gridW;
    private int _gridH;

    // GAME-03 fire (UNK-42): a completed burnable building below burnThresholdPercent of its hit points burns and loses
    // burnDamagePerMinute; repaired above the threshold, the fire goes out; at 0 it burns down (a ruin stays).
    private void Burn(World world, int tickMs)
    {
        GameplaySettings g = world.Ruleset.Gameplay;
        if (g.BurnDamagePerMinute <= 0)
        {
            return;
        }

        int msPerPoint = Math.Max(1, 60_000 / g.BurnDamagePerMinute);
        _burnedDown.Clear();
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            EntityId building = structures[i];
            EntityTypeDefinition type = world.TypeOf(building);
            if (!type.Traits.Burnable || world.Constructions.Has(building))
            {
                continue;
            }

            ref Health health = ref world.Healths.Get(building);
            bool burning = (long)health.Current * 10_000 < (long)health.Max * g.BurnThresholdBasisPoints;
            int b = world.Burnings.IndexOf(building);
            if (!burning)
            {
                if (b >= 0)
                {
                    world.Burnings.Remove(building);
                    world.Events.Emit(new SimEvent(SimEventKind.FireExtinguished, world.Identities.Get(building).Owner, building, 0, 0, type.Id));
                }

                continue;
            }

            if (b < 0)
            {
                world.Burnings.Set(building, default);
                world.Events.Emit(new SimEvent(SimEventKind.FireStarted, world.Identities.Get(building).Owner, building, 0, 0, type.Id));
                b = world.Burnings.IndexOf(building);
            }

            ref Burning fire = ref world.Burnings.At(b);
            fire.AccumMs += tickMs;
            while (fire.AccumMs >= msPerPoint && health.Current > 0)
            {
                fire.AccumMs -= msPerPoint;
                health.Current--;
            }

            fire.AccumMs = Math.Min(fire.AccumMs, 59_999);
            if (health.Current <= 0)
            {
                _burnedDown.Add(building);
            }
        }

        foreach (EntityId building in _burnedDown)
        {
            Identity identity = world.Identities.Get(building);
            world.Events.Emit(new SimEvent(SimEventKind.BurnedDown, identity.Owner, building, 0, 0, identity.TypeId));
            world.Kill(building);
        }
    }

    // Mana regenerates per minute of game time (ManaPool.regenPerMinute and the owner's technologies, e.g. Concentration),
    // up to the pool's maximum. The share of each tick comes from the tick number, so no remainder is stored and the
    // long-run rate is exact.
    private static void RegainMana(World world, int tickMs)
    {
        long now = (long)world.Tick.Value * tickMs;
        ReadOnlySpan<EntityId> casters = world.Manas.Entities;
        Span<ManaState> pools = world.Manas.Components;
        for (int i = 0; i < casters.Length; i++)
        {
            Identity identity = world.Identities.Get(casters[i]);
            ManaPoolDefinition pool = world.Ruleset.EntityTypes[identity.TypeId].Traits.ManaPool!;
            long max = (long)world.Stats.Apply(identity.Owner, ModifierStat.ManaMax, identity.TypeId, pool.Max) * 1000;
            long regen = world.Stats.Apply(identity.Owner, ModifierStat.ManaRegen, identity.TypeId, pool.RegenPerMinute);
            long gained = ((now + tickMs) * regen / 60) - (now * regen / 60); // milli-mana: regen × 1,000 / 60,000 ms
            pools[i].MilliMana = (int)Math.Clamp(pools[i].MilliMana + gained, 0, Math.Min(max, int.MaxValue));
        }
    }
}
