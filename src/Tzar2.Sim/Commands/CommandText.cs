using System;
using System.Globalization;
using System.Text;
using Tzar2.Core.Ids;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Commands;

/// <summary>
/// Commands and entities in words, for diagnostics only (ADR 0176): the host's match trace and the AI's decision trace
/// both use it, and nothing in the simulation reads the text.
/// </summary>
public static class CommandText
{
    /// <summary>A command: player, kind, units (the first six), target, tile and arguments.</summary>
    public static string Describe(World world, CommandList commands, int index)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(commands);
        ref readonly Command c = ref commands[index];
        ReadOnlySpan<EntityId> units = commands.EntitiesOf(c);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"p{c.Player} {c.Kind}");
        if (units.Length > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" units {units.Length} [");
            for (int i = 0; i < Math.Min(units.Length, 6); i++)
            {
                text.Append(i == 0 ? string.Empty : " ").Append(Entity(world, units[i]));
            }

            text.Append(units.Length > 6 ? " ...]" : "]");
        }

        if (!c.Target.IsNone)
        {
            text.Append(" target ").Append(Entity(world, c.Target));
        }

        if (c.X != 0 || c.Y != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" at {c.X / MapState.UnitsPerTile},{c.Y / MapState.UnitsPerTile}");
        }

        if (c.Arg0 != 0 || c.Arg1 != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" args {c.Arg0},{c.Arg1}");
        }

        if (c.Flags != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" flags {c.Flags}");
        }

        return text.ToString();
    }

    /// <summary>An entity as <c>type#id</c>, or <c>#id</c> when it no longer exists.</summary>
    public static string Entity(World world, EntityId id)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Entities.IsAlive(id)
            ? string.Create(CultureInfo.InvariantCulture, $"{world.TypeOf(id).Key}#{id.Value}")
            : string.Create(CultureInfo.InvariantCulture, $"#{id.Value}");
    }
}
