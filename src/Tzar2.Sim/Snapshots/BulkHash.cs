using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tzar2.Core.Hashing;
using Tzar2.Sim.Threading;

namespace Tzar2.Sim.Snapshots;

/// <summary>
/// The large flat arrays of the full-state hash (the map's layers, the visibility bitsets), hashed in fixed chunks of
/// <see cref="ChunkBytes"/> on the sim's workers (ADR 0215). Each array goes into the stream as its byte length and the
/// xxHash3 of each of its chunks, in order: the value depends on the data and the chunk size only, never on the worker
/// count or which thread hashed which chunk. Allocates only when an array needs more chunks than ever before.
/// </summary>
/// <remarks>
/// Use: <see cref="Clear"/>, <see cref="Add{T}"/> every array in stream order, <see cref="Run"/>, then
/// <see cref="AppendNext"/> once per array, in the same order, where the array's bytes would have gone.
/// </remarks>
public sealed class BulkHash : IParallelJob
{
    /// <summary>Bytes per chunk (a part of the hash's definition: changing it changes every full hash).</summary>
    public const int ChunkBytes = 64 * 1024;

    private Array?[] _arrays = new Array?[16];
    private int[] _bytes = new int[16];
    private int[] _firstChunk = new int[17];
    private int _count;
    private int _next;
    private ulong[] _chunks = new ulong[256];
    private int[] _chunkArray = new int[256];

    /// <summary>Starts a new hash.</summary>
    public void Clear()
    {
        Array.Clear(_arrays, 0, _count);
        _count = 0;
        _next = 0;
    }

    /// <summary>Adds the next array of the stream.</summary>
    public void Add<T>(T[] array)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(array);
        if (_count == _arrays.Length)
        {
            Array.Resize(ref _arrays, _count * 2);
            Array.Resize(ref _bytes, _count * 2);
            Array.Resize(ref _firstChunk, (_count * 2) + 1);
        }

        _arrays[_count] = array;
        _bytes[_count] = array.Length * Unsafe.SizeOf<T>();
        _count++;
    }

    /// <summary>Hashes every chunk of every array added, on <paramref name="pool"/> when given, else inline.</summary>
    public void Run(DeterministicWorkerPool? pool)
    {
        int chunks = 0;
        for (int a = 0; a < _count; a++)
        {
            _firstChunk[a] = chunks;
            chunks += (_bytes[a] + ChunkBytes - 1) / ChunkBytes;
        }

        _firstChunk[_count] = chunks;
        if (chunks > _chunks.Length)
        {
            _chunks = new ulong[chunks];
            _chunkArray = new int[chunks];
        }

        for (int a = 0; a < _count; a++)
        {
            for (int c = _firstChunk[a]; c < _firstChunk[a + 1]; c++)
            {
                _chunkArray[c] = a;
            }
        }

        if (pool is null || pool.WorkerCount == 1 || chunks < 2)
        {
            for (int c = 0; c < chunks; c++)
            {
                Execute(c);
            }
        }
        else
        {
            pool.Run(this, chunks);
        }
    }

    /// <summary>Appends the next array: its byte length, then its chunks' hashes.</summary>
    public void AppendNext(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        int a = _next++;
        hasher.Add((long)_bytes[a]).AddSpan<ulong>(_chunks.AsSpan(_firstChunk[a], _firstChunk[a + 1] - _firstChunk[a]));
    }

    /// <inheritdoc />
    public void Execute(int jobIndex)
    {
        int a = _chunkArray[jobIndex];
        int offset = (jobIndex - _firstChunk[a]) * ChunkBytes;
        int length = Math.Min(ChunkBytes, _bytes[a] - offset);
        ref byte start = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_arrays[a]!), offset);
        _chunks[jobIndex] = StateHasher.HashChunk(MemoryMarshal.CreateReadOnlySpan(ref start, length));
    }
}
