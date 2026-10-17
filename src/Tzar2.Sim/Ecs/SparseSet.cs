using System;
using System.Collections.Generic;
using Tzar2.Core.Ids;

namespace Tzar2.Sim.Ecs;

/// <summary>
/// A sparse set from <see cref="EntityId"/> to <typeparamref name="T"/> whose dense arrays stay sorted by entity index
/// (REQ-SIM-07: all iteration is in ascending index order; ADR 0015). The order therefore depends only on which
/// entities have the component, never on insertion history, so two worlds in the same logical state have
/// byte-identical stores, hashes and snapshots.
/// </summary>
/// <remarks>
/// Lookups are O(1) through the sparse array. Adding an entity with a higher index than all present ones (the usual
/// case: fresh slots are allocated in ascending order) is O(1); adding into the middle or removing shifts the tail
/// (one <c>memmove</c> plus sparse fix-ups). References returned by <see cref="Get"/> and spans from
/// <see cref="Entities"/> are invalidated by any add or remove on the same store.
/// </remarks>
/// <typeparam name="T">The component type.</typeparam>
public abstract class SparseSet<T>
{
    private const int InitialCapacity = 64;

    // Sparse: entity index -> dense position + 1 (0 = absent).
    private int[] _sparse = new int[InitialCapacity];
    private EntityId[] _entities = new EntityId[InitialCapacity];
    private T[] _dense = new T[InitialCapacity];
    private int _count;

    /// <summary>Number of entities that have the component.</summary>
    public int Count => _count;

    /// <summary>The entities that have the component, ascending by index; parallel to the component data.</summary>
    public ReadOnlySpan<EntityId> Entities => _entities.AsSpan(0, _count);

    /// <summary>The dense component data (writable by systems; parallel to <see cref="Entities"/>).</summary>
    protected Span<T> DenseSpan => _dense.AsSpan(0, _count);

    /// <summary>The backing entity array (first <see cref="Count"/> entries valid).</summary>
    protected EntityId[] EntityArray => _entities;

    /// <summary>The backing dense array (first <see cref="Count"/> entries valid).</summary>
    protected T[] DenseArray => _dense;

    /// <summary>Whether <paramref name="entity"/> has the component.</summary>
    public bool Has(EntityId entity) => IndexOf(entity) >= 0;

    /// <summary>Dense position of <paramref name="entity"/>, or -1.</summary>
    public int IndexOf(EntityId entity)
    {
        int index = entity.Index;
        if ((uint)index >= (uint)_sparse.Length || entity.IsNone)
        {
            return -1;
        }

        int position = _sparse[index] - 1;
        return position >= 0 && _entities[position] == entity ? position : -1;
    }

    /// <summary>The component of <paramref name="entity"/>, by reference.</summary>
    /// <exception cref="KeyNotFoundException">The entity does not have the component.</exception>
    public ref T Get(EntityId entity)
    {
        int position = IndexOf(entity);
        if (position < 0)
        {
            throw new KeyNotFoundException($"{entity} has no {typeof(T).Name}.");
        }

        return ref _dense[position];
    }

    /// <summary>The component at dense position <paramref name="position"/>, by reference.</summary>
    public ref T At(int position)
    {
        if ((uint)position >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        return ref _dense[position];
    }

    /// <summary>Adds the component, or replaces it if present. The caller guarantees the entity is alive.</summary>
    internal void Set(EntityId entity, T value)
    {
        if (entity.IsNone)
        {
            throw new ArgumentException("Cannot add a component to EntityId.None.", nameof(entity));
        }

        int existing = IndexOf(entity);
        if (existing >= 0)
        {
            _dense[existing] = value;
            return;
        }

        int index = entity.Index;
        EnsureSparse(index);
        if (_sparse[index] != 0)
        {
            throw new InvalidOperationException($"Slot {index} still holds a stale {typeof(T).Name}.");
        }

        EnsureDense(_count + 1);
        int position = _count;
        if (_count > 0 && _entities[_count - 1].Index > index)
        {
            position = LowerBound(index);
            Array.Copy(_entities, position, _entities, position + 1, _count - position);
            Array.Copy(_dense, position, _dense, position + 1, _count - position);
            for (int i = position + 1; i <= _count; i++)
            {
                _sparse[_entities[i].Index] = i + 1;
            }
        }

        _entities[position] = entity;
        _dense[position] = value;
        _sparse[index] = position + 1;
        _count++;
    }

    /// <summary>Removes the component of <paramref name="entity"/>.</summary>
    /// <returns><c>false</c> if it was not present.</returns>
    internal bool Remove(EntityId entity)
    {
        int position = IndexOf(entity);
        if (position < 0)
        {
            return false;
        }

        _sparse[entity.Index] = 0;
        int tail = _count - position - 1;
        if (tail > 0)
        {
            Array.Copy(_entities, position + 1, _entities, position, tail);
            Array.Copy(_dense, position + 1, _dense, position, tail);
            for (int i = position; i < _count - 1; i++)
            {
                _sparse[_entities[i].Index] = i + 1;
            }
        }

        _count--;
        _entities[_count] = default;
        _dense[_count] = default!;
        return true;
    }

    /// <summary>Removes everything.</summary>
    internal void Clear()
    {
        for (int i = 0; i < _count; i++)
        {
            _sparse[_entities[i].Index] = 0;
        }

        Array.Clear(_entities, 0, _count);
        Array.Clear(_dense, 0, _count);
        _count = 0;
    }

    /// <summary>
    /// Restores contents from a snapshot: <paramref name="entities"/> must be strictly ascending. The data must be written
    /// into <see cref="DenseArray"/> by the caller after this returns.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">The entities are not strictly ascending by index.</exception>
    protected void LoadEntities(ReadOnlySpan<EntityId> entities)
    {
        Clear();
        EnsureDense(entities.Length);
        int previous = -1;
        for (int i = 0; i < entities.Length; i++)
        {
            EntityId entity = entities[i];
            if (entity.IsNone || entity.Index <= previous)
            {
                throw new System.IO.InvalidDataException($"{typeof(T).Name} store entities are not strictly ascending.");
            }

            previous = entity.Index;
            EnsureSparse(entity.Index);
            _entities[i] = entity;
            _sparse[entity.Index] = i + 1;
        }

        _count = entities.Length;
    }

    private int LowerBound(int index)
    {
        int lo = 0;
        int hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_entities[mid].Index < index)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private void EnsureSparse(int index)
    {
        if (index < _sparse.Length)
        {
            return;
        }

        int capacity = _sparse.Length;
        while (capacity <= index)
        {
            capacity *= 2;
        }

        Array.Resize(ref _sparse, capacity);
    }

    private void EnsureDense(int required)
    {
        if (required <= _dense.Length)
        {
            return;
        }

        int capacity = _dense.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        Array.Resize(ref _entities, capacity);
        Array.Resize(ref _dense, capacity);
    }
}
