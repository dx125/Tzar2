using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Core.Hashing;
using Tzar2.Core.Serialization;

namespace Tzar2.Sim.Snapshots;

/// <summary>
/// State that an in-sim controller outside Tzar2.Sim keeps across ticks (the AI players' script state, REQ-AI-03; later
/// trigger and Lua state, REQ-SIM-13), stored in snapshots and in the full-state hash next to the world (ADR 0051).
/// </summary>
public interface IWorldStateExtension
{
    /// <summary>A stable key (e.g. <c>ai.legacy</c>); one extension per key and world.</summary>
    string Key { get; }

    /// <summary>Writes the complete state, canonically: equal states give equal bytes.</summary>
    void WriteTo(BinaryBufferWriter writer);

    /// <summary>Replaces the state with one written by <see cref="WriteTo"/>.</summary>
    /// <exception cref="InvalidDataException">The bytes are malformed or do not fit this extension (snapshots are untrusted, ADR 0019).</exception>
    void ReadFrom(ReadOnlySpan<byte> data);
}

/// <summary>
/// The <see cref="IWorldStateExtension"/>s of a world. A restored snapshot's extension blocks are kept as bytes until the
/// extension with their key registers (controllers attach after <c>Simulation.Restore</c>, before the first tick), so the
/// order of restoring and registering does not matter; a block nobody claims is carried through unchanged.
/// </summary>
public sealed class WorldStateExtensions
{
    private const int MaxKeyBytes = 64;
    private const int MaxBlockBytes = 64 << 20;

    // Both lists are kept in ordinal key order; a key is in at most one of them.
    private readonly List<IWorldStateExtension> _registered = [];
    private readonly List<(string Key, byte[] Bytes)> _pending = [];
    private readonly BinaryBufferWriter _scratch = new(1024);

    /// <summary>Whether there is no extension state at all (then hashes and snapshots are as without extensions).</summary>
    public bool IsEmpty => _registered.Count == 0 && _pending.Count == 0;

    /// <summary>Registers an extension; if a restored snapshot carried state for its key, the extension reads it now.</summary>
    /// <exception cref="ArgumentException">The key is empty, too long, or already registered.</exception>
    /// <exception cref="InvalidDataException">The restored state is invalid for the extension.</exception>
    public void Register(IWorldStateExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        string key = extension.Key;
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyBytes)
        {
            throw new ArgumentException("An extension key must have 1-64 characters.", nameof(extension));
        }

        if (IndexOf(_registered, key) >= 0)
        {
            throw new ArgumentException($"An extension with key '{key}' is already registered.", nameof(extension));
        }

        int pending = PendingIndex(key);
        if (pending >= 0)
        {
            extension.ReadFrom(_pending[pending].Bytes);
            _pending.RemoveAt(pending);
        }

        int at = 0;
        while (at < _registered.Count && string.CompareOrdinal(_registered[at].Key, key) < 0)
        {
            at++;
        }

        _registered.Insert(at, extension);
    }

    /// <summary>Whether a restored state block for <paramref name="key"/> waits for its extension.</summary>
    public bool HasPending(string key) => PendingIndex(key) >= 0;

    internal void WriteTo(BinaryBufferWriter writer)
    {
        writer.WriteVarUInt64((ulong)(_registered.Count + _pending.Count));
        int r = 0;
        int p = 0;
        while (r < _registered.Count || p < _pending.Count)
        {
            string key = Next(ref r, ref p, out ReadOnlySpan<byte> bytes);
            writer.WriteString(key);
            writer.WriteVarUInt64((ulong)bytes.Length);
            writer.WriteBytes(bytes);
        }
    }

    internal void ReadFrom(ref BinaryBufferReader reader)
    {
        if (_registered.Count > 0)
        {
            throw new InvalidOperationException("Extensions are restored into a fresh world before any registers.");
        }

        _pending.Clear();
        ulong count = reader.ReadVarUInt64();
        if (count > 256)
        {
            throw new InvalidDataException("Too many extension blocks in the snapshot.");
        }

        string? previous = null;
        for (ulong i = 0; i < count; i++)
        {
            string key = reader.ReadString(MaxKeyBytes * 4);
            if (key.Length is 0 or > MaxKeyBytes || (previous is not null && string.CompareOrdinal(previous, key) >= 0))
            {
                throw new InvalidDataException("Extension blocks must have distinct keys in ordinal order.");
            }

            ulong length = reader.ReadVarUInt64();
            if (length > MaxBlockBytes || length > (ulong)reader.Remaining)
            {
                throw new InvalidDataException($"Extension block '{key}' is too long.");
            }

            _pending.Add((key, reader.ReadBytes((int)length).ToArray()));
            previous = key;
        }
    }

    // Nothing when empty, so worlds without extensions hash exactly as before extensions existed.
    internal void AppendTo(StateHasher hasher)
    {
        if (IsEmpty)
        {
            return;
        }

        hasher.Add(0x45585453).Add(_registered.Count + _pending.Count);
        int r = 0;
        int p = 0;
        while (r < _registered.Count || p < _pending.Count)
        {
            string key = Next(ref r, ref p, out ReadOnlySpan<byte> bytes);
            hasher.Add(key.Length);
            foreach (char c in key)
            {
                hasher.Add((int)c);
            }

            hasher.Add(bytes);
        }
    }

    // The next block in ordinal key order of the merged lists; a registered extension is serialized into the scratch buffer.
    private string Next(ref int r, ref int p, out ReadOnlySpan<byte> bytes)
    {
        if (p >= _pending.Count || (r < _registered.Count && string.CompareOrdinal(_registered[r].Key, _pending[p].Key) < 0))
        {
            IWorldStateExtension extension = _registered[r++];
            _scratch.Clear();
            extension.WriteTo(_scratch);
            bytes = _scratch.WrittenSpan;
            return extension.Key;
        }

        (string key, byte[] block) = _pending[p++];
        bytes = block;
        return key;
    }

    private int PendingIndex(string key)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            if (string.Equals(_pending[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOf(List<IWorldStateExtension> list, string key)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
