using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;

namespace Tzar2.Scripting.Legacy;

/// <summary>
/// Tzar2's own actions, which the original does not have (ADR 0293): patrol routes and reinforcement waves, kept in the
/// trigger state and looked after every tick, and the camera path, which only tells the presentation where to look.
/// </summary>
public sealed partial class LegacyTriggerSystem
{
    /// <summary>A patrolling unit this close to its point (world units, each axis) has reached it.</summary>
    public const int PatrolReach = 2 * Tile;

    /// <summary>Patrols are looked after every so many ticks (an order takes a moment to start moving).</summary>
    public const int PatrolEveryTicks = 10;

    private readonly List<EntityId> _spawned = [];

    // Patrol Route: each unit walks to the first point; RunPatrols sends it on.
    private void StartPatrol(int index, PatrolRouteAction patrol)
    {
        if (patrol.Points.Length == 0)
        {
            Fault(ScenarioFault.ActionFailed);
            return;
        }

        foreach (EntityId e in Collect(patrol.Objects, _targets))
        {
            if (!_world.Entities.IsAlive(e) || !_world.TypeOf(e).IsMobile)
            {
                continue;
            }

            var entry = new PatrolState(e.Value, index, 0);
            int at = IndexOfPatrol(e.Value);
            if (at >= 0)
            {
                _state.Patrols[at] = entry;
            }
            else if (_state.Patrols.Count < TriggerState.MaxPatrols)
            {
                _state.Patrols.Add(entry);
            }
            else
            {
                Fault(ScenarioFault.ActionFailed);
                return;
            }
        }
    }

    private int IndexOfPatrol(uint entity)
    {
        for (int i = 0; i < _state.Patrols.Count; i++)
        {
            if (_state.Patrols[i].Entity == entity)
            {
                return i;
            }
        }

        return -1;
    }

    // Reinforcement Waves: the first wave comes on the next tick, the others every interval after it.
    private void StartWaves(int index, ReinforcementWavesAction waves)
    {
        if (waves.Waves < 1 || waves.Count < 1)
        {
            Fault(ScenarioFault.ActionFailed);
            return;
        }

        var entry = new WaveState(index, Math.Min(waves.Waves, 1_000_000), 0);
        for (int i = 0; i < _state.Waves.Count; i++)
        {
            if (_state.Waves[i].Action == index)
            {
                _state.Waves[i] = entry; // run again: it starts over
                return;
            }
        }

        if (_state.Waves.Count < TriggerState.MaxWaves)
        {
            _state.Waves.Add(entry);
        }
        else
        {
            Fault(ScenarioFault.ActionFailed);
        }
    }

    // Camera Path: the points as they are now, in order; the presentation glides through them.
    private void CameraPath(CameraPathAction camera)
    {
        foreach (AreaSpec p in camera.Points.Items)
        {
            WorldRect at = Resolve(p);
            if (!at.IsEmpty)
            {
                _world.Events.Emit(new SimEvent(SimEventKind.CameraPoint, Everyone, EntityId.None, at.CenterX, at.CenterY, Math.Max(0, camera.LegMs)));
            }
        }
    }

    private void RunOwnActions(int tickMs)
    {
        if (_state.Waves.Count > 0)
        {
            RunWaves(tickMs);
        }

        if (_state.Patrols.Count > 0 && _world.Tick.Value % PatrolEveryTicks == 0)
        {
            RunPatrols();
        }
    }

    // A unit free of orders walks (fighting on the way) to its point; at the point it takes the next, or stops at the end.
    private void RunPatrols()
    {
        _orders.Clear();
        List<PatrolState> patrols = _state.Patrols;
        Span<EntityId> one = stackalloc EntityId[1];
        for (int i = 0; i < patrols.Count; i++)
        {
            PatrolState p = patrols[i];
            EntityId unit = EntityId.FromValue(p.Entity);
            if (!_world.Entities.IsAlive(unit) || _scenario.Actions[p.Action] is not PatrolRouteAction { Points.Length: > 0 } route)
            {
                patrols.RemoveAt(i--);
                continue;
            }

            if (Busy(unit) || !_world.Positions.Has(unit))
            {
                continue;
            }

            int next = p.Next % route.Points.Length;
            WorldRect point = Resolve(route.Points[next]);
            Position at = _world.Positions.Get(unit);
            if (!point.IsEmpty && Math.Abs(at.X - point.CenterX) <= PatrolReach && Math.Abs(at.Y - point.CenterY) <= PatrolReach)
            {
                next++;
                if (next >= route.Points.Length)
                {
                    if (!route.Loop)
                    {
                        patrols.RemoveAt(i--);
                        continue;
                    }

                    next = 0;
                }

                patrols[i] = p with { Next = next };
                point = Resolve(route.Points[next]);
            }

            if (point.IsEmpty)
            {
                continue; // a point that follows an object that is gone: wait for it
            }

            one[0] = unit;
            _orders.Add(CommandKind.Move, one, point.CenterX, point.CenterY, EntityId.None, 0, 0, CommandFlags.Ctrl, _world.Identities.Get(unit).Owner);
        }

        if (_orders.Count > 0)
        {
            CommandSystem.ExecuteScripted(_world, _orders);
        }
    }

    // Moving, fighting or inside a holder: the patrol leaves it be.
    private bool Busy(EntityId unit)
        => _world.IsHeld(unit)
            || (_world.Motions.IndexOf(unit) is >= 0 and var m && _world.Motions.At(m).State == MotionState.Moving)
            || (_world.Combats.IndexOf(unit) is >= 0 and var c && _world.Combats.At(c).Target != 0);

    // A wave whose time has come appears and marches; the action ends with its last wave.
    private void RunWaves(int tickMs)
    {
        List<WaveState> waves = _state.Waves;
        for (int i = 0; i < waves.Count; i++)
        {
            WaveState w = waves[i];
            if (_scenario.Actions[w.Action] is not ReinforcementWavesAction a)
            {
                waves.RemoveAt(i--);
                continue;
            }

            if (w.RemainingMs > 0)
            {
                waves[i] = w with { RemainingMs = Math.Max(0, w.RemainingMs - tickMs) };
                continue;
            }

            _spawned.Clear();
            Spawn(new SpawnObjectAction(a.Name, a.TypeKey, a.Owner, a.Count, a.Area), _spawned);
            if (a.Target is { } target && _spawned.Count > 0 && Resolve(target) is { IsEmpty: false } to)
            {
                _orders.Clear();
                _orders.Add(CommandKind.Move, CollectionsMarshal.AsSpan(_spawned), to.CenterX, to.CenterY, EntityId.None, 0, 0, CommandFlags.Ctrl, _world.Identities.Get(_spawned[0]).Owner);
                CommandSystem.ExecuteScripted(_world, _orders);
            }

            if (w.WavesLeft <= 1)
            {
                waves.RemoveAt(i--);
            }
            else
            {
                waves[i] = new WaveState(w.Action, w.WavesLeft - 1, Math.Max(tickMs, a.IntervalMs));
            }
        }
    }
}
