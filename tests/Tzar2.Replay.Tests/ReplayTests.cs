using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
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
            using Simulation fresh = Simulation.Restore(replay.Keyframes[0].Snapshot.Span, scenario.Ruleset, scenario.Map, workerCount: 1);
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
                snapshot => Simulation.Restore(snapshot.Span, scenario.Ruleset, scenario.Map, workerCount: 4));
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
            ReplayFile original = ReplayFile.Parse(bytes);

            // Re-record the same replay with tick 33's commands removed but the original hashes kept: the hash at the
            // next checkpoint (tick 39) must differ.
            var recorder = new ReplayRecorder(original.Header);
            recorder.RecordKeyframe(Tick.Zero, original.Keyframes[0].Snapshot.Span);
            var commands = new CommandList();
            foreach (ReplayTick tick in original.Ticks)
            {
                original.ReadCommands(tick.Tick.Value, commands);
                if (tick.Tick.Value == 33)
                {
                    commands.Clear();
                }

                recorder.RecordTick(commands, new TickResult(tick.Tick, tick.StateHash ?? 0, tick.FullHash));
            }

            ReplayFile replay = ReplayFile.Parse(recorder.Finish());
            using Simulation restored = Simulation.Restore(replay.Keyframes[0].Snapshot.Span, scenario.Ruleset, scenario.Map);
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
        Assert.Throws<InvalidDataException>(() => ReplayFile.Parse(bytes.AsSpan(0, bytes.Length - 1).ToArray()));
        byte[] badTag = (byte[])bytes.Clone();
        badTag[^2] = 9;
        Assert.Throws<InvalidDataException>(() => ReplayFile.Parse(badTag));
        Assert.Throws<InvalidDataException>(() => ReplayFile.Parse([1, 2, 3, 4]));
    }

    [Fact]
    public void Review_R8_a_keyframe_holding_another_tick_is_rejected_when_seeking()
    {
        var options = Options with { EntityCount = 200, Players = 2 };
        using var ahead = new SyntheticScenario(options, workerCount: 1);
        for (int t = 0; t < 50; t++)
        {
            ahead.RunTick();
        }

        byte[] tick50 = ahead.Simulation.CaptureSnapshot();
        using var scenario = new SyntheticScenario(options, workerCount: 1);
        var recorder = new ReplayRecorder(ReplayHeader.For(scenario.Simulation, "0.0.1") with { KeyframeIntervalTicks = 1000 });
        recorder.RecordKeyframe(Tick.Zero, tick50); // the record says tick 0, the payload is tick 50
        for (int t = 0; t < 30; t++)
        {
            recorder.RecordTick(scenario.LastCommands, scenario.RunTick());
        }

        ReplayFile replay = ReplayFile.Parse(recorder.Finish());
        Assert.Throws<InvalidDataException>(() => ReplayVerifier.SeekTo(
            replay, new Tick(10), () => throw new Xunit.Sdk.XunitException("a keyframe exists"), s => Simulation.Restore(s.Span, scenario.Ruleset, scenario.Map)));

        // Verify refuses to run "backwards" or past the end instead of reporting success.
        using Simulation at50 = Simulation.Restore(tick50, scenario.Ruleset, scenario.Map);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplayVerifier.Verify(replay, at50, new Tick(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplayVerifier.SeekTo(
            replay, new Tick(31), () => throw new Xunit.Sdk.XunitException("not reached"), _ => throw new Xunit.Sdk.XunitException("not reached")));
    }

    [Fact]
    public void Review_R9_parsing_a_two_hour_replay_allocates_little()
    {
        using var sim = new Simulation(SyntheticScenario.CreateRuleset(), SyntheticScenario.CreateMap(64, 1), new SimulationConfig { PlayerCount = 2 });
        var recorder = new ReplayRecorder(ReplayHeader.For(sim, "0.0.1"));
        var empty = new CommandList();
        const int ticks = 144_000; // 2 h at 20 Hz
        for (int t = 0; t < ticks; t++)
        {
            recorder.RecordTick(empty, new TickResult(new Tick(t), 0, null));
        }

        byte[] bytes = recorder.Finish();
        long before = GC.GetAllocatedBytesForCurrentThread();
        ReplayFile replay = ReplayFile.Parse(bytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(ticks, replay.Ticks.Count);
        Assert.True(allocated < 16L << 20, $"Parse allocated {allocated >> 20} MiB for a {bytes.Length >> 10} KiB replay.");
    }

    [Fact]
    public void Verification_reports_the_overflow_mode_of_the_recording()
    {
        (byte[] bytes, _, SyntheticScenario scenario) = Record(10);
        using (scenario)
        {
            ReplayFile replay = ReplayFile.Parse(bytes);
            using Simulation sim = Simulation.Restore(replay.Keyframes[0].Snapshot.Span, scenario.Ruleset, scenario.Map);
            Assert.True(ReplayVerifier.Verify(replay, sim).OverflowModeMatches);
        }
    }

    [Fact]
    public void The_header_keeps_the_ai_programs_and_rejects_bad_ones()
    {
        using var sim = new Simulation(SyntheticScenario.CreateRuleset(), SyntheticScenario.CreateMap(64, 1), new SimulationConfig { PlayerCount = 3 });
        ReplayHeader header = ReplayHeader.For(sim, "0.0.1") with { AiPrograms = [new ReplayAiProgram(1, 0xABCDUL), new ReplayAiProgram(2, 0x1234UL)] };
        ReplayFile replay = ReplayFile.Parse(new ReplayRecorder(header).Finish());
        Assert.Equal(header.AiPrograms.ToArray(), replay.Header.AiPrograms.ToArray());
        Assert.Empty(ReplayFile.Parse(new ReplayRecorder(ReplayHeader.For(sim, "0.0.1")).Finish()).Header.AiPrograms);

        byte[] outOfOrder = new ReplayRecorder(header with { AiPrograms = [new ReplayAiProgram(2, 1), new ReplayAiProgram(1, 2)] }).Finish();
        Assert.Throws<System.IO.InvalidDataException>(() => ReplayFile.Parse(outOfOrder));
        byte[] noSuchSlot = new ReplayRecorder(header with { AiPrograms = [new ReplayAiProgram(5, 1)] }).Finish();
        Assert.Throws<System.IO.InvalidDataException>(() => ReplayFile.Parse(noSuchSlot));
    }
}
