using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;

namespace Tzar2.Sim.Navigation;

/// <summary>
/// The plan of every unit that follows a path (state: part of snapshots and the full-state hash, REQ-SIM-13): either
/// waypoints (x, y pairs; <c>MotionMode.Path</c>) or the portal nodes of an abstract route (<c>MotionMode.Route</c>).
/// Indexed by entity slot; buffers are reused and only grow, so steady-state planning does not allocate.
/// </summary>
public sealed class PathStore
{
    /// <summary>Most values per plan.</summary>
    public const int MaxValues = 1 << 17;

    // Every slot starts with a small buffer and keeps buffers up to KeepBuffer when cleared; only longer ones go back
    // to the shared pool. Steady-state planning then stops allocating quickly.
    private const int SmallBuffer = 64;
    private const int KeepBuffer = 512;

    private int[][] _points = [];
    private Stack<int[]>?[] _free = [];
    private int[] _lengths = [];

    /// <summary>Plan buffers created or grown so far (diagnostics).</summary>
    public int BuffersAllocated { get; private set; }

    /// <summary>The plan of slot <paramref name="entityIndex"/>; empty if it has none.</summary>
    public ReadOnlySpan<int> Get(int entityIndex)
        => (uint)entityIndex < (uint)_lengths.Length ? _points[entityIndex].AsSpan(0, _lengths[entityIndex]) : [];

    /// <summary>Number of values in a slot's plan.</summary>
    public int Count(int entityIndex) => (uint)entityIndex < (uint)_lengths.Length ? _lengths[entityIndex] : 0;

    /// <summary>Replaces a slot's plan.</summary>
    public void Set(int entityIndex, ReadOnlySpan<int> waypoints)
    {
        if (waypoints.Length > MaxValues)
        {
            throw new ArgumentException("Plan too long.", nameof(waypoints));
        }

        Ensure(entityIndex);
        if (_points[entityIndex].Length < waypoints.Length)
        {
            Return(_points[entityIndex]);
            _points[entityIndex] = Rent(waypoints.Length);
        }

        waypoints.CopyTo(_points[entityIndex]);
        _lengths[entityIndex] = waypoints.Length;
    }

    /// <summary>Forgets a slot's path (a large buffer goes back to the shared pool).</summary>
    public void Clear(int entityIndex)
    {
        if ((uint)entityIndex < (uint)_lengths.Length)
        {
            _lengths[entityIndex] = 0;
            if (_points[entityIndex].Length > KeepBuffer)
            {
                Return(_points[entityIndex]);
                _points[entityIndex] = Rent(SmallBuffer);
            }
        }
    }

    // Power-of-two size classes shared by all slots, so the number of buffers is bounded by the plans alive at once.
    private int[] Rent(int length)
    {
        int size = SmallBuffer;
        int cls = 0;
        while (size < length)
        {
            size *= 2;
            cls++;
        }

        if (cls >= _free.Length)
        {
            Array.Resize(ref _free, cls + 1);
        }

        Stack<int[]> pool = _free[cls] ??= new Stack<int[]>();
        if (pool.Count > 0)
        {
            return pool.Pop();
        }

        BuffersAllocated++;
        return new int[size];
    }

    private void Return(int[] buffer)
    {
        if (buffer.Length < SmallBuffer)
        {
            return;
        }

        int cls = 0;
        for (int size = SmallBuffer; size < buffer.Length; size *= 2)
        {
            cls++;
        }

        if (cls >= _free.Length)
        {
            Array.Resize(ref _free, cls + 1);
        }

        (_free[cls] ??= new Stack<int[]>()).Push(buffer);
    }

    /// <summary>Adds every non-empty path, ascending by slot, to a hash.</summary>
    public void AppendTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        for (int i = 0; i < _lengths.Length; i++)
        {
            if (_lengths[i] > 0)
            {
                hasher.Add(i).AddSpan<int>(Get(i));
            }
        }

        hasher.Add(-1);
    }

    /// <summary>Writes every non-empty path, ascending by slot (canonical).</summary>
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int n = 0;
        foreach (int length in _lengths)
        {
            n += length > 0 ? 1 : 0;
        }

        writer.WriteVarUInt64((ulong)n);
        for (int i = 0; i < _lengths.Length; i++)
        {
            if (_lengths[i] == 0)
            {
                continue;
            }

            writer.WriteVarUInt64((ulong)i);
            writer.WriteVarUInt64((ulong)_lengths[i]);
            foreach (int v in Get(i))
            {
                writer.WriteVarInt64(v);
            }
        }
    }

    /// <summary>Replaces the contents with data written by <see cref="WriteTo"/>.</summary>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    public void ReadFrom(ref BinaryBufferReader reader)
    {
        Array.Clear(_lengths);
        ulong n = reader.ReadVarUInt64();
        if (n > EntityId.MaxIndex + 1UL)
        {
            throw new InvalidDataException("Too many paths in snapshot.");
        }

        long previous = -1;
        int[] scratch = [];
        for (ulong k = 0; k < n; k++)
        {
            ulong index = reader.ReadVarUInt64();
            ulong length = reader.ReadVarUInt64();
            if (index > EntityId.MaxIndex || (long)index <= previous || length is 0 or > MaxValues)
            {
                throw new InvalidDataException("Invalid path in snapshot.");
            }

            previous = (long)index;
            if (scratch.Length < (int)length)
            {
                scratch = new int[(int)length];
            }

            for (int i = 0; i < (int)length; i++)
            {
                long v = reader.ReadVarInt64();
                if (v is < int.MinValue or > int.MaxValue)
                {
                    throw new InvalidDataException("Waypoint out of range.");
                }

                scratch[i] = (int)v;
            }

            Set((int)index, scratch.AsSpan(0, (int)length));
        }
    }

    private void Ensure(int entityIndex)
    {
        if (entityIndex >= _lengths.Length)
        {
            int size = Math.Max(entityIndex + 1, Math.Max(64, _lengths.Length * 2));
            int old = _lengths.Length;
            Array.Resize(ref _lengths, size);
            Array.Resize(ref _points, size);
            for (int i = old; i < size; i++)
            {
                _points[i] = Rent(SmallBuffer);
            }
        }
    }
}
