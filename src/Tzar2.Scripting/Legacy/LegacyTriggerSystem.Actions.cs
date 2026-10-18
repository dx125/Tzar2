using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Systems;
using SimStance = Tzar2.Sim.Kingdoms.Stance;

namespace Tzar2.Scripting.Legacy;

public sealed partial class LegacyTriggerSystem
{
    /// <summary>Most objects one Spawn Object action creates.</summary>
    public const int MaxSpawnPerAction = 256;

    private const byte Everyone = 0xFF;

    private readonly CommandList _orders = new();
    private readonly List<EntityId> _targets = [];

    // The rule and action being run (fault events name them), -1 outside.
    private int _rule = -1;
    private int _action = -1;

    /// <summary>Runs one action of a firing rule (Appendix C.7).</summary>
    private void Run(int index)
    {
        _action = index;
        _steps++;
        ActionDef action = _scenario.Actions[index];
        switch (action)
        {
            case AddQuestAction:
                if (!_state.Quests.Contains(index))
                {
                    _state.Quests.Add(index);
                    Present(SimEventKind.QuestAdded, _options.ResponderMask, index);
                }

                break;
            case RemoveQuestAction remove:
                if (_state.Quests.Remove(remove.AddQuestActionIndex))
                {
                    Present(SimEventKind.QuestRemoved, _options.ResponderMask, remove.AddQuestActionIndex);
                }

                break;
            case AppearObjectAction appear:
                Appear(appear.Objects);
                break;
            case ChangeDiplomacyAction d:
            {
                int a = d.Kingdom1 - 1;
                int b = d.Kingdom2 - 1;
                if (!_world.HasPlayer(a) || !_world.HasPlayer(b) || a == b)
                {
                    Fault(ScenarioFault.ActionFailed);
                    break;
                }

                // Both directions (UNVERIFIED: the editor page shows one pair, UNK-55).
                SimStance stance = d.Stance switch { Content.Compiled.Scenario.Stance.Ally => SimStance.Ally, Content.Compiled.Scenario.Stance.Neutral => SimStance.Neutral, _ => SimStance.Enemy };
                Sharing sharing = stance != SimStance.Ally ? Sharing.None
                    : (d.SharedVision ? Sharing.Vision : 0) | (d.SharedResources ? Sharing.Resources : 0) | (d.CommandUnits ? Sharing.UnitControl : 0);
                Sharing victory = _world.Diplomacy.OffersOf(a, b) & Sharing.Victory;
                ScenarioOperations.SetDiplomacy(_world, a, b, stance, stance == SimStance.Ally ? sharing | victory : sharing);
                victory = _world.Diplomacy.OffersOf(b, a) & Sharing.Victory;
                ScenarioOperations.SetDiplomacy(_world, b, a, stance, stance == SimStance.Ally ? sharing | victory : sharing);
                break;
            }

            case ChangeObjectOwnerAction change:
                if (!_world.HasPlayer(change.Kingdom - 1))
                {
                    Fault(ScenarioFault.ActionFailed);
                    break;
                }

                foreach (EntityId e in Collect(change.Objects, _targets))
                {
                    if (_world.Identities.Get(e).Owner != change.Kingdom - 1)
                    {
                        _world.ChangeOwner(e, (byte)(change.Kingdom - 1));
                    }
                }

                break;
            case ChangePlayerStatusAction status:
                if (!_world.HasPlayer(status.Kingdom - 1))
                {
                    Fault(ScenarioFault.ActionFailed);
                    break;
                }

                _state.PlayerStatus[status.Kingdom - 1] = (byte)((status.CannotWin ? 1 : 0) | (status.CannotLose ? 2 : 0));
                break;
            case ConversationAction c when (uint)c.ConversationIndex < (uint)_scenario.Conversations.Length:
                StartConversation(c.ConversationIndex);
                break;
            case DestroyObjectAction destroy:
                foreach (EntityId e in Collect(destroy.Objects, _targets))
                {
                    _world.Kill(e);
                }

                break;
            case EraseObjectAction erase:
                foreach (EntityId e in Collect(erase.Objects, _targets))
                {
                    _world.Destroy(e);
                }

                break;
            case ExploreAreaAction explore:
                Explore(explore);
                break;
            case ExpressionAction expression:
                Evaluate(expression.Expression, 0, EntityId.None);
                break;
            case KeepRuleAction:
            case DelayAction: // at the end of a list there is nothing to delay
                break;
            case LoadMapAction:
                RequestMap(index, 0);
                break;
            case LockViewAction lockView:
            {
                WorldRect area = Resolve(lockView.Area);
                if (!area.IsEmpty)
                {
                    _world.Events.Emit(new SimEvent(SimEventKind.ViewLocked, Everyone, EntityId.None, area.CenterX, area.CenterY, Math.Max(0, lockView.TimeMs)));
                }

                break;
            }

            case LockUnitsAction lockUnits:
                foreach (EntityId e in Collect(lockUnits.Objects, _targets))
                {
                    _state.SetLocked(e.Value, lockUnits.Locked);
                }

                break;
            case ObjectOrderAction order:
                Order(order);
                break;
            case PlayerLoseAction lose:
                ScenarioOperations.ForceDefeat(_world, lose.Players);
                if (!string.IsNullOrEmpty(lose.Map))
                {
                    RequestMap(index, 2);
                }

                break;
            case PlayerWinAction win:
                ScenarioOperations.ForceVictory(_world, win.Players);
                if (!string.IsNullOrEmpty(win.Map))
                {
                    RequestMap(index, 1);
                }

                break;
            case ScrollViewAction scroll:
            {
                WorldRect to = Resolve(scroll.To);
                if (!to.IsEmpty)
                {
                    _world.Events.Emit(new SimEvent(SimEventKind.ViewScrolled, Everyone, EntityId.None, to.CenterX, to.CenterY, index));
                }

                break;
            }

            case SendChatAction chat:
                Present(SimEventKind.ScenarioChat, chat.Players, index);
                break;
            case SetObjectPropertiesAction set:
                foreach (EntityId e in Collect(set.Objects, _targets))
                {
                    if (_world.Entities.IsAlive(e))
                    {
                        Evaluate(set.Expression, 0, e);
                    }
                }

                break;
            case SetTimerAction timer:
            {
                int initial = Evaluate(timer.Initial, 0, EntityId.None);
                _state.Timers[index] = new TimerState { Started = true, Running = true, CountDown = timer.CountDown, ValueMs = Math.Max(0, initial) };
                break;
            }

            case ShowMessageAction:
                Present(SimEventKind.ScenarioMessage, _options.ResponderMask, index);
                break;
            case ShowStoryScreenAction:
                Present(SimEventKind.StoryScreen, _options.ResponderMask, index);
                break;
            case SpawnObjectAction spawn:
                Spawn(spawn);
                break;
            case StopTimerAction stop when (uint)stop.SetTimerActionIndex < (uint)_state.Timers.Length:
                _state.Timers[stop.SetTimerActionIndex].Running = false;
                break;
            case UnlockViewAction:
                _world.Events.Emit(new SimEvent(SimEventKind.ViewUnlocked, Everyone, EntityId.None, 0, 0, index));
                break;
            case UnknownAction:
                Fault(ScenarioFault.UnknownAction);
                break;
            default:
                Fault(ScenarioFault.ActionFailed);
                break;
        }
    }

