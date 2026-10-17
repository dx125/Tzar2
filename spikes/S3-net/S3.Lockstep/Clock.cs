using System.Diagnostics;

namespace Tzar2.Spikes.S3.Lockstep;

/// <summary>Monotonic wall clock in microseconds (network code only, never the sim).</summary>
public static class Clock
{
    /// <summary>Microseconds since an arbitrary process-local origin.</summary>
    public static long NowMicroseconds() => Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1_000_000);
}
