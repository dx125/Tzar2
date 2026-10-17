using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;

namespace Tzar2.Sim.Ecs;

/// <summary>
/// Marker for <b>hot</b> components (REQ-SIM-08): unmanaged, padding-free structs that live in dense arrays and are
/// snapshotted and hashed as raw memory. Padding bytes would make hashes and snapshots irreproducible; a test checks
/// every implementing type in Tzar2.Sim.
/// </summary>
public interface IComponent
{
}

/// <summary>
/// <b>Cold</b> components (names, inventories, text): managed data that is touched rarely. They are serialized
/// field by field and included in the full-state hash only (REQ-SIM-12).
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
public interface IColdComponent<TSelf>
    where TSelf : IColdComponent<TSelf>
{
    /// <summary>Writes one value.</summary>
    static abstract void Write(BinaryBufferWriter writer, in TSelf value);

    /// <summary>Reads one value written by <see cref="Write"/>.</summary>
    static abstract TSelf Read(ref BinaryBufferReader reader);
}

/// <summary>Whether a store holds hot or cold data.</summary>
public enum ComponentTier : byte
{
    /// <summary>Unmanaged, block-copied, in the per-tick hash.</summary>
    Hot = 0,

    /// <summary>Managed, serialized, in the full-state hash only.</summary>
    Cold = 1,
}

/// <summary>Type-erased view of a component store, used by the world, hashing and snapshots.</summary>
public interface IComponentStore
{
    /// <summary>Stable key written into snapshots (e.g. <c>core.position</c>).</summary>
    string Key { get; }

    /// <summary>Hot or cold.</summary>
    ComponentTier Tier { get; }

    /// <summary>Number of entities with the component.</summary>
    int Count { get; }

    /// <summary>The entities with the component, ascending.</summary>
    ReadOnlySpan<EntityId> Entities { get; }

    /// <summary>Removes the entity's component, if present.</summary>
    bool RemoveEntity(EntityId entity);

    /// <summary>Adds the store's contents to a state hash.</summary>
    void AppendTo(StateHasher hasher);

    /// <summary>Writes the store's contents.</summary>
    void WriteTo(BinaryBufferWriter writer);

    /// <summary>Replaces the store's contents with data written by <see cref="WriteTo"/>.</summary>
    void ReadFrom(ref BinaryBufferReader reader);
}

/// <summary>A hot component store: dense unmanaged data sorted by entity index (REQ-SIM-08, ADR 0015).</summary>
/// <typeparam name="T">An unmanaged, padding-free component struct.</typeparam>
public sealed class ComponentStore<T> : SparseSet<T>, IComponentStore
    where T : unmanaged, IComponent
{
    internal ComponentStore(string key) => Key = key;

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public ComponentTier Tier => ComponentTier.Hot;

    /// <summary>The dense component data, parallel to <see cref="SparseSet{T}.Entities"/>, writable in place.</summary>
    public Span<T> Components => DenseSpan;

    /// <inheritdoc />
    public bool RemoveEntity(EntityId entity) => Remove(entity);

    /// <inheritdoc />
    public void AppendTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.AddSpan<EntityId>(Entities).AddSpan<T>(DenseSpan);
    }

    /// <inheritdoc />
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt64((ulong)Count);
        writer.WriteVarUInt64((ulong)Unsafe.SizeOf<T>());
        writer.WriteBytes(MemoryMarshal.AsBytes(Entities));
        writer.WriteBytes(MemoryMarshal.AsBytes(DenseSpan));
    }

    /// <inheritdoc />
    public void ReadFrom(ref BinaryBufferReader reader)
    {
        ulong count = reader.ReadVarUInt64();
        ulong size = reader.ReadVarUInt64();
        if (count > EntityId.MaxIndex + 1UL)
        {
            throw new InvalidDataException($"Store '{Key}' has {count} entries.");
        }

        if (size != (ulong)Unsafe.SizeOf<T>())
        {
            throw new InvalidDataException($"Store '{Key}' element size {size} does not match {Unsafe.SizeOf<T>()}.");
        }

        int n = (int)count;
        ReadOnlySpan<EntityId> entities = MemoryMarshal.Cast<byte, EntityId>(reader.ReadBytes(checked(n * sizeof(uint))));
        LoadEntities(entities);
        reader.ReadBytes(checked(n * Unsafe.SizeOf<T>())).CopyTo(MemoryMarshal.AsBytes(DenseArray.AsSpan(0, n)));
    }
}

/// <summary>A cold component store: managed data sorted by entity index, serialized per value.</summary>
/// <typeparam name="T">The cold component type.</typeparam>
public sealed class ColdComponentStore<T> : SparseSet<T>, IComponentStore
    where T : IColdComponent<T>
{
    private readonly BinaryBufferWriter _scratch = new();

    internal ColdComponentStore(string key) => Key = key;

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public ComponentTier Tier => ComponentTier.Cold;

    /// <summary>The dense component data, parallel to <see cref="SparseSet{T}.Entities"/>.</summary>
    public ReadOnlySpan<T> Components => DenseSpan;

    /// <inheritdoc />
    public bool RemoveEntity(EntityId entity) => Remove(entity);

    /// <inheritdoc />
    public void AppendTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        _scratch.Clear();
        WriteTo(_scratch);
        hasher.Add(_scratch.WrittenSpan);
    }

    /// <inheritdoc />
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt64((ulong)Count);
        ReadOnlySpan<EntityId> entities = Entities;
        ReadOnlySpan<T> values = DenseSpan;
        for (int i = 0; i < entities.Length; i++)
        {
            writer.WriteUInt32(entities[i].Value);
            T.Write(writer, values[i]);
        }
    }

    /// <inheritdoc />
    public void ReadFrom(ref BinaryBufferReader reader)
    {
        ulong count = reader.ReadVarUInt64();
        if (count > (ulong)reader.Remaining / sizeof(uint))
        {
            throw new InvalidDataException($"Store '{Key}' claims {count} entries.");
        }

        int n = (int)count;
        var entities = new EntityId[n];
        var values = new T[n];
        for (int i = 0; i < n; i++)
        {
            entities[i] = EntityId.FromValue(reader.ReadUInt32());
            values[i] = T.Read(ref reader);
        }

        LoadEntities(entities);
        values.CopyTo(DenseArray, 0);
    }

    /// <summary>Adds or replaces the component (cold stores are written through <see cref="World"/>).</summary>
    internal void SetValue(EntityId entity, T value) => Set(entity, value);
}
