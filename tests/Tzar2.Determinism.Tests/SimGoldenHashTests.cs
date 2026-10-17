using System;
using Tzar2.Core.Hashing;
using Tzar2.Sim;
using Tzar2.Sim.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Determinism.Tests;

/// <summary>
/// Cross-platform determinism of the sim core (TDD §22.2, ADR 0017): the synthetic scenario — generated map,
/// 2,000 units, bot move orders and delete/respawn churn through the command codec, fork/join movement — must give
/// the same hash stream on Windows x64, Linux x64 and macOS arm64, on .NET 8 and .NET 10, in Debug (overflow policy
/// Throw) and Release (Saturate), and with any worker count. CI's <c>determinism</c> job runs this on all three OSes.
/// </summary>
public class SimGoldenHashTests(ITestOutputHelper output)
{
    // Changing the scenario, a system, the command codec, the hashed layout or a Core primitive changes these values on
    // purpose: update them in the same commit and say why. An unexpected change, or a difference between platforms,
    // is a determinism bug. History: (M2 part A) 06D4D032CD216BBC / 25418CEBC3F46249 -> (M2 hardening, ADR 0019)
    // 297C6D90FC748B9E / 38232E7ED6D36D86: occupancy counts became int instead of ushort, which changes the bytes of the
    // full-state hash only (per-tick hashes are unchanged; the stream includes the full hashes every 20 ticks).
    private const ulong GoldenFinalFullHash = 0x38232E7ED6D36D86UL;
    private const ulong GoldenHashStream = 0x297C6D90FC748B9EUL;

    private const int Ticks = 300;

    private static readonly SyntheticScenarioOptions Options = new()
    {
        MapSize = 128,
        EntityCount = 2000,
        Players = 4,
        Seed = 0x5EED_2026UL,
        OrdersPerPlayerPerTick = 12,
        ChurnPerPlayerPerTick = 2,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Sim_scenario_produces_the_golden_hashes(int workers)
    {
        (ulong stream, ulong final) = Run(workers);
        output.WriteLine($"workers={workers} runtime={Environment.Version} arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} stream={stream:X16} final={final:X16}");
        Assert.Equal(GoldenHashStream, stream);
        Assert.Equal(GoldenFinalFullHash, final);
    }

    private static (ulong Stream, ulong Final) Run(int workers)
    {
        using var scenario = new SyntheticScenario(Options, workers);
        var stream = new StateHasher();
        for (int t = 0; t < Ticks; t++)
        {
            TickResult result = scenario.RunTick();
            stream.Add(result.StateHash).Add(result.FullHash ?? 0UL);
        }

        return (stream.GetCurrentHash(), scenario.Simulation.ComputeFullHash());
    }
}
