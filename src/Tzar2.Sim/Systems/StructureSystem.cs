using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
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
        for (int dy = 0; dy < h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                if (type.Traits.Gate!.Passage[f.MaskIndex(dx, dy, type.FootprintWidth)] == 0)
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

    // Automatic gates (gate_subai_on, the default): open for the owner's units within gateOpenReachPx of the footprint.
    // Every kingdom's units pass an open gate (per-player portals are a pathfinding follow-up, ADR 0043).
    private void UpdateGates(World world)
    {
        if (world.Gates.Count == 0)
        {
            return;
        }

        _gateChanges.Clear();
        long reach = world.Ruleset.Gameplay.GateOpenReachUnits;
        ReadOnlySpan<EntityId> gates = world.Gates.Entities;
        Span<GateState> states = world.Gates.Components;
        ReadOnlySpan<EntityId> movers = world.Motions.Entities;
        for (int i = 0; i < gates.Length; i++)
        {
            if (states[i].Mode != GateMode.Automatic)
            {
                continue;
            }

            byte owner = world.Identities.Get(gates[i]).Owner;
            (int X0, int Y0, int X1, int Y1) rect = Interaction.Rect(world, gates[i]);
            bool near = false;
            for (int m = 0; m < movers.Length && !near; m++)
            {
                if (world.Identities.Get(movers[m]).Owner != owner)
                {
                    continue;
                }

                Position p = world.Positions.Get(movers[m]);
                long r = reach + world.Motions.Components[m].Radius;
                near = Interaction.DistanceSquaredToRect(p.X, p.Y, rect) <= r * r;
            }

            bool open = states[i].Open != 0;
            if (near != open && (near || !PassageOccupied(world, gates[i])))
            {
                _gateChanges.Add((gates[i], near));
            }
        }

        foreach ((EntityId gate, bool open) in _gateChanges)
        {
            world.SetGateOpen(gate, open);
        }
    }

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
            world.Events.Emit(new SimEvent(SimEventKind.Decayed, identity.Owner, building, 0, 0, identity.TypeId));
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
