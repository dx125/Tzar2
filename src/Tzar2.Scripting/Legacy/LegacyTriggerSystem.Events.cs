using System;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;

namespace Tzar2.Scripting.Legacy;

public sealed partial class LegacyTriggerSystem
{
    /// <summary>Memory cells an event keeps between passes (<see cref="TriggerShape.EventMemory"/>).</summary>
    internal static int MemoryOf(EventDef e) => e switch
    {
        ObjectDestroyedEvent { Objects: TypedObjects } => 16,
        ObjectLeaveHolderEvent { Objects: NamedObjects n } => n.ObjectIndices.Length,
        ObjectLeaveHolderEvent { Objects: TypedObjects } => 16,
        _ => 0,
    };

    /// <summary>
    /// The value of an event now (Appendix C.5: "a named variable that the game sets to TRUE while the event condition
    /// holds"). Destroyed and Leave Holder of a type count what the collector saw since the match started.
    /// </summary>
    private bool EvaluateEvent(int index)
    {
        _steps++;
        int[] memory = _state.EventMemory[index];
        switch (_scenario.Events[index])
        {
            case ObjectInAreaEvent e:
            {
                WorldRect area = Resolve(e.Area);
                return !area.IsEmpty && Holds(e.Objects, x => !_world.IsHeld(x) && area.Contains(_world.Positions.Get(x)));
            }

            case ObjectDestroyedEvent { Objects: NamedObjects named }:
            {
                // A hidden (not yet appeared) object is not destroyed; a dead one is.
                bool all = named.Quantifier == Quantifier.All;
                if (named.ObjectIndices.Length == 0)
                {
                    return false;
                }

                foreach (int i in named.ObjectIndices)
                {
                    bool dead = (uint)i < (uint)_state.Hidden.Length && !_state.Hidden[i] && MapEntity(i) == EntityId.None;
                    if (all != dead)
                    {
                        return dead;
                    }
                }

                return all;
            }

            case ObjectDestroyedEvent { Objects: TypedObjects typed }:
                return Compare(typed, memory);
            case ObjectExistEvent e:
                return Holds(e.Objects, static _ => true);
            case ObjectsEnterHolderEvent e:
            {
                EntityId holder = MapEntity(e.HolderIndex);
                return holder != EntityId.None && Holds(e.Objects, x => _world.Helds.Has(x) && _world.Helds.Get(x).Holder == holder.Value);
            }

            case ObjectLeaveHolderEvent { Objects: NamedObjects named } e:
            {
                EntityId holder = MapEntity(e.HolderIndex);
                bool all = named.Quantifier == Quantifier.All;
                if (named.ObjectIndices.Length == 0)
                {
                    return false;
                }

                for (int k = 0; k < named.ObjectIndices.Length; k++)
                {
                    EntityId unit = MapEntity(named.ObjectIndices[k]);
                    bool left = memory[k] != 0 && unit != EntityId.None && !(holder != EntityId.None && _world.Helds.Has(unit) && _world.Helds.Get(unit).Holder == holder.Value);
                    if (all != left)
                    {
                        return left;
                    }
                }

                return all;
            }

            case ObjectLeaveHolderEvent { Objects: TypedObjects typed }:
                return Compare(typed, memory);
            case PlayerDefeatedEvent e:
                return AnyPlayer(e.Players, PlayerFlags.Defeated);
            case PlayerWonEvent e:
                return AnyPlayer(e.Players, PlayerFlags.Victorious);
            case ObjectExpressionEvent e:
                return e.Expression.Root is not null && Holds(e.Objects, x => Evaluate(e.Expression, 0, x) != 0);
            default:
                return false; // UnknownEvent
        }
    }

    private bool AnyPlayer(ushort legacyMask, PlayerFlags flag)
    {
        for (int p = 0; p < _world.Players.Length; p++)
        {
            if ((legacyMask & (1 << p)) != 0 && (_world.Players[p].Flags & flag) != 0)
            {
                return true;
            }
        }

        return false;
    }

    // Events of this tick already counted (transient within a tick: 0 between ticks).
    private int _collected;

    /// <summary>
    /// The tick's removals and unloadings for the events that count them: those of the command step when the trigger
    /// step starts (so a pass sees them at once), the rest after cleanup (step 17). Events are output and gone by the
    /// next tick; each is counted once.
    /// </summary>
    private void Collect(World world, bool endOfTick)
    {
        ReadOnlySpan<SimEvent> events = world.Events.Events;
        int from = _collected;
        _collected = endOfTick ? 0 : events.Length;
        if (_countingEvents.Length == 0)
        {
            return;
        }

        for (int i = from; i < events.Length; i++)
        {
            SimEvent ev = events[i];
            if (ev.Kind is not (SimEventKind.Destroyed or SimEventKind.Unloaded))
            {
                continue;
            }

            foreach (int index in _countingEvents)
            {
                int[] memory = _state.EventMemory[index];
                switch (_scenario.Events[index])
                {
                    case ObjectDestroyedEvent { Objects: TypedObjects typed } when ev.Kind == SimEventKind.Destroyed:
                        if (ev.Arg == TypeOf(typed) && ev.Player < 16 && InMask(typed.Players, ev.Player))
                        {
                            memory[ev.Player] = (int)Math.Min((long)memory[ev.Player] + 1, int.MaxValue);
                        }

                        break;
                    case ObjectLeaveHolderEvent leave when ev.Kind == SimEventKind.Unloaded:
                    {
                        EntityId holder = MapEntity(leave.HolderIndex);
                        if (holder == EntityId.None || ev.X != holder.Index)
                        {
                            break;
                        }

                        if (leave.Objects is NamedObjects named)
                        {
                            for (int k = 0; k < named.ObjectIndices.Length; k++)
                            {
                                if (MapEntity(named.ObjectIndices[k]) == ev.Entity)
                                {
                                    memory[k] = 1;
                                }
                            }
                        }
                        else if (leave.Objects is TypedObjects typed && _world.Entities.IsAlive(ev.Entity) && _world.Identities.Has(ev.Entity))
                        {
                            Sim.Components.Identity id = _world.Identities.Get(ev.Entity);
                            if (id.TypeId == TypeOf(typed) && InMask(typed.Players, id.Owner))
                            {
                                memory[id.Owner] = (int)Math.Min((long)memory[id.Owner] + 1, int.MaxValue);
                            }
                        }

                        break;
                    }
                }
            }
        }
    }

    // The collector half: a system of step 17 registered after the cleanup system.
    private sealed class Collector(LegacyTriggerSystem owner) : ISimSystem
    {
        public SimPhase Phase => SimPhase.Cleanup;

        public string Name => "triggers.collect";

        public void Execute(SimContext context) => owner.Collect(context.World, endOfTick: true);
    }
}
