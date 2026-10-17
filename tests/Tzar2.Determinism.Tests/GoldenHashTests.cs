using System;
using Tzar2.Core.Hashing;
using Tzar2.Core.Numerics;
using Tzar2.Core.Rng;
using Tzar2.Core.Serialization;
using Xunit;

namespace Tzar2.Determinism.Tests;

/// <summary>
/// Cross-platform determinism check of the Core primitives (TDD §22.2; seed of spike S2). CI runs this on
/// Windows x64, Linux x64 and macOS arm64 and on both .NET runtimes; the golden hash must be identical everywhere.
/// </summary>
public class GoldenHashTests
{
    // Changing Fixed, Trig, the RNG or the hashing scheme on purpose changes this value; update it in the same
    // commit and say why. An unexpected change, or a difference between platforms, is a determinism bug.
    // History: 908379687207490273 (M0) -> 1993851233297384235 when variable-length data became length-prefixed and the workload
    // started hashing spans (M0 hardening review).
    private const ulong GoldenHash = 1993851233297384235UL;

    [Fact]
    public void Core_workload_produces_the_golden_hash() => Assert.Equal(GoldenHash, RunWorkload());

    [Fact]
    public void Workload_is_repeatable_within_a_process() => Assert.Equal(RunWorkload(), RunWorkload());

    [Fact]
    public void Snapshot_mid_run_continues_identically()
    {
        // REQ-SIM-13 in miniature: snapshot the RNG streams halfway, restore, and continue.
        var uninterrupted = new RngStreams(2026, 8);
        var hasherA = new StateHasher();
        for (int i = 0; i < 2000; i++)
        {
            hasherA.Add(uninterrupted.Combat.NextUInt64()).Add(uninterrupted.Ai(i % 8).NextInt(1000));
        }

        var first = new RngStreams(2026, 8);
        var hasherB = new StateHasher();
        for (int i = 0; i < 1000; i++)
        {
            hasherB.Add(first.Combat.NextUInt64()).Add(first.Ai(i % 8).NextInt(1000));
        }

        var writer = new BinaryBufferWriter();
        first.WriteTo(writer);
        var reader = new BinaryBufferReader(writer.WrittenSpan);
        RngStreams restored = RngStreams.ReadFrom(ref reader);
        for (int i = 1000; i < 2000; i++)
        {
            hasherB.Add(restored.Combat.NextUInt64()).Add(restored.Ai(i % 8).NextInt(1000));
        }

        Assert.Equal(hasherA.GetCurrentHash(), hasherB.GetCurrentHash());
    }

    private static ulong RunWorkload()
    {
        var streams = new RngStreams(0x5EED_7A42UL, 8);
        var hasher = new StateHasher();
        Fixed accumulator = Fixed.One;
        for (int i = 0; i < 20_000; i++)
        {
            ref Xoshiro256StarStar rng = ref streams.Combat;
            Fixed a = Fixed.FromRaw(rng.NextInt(-1 << 24, 1 << 24));
            Fixed b = Fixed.FromRaw(rng.NextInt(1, 1 << 24));
            Angle angle = Angle.FromRaw(rng.NextInt(Angle.UnitsPerTurn));

            Fixed product = a * b;
            Fixed quotient = a / b;
            Fixed root = Fixed.Sqrt(Fixed.Abs(product));
            Fixed sin = Trig.Sin(angle);
            Fixed cos = Trig.Cos(angle);
            Angle back = Trig.Atan2((a * sin).Raw, (b * cos).Raw);
            accumulator = Fixed.SaturatingAdd(Fixed.SaturatingMultiply(accumulator, Fixed.FromRatio(65537, 65536)), quotient);

            hasher.Add(product).Add(quotient).Add(root).Add(sin).Add(cos).Add((int)back.Raw).Add(accumulator)
                .Add(IntMath.MulBasisPoints(product.Raw, rng.NextInt(20_000)))
                .Add(streams.Ai(i % streams.PlayerCount).NextChance(2500));
        }

        streams.AppendTo(hasher);

        // Variable-length data (length-prefixed, see StateHasher).
        long[] components = new long[37];
        for (int i = 0; i < components.Length; i++)
        {
            components[i] = streams.Misc.NextInt(int.MinValue, int.MaxValue);
        }

        hasher.AddSpan<long>(components).AddSpan<long>(components.AsSpan(0, 5)).Add([1, 2, 3]).Add([]);
        return hasher.GetCurrentHash();
    }
}
