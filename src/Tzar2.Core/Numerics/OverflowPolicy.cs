using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tzar2.Core.Numerics;

/// <summary>What <see cref="Fixed"/> arithmetic does when a result leaves the representable range (ADR 0014).</summary>
public enum OverflowMode : byte
{
    /// <summary>Throw <see cref="OverflowException"/> (development and debug builds).</summary>
    Throw = 0,

    /// <summary>Clamp to <see cref="Fixed.MinValue"/>/<see cref="Fixed.MaxValue"/> and report the event (release builds).</summary>
    Saturate = 1,
}

/// <summary>The <see cref="Fixed"/> operation that overflowed.</summary>
public enum OverflowSite : byte
{
    /// <summary><c>a + b</c>.</summary>
    Add = 1,

    /// <summary><c>a - b</c>.</summary>
    Subtract = 2,

    /// <summary><c>-a</c> or <see cref="Fixed.Abs"/> of <see cref="Fixed.MinValue"/>.</summary>
    Negate = 3,

    /// <summary><c>a * b</c>.</summary>
    Multiply = 4,

    /// <summary><c>a / b</c>.</summary>
    Divide = 5,

    /// <summary><see cref="Fixed.FromInt"/> or <see cref="Fixed.FromRatio"/>.</summary>
    Convert = 6,
}

/// <summary>
/// The build-time overflow policy of <see cref="Fixed"/> (owner decision, ADR 0014). The MSBuild property
/// <c>Tzar2OverflowPolicy</c> selects it when Tzar2.Core is compiled: <c>Throw</c> by default (Debug, Godot
/// <c>Debug</c>/<c>ExportDebug</c>), <c>Saturate</c> for <c>Release</c> and Godot <c>ExportRelease</c>.
/// </summary>
/// <remarks>
/// Both policies are deterministic: a throw happens on every peer at the same operation, and a saturated result is the
/// same clamped value everywhere. Peers of one match must therefore run the same policy; <see cref="Mode"/> is part of
/// the engine identity that sessions compare before a match starts. The saturation counter and the
/// <see cref="Saturated"/> callback are diagnostics only: they never feed back into simulation state.
/// </remarks>
public static class OverflowPolicy
{
#pragma warning disable CS0649 // Only incremented when compiled with TZAR2_OVERFLOW_SATURATE.
    private static long _saturations;
#pragma warning restore CS0649
    private static Action<OverflowSite>? _saturated;

    /// <summary>The policy Tzar2.Core was compiled with.</summary>
    public static OverflowMode Mode =>
#if TZAR2_OVERFLOW_SATURATE
        OverflowMode.Saturate;
#else
        OverflowMode.Throw;
#endif

    /// <summary>Number of saturated <see cref="Fixed"/> results in this process (always 0 under <see cref="OverflowMode.Throw"/>).</summary>
    public static long SaturationCount => Interlocked.Read(ref _saturations);

    /// <summary>
    /// Optional logging hook, called on the thread that saturated (possibly a sim worker thread), so it must be
    /// thread-safe and cheap. Hosts (game, CLI, server) set it once at startup to log the event; it must not touch
    /// simulation state.
    /// </summary>
    public static Action<OverflowSite>? Saturated
    {
        get => Volatile.Read(ref _saturated);
        set => Volatile.Write(ref _saturated, value);
    }

    /// <summary>Applies the policy to an overflowed result: throws, or counts, reports and returns <paramref name="saturatedRaw"/>.</summary>
    /// <exception cref="OverflowException">The policy is <see cref="OverflowMode.Throw"/>.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static long Overflow(OverflowSite site, long saturatedRaw)
    {
#if TZAR2_OVERFLOW_SATURATE
        Interlocked.Increment(ref _saturations);
        Volatile.Read(ref _saturated)?.Invoke(site);
        return saturatedRaw;
#else
        _ = saturatedRaw;
        throw new OverflowException($"Fixed {site} overflowed (overflow policy: Throw).");
#endif
    }
}
