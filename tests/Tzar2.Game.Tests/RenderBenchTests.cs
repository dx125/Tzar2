using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.MapGen;
using Tzar2.Content.Rules;
using Tzar2.Game.Diagnostics;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>The S2 render bench's match and its report line (ADR 0244).</summary>
public class RenderBenchTests
{
    private static readonly Lazy<Ruleset> Rules = new(() => SyntheticLegacyContent.CompileRuleset());

    [Fact]
    public void The_bench_match_has_computer_run_kingdoms_and_its_armies_beside_the_castles()
    {
        MatchSetup setup = RenderBench.Setup(Rules.Value, SkirmishAi.EngineDefault, RandomMapSize.Medium, units: 400)!;
        Assert.True(setup.NoFog && setup.DayNight && setup.Weather && setup.AiAssist);
        Assert.Equal(ControllerKind.Human, setup.Players[0].Controller);
        Assert.All(setup.Players.Skip(1), p => Assert.Equal((ControllerKind.Computer, Difficulty.Hard), (p.Controller, p.Difficulty)));
        Assert.Equal(8, RenderBench.Setup(Rules.Value, SkirmishAi.EngineDefault)!.Players.Count);

        using var session = new GameSession(setup, Rules.Value);
        Sim.World world = session.Simulation.World;
        Assert.InRange(RenderBench.MobileUnits(world), 400, 420);
        for (int p = 0; p < world.Players.Length; p++)
        {
            Assert.True((world.Players[p].Flags & (PlayerFlags.AssistEconomy | PlayerFlags.AssistWar)) == (PlayerFlags.AssistEconomy | PlayerFlags.AssistWar));
        }

        Assert.All(RenderBench.Castles(world), c => Assert.NotEqual(default, c));
        session.Step(20); // the armies and the AIs run
        Assert.Null(session.Fault);
    }

    [Fact]
    public void The_report_line_holds_the_rates_the_percentiles_and_the_s2_verdict()
    {
        var result = new RenderBenchResult { Width = 2560, Height = 1440, Renderer = "mobile/vulkan", Gpu = "GTX \"1060\"", Cpu = "Ryzen 5 3600", Units = 4000, LoadSeconds = 7.25 };
        for (int i = 0; i < 99; i++)
        {
            result.AddFrame(10);
        }

        result.AddFrame(50);
        result.AddTicks([2, 3, 4, 5, 11]);
        result.SampleMemory(2L << 30, 1L << 30);
        result.SampleMemory(1L << 30, 0);

        Assert.Equal(1000.0 * 100 / 1040, result.Fps, 6);
        Assert.Equal(20, result.Low1Fps, 6);
        Assert.Equal(10, result.FramePercentile(50));
        Assert.Equal(50, result.FramePercentile(99.5));
        Assert.Equal(11, result.TickPercentile(99));
        Assert.Equal("PASS", result.S2Verdict);
        string line = result.Line();
        Assert.StartsWith("RENDER-BENCH v1 S2=PASS res=2560x1440 fps=96.2 low1=20.0 frame_p50=10.0ms", line, StringComparison.Ordinal);
        Assert.Contains("sim_p99=11.0ms ticks=5 frames=100 units=4000 load=7.3s ram=2.00GB vram=1.00GB phases=none renderer=mobile/vulkan gpu=\"GTX '1060'\" cpu=\"Ryzen 5 3600\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);

        Assert.Equal("n/a", new RenderBenchResult { Width = 1920, Height = 1080 }.S2Verdict);
        var slow = new RenderBenchResult { Width = 2560, Height = 1440, Renderer = "mobile/vulkan" };
        slow.AddFrame(20);
        Assert.Equal("FAIL", slow.S2Verdict);
        var headless = new RenderBenchResult { Width = 2560, Height = 1440, Renderer = "dummy/headless" };
        headless.AddFrame(1);
        Assert.Equal("n/a", headless.S2Verdict);
    }

    [Fact]
    public void Phase_times_split_a_tick_into_its_top_level_sections_and_the_rest()
    {
        var phases = new PhaseTimes();
        Assert.Equal("phases=none", phases.Field());
        for (int tick = 0; tick < 100; tick++)
        {
            phases.Begin("ai");
            phases.Begin("nested"); // counts in "ai"
            phases.End();
            phases.End();
            phases.Begin("movement");
            phases.End();
            phases.EndTick(tick == 0 ? 1000 : 1);
        }

        Assert.Equal(100, phases.TickCount);
        var rows = phases.Summary();
        Assert.Equal(["(outside)", "ai", "movement"], rows.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(PhaseTimes.Outside, rows[0].Name); // the slow tick's 1000 ms are outside both sections
        Assert.True(rows[0].SlowMean > 900);
        Assert.StartsWith("phases=(outside):", phases.Field(), StringComparison.Ordinal);
        Assert.Equal(3, phases.Field().Split(',').Length);
        Assert.Contains(",rest:", phases.Field(top: 1), StringComparison.Ordinal); // the first section, then the rest
        phases.Reset();
        Assert.Equal(0, phases.TickCount);
    }

    [Fact]
    public void A_sample_ring_drains_what_came_since_the_last_drain_and_keeps_the_latest()
    {
        var ring = new SampleRing(4);
        Assert.Empty(ring.Drain());
        ring.Add(1);
        ring.Add(2);
        Assert.Equal([1.0, 2.0], ring.Drain());
        for (int i = 3; i <= 8; i++)
        {
            ring.Add(i);
        }

        Assert.Equal([5.0, 6.0, 7.0, 8.0], ring.Drain());
        Assert.Empty(ring.Drain());
    }
}
