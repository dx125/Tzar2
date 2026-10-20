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
/// Pipeline step 6 (activities) for errands (ADRs 0093-0095): units walk to a holder and board it, to an item and pick it
/// up, to a friend and give it an item, to an enemy and bribe it; converting units wait out their conversion. Every
/// transfer happens when the unit arrives and is re-validated then (room, acceptance, ownership, gold), so an item
/// can never be in two places and a holder never overflows.
/// </summary>
public sealed class ErrandSystem : ISimSystem
{
    /// <summary>Least ticks between two movement requests of one errand.</summary>
    public const int MoveIntervalTicks = 10;

    /// <summary>Movement requests without arriving before an errand is given up.</summary>
    public const int MaxRetries = 8;

    private readonly List<EntityId> _finished = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Activities;

    /// <inheritdoc />
    public string Name => "errands";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        if (world.Errands.Count == 0)
        {
            return;
        }

        _finished.Clear();
        int tick = world.Tick.Value;
        int tickMs = world.Config.TickLengthMilliseconds;
        for (int i = 0; i < world.Errands.Count; i++)
        {
            EntityId unit = world.Errands.Entities[i];
            ref Errand e = ref world.Errands.At(i);
            bool done = e.Kind == ErrandKind.Convert ? Converting(world, unit, ref e, tickMs) : Walk(world, unit, ref e, tick);
            if (done)
            {
                _finished.Add(unit);
            }
        }

        // Completions change stores (boarding removes movement, conversions replace units): after the pass, in order.
        foreach (EntityId unit in _finished)
        {
            if (!world.Entities.IsAlive(unit) || world.Errands.IndexOf(unit) is not (>= 0 and var k))
            {
                continue;
            }

            Errand e = world.Errands.At(k);
            world.Errands.Remove(unit);
            Complete(world, unit, e);
        }
    }

    // Walks toward the target; returns true when the unit reached it (or the errand is void and must end).
    private static bool Walk(World world, EntityId unit, ref Errand e, int tick)
    {
        EntityId target = EntityId.FromValue(e.Target);
        if (!world.Entities.IsAlive(target) || !world.Positions.Has(target) || !world.CanAct(unit))
        {
            e.Kind = ErrandKind.None;
            return true;
        }

        if (Reached(world, unit, target))
        {
            Interaction.Halt(world, unit);
            return true;
        }

        if (Interaction.IsMoving(world, unit) && e.MoveTick >= 0 && tick - e.MoveTick < MoveIntervalTicks * 4)
        {
            return false;
        }

        if (e.MoveTick >= 0 && tick - e.MoveTick < MoveIntervalTicks)
        {
            return false;
        }

        if (world.Dockings.Has(target))
        {
            e.Retries = 0; // the ship is on its way to meet it (ADR 0140): wait for it, do not give up
        }

        if (++e.Retries > MaxRetries)
        {
            e.Kind = ErrandKind.None;
            return true;
        }

        e.MoveTick = tick;
        Position from = world.Positions.Get(unit);
        (int x, int y) = Interaction.WorkPoint(world, target, from, world.TypeOf(unit).Locomotor);
        if (e.Kind == ErrandKind.Board && world.IsShip(target) && !world.IsShip(unit))
        {
            // To the land next to the ship, not to the point of land nearest to its position (which may lie across a
            // spit); a ship that is not at a shore the unit can reach is asked to come again (ADR 0146).
            if (Shore.LandingNear(world, world.Identities.Get(unit).TypeId, from, world.Positions.Get(target), out Position landing))
            {
                (x, y) = (landing.X, landing.Y);
            }
            else if (e.Retries == (MaxRetries / 2) + 1 && !world.Dockings.Has(target))
            {
                NavalCommands.MeetAtShore(world, target, [unit], DockPurpose.Board);
            }
        }

        Interaction.MoveTo(world, unit, x, y);
        return false;
    }

    // Within the interaction reach; a ship is reached across one shore tile (like workers at ships, ADR 0042).
    private static bool Reached(World world, EntityId unit, EntityId target)
    {
        EntityTypeDefinition type = world.TypeOf(unit);
        EntityTypeDefinition other = world.TypeOf(target);
        bool across = other.IsMobile && other.Locomotor != type.Locomotor && (other.Locomotor == Locomotor.Ship || other.Locomotor == Locomotor.Air);
        int extra = across ? 2 * MapState.UnitsPerTile : 0;
        return Interaction.Reaches(world, world.Positions.Get(unit), type.Radius + extra, target, Interaction.IsMoving(world, unit));
    }

    private static bool Converting(World world, EntityId unit, ref Errand e, int tickMs)
    {
        Interaction.Halt(world, unit);
        e.TimerMs = Math.Max(0, e.TimerMs - tickMs);
        return e.TimerMs == 0;
    }

    private static void Complete(World world, EntityId unit, Errand e)
    {
        EntityId target = EntityId.FromValue(e.Target);
        byte owner = world.Identities.Get(unit).Owner;
        switch (e.Kind)
        {
            case ErrandKind.Board:
                if (CombatMagicCommands.CanBoard(world, owner, unit, target) is null)
                {
                    world.Board(unit, target);
                }

                break;
            case ErrandKind.Pickup:
                CombatMagicCommands.TryPickUp(world, unit, target);
                break;
            case ErrandKind.Give:
            {
                EntityId item = EntityId.FromValue((uint)e.Arg);
                if (world.Entities.IsAlive(target) && world.Identities.Get(target).Owner == owner && world.Helds.IndexOf(item) is >= 0 and var h
                    && world.Helds.At(h).Holder == unit.Value && CombatMagicCommands.CanCarry(world, target, item))
                {
                    world.PutInInventory(target, item);
                    world.Events.Emit(new SimEvent(SimEventKind.ItemPicked, owner, item, target.Index, 0, world.Identities.Get(item).TypeId));
                }

                break;
            }

            case ErrandKind.Bribe:
                CombatMagicCommands.TryBribe(world, unit, target);
                break;
            case ErrandKind.Convert:
                CombatMagicCommands.FinishConversion(world, unit, (ushort)e.Arg);
                break;
        }
    }
}

