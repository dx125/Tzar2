using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Tzar2.Core.Hashing;
using Tzar2.Sim.Scenarios;
using Tzar2.Sim.Snapshots;
using Tzar2.Sim.Threading;
using Xunit;

namespace Tzar2.Sim.Tests;

/// <summary>ADR 0215: the full-state hash's bulk part and the path store's batched encoding.</summary>
public class FullHashTests
{
    [Fact]
    public void A_bulk_array_goes_in_as_its_length_and_its_chunks_hashes_on_any_worker_count()
    {
        int[] big = new int[(BulkHash.ChunkBytes / sizeof(int) * 3) + 7]; // three whole chunks and a partial one
        for (int i = 0; i < big.Length; i++)
        {
            big[i] = (i * 2654435761u).GetHashCode();
        }

        byte[] small = [1, 2, 3];
        byte[] empty = [];

        var expected = new StateHasher();
        foreach (byte[] bytes in new[] { MemoryMarshal.AsBytes(big.AsSpan()).ToArray(), small, empty })
        {
            var chunks = new List<ulong>();
            for (int o = 0; o < bytes.Length; o += BulkHash.ChunkBytes)
            {
                chunks.Add(StateHasher.HashChunk(bytes.AsSpan(o, Math.Min(BulkHash.ChunkBytes, bytes.Length - o))));
            }

            expected.Add((long)bytes.Length).AddSpan<ulong>(chunks.ToArray());
        }

        foreach (int workers in new[] { 0, 1, 3, 8 })
        {
            using var pool = workers > 0 ? new DeterministicWorkerPool(workers) : null;
            var bulk = new BulkHash();
            bulk.Clear();
            bulk.Add(big);
            bulk.Add(small);
            bulk.Add(empty);
            bulk.Run(pool);
            var actual = new StateHasher();
            bulk.AppendNext(actual);
            bulk.AppendNext(actual);
            bulk.AppendNext(actual);
            Assert.Equal(expected.GetCurrentHash(), actual.GetCurrentHash());
        }
    }

    [Fact]
    public void The_path_store_hashes_the_same_bytes_in_batches_and_the_full_hash_ignores_the_workers()
    {
        var options = new SyntheticScenarioOptions { MapSize = 128, EntityCount = 600, Players = 4, OrdersPerPlayerPerTick = 8 };
        using var scenario = new SyntheticScenario(options, 4);
        for (int t = 0; t < 60; t++)
        {
            scenario.RunTick();
        }

        World w = scenario.Simulation.World;
        var oneByOne = new StateHasher();
        int paths = 0;
        for (int i = 0; i < w.Entities.HighWater; i++)
        {
            if (w.Paths.Count(i) > 0)
            {
                oneByOne.Add(i).AddSpan<int>(w.Paths.Get(i));
                paths++;
            }
        }

        oneByOne.Add(-1);
        var batched = new StateHasher();
        w.Paths.AppendTo(batched);
        Assert.True(paths > 100, $"only {paths} paths");
        Assert.Equal(oneByOne.GetCurrentHash(), batched.GetCurrentHash());

        using var pool = new DeterministicWorkerPool(4);
        Assert.Equal(StateHashing.ComputeFullHash(w, new StateHasher()), StateHashing.ComputeFullHash(w, new StateHasher(), new BulkHash(), pool));
    }
}
