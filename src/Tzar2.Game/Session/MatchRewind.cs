using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Tzar2.Game.Session;

/// <summary>
/// Rewind for testing (ADR 0285): a single-player match keeps a snapshot of itself every <see cref="IntervalTicks"/>
/// (10 s of game time) and can go back to any of them. The match then goes on from there as a new branch: the points
/// after it are dropped, and the restored match plays exactly as the original did from that point until the player's
/// orders differ (the simulation is deterministic). Every point of the latest <see cref="DenseWindowTicks"/> is kept;
/// older ones only on a coarser grid (a minute), which doubles while the points take more than the budget. The first
/// point (where the tool was attached) is always kept.
/// </summary>
/// <remarks>
/// The points are taken on the sim thread after a tick (<see cref="GameSession.AfterTick"/>); the rest is called on the
/// main thread. A rewind stops the current session and returns its successor, not started: the caller shows it, starts
/// it and disposes the old one.
/// </remarks>
public sealed class MatchRewind : IDisposable
{
    /// <summary>Seconds of game time between two points.</summary>
    public const int IntervalSeconds = 10;

    /// <summary>The span (seconds) before the newest point in which every point is kept.</summary>
    public const int DenseWindowSeconds = 180;

    /// <summary>The grid (seconds) older points are kept on, before the budget makes it coarser.</summary>
    public const int SparseSeconds = 60;

    /// <summary><see cref="Back"/> skips a point taken less than this before now (seconds), so pressing it again goes further back.</summary>
    public const int BackGraceSeconds = 2;

    /// <summary>The memory the points may take by default (bytes).</summary>
    public const long DefaultBudgetBytes = 256L << 20;

    private readonly object _gate = new();
    private readonly List<(int Tick, byte[] Snapshot)> _points = [];
    private readonly long _budget;
    private readonly int _denseWindow;
    private long _bytes;
    private int _sparse;
    private int _start;
    private bool _disposed;

    /// <summary>Attaches to a single-player session that has not started yet; its current state is the first point.</summary>
    /// <param name="session">The session.</param>
    /// <param name="budgetBytes">The memory the points may take.</param>
    /// <exception cref="InvalidOperationException">The session runs, or is a multiplayer match.</exception>
    public MatchRewind(GameSession session, long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsRunning || session.IsMultiplayer)
        {
            throw new InvalidOperationException("Rewind attaches to a single-player session before it starts.");
        }

