using System.Diagnostics;

namespace Tzar2.Net;

/// <summary>Monotonic wall clock in microseconds for network code (tick deadlines, RTT, rate limits); never used by the sim.</summary>
public static class MonotonicClock
{
    /// <summary>Microseconds since an arbitrary process-local origin.</summary>
    public static long NowMicroseconds() => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));
}
