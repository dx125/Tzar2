using System;
using Tzar2.Core.Ids;

namespace Tzar2.Sim.Commands;

/// <summary>
/// An ordered list of commands with their entity sets (TDD §7.4). Used for a client's per-tick batch (players not
/// yet stamped) and for the closed command set of one tick (every command stamped with its sender's slot).
/// Reusable: <see cref="Clear"/> keeps the buffers, so steady-state ticks do not allocate.
/// </summary>
public sealed class CommandList
{
    /// <summary>Most commands in one list (bounded decoding, REQ-NET-11).</summary>
    public const int MaxCommands = 4096;

    /// <summary>Most entities in one command's set.</summary>
    public const int MaxEntitiesPerCommand = 4096;

    /// <summary>Most entities over all commands of one list (bounds decoding memory to 128 KiB, REQ-NET-11).</summary>
    public const int MaxEntitiesPerList = 32768;

    /// <summary>The flags a command may carry.</summary>
    internal const CommandFlags ValidFlags = CommandFlags.Queue | CommandFlags.Ctrl | CommandFlags.Alt;

    private Command[] _commands = new Command[16];
    private EntityId[] _entities = new EntityId[64];
    private int _count;
    private int _entityCount;
    private EntityId[]? _decodeScratch;

    /// <summary>Number of commands.</summary>
    public int Count => _count;

    /// <summary>The commands in execution order.</summary>
    public ReadOnlySpan<Command> Commands => _commands.AsSpan(0, _count);

    /// <summary>The command at <paramref name="index"/>.</summary>
    public ref readonly Command this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return ref _commands[index];
        }
    }

    /// <summary>The entity set of <paramref name="command"/> (a command of this list).</summary>
    public ReadOnlySpan<EntityId> EntitiesOf(in Command command) => _entities.AsSpan(command.EntityOffset, command.EntityCount);

    /// <summary>Removes all commands, keeping the buffers.</summary>
    public void Clear()
    {
        _count = 0;
        _entityCount = 0;
    }

    /// <summary>
    /// Appends a command. The entity set is canonicalized: sorted ascending and de-duplicated by slot index (of two
    /// handles for one slot the <b>higher</b> generation, i.e. the newer handle, is kept; at most one can be alive), so
    /// equal selections encode identically. <see cref="EntityId.None"/> entries are dropped. The same limits and
    /// validity rules apply as when decoding, so every list built here round-trips through <see cref="CommandCodec"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The kind is <see cref="CommandKind.None"/> or the flags are unknown.</exception>
    /// <exception cref="InvalidOperationException">The list or the entity set is over its limit.</exception>
    public void Add(
        CommandKind kind,
        ReadOnlySpan<EntityId> entities = default,
        int x = 0,
        int y = 0,
        EntityId target = default,
        int arg0 = 0,
        int arg1 = 0,
        CommandFlags flags = CommandFlags.None,
        byte player = 0)
    {
        if (_count >= MaxCommands)
        {
            throw new InvalidOperationException($"A command list holds at most {MaxCommands} commands.");
        }

        if (entities.Length > MaxEntitiesPerCommand)
        {
            throw new InvalidOperationException($"A command holds at most {MaxEntitiesPerCommand} entities.");
        }

        if (kind == CommandKind.None)
        {
            throw new ArgumentException("CommandKind.None is not a command.", nameof(kind));
        }

        if ((flags & ~ValidFlags) != 0)
        {
            throw new ArgumentException($"Unknown command flags {flags}.", nameof(flags));
        }

        int offset = _entityCount;
        EnsureEntities(offset + entities.Length);
        Span<EntityId> set = _entities.AsSpan(offset, entities.Length);
        entities.CopyTo(set);
        set.Sort();
        int unique = 0;
        for (int i = 0; i < set.Length; i++)
        {
            if (set[i].IsNone)
            {
                continue;
            }

            // Sorted by index, then generation: a later entry for the same slot is the newer handle and replaces it.
            if (unique > 0 && set[unique - 1].Index == set[i].Index)
            {
                set[unique - 1] = set[i];
            }
            else
            {
                set[unique++] = set[i];
            }
        }

        if (_entityCount + unique > MaxEntitiesPerList)
        {
            throw new InvalidOperationException($"A command list holds at most {MaxEntitiesPerList} entities.");
        }

        _entityCount += unique;
        EnsureCommands(_count + 1);
        _commands[_count++] = new Command(kind, player, flags, target, x, y, arg0, arg1, offset, unique);
    }

    /// <summary>
    /// Appends every command of <paramref name="batch"/> with its player stamped as <paramref name="slot"/>
    /// (ADR 0009: the relay overwrites the player of every command with the sender's slot).
    /// </summary>
    public void AppendStamped(byte slot, CommandList batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_count + batch._count > MaxCommands)
        {
            throw new InvalidOperationException($"A command list holds at most {MaxCommands} commands.");
        }

        if (_entityCount + batch._entityCount > MaxEntitiesPerList)
        {
            throw new InvalidOperationException($"A command list holds at most {MaxEntitiesPerList} entities.");
        }

        EnsureCommands(_count + batch._count);
        EnsureEntities(_entityCount + batch._entityCount);
        for (int i = 0; i < batch._count; i++)
        {
            ref readonly Command source = ref batch._commands[i];
            batch.EntitiesOf(source).CopyTo(_entities.AsSpan(_entityCount));
            _commands[_count++] = source.WithPlayer(slot, _entityCount);
            _entityCount += source.EntityCount;
        }
    }

    /// <summary>Whether two lists hold the same commands with the same entity sets.</summary>
    public bool ContentEquals(CommandList other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other._count != _count)
        {
            return false;
        }

        for (int i = 0; i < _count; i++)
        {
            Command a = _commands[i];
            Command b = other._commands[i];
            if (a.Kind != b.Kind || a.Player != b.Player || a.Flags != b.Flags || a.Target != b.Target || a.X != b.X
                || a.Y != b.Y || a.Arg0 != b.Arg0 || a.Arg1 != b.Arg1 || !EntitiesOf(a).SequenceEqual(other.EntitiesOf(b)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Total entities over all commands.</summary>
    internal int EntityTotal => _entityCount;

    /// <summary>Decoder scratch for entity sets (kept per list so decoding does not allocate per call).</summary>
    internal EntityId[] DecodeScratch => _decodeScratch ??= new EntityId[MaxEntitiesPerCommand];

    internal void AddDecoded(in Command command, ReadOnlySpan<EntityId> entities)
    {
        EnsureEntities(_entityCount + entities.Length);
        entities.CopyTo(_entities.AsSpan(_entityCount));
        EnsureCommands(_count + 1);
        _commands[_count++] = command.WithPlayer(command.Player, _entityCount);
        _entityCount += entities.Length;
    }

    private void EnsureCommands(int required)
    {
        if (required > _commands.Length)
        {
            Array.Resize(ref _commands, Math.Max(required, _commands.Length * 2));
        }
    }

    private void EnsureEntities(int required)
    {
        if (required > _entities.Length)
        {
            Array.Resize(ref _entities, Math.Max(required, _entities.Length * 2));
        }
    }
}
