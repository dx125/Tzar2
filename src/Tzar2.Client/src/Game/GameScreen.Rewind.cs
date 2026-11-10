using System;
using Godot;
using Tzar2.Core.Ids;
using Tzar2.Game.Input;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// Rewind for testing in the game screen (ADR 0285): the match's <see cref="MatchRewind"/> is driven by the rewind bar
/// and Ctrl+Backspace; a rewind swaps the session under the screen without loading it again (the sprites, the terrain
/// and the camera stay), and a match that ends waits for FINISH so that the tester can go back instead.
/// </summary>
public partial class GameScreen
{
    private RewindBar? _rewindBar;

    /// <summary>
    /// The match's rewind (playtests; any single-player match with the testing setting on), or null. Set it before the
    /// screen enters the tree; the screen disposes it.
    /// </summary>
    public MatchRewind? Rewind { get; set; }

    /// <summary>Raised after a rewind replaced <see cref="Session"/> (the new session, already started).</summary>
    public event Action<GameSession>? SessionReplaced;

    /// <summary>The outcome waiting for FINISH (rewind on), or <see cref="MatchOutcome.None"/>.</summary>
    public MatchOutcome HeldOutcome { get; private set; }

    /// <summary>Ends a match whose outcome waits for FINISH (the results or the editor, as without rewind).</summary>
    public void FinishHeldOutcome()
    {
        MatchOutcome outcome = HeldOutcome;
        if (outcome != MatchOutcome.None)
        {
            HeldOutcome = MatchOutcome.None;
            Finished?.Invoke(outcome, Stats);
        }
    }

    /// <summary>Goes back to the previous rewind point; false when there is none.</summary>
    public bool RewindBack() => Rewind is { } r && Swap(r.Back());

    /// <summary>Goes back by about <paramref name="seconds"/> of game time; false when there is no point that early.</summary>
    public bool RewindBy(int seconds) => Rewind is { } r && Swap(r.BackBy(seconds));

    /// <summary>Goes back to the latest point at or before <paramref name="tick"/>; false when there is none.</summary>
    public bool RewindTo(int tick) => Rewind is { } r && Swap(r.RewindTo(tick));

    // Before the session starts: the testing setting turns rewind on in any single-player match the player plays. The
    // automated runs (the render bench, the smokes, autoplay) keep their speed and never get it: it would change their timings.
    private void AttachRewind()
    {
        if (Rewind is null && Settings.TestRewind && !KeepSpeed && !_session.IsMultiplayer && !_session.IsRunning)
        {
            Rewind = new MatchRewind(_session);
        }

        if (Rewind is not null && _rewindBar is null)
        {
            var layer = new CanvasLayer { Layer = 4, Name = "Rewind" };
            _rewindBar = new RewindBar(this, Rewind);
            layer.AddChild(_rewindBar);
            AddChild(layer);
        }
    }

    // Ctrl+Backspace: back to the previous point (the original binds Backspace alone).
    private bool RewindKey(GameKey key, KeyMods mods)
    {
        if (Rewind is null || key != GameKey.Backspace || (mods & KeyMods.Ctrl) == 0)
        {
            return false;
        }

        if (!RewindBack())
        {
            Message("No earlier rewind point.");
        }

        return true;
    }

    private bool Swap(GameSession? next)
    {
        if (next is null)
        {
            return false;
        }

        ReplaceSession(next);
        return true;
    }

    // The match goes on in another session (a rewind): the client's per-match state starts again from its first frame.
    private void ReplaceSession(GameSession next)
    {
        GameSession old = _session;
        old.Stop();
        _tracer?.Dispose();
        _tracer = null;
        ClientLog.TrackSession(null);
        _session = next;
        old.Dispose();

        _events.Clear();
        _notices.Clear();
        _modal.Clear();
        CloseTalk();
        if (_open is not null)
        {
            _open.GetParent()?.QueueFree();
            _open = null;
        }

        _storyPause = false;
        _viewLockedUntil = 0;
        _outcomeShown = false;
        _outcomeAt = -1;
        HeldOutcome = MatchOutcome.None;
        _faultShown = false;
        _mixer?.StopAll();
        _mapSounds = null;
        _mapSoundSeen = null;
        _picked = EntityId.None;
        _mode = OrderMode.None;

        if (ClientLog.Sink is { } sink)
        {
            _tracer = Tzar2.Game.Diagnostics.MatchTracer.Attach(next, sink, "match (rewound)");
        }

        ClientLog.TrackSession(next);
        if (next.AcquireFrame(out _) is { } first)
        {
            Accept(first);
        }

        next.Start();
        int seconds = (Rewind?.Tick ?? 0) * (Rewind?.TickMilliseconds ?? 0) / 1000;
        Message(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Rewound to {seconds / 60}:{seconds % 60:00}"));
        SessionReplaced?.Invoke(next);
    }
}
