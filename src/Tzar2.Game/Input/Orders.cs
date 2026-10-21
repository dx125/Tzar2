using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;

namespace Tzar2.Game.Input;

/// <summary>An order chosen on the command card that waits for a target click (the original's order cursors).</summary>
public enum OrderMode
{
    /// <summary>No pending order: right-click gives the default action.</summary>
    None = 0,

    /// <summary>Move to the clicked point.</summary>
    Move,

    /// <summary>Attack the clicked enemy; on ground: move-advance (attack-move).</summary>
    Attack,

    /// <summary>Harvest the clicked resource.</summary>
    Harvest,

    /// <summary>Patrol between here and the clicked point.</summary>
    Patrol,

    /// <summary>Follow and protect the clicked unit (guard; the sim's Follow order).</summary>
    Guard,

    /// <summary>Repair the clicked own damaged building, ship or siege weapon (builders).</summary>
    Repair,

    /// <summary>Set the rally point of the selected buildings.</summary>
    Rally,

    /// <summary>Place a building (the placement ghost).</summary>
    Place,

    /// <summary>Capture the clicked enemy building (peasants; GAME-04).</summary>
    Capture,

    /// <summary>Siege weapons attack the clicked ground (GAME-07).</summary>
    AttackGround,

    /// <summary>Move-advance to the clicked point (a move with Ctrl: fight what comes into sight).</summary>
    MoveAdvance,

    /// <summary>Move-sneak to the clicked point (a move with Alt).</summary>
    MoveSneak,

    /// <summary>Cast a spell (its id is the mode's argument) on the clicked unit, or at the clicked point.</summary>
    Spell,

    /// <summary>Peasants bribe the clicked enemy unit (Bribery).</summary>
    Bribe,

    /// <summary>The selected cathedral preaches a crusade at the clicked point (GAME-11).</summary>
    Crusade,

    /// <summary>Pick up the clicked item (GAME-10).</summary>
    PickItem,

    /// <summary>Use the default item on the clicked target or point (GAME-10).</summary>
    UseItem,
}

/// <summary>One command to send: the kind, its parameters and the subset of the selection it applies to.</summary>
/// <param name="Kind">Command kind.</param>
/// <param name="Flags">Modifiers.</param>
/// <param name="Target">Target entity.</param>
/// <param name="X">Target X (world units).</param>
/// <param name="Y">Target Y.</param>
/// <param name="Arg0">Kind-specific argument.</param>
/// <param name="Arg1">Kind-specific argument.</param>
/// <param name="Entities">The entities that execute it.</param>
public sealed record PlannedCommand(CommandKind Kind, CommandFlags Flags, EntityId Target, int X, int Y, int Arg0, int Arg1, IReadOnlyList<EntityId> Entities)
{
    /// <summary>Appends this command to <paramref name="batch"/>.</summary>
    public void AddTo(CommandList batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var ids = new EntityId[Entities.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = Entities[i];
        }

        batch.Add(Kind, ids, X, Y, Target, Arg0, Arg1, Flags);
    }
}

/// <summary>
/// Turns a right click (or a click with a pending <see cref="OrderMode"/>) into commands (TDD §7.4 CommandBuilder; UI
/// semantics of Appendix E.6): enemy → attack, resource → harvest (harvesters; the others walk there), own construction
/// site → help build (builders), own damaged building → repair (builders), ground → move; Ctrl = move-advance, Alt =
/// move-sneak, Shift = waypoints (queue); a selection of producers sets the rally point. Engine-free and unit-tested.
/// </summary>
public static class OrderResolver
{
    /// <summary>Plans the default action of <paramref name="selection"/> on <paramref name="target"/> / (x, y).</summary>
    public static IReadOnlyList<PlannedCommand> RightClick(IReadOnlyList<EntityInfo> selection, EntityInfo? target, int x, int y, KeyMods mods, byte player)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var result = new List<PlannedCommand>();
        var mobile = new List<EntityInfo>();
        var producers = new List<EntityId>();
        foreach (EntityInfo e in selection)
        {
            if (e.Owner != player)
            {
                continue;
            }

            if (e.IsMobile)
            {
                mobile.Add(e);
            }
            else if (e.IsProducer && !e.UnderConstruction)
            {
                producers.Add(e.Id);
            }
        }

