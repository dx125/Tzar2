using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 12 (GAME-04; ADR 0044, ADR 0280): capture by the original's resistance (ReTzar's reading of the original,
/// 0x4dfebf, 0x4dfe52, 0x505040, 0x41f271, 0x41f7b0, 0x41f920).
/// </summary>
/// <remarks>
/// <para>A building's <b>resistance target</b> (CAP-002) is its RESISTANCE after its owner's technologies (Fanaticism,
/// Buddhism) plus the EMIT of every other finished building whose footprint, grown by that building's sight, meets it:
/// the emitter's <c>own</c> value when the two kingdoms are allies (or one kingdom), its <c>other</c> value otherwise.</para>
/// <para>Its <b>current resistance</b> is the target unless a <see cref="CaptureState"/> says otherwise. Each cycle of its
/// capture picture (<c>captureCycleMs</c>) a peasant at a hostile building takes <c>captureStep</c> from it and takes the
/// building once no more than that is left (CAP-005); at its own or an ally's building it gives <c>captureStep</c> back
/// until the building is whole, so the capturer goes on after the capture. Every <c>resistancePulseMs</c> a lowered or
/// raised resistance moves <c>resistancePulseStep</c> toward its target (CAP-006).</para>
/// </remarks>
public sealed class CaptureSystem : ISimSystem
{
    private readonly List<Emitter> _emitters = [];
    private readonly List<EntityId> _settled = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Capture;

    /// <inheritdoc />
    public string Name => "capture";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        GameplaySettings settings = world.Ruleset.Gameplay;
        int tickMs = world.Config.TickLengthMilliseconds;
        bool collected = false;

        // Capturers at work, in ascending worker order.
        for (int i = 0; i < world.Workers.Count; i++)
        {
            ref Worker w = ref world.Workers.At(i);
            if (w.Task != WorkerTask.Capture || w.Phase != WorkerPhase.Capturing)
            {
                continue;
            }

            EntityId worker = world.Workers.Entities[i];
            EntityId target = EntityId.FromValue(w.Target);
            byte player = world.Identities.Get(worker).Owner;
            if (!world.Entities.IsAlive(target) || world.TypeOf(target).Traits.Capturable is not { Instant: false } || world.Constructions.Has(target)
                || !Interaction.WorkerReaches(world, worker, target))
            {
                continue; // the worker system re-approaches or gives up
            }

            w.TimerMs += tickMs;
            if (w.TimerMs < settings.CaptureCycleMs)
            {
                continue;
            }

            w.TimerMs -= settings.CaptureCycleMs;
            if (!collected)
            {
                CollectEmitters(world, _emitters);
                collected = true;
            }

            int goal = Target(world, target, _emitters);
            int current = Current(world, target, goal);
            byte owner = world.Identities.Get(target).Owner;
            if (IsHostile(world, player, target))
            {
                if (current <= settings.CaptureStep)
                {
                    current = 0;
                    world.ChangeOwner(target, player);
                    world.Events.Emit(new SimEvent(SimEventKind.Captured, player, target, owner, 0, world.Identities.Get(target).TypeId));
                    goal = Target(world, target, _emitters); // its auras now count for its new kingdom
                }
                else
                {
                    current -= settings.CaptureStep;
                }
            }
            else
            {
                current += settings.CaptureStep;
                if (current >= goal)
                {
                    current = goal;
                    w.Task = WorkerTask.None; // whole again: the work is done (0x41fab0)
                    w.Phase = WorkerPhase.None;
                    w.Target = 0;
                    w.TimerMs = 0;
                }
            }

            Store(world, target, current, goal);
        }

        // The buildings' pulse: a resistance away from its target moves toward it (0x41f271).
        int pulseTicks = Math.Max(1, settings.ResistancePulseMs / tickMs);
        if (world.CaptureStates.Count == 0 || world.Tick.Value % pulseTicks != 0)
        {
            return;
        }

        if (!collected)
        {
            CollectEmitters(world, _emitters);
        }

        _settled.Clear();
        ReadOnlySpan<EntityId> ids = world.CaptureStates.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            ref CaptureState state = ref world.CaptureStates.At(i);
            int goal = Target(world, ids[i], _emitters);
            int current = state.Resistance;
            if (current == goal || (goal < 0 && current <= 0))
            {
                if (current == goal)
                {
                    _settled.Add(ids[i]);
                }

                continue;
            }