/// <summary>
/// Pipeline step 13 (TDD §8.5, GAME-09; ADR 0093): units inside a holder move with it and heal there (castles and
/// outposts, UNK-24).
/// </summary>
public sealed class HolderSystem : ISimSystem
{
    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Holders;

    /// <inheritdoc />
    public string Name => "holders";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        if (world.Helds.Count == 0)
        {
            return;
        }

        int tickMs = world.Config.TickLengthMilliseconds;

        // Healing: per holder the hit points of this tick, with carry-over (exact long-run rate).
        ReadOnlySpan<EntityId> holders = world.Holders.Entities;
        Span<HolderState> states = world.Holders.Components;
        for (int i = 0; i < holders.Length; i++)
        {
            ref HolderState s = ref states[i];
            int heal = world.TypeOf(holders[i]).Traits.Holder!.HealPerMinute;
            if (s.Count == 0 || heal == 0)
            {
                s.HealAccumMs = 0;
                continue;
            }

            long t = s.HealAccumMs;
            int amount = (int)(((t + tickMs) * heal / 60_000) - (t * heal / 60_000));
            s.HealAccumMs = (int)((t + tickMs) % 60_000);
            if (amount > 0)
            {
                HealInside(world, holders[i], amount);
            }
        }

        // Units inside follow their holder (ships, carpets).
        ReadOnlySpan<EntityId> held = world.Helds.Entities;
        Span<Held> holds = world.Helds.Components;
        for (int i = 0; i < held.Length; i++)
        {
            EntityId holder = EntityId.FromValue(holds[i].Holder);
            world.Positions.Get(held[i]) = world.Positions.Get(holder);
        }
    }

    private static void HealInside(World world, EntityId holder, int amount)
    {
        ReadOnlySpan<EntityId> held = world.Helds.Entities;
        Span<Held> holds = world.Helds.Components;
        for (int i = 0; i < held.Length; i++)
        {
            if (holds[i].Holder != holder.Value || world.Items.Has(held[i]))
            {
                continue;
            }

            ref Health h = ref world.Healths.Get(held[i]);
            h.Current = (int)Math.Min((long)h.Current + amount, h.Max);
        }
    }
}