        if (mobile.Count == 0)
        {
            if (producers.Count > 0)
            {
                // Rally point (Ctrl/Alt/Ctrl+Alt variants all set it; the default-action and move-advance rally kinds are
                // not in the sim yet).
                result.Add(new PlannedCommand(CommandKind.SetRally, CommandFlags.None, EntityId.None, x, y, 0, 0, producers));
            }

            return result;
        }

        CommandFlags flags = Flags(mods);
        var move = new List<EntityId>();
        // Enemies are units and buildings of another owner, Nature's animals and creatures included (as in the sim).
        if (target is { } t && t.Owner != player && t.Category is EntityCategory.Unit or EntityCategory.Building && !t.IsResourceNode)
        {
            var attackers = new List<EntityId>();
            foreach (EntityInfo e in mobile)
            {
                (e.IsArmed ? attackers : move).Add(e.Id);
            }

            if (attackers.Count > 0)
            {
                result.Add(new PlannedCommand(CommandKind.Attack, flags & CommandFlags.Queue, t.Id, x, y, 0, 0, attackers));
            }
        }
        else if (target is { IsResourceNode: true } node)
        {
            var harvesters = new List<EntityId>();
            foreach (EntityInfo e in mobile)
            {
                (e.CanHarvest ? harvesters : move).Add(e.Id);
            }

            if (harvesters.Count > 0)
            {
                result.Add(new PlannedCommand(CommandKind.Harvest, flags & CommandFlags.Queue, node.Id, x, y, 0, 0, harvesters));
            }
        }
        else if (target is { } own && own.Owner == player && (own.UnderConstruction || NeedsRepair(own)))
        {
            var builders = new List<EntityId>();
            foreach (EntityInfo e in mobile)
            {
                (e.CanBuild ? builders : move).Add(e.Id);
            }

            if (builders.Count > 0)
            {
                CommandKind kind = own.UnderConstruction ? CommandKind.Build : CommandKind.Repair;
                result.Add(new PlannedCommand(kind, flags & CommandFlags.Queue, own.Id, x, y, 0, 0, builders));
            }
        }
        else
        {
            foreach (EntityInfo e in mobile)
            {
                move.Add(e.Id);
            }
        }

        if (move.Count > 0)
        {
            result.Add(new PlannedCommand(CommandKind.Move, flags, EntityId.None, x, y, 0, 0, move));
        }