            current = current < goal ? Math.Min(goal, current + settings.ResistancePulseStep) : Math.Max(goal, current - settings.ResistancePulseStep);
            state.Resistance = Math.Max(0, current);
            if (state.Resistance == goal)
            {
                _settled.Add(ids[i]);
            }
        }

        foreach (EntityId id in _settled)
        {
            world.CaptureStates.Remove(id);
        }
    }

    /// <summary>
    /// Whether a peasant of <paramref name="player"/> may dig at <paramref name="building"/> (CAP-004; ReTzar's reading of
    /// the original, 0x4682e0): its own or an ally's whose resistance is below its target, to give it back; a watch, guard,
    /// stone or cannon tower of any kingdom that is not its ally; any other building of an enemy.
    /// </summary>
    public static bool MayCapture(World world, byte player, EntityId building)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.Entities.IsAlive(building) || world.Identities.IndexOf(building) is not (>= 0 and var t)
            || world.TypeOf(building).Traits.Capturable is not { Instant: false } || world.Constructions.Has(building) || !world.HasPlayer(world.Identities.At(t).Owner))
        {
            return false;
        }

        byte owner = world.Identities.At(t).Owner;
        if (Allied(world, player, owner))
        {
            var emitters = new List<Emitter>();
            CollectEmitters(world, emitters);
            int goal = Target(world, building, emitters);
            return Current(world, building, goal) < goal;
        }

        return IsHostile(world, player, building);
    }

    /// <summary>The current resistance of <paramref name="building"/> (CAP-002, CAP-006): its target unless capturers moved it.</summary>
    public static int CaptureResistance(World world, EntityId building)
    {
        ArgumentNullException.ThrowIfNull(world);
        var emitters = new List<Emitter>();
        CollectEmitters(world, emitters);
        int goal = Target(world, building, emitters);
        return Current(world, building, goal);
    }

    /// <summary>The resistance <paramref name="building"/> moves back toward (CAP-002): its own plus the auras round it.</summary>
    public static int ResistanceTarget(World world, EntityId building)
    {
        ArgumentNullException.ThrowIfNull(world);
        var emitters = new List<Emitter>();
        CollectEmitters(world, emitters);
        return Target(world, building, emitters);
    }

    // A building the peasant takes rather than mends: a watch, guard, stone or cannon tower of a kingdom that is not its
    // ally, or any other building of an enemy (0x468190, 0x4682e0).
    private static bool IsHostile(World world, byte player, EntityId building)
    {
        Identity identity = world.Identities.Get(building);
        if (Allied(world, player, identity.Owner))
        {
            return false;
        }

        return world.Ruleset.Combat.CaptureTowers.AsSpan().BinarySearch(identity.TypeId) >= 0
            || world.Diplomacy.StanceOf(player, identity.Owner) == Kingdoms.Stance.Enemy;
    }

    private static bool Allied(World world, byte a, byte b) => a == b || world.Diplomacy.StanceOf(a, b) == Kingdoms.Stance.Ally;

    private static int Current(World world, EntityId building, int goal)
        => world.CaptureStates.IndexOf(building) is >= 0 and var c ? world.CaptureStates.At(c).Resistance : Math.Max(0, goal);

    private static void Store(World world, EntityId building, int current, int goal)
    {
        if (current == goal)
        {
            world.CaptureStates.Remove(building);
        }
        else
        {
            world.CaptureStates.Set(building, new CaptureState { Resistance = Math.Max(0, current) });
        }
    }

    // The finished, player-owned buildings with an EMIT (a few dozen), with their footprints grown by their sight.
    private static void CollectEmitters(World world, List<Emitter> emitters)
    {
        emitters.Clear();
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            Identity o = world.Identities.Get(structures[i]);
            EntityTypeDefinition type = world.Ruleset.EntityTypes[o.TypeId];
            if (type.Traits.Influence is { } influence && (influence.Own != 0 || influence.Other != 0) && world.HasPlayer(o.Owner) && !world.Constructions.Has(structures[i]))
            {
                (int x0, int y0, int x1, int y1) = Tiles(world, structures[i]);
                int d = type.Traits.SightUnits / MapState.UnitsPerTile;
                emitters.Add(new Emitter(structures[i], x0 - d, y0 - d, x1 + d, y1 + d, o.Owner, influence.Own, influence.Other));
            }
        }
    }

    private static int Target(World world, EntityId building, List<Emitter> emitters)
    {
        Identity identity = world.Identities.Get(building);
        EntityTypeDefinition type = world.Ruleset.EntityTypes[identity.TypeId];
        long resistance = world.Stats.Apply(identity.Owner, ModifierStat.Resistance, type.Id, type.Traits.Resistance);
        (int x0, int y0, int x1, int y1) = Tiles(world, building);
        foreach (Emitter e in emitters)
        {
            if (e.Entity != building && e.X0 <= x1 && e.X1 >= x0 && e.Y0 <= y1 && e.Y1 >= y0)
            {
                resistance += Allied(world, identity.Owner, e.Owner) ? e.Own : e.Other;
            }
        }

        return (int)Math.Clamp(resistance, -1_000_000, 1_000_000);
    }

    // The tiles a building covers, first and last.
    private static (int X0, int Y0, int X1, int Y1) Tiles(World world, EntityId building)
    {
        (int tx, int ty, int w, int h) = world.FootprintRect(building);
        return (tx, ty, tx + w - 1, ty + h - 1);
    }

    private readonly record struct Emitter(EntityId Entity, int X0, int Y0, int X1, int Y1, byte Owner, int Own, int Other);
}
