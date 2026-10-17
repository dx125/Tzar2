using System;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;

namespace Tzar2.Sim.Ecs;

/// <summary>
/// Allocates <see cref="EntityId"/> handles (REQ-SIM-07, ADR 0015). Fresh slots are handed out in ascending index
/// order; destroyed slots are recycled first-in first-out, but only after <see cref="CommitDestroyed"/> (pipeline step
/// 17, "ID recycling"), so a handle destroyed in tick T is never reused within tick T. Every reuse bumps the slot's
/// 12-bit generation, so stale handles stop resolving.
/// </summary>
public sealed class EntityRegistry
{
    private const int InitialCapacity = 1024;

    // Per slot: current generation (0 = never used) and whether it is alive.
    private ushort[] _generations;
    private byte[] _alive;
    private int _highWater;
    private int _aliveCount;

    // FIFO ring of recyclable slot indices.
    private int[] _free;
    private int _freeHead;
    private int _freeCount;

    // Destroyed this tick, recycled by CommitDestroyed.
    private int[] _pending;
    private int _pendingCount;

    /// <summary>Creates an empty registry.</summary>
    public EntityRegistry()
    {
        _generations = new ushort[InitialCapacity];
        _alive = new byte[InitialCapacity];
        _free = new int[InitialCapacity];
        _pending = new int[64];
    }

    /// <summary>Number of live entities.</summary>
    public int AliveCount => _aliveCount;

    /// <summary>One past the highest slot index ever used.</summary>
    public int HighWater => _highWater;


    /// <summary>Allocates a handle.</summary>
    /// <exception cref="InvalidOperationException">All 1,048,576 slots are alive.</exception>
    public EntityId Create()
    {
        int index;
        int generation;
        if (_freeCount > 0)
        {
            index = _free[_freeHead];
            _freeHead = (_freeHead + 1) % _free.Length;
            _freeCount--;
            generation = EntityId.NextGeneration(_generations[index]);
        }
        else
        {
            if (_highWater > EntityId.MaxIndex)
            {
                throw new InvalidOperationException("Entity limit reached (ADR 0005: 20-bit index).");
            }

            index = _highWater++;
            EnsureCapacity(_highWater);
            generation = 1;
        }

        _generations[index] = (ushort)generation;
        _alive[index] = 1;
        _aliveCount++;
        return EntityId.Create(index, generation);
    }

    /// <summary>Whether <paramref name="id"/> refers to a live entity (right slot and generation).</summary>
    public bool IsAlive(EntityId id)
    {
        int index = id.Index;
        return !id.IsNone && index < _highWater && _alive[index] != 0 && _generations[index] == id.Generation;
    }

    /// <summary>The live handle in slot <paramref name="index"/>, or <see cref="EntityId.None"/>.</summary>
    public EntityId AtIndex(int index)
        => (uint)index < (uint)_highWater && _alive[index] != 0 ? EntityId.Create(index, _generations[index]) : EntityId.None;

    /// <summary>Marks <paramref name="id"/> dead. Its slot is recycled by the next <see cref="CommitDestroyed"/>.</summary>
    /// <returns><c>false</c> if <paramref name="id"/> was not alive.</returns>
    internal bool Destroy(EntityId id)
    {
        if (!IsAlive(id))
        {
            return false;
        }

        _alive[id.Index] = 0;
        _aliveCount--;
        if (_pendingCount == _pending.Length)
        {
            Array.Resize(ref _pending, _pending.Length * 2);
        }

        _pending[_pendingCount++] = id.Index;
        return true;
    }

    /// <summary>Moves the slots destroyed since the last call to the recycle queue, in destruction order.</summary>
    public void CommitDestroyed()
    {
        for (int i = 0; i < _pendingCount; i++)
        {
            EnqueueFree(_pending[i]);
        }

        _pendingCount = 0;
    }

