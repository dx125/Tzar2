using System;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;

namespace Tzar2.Sim.Systems;

public sealed partial class CommandSystem
{
    /// <summary>
    /// Executes commands that an in-sim controller issues during a tick (the AI players, REQ-AI-03; ADR 0051) through
    /// exactly the validation of step 1: the AI is just another player and gets no other way to change the world.
    /// Each command's <see cref="Command.Player"/> must be the controlled slot (stamped by the caller). Rejections are
    /// reported as <see cref="SimEventKind.CommandRejected"/> events with the command's index in
    /// <paramref name="commands"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called between ticks (the world is locked).</exception>
    public static void ExecuteInTick(World world, CommandList commands)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(commands);
        if (world.IsLocked)
        {
            throw new InvalidOperationException("In-sim commands can only be executed during a tick.");
        }

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
}
