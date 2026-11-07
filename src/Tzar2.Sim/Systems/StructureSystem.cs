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
/// Pipeline step 10 (status effects, TDD §8.5) for structures and casters (GAME-01/03; ADR 0043): automatic gates close
/// while a stranger's land unit is near their door and stand open otherwise (never closed on a unit); badly damaged
/// buildings burn and lose hit points until repaired above the threshold or burned down; casters regain mana.
/// </summary>
public sealed class StructureSystem : ISimSystem
{
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
        TakeDown(world, tickMs);
        Burn(world);
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

    // Gates (ADR 0043; ORD-013 as the original, ReTzar 0x4571a0, 0x456fd0). An automatic gate (gate_subai_on, the default)
    // closes while a land unit of a kingdom it does not open for (ADR 0102) is within gateWatchTiles of its door, and stands
    // open otherwise; an order to open or close keeps that rule away for gateOrderHoldMs. The units are found through a
    // coarse grid rebuilt this tick, so the work is bounded by the units near gates. Then every gate whose wanted state
    // differs is changed, at most maxGateChangesPerTick per tick (ascending entity order, the rest wait), never sooner than
    // gateChangeIntervalMs after its last change, and never closed on a unit standing in it.
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

                if (state.Hold > 0)
                {
                    state.Hold--;
                    continue;
                }

                byte owner = world.Identities.Get(gates[i]).Owner;
                state.Desired = StrangerNear(world, gates[i], owner, g.GateWatchTiles) ? (byte)0 : (byte)1;
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

    // Movers bucketed by 8-tile cells (stable counting sort, ascending entity order per cell).
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

    // Whether a land unit of a kingdom the gate does not open for stands within `watch` tiles of its door (the original's
    // gate looks at land units not allied to its kingdom within 4 cells of the passage, ReTzar 0x4571a0).
    private bool StrangerNear(World world, EntityId gate, byte owner, int watch)
    {
        Footprint f = world.Footprints.Get(gate);
        EntityTypeDefinition type = world.TypeOf(gate);
        int w = f.Width(type);
        int h = f.Height(type);
        ReadOnlySpan<byte> door = f.Passage(type);
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (int dy = 0; dy < h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                if (door.Length == w * h && door[(dy * w) + dx] == 0)
                {
                    continue;
                }

                x0 = Math.Min(x0, f.TileX + dx);
                y0 = Math.Min(y0, f.TileY + dy);
                x1 = Math.Max(x1, f.TileX + dx);
                y1 = Math.Max(y1, f.TileY + dy);
            }
        }

        if (x0 > x1)
        {
            return false;
        }

        (x0, y0, x1, y1) = (x0 - watch, y0 - watch, x1 + watch, y1 + watch);
        int cx0 = Math.Max(0, x0 * MapState.UnitsPerTile / GridCell);
        int cy0 = Math.Max(0, y0 * MapState.UnitsPerTile / GridCell);
        int cx1 = Math.Min(_gridW - 1, ((x1 + 1) * MapState.UnitsPerTile) / GridCell);
        int cy1 = Math.Min(_gridH - 1, ((y1 + 1) * MapState.UnitsPerTile) / GridCell);
        ReadOnlySpan<EntityId> movers = world.Motions.Entities;
        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int cell = (cy * _gridW) + cx;
                for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                {
                    EntityId unit = movers[_items[k]];
                    Position p = world.Positions.Get(unit);
                    int tx = p.X / MapState.UnitsPerTile;
                    int ty = p.Y / MapState.UnitsPerTile;
                    if (tx < x0 || tx > x1 || ty < y0 || ty > y1 || world.Diplomacy.OpensGateFor(owner, world.Identities.Get(unit).Owner)
                        || world.TypeOf(unit).Locomotor is Locomotor.Air or Locomotor.Ship || world.IsHeld(unit))
                    {
                        continue;
                    }

                    return true;
                }
            }
        }

        return false;
    }

    private const int GridCell = 8 * MapState.UnitsPerTile;
    private int[] _cellStart = [];
    private int[] _cursor = [];
    private int[] _cellOf = [];
    private int[] _items = [];
    private int _gridW;
    private int _gridH;

    // BLD-006 (the original's code, 0x4200c0 and the fire timer 0x41efe9): a finished burnable building burns by its health,
    // and the fire takes nothing - it is pictures and a sound. Above half its hit points (as a whole percent) it does not
    // burn; down to 36 % one small fire, down to 16 % two, below that two and a big one. Repaired, the fire goes back down.
    private static void Burn(World world)
    {
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            EntityId building = structures[i];
            EntityTypeDefinition type = world.TypeOf(building);
            if (!type.Traits.Burnable || world.Constructions.Has(building))
            {
                continue;
            }

            int level = FireLevel(world.Healths.Get(building));
            int b = world.Burnings.IndexOf(building);
            if (level == 0)
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
                world.Burnings.Set(building, new Burning { Level = level });
                world.Events.Emit(new SimEvent(SimEventKind.FireStarted, world.Identities.Get(building).Owner, building, 0, 0, type.Id));
            }
            else
            {
                world.Burnings.At(b).Level = level;
            }
        }
    }

    /// <summary>The fires of a building at <paramref name="health"/> (BLD-006): 0 above half, then 1, 2 and 3.</summary>
    public static int FireLevel(in Health health)
    {
        int percent = health.Max <= 0 ? 100 : (int)((long)health.Current * 100 / health.Max);
        return percent switch
        {
            >= 51 => 0,
            >= 36 => 1,
            >= 16 => 2,
            _ => 3,
        };
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
            long max = (long)Math.Max(world.Stats.Apply(identity.Owner, ModifierStat.ManaMax, identity.TypeId, pool.Max), pools[i].RaisedMax) * 1000;
            long regen = world.Stats.Apply(identity.Owner, ModifierStat.ManaRegen, identity.TypeId, pool.RegenPerMinute);
            if (identity.Owner == MapObject.Nature)
            {
                regen = regen * world.Ruleset.Combat.NatureManaRegenPercent / 100; // a neutral caster regains half (ReTzar 0x502ea2)
            }

            long gained = ((now + tickMs) * regen / 60) - (now * regen / 60); // milli-mana: regen × 1,000 / 60,000 ms
            pools[i].MilliMana = (int)Math.Clamp(pools[i].MilliMana + gained, 0, Math.Min(max, int.MaxValue));
        }
    }

    // BLD-027: a building being taken down goes when its time is out, and gives back a share of what it cost.
    private readonly List<EntityId> _down = [];

    private void TakeDown(World world, int tickMs)
    {
        if (world.Dismantlings.Count == 0)
        {
            return;
        }

        _down.Clear();
        ReadOnlySpan<EntityId> ids = world.Dismantlings.Entities;
        Span<Dismantling> all = world.Dismantlings.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            all[i].LeftMs -= tickMs;
            if (all[i].LeftMs <= 0)
            {
                _down.Add(ids[i]);
            }
        }

        foreach (EntityId building in _down)
        {
            GameplayCommands.RefundDismantled(world, building);
            world.Destroy(building);
        }
    }
}
