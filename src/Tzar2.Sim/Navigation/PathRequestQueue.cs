using System;
using System.IO;
using System.Runtime.InteropServices;
using Tzar2.Core.Hashing;
using Tzar2.Core.Serialization;

namespace Tzar2.Sim.Navigation;

/// <summary>Flags of <see cref="PathRequest"/>.</summary>
[Flags]
public enum PathRequestFlags
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Start, target (and for groups the arrival slots) are fixed; the result is a pure function of them.</summary>
    Prepared = 1,

    /// <summary>The unit follows its group's shared flow field instead of an own path.</summary>
    Flow = 2,

    /// <summary>The first flow member of its group: carries the flow field's budget cost.</summary>
    FlowHead = 4,
}

/// <summary>One queued planning request (state).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PathRequest
{
#pragma warning disable CA1051 // Plain data.
    /// <summary>The unit (<see cref="Tzar2.Core.Ids.EntityId.Value"/>).</summary>
    public uint Entity;

    /// <summary>The unit's request serial when queued; a newer order makes the request stale.</summary>
    public int Serial;

    /// <summary>Group id (members of one move order are queued consecutively), 0 = single unit.</summary>
    public int Group;

    /// <summary>Flags.</summary>
    public PathRequestFlags Flags;

    /// <summary>Start point (world units), fixed when prepared.</summary>
    public int StartX;

    /// <summary>Start point Y.</summary>
    public int StartY;

    /// <summary>Target point, fixed when prepared.</summary>
    public int TargetX;

    /// <summary>Target point Y.</summary>
    public int TargetY;

    /// <summary>Node expansions spent preparing (goal resolution, slots), charged on acceptance.</summary>
    public int PrepCost;
#pragma warning restore CA1051
}

/// <summary>
/// The FIFO of planning requests (REQ-PATH-03: processed in order of (tick, order, entity) under a count-based
/// budget). State: snapshotted and in the full-state hash.
/// </summary>
public sealed class PathRequestQueue
{
    private PathRequest[] _items = new PathRequest[256];
    private int _head;
    private int _count;

    /// <summary>Queued requests (including stale ones not yet skipped).</summary>
    public int Count => _count;

    /// <summary>The request at queue position <paramref name="i"/> (0 = head).</summary>
    public ref PathRequest this[int i]
    {
        get
        {
            if ((uint)i >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(i));
            }

            return ref _items[_head + i];
        }
    }

    /// <summary>Appends a request.</summary>
    public void Enqueue(in PathRequest request)
    {
        if (_head + _count == _items.Length)
        {
            if (_head > 0)
            {
                Array.Copy(_items, _head, _items, 0, _count);
                _head = 0;
            }

            if (_count == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
            }
        }

        _items[_head + _count++] = request;
    }

    /// <summary>Removes <paramref name="n"/> requests from the head.</summary>
    public void Dequeue(int n)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, _count);
        _head += n;
        _count -= n;
        if (_count == 0)
        {
            _head = 0;
        }
    }

    /// <summary>Removes every request <paramref name="isStale"/> accepts, preserving order.</summary>
    public void RemoveWhere<TState>(TState state, Func<TState, PathRequest, bool> isStale)
    {
        ArgumentNullException.ThrowIfNull(isStale);
        int w = 0;
        for (int i = 0; i < _count; i++)
        {
            PathRequest r = _items[_head + i];
            if (!isStale(state, r))
            {
                _items[w++] = r;
            }
        }

        _head = 0;
        _count = w;
    }

    /// <summary>Adds the queue to a hash.</summary>
    public void AppendTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.AddSpan<PathRequest>(_items.AsSpan(_head, _count));
    }

    /// <summary>Writes the queue.</summary>
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt64((ulong)_count);
        writer.WriteBytes(MemoryMarshal.AsBytes(_items.AsSpan(_head, _count)));
    }

    /// <summary>Replaces the queue with data written by <see cref="WriteTo"/>.</summary>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    public void ReadFrom(ref BinaryBufferReader reader)
    {
        ulong count = reader.ReadVarUInt64();
        int size = Marshal.SizeOf<PathRequest>();
        if (count > (ulong)(reader.Remaining / size))
        {
            throw new InvalidDataException("Path queue truncated.");
        }

        _head = 0;
        _count = (int)count;
        if (_items.Length < _count)
        {
            _items = new PathRequest[Math.Max(_count, 256)];
        }

        reader.ReadBytes(_count * size).CopyTo(MemoryMarshal.AsBytes(_items.AsSpan(0, _count)));
    }
}