    private void Present(SimEventKind kind, uint mask, int actionIndex)
        => _world.Events.Emit(new SimEvent(kind, Everyone, EntityId.None, (int)(mask & 0xFF), 0, actionIndex));

    private void RequestMap(int actionIndex, int kind)
    {
        _state.MapRequestAction = actionIndex;
        _world.Events.Emit(new SimEvent(SimEventKind.MapRequested, Everyone, EntityId.None, 0, kind, actionIndex));
    }

    // Appear Object: hidden map objects come into the world where the map placed them, with their scenario properties.
    private void Appear(ObjectSpec spec)
    {
        for (int i = 0; i < _state.Hidden.Length; i++)
        {
            if (!_state.Hidden[i] || !Names(spec, i))
            {
                continue;
            }

            MapObject o = _world.Map.Data.Objects[i];
            int type = _world.Ruleset.EntityTypeId(o.TypeKey);
            if (type < 0 || _world.Entities.RemainingCapacity < 1 || !_world.IsValidOwner(o.Owner))
            {
                Fault(ScenarioFault.ActionFailed);
                continue;
            }

            EntityTypeDefinition t = _world.Ruleset.EntityTypes[type];
            if (t.HasFootprint && !PlacementQueries.CanPlace(_world, (ushort)type, o.X / Tile, o.Y / Tile))
            {
                Fault(ScenarioFault.ActionFailed); // something stands there now; try again at the next Appear
                continue;
            }

            EntityId e = _world.Spawn((ushort)type, o.Owner, o.X, o.Y);
            _state.Hidden[i] = false;
            _state.MapEntities[i] = e.Value;
            ScenarioSetup.ApplyInfo(_world, e, _scenario.ObjectAt(i));
        }
    }