    /// <summary>Adds the registry to a state hash.</summary>
    public void AppendTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Add(_highWater).Add(_aliveCount);
        hasher.AddSpan<ushort>(_generations.AsSpan(0, _highWater));
        hasher.AddSpan<byte>(_alive.AsSpan(0, _highWater));
        hasher.Add(_freeCount);
        for (int i = 0; i < _freeCount; i++)
        {
            hasher.Add(_free[(_freeHead + i) % _free.Length]);
        }

        hasher.AddSpan<int>(_pending.AsSpan(0, _pendingCount));
    }

    /// <summary>Writes the registry for a snapshot.</summary>
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt64((ulong)_highWater);
        for (int i = 0; i < _highWater; i++)
        {
            writer.WriteUInt16(_generations[i]);
            writer.WriteByte(_alive[i]);
        }

        writer.WriteVarUInt64((ulong)_freeCount);
        for (int i = 0; i < _freeCount; i++)
        {
            writer.WriteVarUInt64((ulong)_free[(_freeHead + i) % _free.Length]);
        }

        writer.WriteVarUInt64((ulong)_pendingCount);
        for (int i = 0; i < _pendingCount; i++)
        {
            writer.WriteVarUInt64((ulong)_pending[i]);
        }
    }

    /// <summary>Replaces this registry's state with one written by <see cref="WriteTo"/>.</summary>
    /// <exception cref="System.IO.InvalidDataException">The data is malformed.</exception>
    public void ReadFrom(ref BinaryBufferReader reader)
    {
        int highWater = ReadCount(ref reader, EntityId.MaxIndex + 1);
        EnsureCapacity(highWater);
        Array.Clear(_generations);
        Array.Clear(_alive);
        _highWater = highWater;
        _aliveCount = 0;
        for (int i = 0; i < highWater; i++)
        {
            ushort generation = reader.ReadUInt16();
            byte alive = reader.ReadByte();
            if (generation > EntityId.MaxGeneration || alive > 1 || (alive == 1 && generation == 0))
            {
                throw new System.IO.InvalidDataException($"Invalid entity slot {i} in snapshot.");
            }

            _generations[i] = generation;
            _alive[i] = alive;
            _aliveCount += alive;
        }

        _freeHead = 0;
        _freeCount = 0;
        int freeCount = ReadCount(ref reader, highWater);
        for (int i = 0; i < freeCount; i++)
        {
            EnqueueFree(ReadSlot(ref reader, highWater));
        }

        _pendingCount = 0;
        int pendingCount = ReadCount(ref reader, highWater);
        for (int i = 0; i < pendingCount; i++)
        {
            if (_pendingCount == _pending.Length)
            {
                Array.Resize(ref _pending, _pending.Length * 2);
            }

            _pending[_pendingCount++] = ReadSlot(ref reader, highWater);
        }
    }

    private static int ReadCount(ref BinaryBufferReader reader, int max)
    {
        ulong value = reader.ReadVarUInt64();
        if (value > (ulong)max)
        {
            throw new System.IO.InvalidDataException($"Count {value} exceeds {max}.");
        }

        return (int)value;
    }

    private int ReadSlot(ref BinaryBufferReader reader, int highWater)
    {
        int slot = ReadCount(ref reader, highWater - 1);
        if (_alive[slot] != 0)
        {
            throw new System.IO.InvalidDataException($"Slot {slot} is both alive and free.");
        }

        return slot;
    }

    private void EnqueueFree(int index)
    {
        if (_freeCount == _free.Length)
        {
            var grown = new int[_free.Length * 2];
            for (int i = 0; i < _freeCount; i++)
            {
                grown[i] = _free[(_freeHead + i) % _free.Length];
            }

            _free = grown;
            _freeHead = 0;
        }

        _free[(_freeHead + _freeCount) % _free.Length] = index;
        _freeCount++;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _generations.Length)
        {
            return;
        }

        int capacity = _generations.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        capacity = Math.Min(capacity, EntityId.MaxIndex + 1);
        Array.Resize(ref _generations, capacity);
        Array.Resize(ref _alive, capacity);
    }
}
