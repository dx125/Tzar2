using System;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;

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
                if (!world.Map.ContainsPoint(command.X, command.Y))
                {
                    return CommandRejection.OutOfMap;
                }

                return ForOwnedMovers(world, command, entities, static (ref Motion m, in Command c) =>
                {
                    m.TargetX = c.X;
                    m.TargetY = c.Y;
                    m.State = MotionState.Moving;
                });

            case CommandKind.Stop:
                return ForOwnedMovers(world, command, entities, static (ref Motion m, in Command c) => m.State = MotionState.Idle);

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

    private delegate void MotionUpdate(ref Motion motion, in Command command);

    private static CommandRejection? ForOwnedMovers(World world, in Command command, ReadOnlySpan<EntityId> entities, MotionUpdate update)
    {
        int applied = 0;
        foreach (EntityId entity in entities)
        {
            if (!IsOwned(world, entity, command.Player))
            {
                continue;
            }

            int position = world.Motions.IndexOf(entity);
            if (position < 0 || world.Motions.At(position).SpeedPerTick <= 0)
            {
                continue;
            }

            update(ref world.Motions.At(position), command);
            applied++;
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
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