        int tickMs = session.Simulation.World.Config.TickLengthMilliseconds;
        TickMilliseconds = tickMs;
        IntervalTicks = Math.Max(1, IntervalSeconds * 1000 / tickMs);
        _denseWindow = DenseWindowSeconds * 1000 / tickMs;
        _sparse = Math.Max(IntervalTicks, SparseSeconds * 1000 / tickMs);
        _budget = budgetBytes;
        Session = session;
        _start = session.Simulation.World.Tick.Value;
        Add(_start, session.CaptureSnapshot());
        session.FastSpeeds = true;
        session.AfterTick += OnTick;
    }

    /// <summary>Raised on the main thread after a rewind replaced <see cref="Session"/> (the new session, not started yet).</summary>
    public event Action<GameSession>? Rewound;

    /// <summary>The session being played (replaced by a rewind).</summary>
    public GameSession Session { get; private set; }

    /// <summary>Increments whenever <see cref="Session"/> is replaced.</summary>
    public int Generation { get; private set; }

    /// <summary>Ticks between two points.</summary>
    public int IntervalTicks { get; }

    /// <summary>The length of a tick (ms).</summary>
    public int TickMilliseconds { get; }

    /// <summary>The tick being played (any thread).</summary>
    public int Tick => _start + Session.TicksRun;

    /// <summary>The ticks of the points, oldest first.</summary>
    public ImmutableArray<int> Points
    {
        get
        {
            lock (_gate)
            {
                var ticks = ImmutableArray.CreateBuilder<int>(_points.Count);
                foreach ((int tick, _) in _points)
                {
                    ticks.Add(tick);
                }

                return ticks.MoveToImmutable();
            }
        }
    }

    /// <summary>The memory the points take (bytes).</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Whether there is a point to go back to (one taken at least <see cref="BackGraceSeconds"/> ago).</summary>
    public bool CanGoBack => PointAtOrBefore(Tick - Grace) is not null;

    private int Grace => BackGraceSeconds * 1000 / TickMilliseconds;

    /// <summary>Goes back to the latest point taken at least <see cref="BackGraceSeconds"/> ago (pressed again: the one before).</summary>
    /// <returns>The new session, or null when there is no such point.</returns>
    public GameSession? Back() => RewindTo(Tick - Grace);

    /// <summary>
    /// Goes back by about <paramref name="seconds"/>: to the latest point at or before that time, or to the first point
    /// when the match is younger.
    /// </summary>
    /// <returns>The new session, or null when there is no point to go back to.</returns>
    public GameSession? BackBy(int seconds)
    {
        int now = Tick;
        int target = now - Math.Max(Grace, seconds * 1000 / TickMilliseconds);
        int first;
        lock (_gate)
        {
            first = _points.Count > 0 ? _points[0].Tick : int.MaxValue;
        }

        return RewindTo(target < first && first <= now - Grace ? first : target);
    }

    /// <summary>
    /// Goes back to the latest point at or before <paramref name="tick"/>: the current session stops, a new one is
    /// restored from the point (same setup, speed, pause, fog and view) and becomes <see cref="Session"/>; the later
    /// points are dropped. Main thread.
    /// </summary>
    /// <returns>The new session (not started), or null when no point is that early.</returns>
    /// <exception cref="ObjectDisposedException">Disposed.</exception>
    public GameSession? RewindTo(int tick)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (PointAtOrBefore(tick) is not { } point)
        {
            return null;
        }

        GameSession old = Session;
        old.Stop();
        old.AfterTick -= OnTick;
        GameSession next;
        try
        {
            next = GameSession.Restore(old.Setup, old.Ruleset, point.Snapshot, old.FogOn);
        }
        catch
        {
            old.AfterTick += OnTick;
            throw;
        }

        next.FastSpeeds = true;
        next.SpeedIndex = old.SpeedIndex;
        next.Paused = old.Paused;
        if (old.ViewIndex >= 0)
        {
            next.View(old.ViewIndex == 255 ? null : (byte)old.ViewIndex);
        }

        lock (_gate)
        {
            for (int i = _points.Count - 1; i >= 0 && _points[i].Tick > point.Tick; i--)
            {
                _bytes -= _points[i].Snapshot.Length;
                _points.RemoveAt(i);
            }
        }

        _start = point.Tick;
        Session = next;
        Generation++;
        next.AfterTick += OnTick;
        Rewound?.Invoke(next);
        return next;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Session.AfterTick -= OnTick;
        lock (_gate)
        {
            _points.Clear();
            _bytes = 0;
        }
    }

    private (int Tick, byte[] Snapshot)? PointAtOrBefore(int tick)
    {
        lock (_gate)
        {
            for (int i = _points.Count - 1; i >= 0; i--)
            {
                if (_points[i].Tick <= tick)
                {
                    return _points[i];
                }
            }

            return null;
        }
    }

    // Sim thread, between two ticks: the state before the next tick is the point of that tick.
    private void OnTick(GameSession session)
    {
        int tick = session.Simulation.World.Tick.Value;
        if (tick % IntervalTicks == 0 && ReferenceEquals(session, Session))
        {
            Add(tick, session.Simulation.CaptureSnapshot());
        }
    }

    private void Add(int tick, byte[] snapshot)
    {
        lock (_gate)
        {
            _points.Add((tick, snapshot));
            _bytes += snapshot.Length;
            while (true)
            {
                // Keep the first point, the latest window, and the older points on the grid.
                for (int i = _points.Count - 2; i >= 1; i--)
                {
                    int t = _points[i].Tick;
                    if (tick - t > _denseWindow && t % _sparse != 0)
                    {
                        _bytes -= _points[i].Snapshot.Length;
                        _points.RemoveAt(i);
                    }
                }

                if (_bytes <= _budget || _sparse > tick)
                {
                    break;
                }

                _sparse *= 2;
            }
        }
    }
}
