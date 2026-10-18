using System;
using System.Collections.Generic;

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

/// <summary>
/// One <see cref="TokenBucket"/> per key, at most <see cref="Capacity"/> of them: when full, the least recently used key
/// is evicted (one at a time), so a flood from many addresses never resets the state of the active ones.
/// </summary>
/// <typeparam name="TKey">The key (an address).</typeparam>
public sealed class KeyedRateLimit<TKey>
    where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TokenBucket Bucket)>> _nodes;
    private readonly LinkedList<(TKey Key, TokenBucket Bucket)> _order = new(); // most recently used first
    private readonly long _rate;
    private readonly long _burst;

    /// <summary>Creates the limit.</summary>
    public KeyedRateLimit(int capacity, long ratePerSecond, long burst, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _rate = ratePerSecond;
        _burst = burst;
        _nodes = new Dictionary<TKey, LinkedListNode<(TKey, TokenBucket)>>(capacity, comparer);
    }

    /// <summary>Most keys tracked.</summary>
    public int Capacity { get; }

    /// <summary>Keys tracked.</summary>
    public int Count => _nodes.Count;

    /// <summary>Takes one token for <paramref name="key"/>.</summary>
    public bool TryTake(TKey key, long nowUs)
    {
        if (_nodes.TryGetValue(key, out LinkedListNode<(TKey Key, TokenBucket Bucket)>? node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Bucket.TryTake(1, nowUs);
        }

        if (_nodes.Count >= Capacity)
        {
            LinkedListNode<(TKey Key, TokenBucket Bucket)> oldest = _order.Last!;
            _order.RemoveLast();
            _nodes.Remove(oldest.Value.Key);
        }

        var bucket = new TokenBucket(_rate, _burst, nowUs);
        _nodes[key] = _order.AddFirst((key, bucket));
        return bucket.TryTake(1, nowUs);
    }
}
