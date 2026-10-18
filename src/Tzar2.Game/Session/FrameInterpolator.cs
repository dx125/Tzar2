using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;

namespace Tzar2.Game.Session;

/// <summary>
/// Smooth movement between ticks (TDD §7.3: render-snapshot interpolation on the main thread). The presentation draws
/// the newest frame's positions blended with the previous frame's by the time elapsed since the newest arrived
/// (<c>alpha</c> 0-1000 per mille of a tick). Jumps longer than <see cref="SnapUnits"/> (spawns, re-used ids) are not
/// blended. The result is rounded to whole world units; the renderer snaps to whole pixels (REQ-GFX-01).
/// </summary>
public sealed class FrameInterpolator
{
    /// <summary>Movements longer than this between two frames are drawn without blending (two tiles).</summary>
    public const int SnapUnits = 2 * MapData.UnitsPerTile;

    private Dictionary<uint, (int X, int Y)> _previous = [];
    private Dictionary<uint, (int X, int Y)> _current = [];

    /// <summary>Records a newly acquired frame; the frame before it becomes the blend source.</summary>
    public void Push(GameFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        (_previous, _current) = (_current, _previous);
        _current.Clear();
        foreach (ref readonly FrameEntity e in frame.Entities)
        {
            _current[e.Id.Value] = (e.X, e.Y);
        }
    }

    /// <summary>The drawn position of <paramref name="entity"/> at <paramref name="alphaPermille"/> of the way from the previous frame.</summary>
    public (int X, int Y) Position(in FrameEntity entity, int alphaPermille)
    {
        int a = Math.Clamp(alphaPermille, 0, 1000);
        if (a >= 1000 || !_previous.TryGetValue(entity.Id.Value, out (int X, int Y) from))
        {
            return (entity.X, entity.Y);
        }

        long dx = entity.X - from.X, dy = entity.Y - from.Y;
        if (Math.Abs(dx) > SnapUnits || Math.Abs(dy) > SnapUnits)
        {
            return (entity.X, entity.Y);
        }

        return ((int)(from.X + (dx * a / 1000)), (int)(from.Y + (dy * a / 1000)));
    }

    /// <summary>Alpha per mille for <paramref name="sinceFrameMs"/> after the newest frame arrived, at <paramref name="tickIntervalMs"/> per tick.</summary>
    public static int Alpha(double sinceFrameMs, double tickIntervalMs)
        => tickIntervalMs <= 0 ? 1000 : (int)Math.Clamp(sinceFrameMs * 1000.0 / tickIntervalMs, 0, 1000);
}