    // Whether map object i is one of the spec's objects (named: listed; typed: of the type and a listed kingdom).
    private bool Names(ObjectSpec spec, int index)
    {
        switch (spec)
        {
            case NamedObjects named:
                foreach (int i in named.ObjectIndices)
                {
                    if (i == index)
                    {
                        return true;
                    }
                }

                return false;
            case TypedObjects typed:
            {
                MapObject o = _world.Map.Data.Objects[index];
                return string.Equals(o.TypeKey, typed.TypeKey, StringComparison.Ordinal) && InMask(typed.Players, o.Owner);
            }

            case CategoryObjects category:
            {
                MapObject o = _world.Map.Data.Objects[index];
                int type = _world.Ruleset.EntityTypeId(o.TypeKey);
                return type >= 0 && InMask(category.Players, o.Owner) && InCategory(category.Category, _world.Ruleset.EntityTypes[type]);
            }
            default:
                return false;
        }
    }

    // Explore Area: a temporary reveal for the responders (UNVERIFIED: who, and what "Reveal Fog" adds, UNK-56).
    private void Explore(ExploreAreaAction explore)
    {
        WorldRect area = Resolve(explore.Area);
        if (area.IsEmpty)
        {
            return;
        }

        int radius = Math.Clamp(Math.Max(area.X1 - area.X0, area.Y1 - area.Y0) / Tile / 2, 1, Visibility.MaxRadiusTiles);
        // At least one tick: an Explore Area of 0 ms still flashes the area (UNVERIFIED, UNK-56).
        int ticks = Math.Max(1, (int)Math.Min(int.MaxValue / 2, (long)Math.Max(0, explore.TimeMs) / _world.Config.TickLengthMilliseconds));
        int expires = (int)Math.Min(int.MaxValue / 2, (long)_world.Tick.Value + ticks);
        for (int p = 0; p < _world.Players.Length; p++)
        {
            if ((_options.ResponderMask & (1u << p)) != 0)
            {
                _world.Visibility.Reveal(_world, p, area.CenterX / Tile, area.CenterY / Tile, radius, expires);
            }
        }
    }

