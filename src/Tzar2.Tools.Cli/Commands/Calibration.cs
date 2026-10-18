using System;
using System.Diagnostics;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// A fixed CPU workload that measures how fast the machine running <c>t2 bench</c> is (ADR 0080), so tick times from
/// different CI runners can be compared: <c>normalized = measured × <see cref="ReferenceMs"/> / calibration</c>.
/// </summary>
/// <remarks>
/// The workload is a frozen copy of the sim's dominant kind of work: 8-connected Dijkstra searches with a binary heap
/// over a 256² grid with random obstacles (a cache-resident working set of about 1 MB, integer arithmetic and
/// unpredictable branches). It must never change: changing it re-bases every normalized number and the committed
/// baseline (<c>tools/ci/bench-baseline.tsv</c>) with it. Single-threaded; the best of several runs is taken, since
/// noise only ever makes a run slower.
/// </remarks>
internal static class Calibration
{
    /// <summary>
    /// The calibration time, in ms, that normalized numbers are scaled to: about what GitHub's <c>ubuntu-24.04</c> x64
    /// runners measured when the baseline was recorded (ADR 0080).
    /// </summary>
    public const double ReferenceMs = 25.0;

    private const int Side = 256;
    private const int Searches = 6;
    private const int Runs = 7;

    private static ReadOnlySpan<int> StepX => [1, 1, 0, -1, -1, -1, 0, 1];

    private static ReadOnlySpan<int> StepY => [0, 1, 1, 1, 0, -1, -1, -1];

    /// <summary>Runs the workload <see cref="Runs"/> times and returns the fastest run in ms.</summary>
    public static double Measure()
    {
        bool[] open = new bool[Side * Side];
        ulong state = 0x9E37_79B9_7F4A_7C15UL;
        for (int i = 0; i < open.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            open[i] = state % 100 >= 22;
        }

        int[] cost = new int[Side * Side];
        long[] heap = new long[8 * Side * Side];
        double best = double.MaxValue;
        long sink = 0;
        for (int run = 0; run < Runs; run++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int s = 0; s < Searches; s++)
            {
                sink += Dijkstra(open, cost, heap, ((s * 37) + 11) * Side + (s * 41) + 7);
            }

            best = Math.Min(best, (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        }

        GC.KeepAlive(sink);
        return best;
    }

    private static long Dijkstra(bool[] open, int[] cost, long[] heap, int root)
    {
        Array.Fill(cost, int.MaxValue);
        int count = 0;
        cost[root] = 0;
        heap[count++] = root;
        long settled = 0;
        while (count > 0)
        {
            long top = heap[0];
            long last = heap[--count];
            int i = 0;
            while (true)
            {
                int child = (2 * i) + 1;
                if (child >= count)
                {
                    break;
                }

                if (child + 1 < count && heap[child + 1] < heap[child])
                {
                    child++;
                }

                if (heap[child] >= last)
                {
                    break;
                }

                heap[i] = heap[child];
                i = child;
            }

            heap[i] = last;
            int tile = (int)(top & 0xFFFFFF);
            int c = (int)(top >> 24);
            if (c != cost[tile])
            {
                continue;
            }

            settled += c;
            int x = tile % Side;
            int y = tile / Side;
            for (int d = 0; d < 8; d++)
            {
                int nx = x + StepX[d];
                int ny = y + StepY[d];
                if ((uint)nx >= Side || (uint)ny >= Side || !open[(ny * Side) + nx])
                {
                    continue;
                }

                int n = (ny * Side) + nx;
                int nc = c + ((d & 1) == 0 ? 1024 : 1448);
                if (nc < cost[n] && count < heap.Length)
                {
                    cost[n] = nc;
                    long entry = ((long)nc << 24) | (uint)n;
                    int j = count++;
                    while (j > 0)
                    {
                        int parent = (j - 1) >> 1;
                        if (heap[parent] <= entry)
                        {
                            break;
                        }

                        heap[j] = heap[parent];
                        j = parent;
                    }

                    heap[j] = entry;
                }
            }
        }

        return settled;
    }
}