        return result;
    }

    /// <summary>Plans a click with a pending command-card order (<paramref name="argument"/>: the spell id of <see cref="OrderMode.Spell"/>).</summary>
    public static IReadOnlyList<PlannedCommand> WithMode(OrderMode mode, IReadOnlyList<EntityInfo> selection, EntityInfo? target, int x, int y, KeyMods mods, byte player, int argument = 0)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var own = new List<EntityId>();
        foreach (EntityInfo e in selection)
        {
            if (e.Owner == player && (e.IsMobile || mode is OrderMode.Rally or OrderMode.Crusade))
            {
                own.Add(e.Id);
            }
        }

        if (own.Count == 0)
        {
            return [];
        }

        CommandFlags queue = Flags(mods) & CommandFlags.Queue;
        return mode switch
        {
            OrderMode.Move => [new PlannedCommand(CommandKind.Move, Flags(mods), EntityId.None, x, y, 0, 0, own)],
            OrderMode.Attack when target is { } t && t.Owner != player && !t.IsResourceNode
                => [new PlannedCommand(CommandKind.Attack, queue, t.Id, x, y, 0, 0, own)],
            OrderMode.Attack => [new PlannedCommand(CommandKind.Move, CommandFlags.Ctrl | queue, EntityId.None, x, y, 0, 0, own)],
            OrderMode.Harvest when target is { IsResourceNode: true } n => [new PlannedCommand(CommandKind.Harvest, queue, n.Id, x, y, 0, 0, own)],
            OrderMode.Patrol => [new PlannedCommand(CommandKind.Patrol, queue, EntityId.None, x, y, 0, 0, own)],
            // Guard: what is clicked (a unit or a building, own or not an enemy's) is followed and defended; an enemy is
            // attacked, open ground held with a move-advance.
            OrderMode.Guard when target is { } g && g.Id != EntityId.None && !g.IsResourceNode && g.Owner == player
                => [new PlannedCommand(CommandKind.Follow, queue, g.Id, x, y, 0, 0, own)],
            OrderMode.Guard when target is { } g && g.Id != EntityId.None && !g.IsResourceNode
                => [new PlannedCommand(CommandKind.Attack, queue, g.Id, x, y, 0, 0, own)],
            OrderMode.Guard => [new PlannedCommand(CommandKind.Move, CommandFlags.Ctrl | queue, EntityId.None, x, y, 0, 0, own)],
            OrderMode.Rally => [new PlannedCommand(CommandKind.SetRally, CommandFlags.None, EntityId.None, x, y, 0, 0, own)],
            OrderMode.Repair when target is { } r && r.Owner == player && NeedsRepair(r) && Builders(selection, player) is { Count: > 0 } builders
                => [new PlannedCommand(CommandKind.Repair, queue, r.Id, x, y, 0, 0, builders)],
            OrderMode.Capture when target is { Category: EntityCategory.Building } c && c.Owner != player && !c.UnderConstruction
                => [new PlannedCommand(CommandKind.Capture, queue, c.Id, x, y, 0, 0, own)],
            OrderMode.AttackGround => [new PlannedCommand(CommandKind.AttackGround, queue, EntityId.None, x, y, 0, 0, own)],
            OrderMode.MoveAdvance => [new PlannedCommand(CommandKind.Move, CommandFlags.Ctrl | queue, EntityId.None, x, y, 0, 0, own)],
            OrderMode.MoveSneak => [new PlannedCommand(CommandKind.Move, CommandFlags.Alt | queue, EntityId.None, x, y, 0, 0, own)],
            OrderMode.Spell when target is { } s && s.Id != EntityId.None => [new PlannedCommand(CommandKind.CastSpell, CommandFlags.None, s.Id, x, y, argument, 0, own)],
            OrderMode.Spell => [new PlannedCommand(CommandKind.CastSpell, CommandFlags.None, EntityId.None, x, y, argument, 0, own)],
            OrderMode.Bribe when target is { IsMobile: true } b && b.Owner != player && b.Id != EntityId.None
                => [new PlannedCommand(CommandKind.Bribe, CommandFlags.None, b.Id, x, y, 0, 0, own)],
            OrderMode.Crusade => [new PlannedCommand(CommandKind.PreachCrusade, CommandFlags.None, EntityId.None, x, y, 0, 0, own)],
            OrderMode.PickItem when target is { } item && item.Id != EntityId.None => [new PlannedCommand(CommandKind.PickItem, queue, item.Id, x, y, 0, 0, own)],
            OrderMode.UseItem => [new PlannedCommand(CommandKind.UseItem, CommandFlags.None, target?.Id ?? EntityId.None, x, y, -1, 0, own)],
            _ => [],
        };
    }

    /// <summary>Whether builders can repair <paramref name="e"/> now: a finished repairable object below full health (GAME-03).</summary>
    public static bool NeedsRepair(in EntityInfo e) => e.IsRepairable && !e.UnderConstruction && e.MaxHealth > 0 && e.Health < e.MaxHealth;

    private static List<EntityId> Builders(IReadOnlyList<EntityInfo> selection, byte player)
    {
        var builders = new List<EntityId>();
        foreach (EntityInfo e in selection)
        {
            if (e.Owner == player && e.IsMobile && e.CanBuild)
            {
                builders.Add(e.Id);
            }
        }

        return builders;
    }

    /// <summary>The command flags of the modifiers (Shift = queue, Ctrl = move-advance, Alt = move-sneak).</summary>
    public static CommandFlags Flags(KeyMods mods)
        => ((mods & KeyMods.Shift) != 0 ? CommandFlags.Queue : CommandFlags.None)
            | ((mods & KeyMods.Ctrl) != 0 ? CommandFlags.Ctrl : CommandFlags.None)
            | ((mods & KeyMods.Alt) != 0 ? CommandFlags.Alt : CommandFlags.None);
}
