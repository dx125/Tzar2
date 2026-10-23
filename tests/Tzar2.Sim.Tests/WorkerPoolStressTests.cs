using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Tzar2.Sim.Threading;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Sim.Tests;

/// <summary>
/// ADR 0214 (code review 2026-10, item 16, performance 8): <see cref="DeterministicWorkerPool.Run"/> waits for every helper
/// it woke, even one that wakes after the other threads ran all the jobs. On a CPU shared with other work, such a helper
/// can be descheduled for a whole time slice: the review machine (load 30-97) showed sim ticks of exactly 10 and 20 ms.
/// This test measures that wait, the "tail" between the last job's end and Run's return, under contention. It fails only
/// if the tail eats the 50 ms tick; its percentiles are written to the test output, so CI runners show whether the stalls
/// are real there. The pool changes only if they are (ADR 0214).
/// </summary>
public sealed class WorkerPoolStressTests(ITestOutputHelper output)
{
    private const int Forks = 500;

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void The_wait_for_late_helpers_stays_far_below_a_tick_under_cpu_contention(int workers)
    {
        using var pool = new DeterministicWorkerPool(workers);
        var job = new SpinJob();
        using var stop = new CancellationTokenSource();
        // Contention: as many busy threads as there are cores, so the pool's helpers compete for time slices.
        var burners = new Thread[Environment.ProcessorCount];
        for (int i = 0; i < burners.Length; i++)
        {
            burners[i] = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Thread.SpinWait(1_000);
                }
            })
            { IsBackground = true, Name = "contention " + i.ToString(CultureInfo.InvariantCulture) };
            burners[i].Start();
        }

        double[] tails = new double[Forks];
        double[] totals = new double[Forks];
        try
        {
            for (int f = 0; f < Forks; f++)
            {
                job.Reset();
                long start = Stopwatch.GetTimestamp();
                pool.Run(job, workers * 2);
                long end = Stopwatch.GetTimestamp();
                tails[f] = Stopwatch.GetElapsedTime(Math.Min(job.LastEnd, end), end).TotalMilliseconds;
                totals[f] = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
            }
        }
        finally
        {
            stop.Cancel();
            foreach (Thread t in burners)
            {
                t.Join();
            }
        }

        Array.Sort(tails);
        Array.Sort(totals);
        int slow = Array.FindAll(tails, t => t >= 5).Length;
        string report = string.Create(
            CultureInfo.InvariantCulture,
            $"workers {workers}, {Environment.ProcessorCount} cores, {Forks} forks under contention: tail after the last job p50 {P(tails, 50):F3} p99 {P(tails, 99):F3} max {tails[^1]:F3} ms ({slow} at 5 ms or more); whole fork p50 {P(totals, 50):F3} p99 {P(totals, 99):F3} max {totals[^1]:F3} ms");
        output.WriteLine(report);
        Assert.True(P(tails, 99) < 50, "late helpers eat a 50 ms tick: " + report);
    }

    private static double P(double[] sorted, int percentile) => sorted[Math.Clamp((int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];

    // About 20 µs of work per job; records when the last job ended.
    private sealed class SpinJob : IParallelJob
    {
        private long _lastEnd;

        public long LastEnd => Volatile.Read(ref _lastEnd);

        public void Reset() => Volatile.Write(ref _lastEnd, 0);

        public void Execute(int jobIndex)
        {
            Thread.SpinWait(2_000);
            long now = Stopwatch.GetTimestamp();
            long seen = Volatile.Read(ref _lastEnd);
            while (now > seen && Interlocked.CompareExchange(ref _lastEnd, now, seen) != seen)
            {
                seen = Volatile.Read(ref _lastEnd);
            }
        }
    }
}
