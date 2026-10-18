using System;

namespace Tzar2.Net.Relay;

/// <summary>
/// A token bucket on integer microseconds: <see cref="RatePerSecond"/> tokens refill per second up to
/// <see cref="Burst"/>; a request takes <c>cost</c> tokens or is refused.
/// </summary>
public sealed class TokenBucket
{
    private long _tokensMicro; // tokens x 1,000,000
    private long _lastUs;

    /// <summary>Creates a full bucket.</summary>
    public TokenBucket(long ratePerSecond, long burst, long nowUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratePerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, ratePerSecond / 1000 + 1);
        RatePerSecond = ratePerSecond;
        Burst = burst;
        _tokensMicro = burst * 1_000_000;
        _lastUs = nowUs;
    }

    /// <summary>Refill rate.</summary>
    public long RatePerSecond { get; }

    /// <summary>Capacity.</summary>
    public long Burst { get; }

    /// <summary>Takes <paramref name="cost"/> tokens if available.</summary>
    public bool TryTake(long cost, long nowUs)
    {
        long elapsed = Math.Clamp(nowUs - _lastUs, 0, 60_000_000);
        _lastUs = nowUs;
        _tokensMicro = Math.Min(Burst * 1_000_000, _tokensMicro + (elapsed * RatePerSecond));
        long need = cost * 1_000_000;
        if (_tokensMicro < need)
        {
            return false;
        }

        _tokensMicro -= need;
        return true;
    }
}

/// <summary>At most <see cref="Limit"/> events in any window of <see cref="WindowMicroseconds"/> (chat: 5 per 10 s, REQ-NET-03).</summary>
public sealed class SlidingWindowLimit
{
    private readonly long[] _times;
    private int _next;
    private int _count;

    /// <summary>Creates the limit.</summary>
    public SlidingWindowLimit(int limit, long windowMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowMicroseconds);
        _times = new long[limit];
        WindowMicroseconds = windowMicroseconds;
    }

    /// <summary>Events per window.</summary>
    public int Limit => _times.Length;

    /// <summary>Window length.</summary>
    public long WindowMicroseconds { get; }

    /// <summary>Records an event if the window has room.</summary>
    public bool TryRecord(long nowUs)
    {
        if (_count == _times.Length)
        {
            // _next is the oldest of the full ring.
            if (nowUs - _times[_next] < WindowMicroseconds)
            {
                return false;
            }
        }
        else
        {
            _count++;
        }

        _times[_next] = nowUs;
        _next = (_next + 1) % _times.Length;
        return true;
    }
}
