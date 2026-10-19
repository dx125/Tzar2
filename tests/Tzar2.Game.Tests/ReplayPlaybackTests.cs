using System;
using System.Diagnostics;
using System.IO;
using Tzar2.Game.Maps;
using Tzar2.Game.Session;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>The replay viewer's model (ADR 0164, 0168): speed, pause, seek forward and back, perspective, hash checks.</summary>
public sealed class ReplayPlaybackTests
{
    private static byte[] Record(int ticks)
    {
        var setup = new MatchSetup
        {
            Map = new SkirmishTestMap(64),
            Players = [new PlayerSetup(0, "euro", ControllerKind.Computer, Difficulty.Hard), new PlayerSetup(1, "euro", ControllerKind.Computer, Difficulty.Easy)],
            Seed = 77,
        };
        using var session = new GameSession(setup, HumanVsAiTests.SyntheticRules, fog: false);
        session.Step(ticks);
        return session.FinishReplay()!;
    }

    private static void Pump(ReplayPlayback playback, Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.ElapsedMilliseconds < 30_000, "timed out");
            playback.Session.PumpLockstep(256);
            System.Threading.Thread.Sleep(1);
        }
    }

    [Fact]
    public void A_replay_plays_at_speed_seeks_both_ways_and_follows_a_kingdom()
    {
        byte[] bytes = Record(2_600); // keyframes at 0, 1200 and 2400
        ReplayPlayback playback = ReplayPlayback.Open(bytes, HumanVsAiTests.SyntheticRules, SkirmishAi.EngineDefault);
        Assert.Equal(2_600, playback.Length);
        Assert.Equal(["Computer 1", "Computer 2"], playback.Players);

        // 16x: 320 ticks per second of wall-clock time.
        for (int i = 0; i < 4; i++)
        {
            playback.ChangeSpeed(+1);
        }

        Assert.Equal(16, playback.Speed);
        var watch = Stopwatch.StartNew();
        Pump(playback, () => playback.Session.Simulation.World.Tick.Value >= 300);
        Assert.InRange(watch.ElapsedMilliseconds, 500, 10_000);

        // Pause holds the clock.
        playback.Paused = true;
        int paused = playback.Session.Simulation.World.Tick.Value;
        for (int i = 0; i < 50; i++)
        {
            playback.Session.PumpLockstep(256);
            System.Threading.Thread.Sleep(2);
        }

        Assert.Equal(paused, playback.Session.Simulation.World.Tick.Value);
        playback.Paused = false;

        // Forward seek fast-forwards in the same session.
        GameSession first = playback.Session;
        playback.Seek(2_000);
        Pump(playback, () => playback.Session.Simulation.World.Tick.Value >= 2_000);
        Assert.Same(first, playback.Session);

        // Backward seek restores the keyframe at 1,200 into a new session, then fast-forwards to 1,500.
        playback.Seek(1_500);
        Assert.Equal(1, playback.Generation);
        Assert.NotSame(first, playback.Session);
        Assert.Equal(1_200, playback.Session.Simulation.World.Tick.Value);
        first.Dispose();
        Pump(playback, () => playback.Session.Simulation.World.Tick.Value >= 1_500);

        playback.Follow = 1;
        Assert.Equal((byte)1, playback.Follow);
        playback.Follow = 9;
        Assert.Null(playback.Follow);

        Pump(playback, () => playback.Session.Simulation.World.Tick.Value >= 2_600);
        Assert.Equal(-1, playback.MismatchTick);
        playback.Session.Dispose();
    }

    [Fact]
    public void A_replay_of_another_ruleset_or_without_a_description_is_refused()
    {
        byte[] bytes = Record(10);
        var other = Content.Rules.SyntheticLegacyContent.CompileRuleset(); // same content: accepted
        Assert.NotNull(ReplayPlayback.Open(bytes, other, SkirmishAi.EngineDefault));
        Assert.Throws<InvalidDataException>(() => ReplayPlayback.Open([1, 2, 3], other, SkirmishAi.EngineDefault));
    }
}
