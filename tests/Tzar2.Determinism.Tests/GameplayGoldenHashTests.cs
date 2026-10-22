using System;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Hashing;
using Tzar2.Sim;
using Tzar2.Sim.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Determinism.Tests;

/// <summary>
/// Cross-platform determinism of the gameplay systems (ADR 0039, TDD §22.2): the economy-and-combat scenario —
/// classic ruleset compiled from the synthetic tables, bases, harvesting, house building, training, and a battle of
/// four kingdoms — must give the same hash stream on Windows x64, Linux x64 and macOS arm64, on .NET 8 and .NET 10, in
/// Debug and Release and with any worker count. The ruleset hash is pinned too: the compiler is deterministic (ADR 0012).
/// </summary>
public class GameplayGoldenHashTests(ITestOutputHelper output)
{
    // Changing the rules compiler, the classic YAML, the synthetic tables, a gameplay system, the scenario or the hashed
    // layout changes these values on purpose: update them in the same commit and say why. History: (M2 part C) first; (M3 A, ADRs 0040-0044) every unit, building, technology and upgrade imported; research, upgrades, capture, repair, fire, gates, bridges and the new stores; the bot researches castle technologies; then resistanceScale 100 -> 1000 (owner decision, UNK-08): only the ruleset hash changed, no hit on a building happens in these 1,600 ticks; then (review of PR #21) paid, crew-limited repair, rate-limited gates, paid refunds, and a bot that attacks and captures houses, repairs, builds a gate, drags a wall and bridges a river (the map gains a river per base); then maxGateChangesPerTick 16 -> 4 (review): ruleset hash only; then (M3 F, ADRs 0100-0107) the world systems: visibility, diplomacy (kingdoms 0 and 1 ally, 2 turns neutral to 3), tributes, two markets and a caravan per kingdom (0 and 1 trade under hmnotech), a magic gateway, bears and wild cows in the middle, Standard victory evaluation; new synthetic rows (trader, gateway, bear, orc, dwarf, ghost, skeleton); then (M3 stream E, ADRs 0090-0096) combat, experience, holders, items and magic: projectiles, splash, the new stores, spells, stances; the map gains a mage, a priest and a catapult per base and items at the center; the bot researches Learning first, garrisons and unloads soldiers, casts, bombards the center and picks items up; then (M3 F tavern, ADR 0108; Taverns option) a tavern for kingdoms 0 and 1, who hire a mercenary group and a hero, and the tavern settings in the ruleset; then (M4-A, ADR 0121) the inert entity comm.ground_object: entity type ids after it shift, which every hash sees; then (naval, ADRs 0140-0143) synthetic hulk and xebec rows, two naval stores, boats fish only water nodes, no auto-chasing across the shore; then (ADR 0146) a unit whose goal is taken by a standing unit settles next to it after 20 ticks, docks keep clear of other ships.
    // (ADR 0169) the legacy TIME column is 1.25 s per unit (operator capture, batch A): every training and construction takes 25 % longer.
    // (ADR 0170) a cow node carries its own load (ResourceNode.carry, hashed into the ruleset): only the ruleset hash moves.
    // (ADR 0171) SPEED 4 is 40 px/s (operator capture, unit-speed): speedPixelsPerSecondPerPoint 12 -> 10, every unit walks a sixth slower; the bot repairs with the nearest busy peasant and picks items up within 24 tiles, so a repair and a pickup still happen in 1,600 ticks.
    // (ADR 0172) the original ignores SPEED: measured classes walk at their own speed (foot 40 px/s, mounted 72, troll 47,
    // ghost 67, cow and bear 26); the bot repairs in its base with a crew of three, and each kingdom's first house starts at
    // three quarters of its hit points, so a repair completes whatever the battle does.
    // (ADR 0173) the day is 37.5 minutes (dayLengthMs 2,250,000, measured on the clock globe): only the ruleset hash moves.
    // (ADR 0175) peasants gather 900 ms per unit (measured once on wood, applied to every resource), not 1,000.
    // (play-test 2026-10-20) a claimed cow stops where it is, and its milker waits for the milk to regrow until the pail
    // is full instead of carrying home a unit or two at a time; fields are walkable (WalkableFootprint, in the ruleset hash).
    // (ADR 0178) a unit on a move-advance that is hit fights back and resumes its way; a unit may chase what can hit it.
    // (owner play-test 2026-10-20) a unit still walking to a structure works on, fights or boards it only next to it
    // (interactionTouchPx 12, in the ruleset hash).
    // (ADR 0180, third play-test) a queue takes any number of any items (queue entries; queueCapacity 24 and the idle-work
    // settings in the ruleset hash); loads but milk appear at once; the next node lies toward the drop-off and within
    // reach; units leave a building on free tiles of its open side; peasants of any civilization help on sites; idle
    // peasants find work after 10 s; hashed layout 8.
    // (fourth play-test 2026-10-21) every load goes to the drop-off nearest to where it was gathered.
    // (the owner's check of technology bonuses, 2026-10-21) Banking raises the profit at markets (it targeted the castle
    // and did nothing) and Gunpowder raises the cannon towers' attack: the ruleset hash only.
    // (crafting on the command card, 2026-10-21) spell books cost four times more and take twice as long to craft (1.05),
    // two new combat settings: the ruleset hash only.
    // (ADR 0184) the spies' disguise store joins the hashed layout (version 9); behaviour unchanged.
    // (the help, 2026-10-21) Landing Tactics gives ships 50 % more room, not 4 places: the ruleset hash only.
    // (the owner's answers, 2026-10-21) every storage takes gold and stone; spies copy no machine, ship or magic
    // creature: the ruleset hash only.
    // (MAG-021, 2026-10-21) the spy's disguise meter fills in gameplay.disguiseMasteryMs: the ruleset hash only.
    // (batch J, 2026-10-21) the meter gains a point every 1.25 s as measured (disguiseMasteryMs 125000): the ruleset hash only.
    // (batch J, 2026-10-21) repairs as measured: blows of max / (4 x TIME) every 1.25 s, every repairer at full strength,
    // free: the scenario's damaged houses are repaired more slowly, so the stream and the final hash change too.
    // (ADR 0184 amendment 6, 2026-10-22) the unmasking reach of a taken-over unit, two settings: the ruleset hash only.
    // (2026-10-22) one peasant per field (workers 1), catapult stones hurt their own side: the ruleset hash only.
    // (ITEM-016, 2026-10-22) items have looks and craft variants, power items are weapons: the ruleset hash.
    // (WORLD-015, 2026-10-22) units get the original's body (14 px from the 32 px object box; terrain footprints stay):
    // the ruleset, the stream and the final hash.
    // (ADR 0191 amendment, 2026-10-22) Supply Storage and Seafood raise the population limit, Ore Flotation halves what mines lose: the ruleset hash only.
    // (ADR 0192, 2026-10-22) gates are 7 x 1 or 1 x 6 with a door of three tiles, bridges turn into their own cells: the bot's gate and bridge change, so the ruleset, the stream and the final hash.
    private const ulong GoldenRulesetHash = 0xF786A7ED2EA081CFUL;
    // (ORD-005, 2026-10-22) the waypoint store joins the hashed layout (version 10): the hashes only.
    // (WAR-014, 2026-10-22) the footman's and the archer's blows land at the action row of their attack animations (60 %
    // and 78.5 % of the swing, not 50 %): the ruleset, the stream and the final hash change.
    // (ITEM-016, 2026-10-22) items keep their own spell, effect and look (layout 11): the stream and the final hash.
    // (ADR 0189, 2026-10-22) queued orders gain a kind and a target, explorers an area (hashed layout 12): the hashes only.
    // (ADR 0190, 2026-10-22) rules from ReTzar's reading of the original: gathered crusaders in the kingdom state, loads raised by the gathering technologies, ship fire rates: the ruleset and the hashes.
    // (ADR 0191, 2026-10-22) technology effects as the original computes them (whole points, cost and time cuts): the ruleset and the hashes.
    private const ulong GoldenHashStream = 0x5160323D05FC2BACUL;
    private const ulong GoldenFinalFullHash = 0xF9F7194383A108C8UL;

