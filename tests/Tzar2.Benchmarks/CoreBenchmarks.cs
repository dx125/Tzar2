using System;
using BenchmarkDotNet.Attributes;
using Tzar2.Core.Hashing;
using Tzar2.Core.Numerics;
using Tzar2.Core.Rng;

namespace Tzar2.Benchmarks;

/// <summary>Micro-benchmarks for the Tzar2.Core primitives every sim system builds on.</summary>
[MemoryDiagnoser]
public class CoreBenchmarks
{
    private const int Count = 1024;

    private readonly Fixed[] _values = new Fixed[Count];
    private readonly long[] _components = new long[16 * 1024];
    private Xoshiro256StarStar _rng = Xoshiro256StarStar.FromSeed(42);
    private StateHasher _hasher = null!;

    [GlobalSetup]
    public void Setup()
    {
        for (int i = 0; i < Count; i++)
        {
            _values[i] = Fixed.FromRaw(_rng.NextInt(1, int.MaxValue));
        }

        _hasher = new StateHasher();
    }

    [Benchmark]
    public Fixed FixedMultiply()
    {
        Fixed sum = Fixed.Zero;
        for (int i = 1; i < Count; i++)
        {
            sum = Fixed.SaturatingAdd(sum, _values[i - 1] * Fixed.Half);
        }

        return sum;
    }

    [Benchmark]
    public Fixed FixedDivide()
    {
        Fixed sum = Fixed.Zero;
        for (int i = 1; i < Count; i++)
        {
            sum = Fixed.SaturatingAdd(sum, _values[i - 1] / _values[i]);
        }

        return sum;
    }

    [Benchmark]
    public Fixed FixedSqrt()
    {
        Fixed sum = Fixed.Zero;
        for (int i = 0; i < Count; i++)
        {
            sum = Fixed.SaturatingAdd(sum, Fixed.Sqrt(_values[i]));
        }

        return sum;
    }

    [Benchmark]
    public long SinCos()
    {
        long sum = 0;
        for (int i = 0; i < Count; i++)
        {
            Angle angle = Angle.FromRaw(i * 64);
            sum += Trig.Sin(angle).Raw + Trig.Cos(angle).Raw;
        }

        return sum;
    }

    [Benchmark]
    public int Atan2()
    {
        int sum = 0;
        for (int i = 0; i < Count; i++)
        {
            sum += Trig.Atan2(_values[i].Raw, _values[Count - 1 - i].Raw - (int.MaxValue / 2)).Raw;
        }

        return sum;
    }

    [Benchmark]
    public ulong RngNextInt()
    {
        ulong sum = 0;
        for (int i = 0; i < Count; i++)
        {
            sum += (ulong)_rng.NextInt(1000);
        }

        return sum;
    }

    [Benchmark]
    public ulong HashComponents128KiB()
    {
        _hasher.AddSpan<long>(_components.AsSpan());
        return _hasher.GetHashAndReset();
    }
}
