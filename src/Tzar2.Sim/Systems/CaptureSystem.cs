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
/// Pipeline step 12 (TDD §8.5, GAME-04; ADR 0044): peasants digging at an enemy building add capture points; when the
/// points reach the building's capture resistance, it changes owner. The resistance is the building's RESISTANCE (after
/// its owner's technologies: Fanaticism, Buddhism) plus the EMIT influence of the buildings around it — its own
/// kingdom's raise it, other kingdoms' lower it (⚠ HYPOTHESIS UNK-19) — so buildings far from their towns resist less
/// (manual ch. 5). Progress belongs to one player at a time and is lost after <c>captureResetMs</c> without a capturer.
/// </summary>
public sealed class CaptureSystem : ISimSystem
{
    private readonly List<EntityId> _done = [];
    private readonly List<EntityId> _reset = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Capture;

    /// <inheritdoc />
    public string Name => "capture";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        int tickMs = world.Config.TickLengthMilliseconds;
        Span<CaptureState> states = world.CaptureStates.Components;
        for (int i = 0; i < states.Length; i++)
        {
            states[i].IdleMs = (int)Math.Min((long)states[i].IdleMs + tickMs, int.MaxValue / 2);
        }

        // Diggers, in ascending worker order.
        long milliPerTick = (long)world.Ruleset.Gameplay.CapturePointsPerSecond * tickMs;
        for (int i = 0; i < world.Workers.Count; i++)
        {
            Worker w = world.Workers.At(i);
            if (w.Task != WorkerTask.Capture || w.Phase != WorkerPhase.Capturing)
            {
                continue;
            }

            EntityId worker = world.Workers.Entities[i];
            EntityId target = EntityId.FromValue(w.Target);
            byte player = world.Identities.Get(worker).Owner;
            if (!world.Entities.IsAlive(target) || world.Identities.Get(target).Owner == player || world.TypeOf(target).Traits.Capturable is not { Instant: false }
                || world.Constructions.Has(target) || !Interaction.WorkerReaches(world, worker, target))
            {
                continue; // the worker system re-approaches or gives up
            }

            int c = world.CaptureStates.IndexOf(target);
            if (c < 0)
            {
                world.CaptureStates.Set(target, new CaptureState { Player = player });
                c = world.CaptureStates.IndexOf(target);
            }

            ref CaptureState state = ref world.CaptureStates.At(c);
            if (state.Player != player)
            {
                if (state.MilliPoints > 0 && state.IdleMs <= tickMs)
                {
                    continue; // another kingdom dug here this tick or the last: its progress stands until it stops
                }

                state.Player = player;
                state.MilliPoints = 0;
            }

            state.MilliPoints = (int)Math.Min(state.MilliPoints + milliPerTick, int.MaxValue / 2);
            state.IdleMs = 0;
        }

        _done.Clear();
        _reset.Clear();
        ReadOnlySpan<EntityId> targets = world.CaptureStates.Entities;
        states = world.CaptureStates.Components;
        for (int i = 0; i < targets.Length; i++)
        {
            if (states[i].IdleMs >= world.Ruleset.Gameplay.CaptureResetMs && states[i].IdleMs > 0)
            {
                _reset.Add(targets[i]);
            }
            else if (states[i].MilliPoints >= (long)CaptureResistance(world, targets[i]) * 1000)
            {
                _done.Add(targets[i]);
            }
        }

        foreach (EntityId target in _reset)
        {
            world.CaptureStates.Remove(target);
        }

        foreach (EntityId target in _done)
        {
            byte player = world.CaptureStates.Get(target).Player;
            byte previous = world.Identities.Get(target).Owner;
            world.ChangeOwner(target, player);
            world.Events.Emit(new SimEvent(SimEventKind.Captured, player, target, previous, 0, world.Identities.Get(target).TypeId));
        }
    }

    /// <summary>
    /// The capture resistance of <paramref name="building"/>: its RESISTANCE after its owner's technologies plus the
    /// influence of every other completed building with an EMIT within <c>influenceRadiusTiles</c> (center to center),
    /// fading linearly to 0 there; at least <c>minimumCaptureResistance</c>.
    /// </summary>
    public static int CaptureResistance(World world, EntityId building)
    {
        ArgumentNullException.ThrowIfNull(world);
        Identity identity = world.Identities.Get(building);
        EntityTypeDefinition type = world.Ruleset.EntityTypes[identity.TypeId];
        long resistance = world.Stats.Apply(identity.Owner, ModifierStat.Resistance, type.Id, type.Traits.Resistance);
        long radius = (long)world.Ruleset.Gameplay.InfluenceRadiusTiles * MapState.UnitsPerTile;
        if (radius > 0)
        {
            Position at = world.Positions.Get(building);
            ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
            for (int i = 0; i < structures.Length; i++)
            {
                EntityId other = structures[i];
                Identity o = world.Identities.Get(other);
                if (other == building || world.Ruleset.EntityTypes[o.TypeId].Traits.Influence is not { } influence || world.Constructions.Has(other)
                    || !world.HasPlayer(o.Owner))
                {
                    continue;
                }

                Position p = world.Positions.Get(other);
                long d = IntMath.ISqrt(IntMath.DistanceSquared(at.X, at.Y, p.X, p.Y));
                if (d < radius)
                {
                    resistance += (o.Owner == identity.Owner ? influence.Own : influence.Other) * (radius - d) / radius;
                }
            }
        }

        return (int)Math.Clamp(resistance, world.Ruleset.Gameplay.MinimumCaptureResistance, int.MaxValue / 2000);
    }
}
