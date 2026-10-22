using System;
using System.Text;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Scenarios;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Hashing;
using Tzar2.Core.Serialization;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Determinism.Tests;

/// <summary>
/// Cross-platform determinism of the legacy AI (REQ-AI-03, ADR 0051; TDD §22.2): four AI kingdoms with the engine-default
/// script on the synthetic classic ruleset must issue the same commands and produce the same hash stream on every OS,
/// runtime, configuration and worker count. The AI is part of the lockstep, so any divergence here is a desync.
/// </summary>
public class AiGoldenHashTests(ITestOutputHelper output)
{
    // Changing the AI (interpreter, executors, default script), a gameplay system, the scenario or the hashed layout changes
    // these values on purpose: update them in the same commit and say why. History: (M3 part B) first; PR #20 review (duplicate goals once per run, bounded site searches); rebase onto M3-A (#21: economy, construction and research rules change the match); research state and executor (the AI state now records the research view, players get their civilization set); M3-F (#23: fog, diplomacy, victory change the match; the AI respects stances); M4-A (ADR 0121: the inert entity comm.ground_object shifts entity type ids, so command and state hashes change); naval (ADRs 0140-0144: synthetic hulk and xebec shift type ids, ships leave the census army and workers, the naval executor runs first and does nothing on this land map); crowded landings (ADR 0146): a unit whose goal is taken by a standing unit settles next to it sooner.
    // (ADR 0169) the legacy TIME column is 1.25 s per unit (operator capture, batch A): every training and construction takes 25 % longer.
    // (ADR 0171) SPEED 4 is 40 px/s (operator capture, unit-speed): speedPixelsPerSecondPerPoint 12 -> 10, every unit walks a sixth slower.
    // (ADR 0172) the original ignores SPEED: measured classes walk at their own speed (foot 40 px/s, mounted 72, ...).
    // (ADR 0175) peasants gather 900 ms per unit, not 1,000.
    // (play-test 2026-10-20) fields are walkable and their farmers work on them; a claimed cow stops; a milker waits for
    // the milk to regrow until the pail is full.
    // (ADR 0178) the AI's tactics weigh threats by difficulty (towers and buildings at home, waves and retreat by power,
    // targets by danger, gatherers avoid towers); a unit on a move-advance answers fire.
    // (owner play-test 2026-10-20) a unit still walking to a structure works on, fights or boards it only next to it
    // (interactionTouchPx 12, in the ruleset hash).
    // (second play-test log) the gather point and the waves' move-advance goals snap to the nearest reachable ground.
    // (ADR 0180, third play-test) queues of entries (the AI keeps at most 10 items queued, as before), loads but milk
    // appear at once, the next node toward the drop-off, units leave on free tiles, idle peasants find work; layout 8.
    // (fourth play-test 2026-10-21) every load goes to the drop-off nearest to where it was gathered.
    // (ADR 0184) the spies' disguise store joins the hashed layout (version 9): the hashes only, the commands are the same.
    // (ECO-021, 2026-10-22) one peasant per field: the AI's farmers spread over its fields, it budgets wood for the
    // missing ones and sends idle workers on to the next resource, so its economy and its commands change.
    // (ORD-005, 2026-10-22) the waypoint store joins the hashed layout (version 10): the hashes only.
    // (WAR-014, 2026-10-22) footmen and archers strike at the action row of their attack animations: the fights and
    // so the commands change.
    private const ulong GoldenCommandStream = 0x4A0864630CED5C22UL;
    private const ulong GoldenHashStream = 0x1AB88C15C71B55F5UL;
    private const ulong GoldenFinalFullHash = 0xBA6B3AC488C22D22UL;

    private const int Ticks = 9600;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Ai_skirmish_produces_the_golden_hashes(int workers)
    {
        Ruleset ruleset = SyntheticLegacyContent.CompileRuleset();
        AiProgram program = AiCompiler.CompileText("skirmish.ais", Encoding.UTF8.GetString(new EngineDefaultsSource().ReadAllBytes("ai/skirmish.ais")));
        using var scenario = new AiSkirmishScenario(ruleset, program, new AiSkirmishOptions { Players = 4, MapSize = 128, Seed = 0x5EED_A1UL }, workers);
        var states = new StateHasher();
        var commands = new StateHasher();
        var wire = new BinaryBufferWriter(1024);
        for (int t = 0; t < Ticks; t++)
        {
            TickResult result = scenario.RunTick();
            states.Add(result.StateHash).Add(result.FullHash ?? 0UL);
            wire.Clear();
            CommandCodec.WriteBatch(wire, scenario.Ai.LastCommands);
            commands.Add(wire.WrittenSpan);
        }

        ulong final = scenario.Simulation.ComputeFullHash();
        output.WriteLine($"workers={workers} runtime={Environment.Version} commands={commands.GetCurrentHash():X16} stream={states.GetCurrentHash():X16} final={final:X16} {scenario.Describe()}");
        Assert.True(scenario.Ai.Issued(0) > 0 && scenario.Trained(0) > 0 && scenario.Ai.Players[0].Waves > 0, scenario.Describe());
        Assert.Equal(GoldenCommandStream, commands.GetCurrentHash());
        Assert.Equal(GoldenHashStream, states.GetCurrentHash());
        Assert.Equal(GoldenFinalFullHash, final);
    }
}
