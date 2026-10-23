using System;
using System.Collections.Generic;

namespace Tzar2.Game.Diagnostics;

/// <summary>
/// The latest samples of a measurement (milliseconds), written by one thread and drained by another without allocating
/// on the writer's side: the sim thread records each tick's time, the render bench drains them (ADR 0244).
/// </summary>
public sealed class SampleRing
{
    private readonly double[] _values;
    private readonly object _gate = new();
    private long _written;
    private long _read;

    /// <summary>Creates a ring of <paramref name="capacity"/> samples; older ones are overwritten.</summary>
    public SampleRing(int capacity = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _values = new double[capacity];
    }

    /// <summary>Records a sample.</summary>
    public void Add(double value)
    {
        lock (_gate)
        {
            _values[_written % _values.Length] = value;
            _written++;
        }
    }

    /// <summary>The samples recorded since the last drain (at most the capacity, oldest first).</summary>
    public IReadOnlyList<double> Drain()
    {
        lock (_gate)
        {
            long from = Math.Max(_read, _written - _values.Length);
            var samples = new double[_written - from];
            for (long i = from; i < _written; i++)
            {
                samples[i - from] = _values[i % _values.Length];
            }

            _read = _written;
            return samples;
        }
    }
}
