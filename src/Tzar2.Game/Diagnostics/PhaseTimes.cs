using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Tzar2.Sim.Systems;

namespace Tzar2.Game.Diagnostics;

/// <summary>
/// Where a real match's tick time goes (ADR 0244): the top-level sections the simulation reports (systems, hashing,
/// the render snapshot) per tick, plus what the tick spent outside them. The render bench prints each section's mean and
/// its mean over the slowest 1 % of ticks, which is what the tick p99 is made of. Fed on the sim thread only.
/// </summary>
public sealed class PhaseTimes : ITickProfiler
{
    /// <summary>Name of the time a tick spent outside every reported section.</summary>
    public const string Outside = "(outside)";

    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string> _names = [];
    private readonly List<(double Total, double[] Sections)> _ticks = [];
    private readonly object _gate = new();
    private double[] _current = new double[16];
    private int _depth;
    private long _started;
    private int _open = -1;

    /// <inheritdoc />
    public void Begin(string section)
    {
        if (_depth++ != 0)
        {
            return; // nested sections count in their top-level one
        }

        lock (_gate)
        {
            if (!_index.TryGetValue(section, out _open))
            {
                _open = _names.Count;
                _index.Add(section, _open);
                _names.Add(section);
            }
        }

        if (_open >= _current.Length)
        {
            Array.Resize(ref _current, _open * 2);
        }

        _started = Stopwatch.GetTimestamp();
    }

    /// <inheritdoc />
    public void End()
    {
        if (_depth == 0)
        {
            return;
        }

        if (--_depth == 0 && _open >= 0)
        {
            _current[_open] += Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            _open = -1;
        }
    }

    /// <summary>Closes a tick that took <paramref name="totalMilliseconds"/> in all.</summary>
    public void EndTick(double totalMilliseconds)
    {
        lock (_gate)
        {
            _ticks.Add((totalMilliseconds, _current[.._names.Count]));
        }

        Array.Clear(_current);
        _depth = 0;
        _open = -1;
    }

    /// <summary>Forgets the ticks so far (the end of the warm-up).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _ticks.Clear();
        }
    }

    /// <summary>Ticks recorded.</summary>
    public int TickCount
    {
        get
        {
            lock (_gate)
            {
                return _ticks.Count;
            }
        }
    }

    /// <summary>
    /// Each section's mean over all ticks and over the slowest 1 % (ms), largest slow-tick share first, with the time
    /// outside sections as <see cref="Outside"/>.
    /// </summary>
    public IReadOnlyList<(string Name, double Mean, double SlowMean)> Summary()
    {
        lock (_gate)
        {
            if (_ticks.Count == 0)
            {
                return [];
            }

            int sections = _names.Count;
            var slow = _ticks.OrderByDescending(t => t.Total).Take(Math.Max(1, _ticks.Count / 100)).ToList();
            double Mean(IReadOnlyList<(double Total, double[] Sections)> ticks, int s)
                => ticks.Average(t => s < sections ? (s < t.Sections.Length ? t.Sections[s] : 0) : Math.Max(0, t.Total - t.Sections.Sum()));
            var rows = new List<(string, double, double)>();
            for (int s = 0; s <= sections; s++)
            {
                rows.Add((s < sections ? _names[s] : Outside, Mean(_ticks, s), Mean(slow, s)));
            }

            return [.. rows.OrderByDescending(r => r.Item3).ThenBy(r => r.Item1, StringComparer.Ordinal)];
        }
    }

    /// <summary>The bench line's field: the <paramref name="top"/> largest sections as <c>name:mean/slow</c> (ms).</summary>
    public string Field(int top = 6)
    {
        IReadOnlyList<(string Name, double Mean, double SlowMean)> rows = Summary();
        if (rows.Count == 0)
        {
            return "phases=none";
        }

        string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        IEnumerable<string> shown = rows.Take(top).Select(r => $"{r.Name.Replace(' ', '_')}:{F(r.Mean)}/{F(r.SlowMean)}");
        if (rows.Count > top)
        {
            IEnumerable<(string Name, double Mean, double SlowMean)> rest = rows.Skip(top);
            shown = shown.Append($"rest:{F(rest.Sum(r => r.Mean))}/{F(rest.Sum(r => r.SlowMean))}");
        }

        return "phases=" + string.Join(',', shown);
    }
}
