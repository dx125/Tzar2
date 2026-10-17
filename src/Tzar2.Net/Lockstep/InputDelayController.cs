using System;

namespace Tzar2.Net.Lockstep;

/// <summary>
/// Adaptive input delay D (REQ-NET-02, ADR 0009): the number of ticks between the tick a client believes the relay
/// is currently closing and the tick it stamps on a new command batch.
/// </summary>
/// <remarks>
/// <para>
/// The target is <c>1 + ceil((SRTT / 2 + JitterFactor * RTTVAR + margin) / tickLength)</c>, clamped to
/// [<see cref="MinDelay"/>, <see cref="MaxDelay"/>]. The leading tick covers the partially elapsed current tick; the
/// rest is the one-way trip to the relay with a jitter allowance. A batch the relay received too late for its tick
/// (<see cref="OnLateBatch"/>) raises D by one at once, at most once per <see cref="LateCooldownTicks"/>: one lost
/// packet on a reliable channel makes several consecutive batches late, and that should cost one tick, not several.
/// </para>
/// <para>
/// D rises immediately and falls by one tick only after the lower target has held for
/// <see cref="DecreaseHoldTicks"/> consecutive updates, so jitter does not make it oscillate.
/// </para>
/// </remarks>
public sealed class InputDelayController
{
    /// <summary>Multiplier of RTTVAR in the jitter allowance.</summary>
    public const int JitterFactor = 2;

    private readonly long _tickLengthMicroseconds;
    private readonly long _marginMicroseconds;
    private int _lowerTargetStreak;
    private int _updatesSinceRaise = int.MaxValue / 2;

    /// <summary>Creates a controller.</summary>
    /// <param name="tickLengthMicroseconds">Wall-clock length of one tick at the current game speed.</param>
    /// <param name="minDelay">Lowest delay in ticks (TDD: 2).</param>
    /// <param name="maxDelay">Highest delay in ticks (TDD: 10).</param>
    /// <param name="marginMicroseconds">Fixed allowance for processing and scheduling.</param>
    /// <param name="decreaseHoldTicks">Updates a lower target must persist before D drops by one.</param>
    /// <param name="lateCooldownTicks">Updates after a raise during which further late reports are ignored.</param>
    public InputDelayController(
        long tickLengthMicroseconds,
        int minDelay = 2,
        int maxDelay = 10,
        long marginMicroseconds = 5_000,
        int decreaseHoldTicks = 40,
        int lateCooldownTicks = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickLengthMicroseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minDelay);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, minDelay);
        ArgumentOutOfRangeException.ThrowIfNegative(marginMicroseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(decreaseHoldTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(lateCooldownTicks);
        _tickLengthMicroseconds = tickLengthMicroseconds;
        _marginMicroseconds = marginMicroseconds;
        MinDelay = minDelay;
        MaxDelay = maxDelay;
        DecreaseHoldTicks = decreaseHoldTicks;
        LateCooldownTicks = lateCooldownTicks;
        Delay = minDelay;
    }

    /// <summary>Lowest delay in ticks.</summary>
    public int MinDelay { get; }

    /// <summary>Highest delay in ticks.</summary>
    public int MaxDelay { get; }

    /// <summary>Updates a lower target must persist before the delay drops by one tick.</summary>
    public int DecreaseHoldTicks { get; }

    /// <summary>Updates after a raise during which late reports are ignored.</summary>
    public int LateCooldownTicks { get; }

    /// <summary>The current input delay in ticks.</summary>
    public int Delay { get; private set; }

    /// <summary>The delay the current RTT estimate asks for, before hysteresis.</summary>
    public int Target(RttEstimator rtt)
    {
        ArgumentNullException.ThrowIfNull(rtt);
        long budget = (rtt.SmoothedMicroseconds / 2) + (JitterFactor * rtt.VariationMicroseconds) + _marginMicroseconds;
        long ticks = 1 + ((budget + _tickLengthMicroseconds - 1) / _tickLengthMicroseconds);
        return (int)Math.Clamp(ticks, MinDelay, MaxDelay);
    }

    /// <summary>Re-evaluates the delay; call once per tick. Returns the new delay.</summary>
    public int Update(RttEstimator rtt)
    {
        int target = Target(rtt);
        _updatesSinceRaise++;
        if (target > Delay)
        {
            Delay = target;
            _lowerTargetStreak = 0;
            _updatesSinceRaise = 0;
        }
        else if (target < Delay)
        {
            _lowerTargetStreak++;
            if (_lowerTargetStreak >= DecreaseHoldTicks)
            {
                Delay--;
                _lowerTargetStreak = 0;
            }
        }
        else
        {
            _lowerTargetStreak = 0;
        }

        return Delay;
    }

    /// <summary>The relay reported a batch that missed its tick: raise the delay by one tick unless it just rose.</summary>
    public void OnLateBatch()
    {
        if (_updatesSinceRaise < LateCooldownTicks)
        {
            return;
        }

        Delay = Math.Min(Delay + 1, MaxDelay);
        _lowerTargetStreak = 0;
        _updatesSinceRaise = 0;
    }
}
