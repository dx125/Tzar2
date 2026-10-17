using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Scenarios;
using Xunit;

namespace Tzar2.Replay.Tests;

public class ReplayTests
{
    private static readonly SyntheticScenarioOptions Options = new()
    {
        MapSize = 64,
        EntityCount = 800,
        Players = 3,
        Seed = 4242,
        OrdersPerPlayerPerTick = 6,
        ChurnPerPlayerPerTick = 1,
    };

    private static (byte[] Bytes, List<ulong> Hashes, SyntheticScenario Scenario) Record(int ticks)
    {
        var scenario = new SyntheticScenario(Options, workerCount: 2);
        var recorder = new ReplayRecorder(ReplayHeader.For(scenario.Simulation, "0.0.1") with { KeyframeIntervalTicks = 100 });
        var hashes = new List<ulong>();
        for (int t = 0; t < ticks; t++)
        {
            Tick tick = scenario.Simulation.World.Tick;
            if (recorder.IsKeyframeDue(tick))
            {
                recorder.RecordKeyframe(tick, scenario.Simulation.CaptureSnapshot());
            }

            TickResult result = scenario.RunTick();
            recorder.RecordTick(scenario.LastCommands, result);
            hashes.Add(result.StateHash);
        }

        return (recorder.Finish(), hashes, scenario);
    }

    [Fact]
    public void Recorded_replay_verifies_headlessly_with_any_worker_count()
    {
        (byte[] bytes, List<ulong> hashes, SyntheticScenario scenario) = Record(250);
        using (scenario)
        {
            ReplayFile replay = ReplayFile.Parse(bytes);
            Assert.Equal(250, replay.Ticks.Count);
            Assert.Equal([0, 100, 200], replay.Keyframes.Select(k => k.Tick.Value));

            // The scenario spawns its units before tick 0, so verification starts from the tick-0 keyframe.
            using Simulation fresh = Simulation.Restore(replay.Keyframes[0].Snapshot, scenario.Ruleset, scenario.Map, workerCount: 1);
            ReplayVerification result = ReplayVerifier.Verify(replay, fresh);
            Assert.True(result.Succeeded);
            Assert.Equal(250, result.TicksRun);
            Assert.Equal(25 + 12, result.HashesChecked); // every 10th tick + every 20th full hash
            Assert.Equal(hashes[^1], result.FinalStateHash);
        }
    }

    [Fact]
    public void Seeking_from_a_keyframe_reaches_the_recorded_state()
    {
        (byte[] bytes, List<ulong> hashes, SyntheticScenario scenario) = Record(250);
        using (scenario)
        {
            ReplayFile replay = ReplayFile.Parse(bytes);
            using Simulation sim = ReplayVerifier.SeekTo(
                replay,
                new Tick(237),
                () => throw new Xunit.Sdk.XunitException("a keyframe exists"),
                snapshot => Simulation.Restore(snapshot, scenario.Ruleset, scenario.Map, workerCount: 4));
            Assert.Equal(237, sim.World.Tick.Value);
            Assert.Equal(hashes[236], sim.ComputeStateHash());
        }
    }

    [Fact]
    public void A_tampered_command_is_detected_as_a_desync()
    {
        (byte[] bytes, _, SyntheticScenario scenario) = Record(60);
        using (scenario)
        {
            ReplayFile replay = ReplayFile.Parse(bytes);
            // Replace tick 33's commands with nothing: the hash at the next checkpoint (tick 39) must differ.
            replay.Ticks[33].Commands.Clear();
            using Simulation restored = Simulation.Restore(replay.Keyframes[0].Snapshot, scenario.Ruleset, scenario.Map);
            ReplayVerification result = ReplayVerifier.Verify(replay, restored);
            Assert.False(result.Succeeded);
            Assert.Equal(39, result.FirstMismatch!.Value.Value);
        }
    }

    [Fact]
    public void Malformed_replays_are_rejected()
    {
        (byte[] bytes, _, SyntheticScenario scenario) = Record(5);
        scenario.Dispose();
        Assert.Throws<InvalidDataException>(() => ReplayFile.Parse(bytes.AsSpan(0, bytes.Length - 1)));
        byte[] badTag = (byte[])bytes.Clone();
        badTag[^2] = 9;
        Assert.Throws<InvalidDataException>(() => ReplayFile.Parse(badTag));
        Assert.Throws<InvalidDataException>(() => ReplayFile.Parse([1, 2, 3, 4]));
    }
}
