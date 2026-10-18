using System;
using System.IO;
using System.Linq;
using FsCheck.Xunit;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Runtime;
using Tzar2.AI.Scenarios;
using Tzar2.Core.Hashing;
using Tzar2.Core.Serialization;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Xunit;

namespace Tzar2.AI.Tests;

/// <summary>
/// REQ-AI-03 / §22.2: the AI is part of the lockstep. Every peer must issue the same commands: whatever the worker
/// count, and after a snapshot restore (including in the middle of a budgeted script pass).
/// </summary>
public class AiDeterminismTests
{
    // Small budgets so passes span ticks and a snapshot can fall inside one.
    private static readonly AiSkirmishOptions Options = new()
    {
        Players = 3,
        MapSize = 96,
        TreesPerPlayer = 80,
        Ai = new AiOptions { InstructionBudgetPerTick = 24, MaxCommandsPerRun = 16 },
    };

    private sealed class Streams
    {
        public StateHasher States { get; } = new();

        public StateHasher Commands { get; } = new();

        private readonly BinaryBufferWriter _wire = new(1024);

        public void Add(AiSkirmishScenario scenario, TickResult result)
        {
            States.Add(result.StateHash).Add(result.FullHash ?? 0);
            _wire.Clear();
            CommandCodec.WriteBatch(_wire, scenario.Ai.LastCommands);
            Commands.Add(_wire.WrittenSpan);
            foreach (Command c in scenario.Ai.LastCommands.Commands)
            {
                Commands.Add(c.Player);
            }
        }
    }

    private static Streams RunFor(AiSkirmishScenario scenario, int ticks, Streams? streams = null)
    {
        streams ??= new Streams();
        for (int t = 0; t < ticks; t++)
        {
            streams.Add(scenario, scenario.RunTick());
        }

        return streams;
    }

    [Fact]
    public void The_worker_count_does_not_change_the_commands_or_the_state()
    {
        using var one = new AiSkirmishScenario(AiFixtures.Classic, AiFixtures.Skirmish, Options, workerCount: 1);
        using var four = new AiSkirmishScenario(AiFixtures.Classic, AiFixtures.Skirmish, Options, workerCount: 4);
        Streams a = RunFor(one, 2400);
        Streams b = RunFor(four, 2400);
        Assert.True(one.Ai.Issued(0) > 20, $"the AI must actually act ({one.Ai.Issued(0)} commands)");
        Assert.Equal(a.Commands.GetCurrentHash(), b.Commands.GetCurrentHash());
        Assert.Equal(a.States.GetCurrentHash(), b.States.GetCurrentHash());
    }

    [Fact]
    public void A_restored_snapshot_continues_with_the_same_commands_and_hashes()
    {
        using var straight = new AiSkirmishScenario(AiFixtures.Classic, AiFixtures.Skirmish, Options);
        RunFor(straight, 1500);
        for (int t = 0; t < 40 && !straight.Ai.Players.Any(p => p.InPass); t++)
        {
            straight.RunTick();
        }

        Assert.Contains(straight.Ai.Players, p => p.InPass); // the snapshot falls inside a pass
        byte[] snapshot = straight.Simulation.CaptureSnapshot();
        using AiSkirmishScenario restored = straight.Restore(snapshot, workerCount: 2);
        Assert.Equal(straight.Simulation.ComputeFullHash(), restored.Simulation.ComputeFullHash());
        Assert.Equal(snapshot, restored.Simulation.CaptureSnapshot());
        Streams a = RunFor(straight, 1500);
        Streams b = RunFor(restored, 1500);
        Assert.Equal(a.Commands.GetCurrentHash(), b.Commands.GetCurrentHash());
        Assert.Equal(a.States.GetCurrentHash(), b.States.GetCurrentHash());
    }

    [Fact]
    public void A_snapshot_restores_only_with_the_same_scripts_and_settings()
    {
        using var scenario = new AiSkirmishScenario(AiFixtures.Classic, AiFixtures.Skirmish, Options);
        RunFor(scenario, 100);
        byte[] snapshot = scenario.Simulation.CaptureSnapshot();
        AiProgram other = AiFixtures.Compile("rule have 3 houses end");
        foreach (Func<AiPlayerSettings, AiPlayerSettings> change in new Func<AiPlayerSettings, AiPlayerSettings>[]
        {
            s => s with { Program = other },
            s => s with { Difficulty = AiDifficulty.Easy },
            s => s with { Civilization = "asia" },
            s => s with { WarAi = false },
        })
        {
            using Simulation restored = Simulation.Restore(snapshot, AiFixtures.Classic, scenario.Map);
            var players = new System.Collections.Generic.List<AiPlayerSettings>();
            foreach (AiPlayerSettings s in AiSkirmishScenario.PlayerSettings(AiFixtures.Skirmish, Options))
            {
                players.Add(s.Slot == 1 ? change(s) : s);
            }

            Assert.Throws<InvalidDataException>(() => LegacyAiSystem.Attach(restored, players, Options.Ai));
        }

        using Simulation fewer = Simulation.Restore(snapshot, AiFixtures.Classic, scenario.Map);
        Assert.Throws<InvalidDataException>(() => LegacyAiSystem.Attach(fewer, [AiSkirmishScenario.PlayerSettings(AiFixtures.Skirmish, Options)[0]], Options.Ai));
    }

    [Property(MaxTest = 300)]
    public void Malformed_ai_state_is_rejected_cleanly(int seed)
    {
        using var scenario = new AiSkirmishScenario(AiFixtures.Classic, AiFixtures.Skirmish, Options with { Players = 2 });
        var writer = new BinaryBufferWriter();
        scenario.Ai.WriteTo(writer);
        byte[] state = writer.ToArray();
        var random = new Random(seed);
        byte[] corrupt = random.Next(3) == 0 ? state[..random.Next(state.Length)] : (byte[])state.Clone();
        for (int i = 0, n = 1 + random.Next(6); i < n && corrupt.Length > 0; i++)
        {
            corrupt[random.Next(corrupt.Length)] = (byte)random.Next(256);
        }

        try
        {
            scenario.Ai.ReadFrom(corrupt);
        }
        catch (InvalidDataException)
        {
            // expected for most corruptions; anything else would fail the property
        }
    }
}
