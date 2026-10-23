using System.Collections.Generic;

namespace Tzar2.Client.Logic;

/// <summary>
/// When each unit's picture was set, so that a picture runs from its start row when it is set (IMAGES.INI
/// <c>startrow</c>, PRES-008; ReTzar's reading, 0x5017f0) rather than on a clock shared by every unit. Engine-free.
/// The world renderer asks it once per drawn unit; units no longer drawn are dropped by <see cref="Sweep"/>, so the
/// table stays as large as the units on screen.
/// </summary>
public sealed class AnimationStarts
{
    /// <summary>How long a unit may go undrawn before its entry is dropped (ms).</summary>
    public const long KeepMs = 5_000;

    private readonly Dictionary<uint, Entry> _starts = [];
    private readonly List<uint> _stale = [];
    private long _sweptMs = long.MinValue;

    /// <summary>The units with an entry.</summary>
    public int Count => _starts.Count;

    /// <summary>
    /// The time since unit <paramref name="id"/>'s picture became <paramref name="picture"/>: 0 when it has just been set
    /// (a new unit, or another picture than the last time), else the time since then.
    /// </summary>
    /// <param name="id">The unit (<c>EntityId.Value</c>).</param>
    /// <param name="picture">Which picture it shows (the renderer's animation kind).</param>
    /// <param name="nowMs">The renderer's clock.</param>
    public long Elapsed(uint id, int picture, long nowMs)
    {
        if (!_starts.TryGetValue(id, out Entry e) || e.Picture != picture || nowMs < e.StartMs)
        {
            _starts[id] = new Entry(picture, nowMs, nowMs);
            return 0;
        }

        _starts[id] = e with { SeenMs = nowMs };
        return nowMs - e.StartMs;
    }

    /// <summary>Drops the units not drawn for <see cref="KeepMs"/>; does the work at most once per <see cref="KeepMs"/>.</summary>
    public void Sweep(long nowMs)
    {
        if (_sweptMs != long.MinValue && nowMs - _sweptMs < KeepMs && nowMs >= _sweptMs)
        {
            return;
        }

        _sweptMs = nowMs;
        _stale.Clear();
        foreach (KeyValuePair<uint, Entry> kv in _starts)
        {
            if (nowMs - kv.Value.SeenMs > KeepMs || nowMs < kv.Value.SeenMs)
            {
                _stale.Add(kv.Key);
            }
        }

        foreach (uint id in _stale)
        {
            _starts.Remove(id);
        }
    }

    private readonly record struct Entry(int Picture, long StartMs, long SeenMs);
}