    // Object Order: the objects are ordered to the area's centre as commands of their owners, through the commands'
    // validation (ADR 0130). Order keys (ORDERS.INI): attack → attack-move, patrol, stop, anything else → move
    // (UNVERIFIED mapping, UNK-57). An attack on an object area fights that object itself, whatever the stances (a
    // scripted duel between allies, ADR 0135; UNVERIFIED, UNK-57).
    private void Order(ObjectOrderAction order)
    {
        WorldRect area = Resolve(order.Area);
        if (area.IsEmpty)
        {
            return;
        }

        string key = order.OrderKey;
        if (order.Area is ObjectArea target && key.Contains("attack", StringComparison.OrdinalIgnoreCase) && MapEntity(target.ObjectIndex) is var victim
            && victim != EntityId.None)
        {
            foreach (EntityId e in Collect(order.Objects, _targets))
            {
                ScenarioOperations.ScriptedAttack(_world, e, victim);
            }

            return;
        }

        CommandKind kind = key.Contains("patrol", StringComparison.OrdinalIgnoreCase) ? CommandKind.Patrol
            : key.Contains("stop", StringComparison.OrdinalIgnoreCase) ? CommandKind.Stop : CommandKind.Move;
        CommandFlags flags = key.Contains("attack", StringComparison.OrdinalIgnoreCase) ? CommandFlags.Ctrl : CommandFlags.None;
        _orders.Clear();
        List<EntityId> units = Collect(order.Objects, _targets);
        Span<EntityId> group = stackalloc EntityId[64];
        for (int p = 0; p < _world.Players.Length; p++)
        {
            int n = 0;
            foreach (EntityId e in units)
            {
                if (_world.Identities.Get(e).Owner != p)
                {
                    continue;
                }

                group[n++] = e;
                if (n == group.Length)
                {
                    _orders.Add(kind, group, area.CenterX, area.CenterY, EntityId.None, 0, 0, flags, (byte)p);
                    n = 0;
                }
            }

            if (n > 0)
            {
                _orders.Add(kind, group[..n], area.CenterX, area.CenterY, EntityId.None, 0, 0, flags, (byte)p);
            }
        }

        if (_orders.Count > 0)
        {
            CommandSystem.ExecuteInTick(_world, _orders);
        }
    }

    // Spawn Object: Count objects of the type for the owner (legacy 1-8; 0, 9 and 10 become Nature, UNVERIFIED), on the
    // area's free tiles from its centre outwards.
    private void Spawn(SpawnObjectAction spawn)
    {
        int type = _world.Ruleset.EntityTypeId(spawn.TypeKey);
        int owner = OwnerOf(spawn.Owner);
        WorldRect area = Resolve(spawn.Area);
        if (type < 0 || !_world.IsValidOwner(owner) || area.IsEmpty)
        {
            Fault(ScenarioFault.ActionFailed);
            return;
        }

        EntityTypeDefinition t = _world.Ruleset.EntityTypes[type];
        int count = Math.Min(Math.Max(0, spawn.Count), MaxSpawnPerAction);
        int x0 = area.X0 / Tile;
        int y0 = area.Y0 / Tile;
        int w = (area.X1 - area.X0) / Tile;
        int h = (area.Y1 - area.Y0) / Tile;
        int cx = x0 + (w / 2);
        int cy = y0 + (h / 2);
        int placed = 0;
        int rings = Math.Max(w, h);
        for (int ring = 0; ring <= rings && placed < count && !OverCap; ring++)
        {
            for (int dy = -ring; dy <= ring && placed < count; dy++)
            {
                for (int dx = -ring; dx <= ring && placed < count; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                    {
                        continue;
                    }

                    int tx = cx + dx;
                    int ty = cy + dy;
                    _steps++;
                    if (tx < x0 || ty < y0 || tx >= x0 + w || ty >= y0 + h || _world.Entities.RemainingCapacity < 1)
                    {
                        continue;
                    }

                    if (t.HasFootprint ? PlacementQueries.CanPlace(_world, (ushort)type, tx, ty) : (!t.IsMobile || _world.Map.IsPassable(t.Locomotor, tx, ty)))
                    {
                        _world.Spawn((ushort)type, owner, (tx * Tile) + (Tile / 2), (ty * Tile) + (Tile / 2));
                        placed++;
                    }
                }
            }
        }

        if (placed < count)
        {
            Fault(ScenarioFault.ActionFailed);
        }
    }
}
