using System;
using System.IO;
using System.Runtime.InteropServices;
using Tzar2.Core.Hashing;
using Tzar2.Core.Serialization;

namespace Tzar2.Sim.Map;

/// <summary>Kinds of <see cref="Decal"/>.</summary>
public enum DecalKind : byte
{
    /// <summary>Unused.</summary>
    None = 0,

    /// <summary>The body of a killed unit (target of Raise Dead/Necromancy while it exists).</summary>
    Corpse = 1,

    /// <summary>The ruin of a destroyed building (GAME-03; never blocks, ORIG-01). Type id = the building's.</summary>
    Ruin = 2,
}

/// <summary>A footprint-less leftover on the ground (REQ-PATH-08). Never part of passability, costs or occupancy.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Decal
{
#pragma warning disable CA1051 // Plain data.
    /// <summary>X in world units.</summary>
    public int X;

    /// <summary>Y in world units.</summary>
    public int Y;

    /// <summary>The tick whose cleanup removes the decal.</summary>
    public int ExpiresTick;

    /// <summary>Entity type of the dead unit.</summary>
    public ushort TypeId;

    /// <summary>Owner of the dead unit.</summary>
    public byte Owner;

    /// <summary>What it is.</summary>
    public DecalKind Kind;
#pragma warning restore CA1051
}

/// <summary>
/// Corpses, bones and debris (ORIG-01 fix, REQ-PATH-08): a separate index of footprint-less decals with a TTL and a
/// global cap (oldest dropped first). Decals never touch <see cref="MapState"/>'s blockers or occupancy, so mass death
/// can never wall off terrain. Part of the snapshot and the full-state hash.
/// </summary>
/// <remarks>
/// All decals share one TTL per ruleset, so creation order is expiry order and a ring buffer suffices: insertion and
/// expiry are O(1). Spatial queries scan the ring (at most <see cref="Capacity"/> entries, 4,000 by default); a bucketed
/// spatial hash replaces the scan when the spells that query corpses arrive (M3).
/// </remarks>
public sealed class DecalIndex
{
    private readonly Decal[] _ring;
    private int _head;
    private int _count;

    /// <summary>Creates an empty index.</summary>
    public DecalIndex(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _ring = new Decal[capacity];
    }

    /// <summary>Most decals at once.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Decals alive.</summary>
    public int Count => _count;

    /// <summary>Decals dropped because the cap was reached (diagnostics; not state).</summary>
    public long Evicted { get; private set; }

    /// <summary>The decal at age rank <paramref name="i"/> (0 = oldest).</summary>
    public Decal this[int i]
    {
        get
        {
            if ((uint)i >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(i));
            }

            return _ring[(_head + i) % _ring.Length];
        }
    }

    /// <summary>Adds a decal; drops the oldest one when the index is full.</summary>
    public void Add(in Decal decal)
    {
        if (_ring.Length == 0)
        {
            return;
        }

        if (_count == _ring.Length)
        {
            _head = (_head + 1) % _ring.Length;
            _count--;
            Evicted++;
        }

        _ring[(_head + _count) % _ring.Length] = decal;
        _count++;
    }

    /// <summary>Removes the decals whose TTL ends at or before <paramref name="tick"/> (pipeline step 17 of that tick).</summary>
    public void Expire(int tick)
    {
        while (_count > 0 && _ring[_head].ExpiresTick <= tick)
        {
            _head = (_head + 1) % _ring.Length;
            _count--;
        }
    }

    /// <summary>
    /// Removes and returns the oldest corpse within <paramref name="radius"/> of a point (Raise Dead, GAME-11); the order
    /// of the others is kept.
    /// </summary>
    /// <returns><c>false</c> when there is none.</returns>
    public bool TakeCorpse(int x, int y, int radius, out Decal corpse) => TakeCorpse(x, y, radius, [], out corpse);

    /// <summary>
    /// Removes and returns the oldest corpse within <paramref name="radius"/> of a point on both axes (a square; raise dead
    /// widens the caster by 8 tiles, ReTzar 0x47367c) whose type is in
    /// <paramref name="types"/> (ascending; empty = any).
    /// </summary>
    /// <returns><c>false</c> when there is none.</returns>
    public bool TakeCorpse(int x, int y, int radius, System.Collections.Immutable.ImmutableArray<ushort> types, out Decal corpse)
    {
        for (int i = 0; i < _count; i++)
        {
            ref Decal d = ref _ring[(_head + i) % _ring.Length];
            long dx = d.X - (long)x;
            long dy = d.Y - (long)y;
            if (d.Kind != DecalKind.Corpse || Math.Max(Math.Abs(dx), Math.Abs(dy)) > radius || (!types.IsDefaultOrEmpty && types.AsSpan().BinarySearch(d.TypeId) < 0))
            {
                continue;
            }

            corpse = d;
            for (int k = i; k < _count - 1; k++)
            {
                _ring[(_head + k) % _ring.Length] = _ring[(_head + k + 1) % _ring.Length];
            }

            _count--;
            _ring[(_head + _count) % _ring.Length] = default;
            return true;
        }

        corpse = default;
        return false;
    }

    /// <summary>Counts the decals within <paramref name="radius"/> of a point.</summary>
    public int CountWithin(int x, int y, int radius)
    {
        long r2 = (long)radius * radius;
        int n = 0;
        for (int i = 0; i < _count; i++)
        {
            ref Decal d = ref _ring[(_head + i) % _ring.Length];
            long dx = d.X - (long)x;
            long dy = d.Y - (long)y;
            if ((dx * dx) + (dy * dy) <= r2)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Adds the decals, oldest first, to a state hash.</summary>
    public void AppendTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Add(_count);
        for (int i = 0; i < _count; i++)
        {
            Decal d = _ring[(_head + i) % _ring.Length];
            hasher.Add(d.X).Add(d.Y).Add(d.ExpiresTick).Add((int)d.TypeId).Add(d.Owner).Add((byte)d.Kind);
        }
    }

    /// <summary>Writes the decals, oldest first (canonical: independent of the ring position).</summary>
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt64((ulong)_count);
        for (int i = 0; i < _count; i++)
        {
            Decal d = _ring[(_head + i) % _ring.Length];
            writer.WriteInt32(d.X);
            writer.WriteInt32(d.Y);
            writer.WriteInt32(d.ExpiresTick);
            writer.WriteUInt16(d.TypeId);
            writer.WriteByte(d.Owner);
            writer.WriteByte((byte)d.Kind);
        }
    }

    /// <summary>Replaces the contents with data written by <see cref="WriteTo"/>.</summary>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    public void ReadFrom(ref BinaryBufferReader reader)
    {
        ulong count = reader.ReadVarUInt64();
        if (count > (ulong)_ring.Length)
        {
            throw new InvalidDataException($"Snapshot has {count} decals, the cap is {_ring.Length}.");
        }

        _head = 0;
        _count = (int)count;
        int previousExpiry = int.MinValue;
        for (int i = 0; i < _count; i++)
        {
            Decal d = default;
            d.X = reader.ReadInt32();
            d.Y = reader.ReadInt32();
            d.ExpiresTick = reader.ReadInt32();
            d.TypeId = reader.ReadUInt16();
            d.Owner = reader.ReadByte();
            d.Kind = (DecalKind)reader.ReadByte();
            if (d.Kind is not (DecalKind.Corpse or DecalKind.Ruin) || d.ExpiresTick < previousExpiry)
            {
                throw new InvalidDataException("Invalid decal in snapshot.");
            }

            previousExpiry = d.ExpiresTick;
            _ring[i] = d;
        }
    }
}