    private const int Ticks = 1600;

    private static readonly GameplayScenarioOptions Options = new()
    {
        MapSize = 128,
        Players = 4,
        WorkersPerPlayer = 20,
        SoldiersPerPlayer = 120,
        TreesPerPlayer = 120,
        Seed = 0x5EED_6A3EUL,
        Taverns = true,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Gameplay_scenario_produces_the_golden_hashes(int workers)
    {
        Ruleset ruleset = SyntheticLegacyContent.CompileRuleset();
        using var scenario = new GameplayScenario(ruleset, Options, workers);
        var stream = new StateHasher();
        for (int t = 0; t < Ticks; t++)
        {
            TickResult result = scenario.RunTick();
            stream.Add(result.StateHash).Add(result.FullHash ?? 0UL);
        }

        ulong final = scenario.Simulation.ComputeFullHash();
        output.WriteLine($"workers={workers} runtime={Environment.Version} ruleset={ruleset.ContentHash:X16} stream={stream.GetCurrentHash():X16} final={final:X16} {scenario.Describe()}");
        Assert.True(scenario.Delivered > 0 && scenario.Kills > 0 && scenario.Trained > 0 && scenario.Built > 0 && scenario.Researched > 0, scenario.Describe());
        Assert.True(scenario.M3Events is { Captured: > 0, Fires: > 0, Repaired: > 0 } && scenario.Simulation.World.Gates.Count > 0, scenario.Describe());
        Assert.True(scenario.WorldEvents is { Claimed: > 0, Sales: > 0, Tributes: > 0, Diplomacy: > 0 } && scenario.Hired > 0, scenario.Describe());
        Assert.True(scenario.CombatMagicEvents is { Spells: > 0, Projectiles: > 0, Boarded: > 0, Picked: > 0, Levels: > 0 }, scenario.Describe());
        Assert.Equal(GoldenRulesetHash, ruleset.ContentHash);
        Assert.Equal(GoldenHashStream, stream.GetCurrentHash());
        Assert.Equal(GoldenFinalFullHash, final);
    }
}
