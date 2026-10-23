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
    // (ADR 0220, 2026-10-22) the corner slide and allies as friends in movement: the fights and so the commands change.
    // (ADR 0250, 2026-10-22) the curse, hypnosis, the banshee and the Messiah as ReTzar reads them: a synthetic Messiah shifts type ids, status effects grow (hashed layout 13): the commands name the shifted type ids.
    private const ulong GoldenCommandStream = 0xA10DD058749DF77DUL;
    // (ITEM-016, 2026-10-22) items keep their own spell, effect and look (layout 11): the hashes only, the commands are the same.
    // (WORLD-015, 2026-10-22) units get the original's body: crowds space out, the economy and the waves change; the run is
    // now 10 minutes.
    // (ADR 0189, 2026-10-22) queued orders gain a kind and a target, explorers an area (hashed layout 12): the hashes only.
    // (ADR 0190, 2026-10-22) rules from ReTzar's reading of the original: gathered crusaders in the kingdom state, loads raised by the gathering technologies, ship fire rates: the ruleset and the hashes.
    // (ADR 0211, 2026-10-22) civilizations in the ruleset's order (euro first): the kingdoms' civilization indices, so the
    // hashes only; the commands are the same.
    // (ADR 0220, 2026-10-22) movement: the corner slide, allies as friends: the hashes.
    // (ADR 0255, 2026-10-22) souls are bound to a unit (item state grows, hashed layout 14), the Messiah takes no magic damage.
    // (ADR 0256, 2026-10-22) cows grow meat (a new store, hashed layout 15) that falls as food items peasants carry home.
    // (ADR 0259, 2026-10-22) Jewelry: the markets' offers (a new store, hashed layout 16) and the original's spell numbering for map books.
    // (ADR 0215, 2026-10-23) the full hash takes the map's layers and the visibility bitsets as the xxHash3 of 64 KiB
    // chunks, hashed on the workers: the full hashes only (the tick hashes and the match are the same).
    // (ADR 0265, 2026-10-23) a spell book keeps its own pool and may be endless (item state grows, hashed layout 17, snapshot 19): the hashes.
    private const ulong GoldenHashStream = 0x970C5A463E55BBCDUL;
    private const ulong GoldenFinalFullHash = 0x4F607348CD7DB30EUL;

    // 10 minutes (8 before the original's body size, WORLD-015, whose crowds slow the economy: the first wave sails later).
    private const int Ticks = 12000;

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
