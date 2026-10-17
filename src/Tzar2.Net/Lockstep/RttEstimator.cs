using System;

namespace Tzar2.Net.Lockstep;

/// <summary>
/// Smoothed round-trip time and round-trip variation, computed as in RFC 6298 (SRTT, RTTVAR) on integer
/// microseconds. Used to choose the adaptive input delay (REQ-NET-02). Not part of the simulation.
/// </summary>
public sealed class RttEstimator
{
    /// <summary>Number of samples added so far.</summary>
    public int SampleCount { get; private set; }

    /// <summary>Smoothed round-trip time in microseconds (0 before the first sample).</summary>
    public long SmoothedMicroseconds { get; private set; }

    /// <summary>Round-trip time variation (mean deviation) in microseconds.</summary>
    public long VariationMicroseconds { get; private set; }

    /// <summary>The most recent sample in microseconds.</summary>
    public long LastMicroseconds { get; private set; }

    /// <summary>Adds one round-trip sample.</summary>
    /// <param name="rttMicroseconds">The measured round-trip time; must not be negative.</param>
    public void AddSample(long rttMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rttMicroseconds);
        LastMicroseconds = rttMicroseconds;
        if (SampleCount == 0)
        {
            SmoothedMicroseconds = rttMicroseconds;
            VariationMicroseconds = rttMicroseconds / 2;
        }
        else
        {
            // RFC 6298 section 2.3 with alpha = 1/8 and beta = 1/4.
            VariationMicroseconds = ((3 * VariationMicroseconds) + Math.Abs(SmoothedMicroseconds - rttMicroseconds)) / 4;
            SmoothedMicroseconds = ((7 * SmoothedMicroseconds) + rttMicroseconds) / 8;
        }

        SampleCount++;
    }
}
