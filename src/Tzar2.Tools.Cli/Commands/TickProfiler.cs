using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Tzar2.Sim.Systems;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// Per-section tick times for <c>t2 bench</c>: the sim reports nested sections (systems, their phases, hashing)
/// through <see cref="ITickProfiler"/>; this records the time of every section per measured tick and prints a
/// p50/p99/max table plus each section's mean over the slowest 1% of ticks (what the tick p99 is made of).
/// </summary>
internal sealed class TickProfiler : ITickProfiler
{
    private const int MaxDepth = 16;

    private readonly int _ticks;
    private readonly Dictionary<(int Parent, string Name), int> _index = [];
    private readonly List<Section> _sections = [];
    private readonly int[] _stack = new int[MaxDepth];
    private readonly long[] _started = new long[MaxDepth];
    private int _depth;
    private int _tick = -1;

    public TickProfiler(int ticks) => _ticks = ticks;

    /// <summary>Starts the next measured tick (call before each measured <c>Tick</c>).</summary>
    public void NextTick()
    {
        _tick++;
        _depth = 0;
    }

    /// <inheritdoc />
    public void Begin(string section)
    {
        if (_tick < 0 || _tick >= _ticks || _depth == MaxDepth)
        {
            return;
        }

        int parent = _depth == 0 ? -1 : _stack[_depth - 1];
        if (!_index.TryGetValue((parent, section), out int id))
        {
            id = _sections.Count;
            _index.Add((parent, section), id);
            _sections.Add(new Section(section, parent, _depth, new double[_ticks]));
        }

        _stack[_depth] = id;
        _started[_depth] = Stopwatch.GetTimestamp();
        _depth++;
    }

    /// <inheritdoc />
    public void End()
    {
        if (_tick < 0 || _tick >= _ticks || _depth == 0)
        {
            return;
        }

        _depth--;
        long elapsed = Stopwatch.GetTimestamp() - _started[_depth];
        _sections[_stack[_depth]].Samples[_tick] += elapsed * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>Prints the table; <paramref name="tickMs"/> are the measured tick times in tick order (unsorted).</summary>
    public void Print(TextWriter output, double[] tickMs)
    {
        int n = Math.Min(tickMs.Length, _ticks);
        int slowCount = Math.Max(1, n / 100);
        int[] order = new int[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => tickMs[b].CompareTo(tickMs[a]));
        int[] slow = order[..slowCount];

        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(inv, $"section ms {"",-26} {"p50",8} {"p99",8} {"max",8} {"mean",8} {"slow 1%",8}"));
        Row(output, "tick", tickMs, n, slow);
        double[] other = (double[])tickMs.Clone();
        foreach (int root in Children(-1))
        {
            Print(output, root, n, slow);
            for (int t = 0; t < n; t++)
            {
                other[t] -= _sections[root].Samples[t];
            }
        }

        Row(output, "  (outside sections)", other, n, slow);
    }

    private void Print(TextWriter output, int id, int n, int[] slow)
    {
        Section s = _sections[id];
        Row(output, new string(' ', 2 * (s.Depth + 1)) + s.Name, s.Samples, n, slow);
        foreach (int child in Children(id))
        {
            Print(output, child, n, slow);
        }
    }

    private List<int> Children(int parent)
    {
        var children = new List<int>();
        for (int i = 0; i < _sections.Count; i++)
        {
            if (_sections[i].Parent == parent)
            {
                children.Add(i);
            }
        }

        return children;
    }

    private static void Row(TextWriter output, string label, double[] samples, int n, int[] slow)
    {
        double[] sorted = samples[..n];
        Array.Sort(sorted);
        double sum = 0;
        foreach (double v in sorted)
        {
            sum += v;
        }

        double slowSum = 0;
        foreach (int t in slow)
        {
            slowSum += samples[t];
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"section ms {label,-26} {Percentile(sorted, 50),8:F3} {Percentile(sorted, 99),8:F3} {sorted[^1],8:F3} {sum / n,8:F3} {slowSum / slow.Length,8:F3}"));
    }

    private static double Percentile(double[] sorted, int percentile)
    {
        int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private sealed record Section(string Name, int Parent, int Depth, double[] Samples);
}
