using System;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Navigation;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 1: executes the tick's commands in order, re-validating each one at execution time (TDD §8.6:
/// ownership, existence, arguments). Invalid commands, or invalid entities inside a command, are ignored
/// deterministically and reported as <see cref="SimEventKind.CommandRejected"/> events.
/// </summary>
public sealed class CommandSystem : ISimSystem
{
    /// <summary>Most units one <see cref="CommandKind.DebugSpawn"/> may create.</summary>
    public const int MaxDebugSpawn = 64;

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Commands;

    /// <inheritdoc />
    public string Name => "commands";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        CommandList commands = context.Commands;
        for (int i = 0; i < commands.Count; i++)
        {
            ref readonly Command command = ref commands[i];
            CommandRejection? rejection = Apply(world, command, commands.EntitiesOf(command));
            if (rejection is { } reason)
            {
                world.Events.Emit(new SimEvent(SimEventKind.CommandRejected, command.Player, EntityId.None, i, (int)command.Kind, (int)reason));
            }
        }
    }

    private static CommandRejection? Apply(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if (!world.HasPlayer(command.Player) || (world.Players[command.Player].Flags & PlayerFlags.Active) == 0
            || (world.Players[command.Player].Flags & PlayerFlags.Defeated) != 0)
        {
            return CommandRejection.InvalidPlayer;
        }

        switch (command.Kind)
        {
            case CommandKind.Move:
            case CommandKind.Patrol:
            {
                if (!world.Map.ContainsPoint(command.X, command.Y))
                {
                    return CommandRejection.OutOfMap;
                }

                bool patrol = command.Kind == CommandKind.Patrol;
                int group = !patrol && CountOwnedMovers(world, command.Player, entities) >= 2 ? world.NextGroupId++ : 0;
                int applied = 0;
                foreach (EntityId entity in entities)
                {
                    int p = OwnedMover(world, entity, command.Player);
                    if (p < 0)
                    {
                        continue;
                    }

                    ref Motion m = ref world.Motions.At(p);
                    m.Order = patrol ? MotionOrder.Patrol : MotionOrder.Move;
                    m.OrderX = command.X;
                    m.OrderY = command.Y;
                    Position position = world.Positions.Get(entity);
                    m.PatrolX = position.X;
                    m.PatrolY = position.Y;
                    m.FollowTarget = 0;
                    m.Replans = 0;
                    MovementOrders.Request(world, entity, ref m, group, command.X, command.Y);
                    applied++;
                }

                return applied > 0 ? null : CommandRejection.NoValidEntities;
            }

            case CommandKind.Follow:
            {
                int t = world.Positions.IndexOf(command.Target);
                if (!world.Entities.IsAlive(command.Target) || t < 0)
                {
                    return CommandRejection.InvalidArgument;
                }

                Position target = world.Positions.At(t);
                int applied = 0;
                foreach (EntityId entity in entities)
                {
                    int p = OwnedMover(world, entity, command.Player);
                    if (p < 0 || entity == command.Target)
                    {
                        continue;
                    }

                    ref Motion m = ref world.Motions.At(p);
                    m.Order = MotionOrder.Follow;
                    m.FollowTarget = command.Target.Value;
                    m.OrderX = target.X;
                    m.OrderY = target.Y;
                    m.Replans = 0;
                    MovementOrders.Request(world, entity, ref m, 0, target.X, target.Y);
                    applied++;
                }

                return applied > 0 ? null : CommandRejection.NoValidEntities;
            }

            case CommandKind.Stop:
            {
                int applied = 0;
                foreach (EntityId entity in entities)
                {
                    int p = OwnedMover(world, entity, command.Player);
                    if (p >= 0)
                    {
                        MovementOrders.Stop(world, entity, ref world.Motions.At(p));
                        applied++;
                    }
                }

                return applied > 0 ? null : CommandRejection.NoValidEntities;
            }

            case CommandKind.DebugKill:
            {
                if (!world.Config.CheatsAllowed)
                {
                    return CommandRejection.CheatsDisabled;
                }

                int killed = 0;
                foreach (EntityId entity in entities)
                {
                    if (world.Kill(entity))
                    {
                        killed++;
                    }
                }

                return killed > 0 ? null : CommandRejection.NoValidEntities;
            }

            case CommandKind.Delete:
            {
                int deleted = 0;
                foreach (EntityId entity in entities)
                {
                    if (IsOwned(world, entity, command.Player) && world.Destroy(entity))
                    {
                        deleted++;
                    }
                }

                return deleted > 0 ? null : CommandRejection.NoValidEntities;
            }

            case CommandKind.DebugSpawn:
                if (!world.Config.CheatsAllowed)
                {
                    return CommandRejection.CheatsDisabled;
                }

                if ((uint)command.Arg0 >= (uint)world.Ruleset.EntityTypes.Count || command.Arg1 is < 1 or > MaxDebugSpawn)
                {
                    return CommandRejection.InvalidArgument;
                }

                if (!world.Map.ContainsPoint(command.X, command.Y))
                {
                    return CommandRejection.OutOfMap;
                }

                // Global entity cap: reject up front, never fail half-way through the tick (ADR 0019).
                if (world.Entities.RemainingCapacity < command.Arg1)
                {
                    return CommandRejection.EntityLimit;
                }

                for (int n = 0; n < command.Arg1; n++)
                {
                    world.Spawn((ushort)command.Arg0, command.Player, command.X, command.Y);
                }

                return null;

            default:
                return CommandRejection.UnknownKind;
        }
    }

    // Dense position in the motion store of an owned, mobile, live unit, or -1.
    private static int OwnedMover(World world, EntityId entity, byte player)
    {
        if (!IsOwned(world, entity, player))
        {
            return -1;
        }

        int position = world.Motions.IndexOf(entity);
        return position >= 0 && world.Motions.At(position).SpeedPerTick > 0 ? position : -1;
    }

    private static int CountOwnedMovers(World world, byte player, ReadOnlySpan<EntityId> entities)
    {
        int n = 0;
        foreach (EntityId entity in entities)
        {
            n += OwnedMover(world, entity, player) >= 0 ? 1 : 0;
        }

        return n;
    }

    private static bool IsOwned(World world, EntityId entity, byte player)
    {
        if (!world.Entities.IsAlive(entity))
        {
            return false;
        }

        int position = world.Identities.IndexOf(entity);
        return position >= 0 && world.Identities.At(position).Owner == player;
    }
}
