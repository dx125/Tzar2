using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Core.Serialization;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim;
using Tzar2.Sim.Events;
using Xunit;
using Xunit.Abstractions;

using Tzar2.Tests.Scenario;

namespace Tzar2.Scripting.Tests;

/// <summary>
/// Trigger state in snapshots (REQ-SIM-13, ADR 0130): save → load → continue gives the uninterrupted hash stream, the
/// state block is range-checked and corruption is rejected, and the synthetic mission is pinned by a golden hash.
/// </summary>
public class TriggerSnapshotTests(ITestOutputHelper output)
{
    private const int MissionTicks = 3600;

    private static (TriggerDriver Driver, int Hero, int Castle) Mission(int workers = 1)
    {
        (ScenarioBuilder b, int hero, int castle) = SyntheticMission.Build();
        return (new TriggerDriver(b.Map(), b.Scenario(), players: 3, workers: workers), hero, castle);
    }

    private static List<ulong> Run(TriggerDriver d, int hero, int castle, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            SyntheticMission.Play(d, hero, castle);
            d.Step();
        }

        return d.Hashes;
    }

    [Fact]
    public void The_synthetic_mission_plays_to_victory_through_its_triggers()
    {
        (TriggerDriver d, int hero, int castle) = Mission();
        using (d)
        {
            Run(d, hero, castle, MissionTicks);
            output.WriteLine($"final {d.Hashes[^1]:X16} passes {d.State.Passes} variables {string.Join(",", d.State.Variables)} quests {string.Join(",", d.State.Quests)} castle {(d.World.Entities.IsAlive(d.Object(castle)) ? d.World.Healths.Get(d.Object(castle)).Current : -1)} army {d.World.Identities.Entities.ToArray().Count(e => d.World.Identities.Get(e).Owner == 0)} enemy {d.World.Identities.Entities.ToArray().Count(e => d.World.Identities.Get(e).Owner == 1)} escort {d.World.Entities.IsAlive(d.Object(14))} maps {string.Join(",", d.Of(SimEventKind.MapRequested).Select(e => e.Y + ":" + e.Arg))} flags {d.World.Players[0].Flags}");
            Assert.True(d.Count(SimEventKind.PhraseSaid) >= 2);
            Assert.Equal(2, d.Variable("road"));
            Assert.Equal(3, d.Variable("waves"));
            Assert.False(d.State.Hidden[14]);
            Assert.True(d.Count(SimEventKind.QuestRemoved) == 1);
            Assert.True((d.World.Players[0].Flags & PlayerFlags.Victorious) != 0, "the castle should have fallen");
            Assert.Equal(1, d.Of(SimEventKind.MapRequested).Single().Y);
        }
    }

    [Fact]
    public void Save_load_continue_matches_the_uninterrupted_run_at_any_point()
    {
        (TriggerDriver reference, int hero, int castle) = Mission();
        List<ulong> expected;
        using (reference)
        {
            expected = [.. Run(reference, hero, castle, 1600)];
        }

        foreach (int at in new[] { 1, 21, 47, 333, 977 })
        {
            (TriggerDriver d, _, _) = Mission();
            using (d)
            {
                Run(d, hero, castle, at);
                using TriggerDriver restored = d.Restore(workers: at % 2 == 0 ? 1 : 4);
                Assert.Equal(d.Sim.ComputeFullHash(), restored.Sim.ComputeFullHash());
                Run(restored, hero, castle, 1600 - at);
                Assert.Equal(expected.Skip(at), restored.Hashes);
            }
        }
    }

    [Fact]
    public void Worker_count_does_not_change_the_hash_stream()
    {
        (TriggerDriver one, int hero, int castle) = Mission(1);
        (TriggerDriver four, _, _) = Mission(4);
        using (one)
        using (four)
        {
            Assert.Equal(Run(one, hero, castle, 1200), Run(four, hero, castle, 1200));
        }
    }

    /// <summary>
    /// Golden: the hash after the whole synthetic mission. It changes when trigger semantics or the sim change; update
    /// it deliberately (printed by the victory test) and note why in the PR.
    /// </summary>
    [Fact]
    public void Golden_hash_of_the_synthetic_mission()
    {
        (TriggerDriver d, int hero, int castle) = Mission();
        using (d)
        {
            Run(d, hero, castle, MissionTicks);
            // Naval (ADRs 0140-0145): synthetic hulk and xebec shift type ids, two naval stores join the hashed layout; then
            // (ADR 0146) a unit whose goal is taken by a standing unit settles next to it sooner; then (ADR 0171) units walk
            // at SPEED x 10 px/s instead of 12; then (ADR 0172) measured classes walk at their own speed, whatever SPEED;
            // then (ADR 0178) a unit on a move-advance that is hit fights back, and a unit may chase what can hit it; then
            // (owner play-test 2026-10-20) a unit still walking to a structure reaches it only next to it; then (ADR 0180)
            // queue entries and the workers' idle timer change the hashed layout, and the third play-test's rules the match;
            // then (ADR 0184) the spies' disguise store joins the hashed layout; then (ORD-005) the waypoint store; then
            // (WAR-014) footmen and archers strike at the action row of their attack animations; then (ITEM-016) items keep
            // their own spell, effect and look (layout 11); then (WORLD-015) units get the original's body; then (ADR 0189)
            // queued orders gain kinds and explorers an area (layout 12); then (ADR 0190) the kingdoms keep gathered
            // crusaders, and the gathering technologies raise loads; then (ADR 0211) civilizations in the ruleset's order,
            // which changes the kingdoms' civilization indices; then (ADR 0220) scripted orders ignore the owner's state,
            // units aboard are where their holder is, a holder's $cargo is what it holds, and movement slides past corners
            // and treats allies as friends.
            // crusaders, and the gathering technologies raise loads; then (ADR 0250) a synthetic Messiah shifts type ids
            // and status effects grow (layout 13).
            // then: (ADR 0255, 2026-10-22) souls are bound to a unit (item state grows, hashed layout 14), the Messiah takes no magic damage.
            // then: (ADR 0256, 2026-10-22) cows grow meat (a new store, hashed layout 15) that falls as food items peasants carry home.
            // then: (ADR 0259, 2026-10-22) Jewelry: the markets' offers (a new store, hashed layout 16) and the original's spell numbering for map books.
            // then: (ADR 0265, 2026-10-23) a spell book keeps its own pool and may be endless (item state grows, hashed layout 17).
            // then: (ADR 0266, 2026-10-23) no spell cooldowns, spells learned from books (a new store, hashed layout 18).
            // then: (ADR 0267, 2026-10-23) mana regrows 2 a second and a pool below a cost is raised to it (hashed layout 19).
            // then: (WORLD-019, 2026-10-24) soldiers attacking a building take tiles of their own around it.
            // then: (WAR-015/016/017, 2026-10-26) the original's choice of targets, the next target at once, calls for help.
            // then: (ADR 0270, 2026-10-27) land units step cell by cell, one to a tile (hashed layout 20).
            Assert.Equal("90F68F3FDFEE7173", d.Hashes[^1].ToString("X16"));
        }
    }

    [Fact]
    public void A_restore_with_another_scenario_or_other_settings_is_refused()
    {
        (TriggerDriver d, int hero, int castle) = Mission();
        using (d)
        {
            Run(d, hero, castle, 30);
            byte[] snapshot = d.Sim.CaptureSnapshot();
            (ScenarioBuilder other, _, _) = SyntheticMission.Build();
            other.Rule("1", other.Action(new Content.Compiled.Scenario.KeepRuleAction("extra")));
            using (Simulation sim = Simulation.Restore(snapshot, ScenarioBuilder.Classic, d.Map))
            {
                Assert.Throws<InvalidDataException>(() => LegacyTriggerSystem.Attach(sim, other.Scenario()));
                Assert.Null(sim.World.Scenario);
            }

            using (Simulation sim = Simulation.Restore(snapshot, ScenarioBuilder.Classic, d.Map))
            {
                Assert.Throws<InvalidDataException>(() => LegacyTriggerSystem.Attach(sim, d.ScenarioData, TriggerOptions.Default with { IntervalMs = 500 }));
            }
        }
    }

    [Fact]
    public void Corrupted_trigger_state_is_rejected_with_invalid_data()
    {
        (TriggerDriver d, int hero, int castle) = Mission();
        using (d)
        {
            Run(d, hero, castle, 437); // mid-mission: timers, quests, a finished conversation, locks
            var writer = new BinaryBufferWriter();
            d.State.WriteTo(writer);
            byte[] good = writer.ToArray();
            TriggerShape shape = d.State.Shape;
            TriggerState round = TriggerState.ReadFrom(good, shape);
            var again = new BinaryBufferWriter();
            round.WriteTo(again);
            Assert.Equal(good, again.ToArray());

            // Every truncation fails.
            for (int length = 0; length < good.Length; length++)
            {
                Assert.Throws<InvalidDataException>(() => TriggerState.ReadFrom(good.AsSpan(0, length), shape));
            }

            // Trailing bytes fail.
            Assert.Throws<InvalidDataException>(() => TriggerState.ReadFrom([.. good, 0], shape));

            // Any single-byte change either fails cleanly or reads back as a state that writes the same bytes.
            var rng = new Random(5);
            int accepted = 0;
            for (int i = 0; i < good.Length; i++)
            {
                byte[] bad = (byte[])good.Clone();
                bad[i] ^= (byte)(1 << rng.Next(8));
                try
                {
                    TriggerState s = TriggerState.ReadFrom(bad, shape);
                    var w = new BinaryBufferWriter();
                    s.WriteTo(w);
                    Assert.Equal(bad, w.ToArray());
                    accepted++;
                }
                catch (InvalidDataException)
                {
                }
            }

            output.WriteLine($"{good.Length} bytes, {accepted} single-bit changes still valid states");

            // A state for another scenario shape fails.
            Assert.Throws<InvalidDataException>(() => TriggerState.ReadFrom(good, shape with { Variables = shape.Variables + 1 }));
        }
    }

    [Fact]
    public void Out_of_range_values_are_rejected()
    {
        (TriggerDriver d, _, _) = Mission();
        using (d)
        {
            d.Step();
            TriggerShape shape = d.State.Shape;
            static byte[] Write(TriggerState s)
            {
                var w = new BinaryBufferWriter();
                s.WriteTo(w);
                return w.ToArray();
            }

            void Rejects(Action<TriggerState> corrupt)
            {
                TriggerState s = TriggerState.ReadFrom(Write(d.State), shape);
                corrupt(s);
                Assert.Throws<InvalidDataException>(() => TriggerState.ReadFrom(Write(s), shape));
            }

            Rejects(s => s.Quests.Add(shape.Actions + 3));
            Rejects(s => s.Quests.Add(s.Quests[0]));
            Rejects(s => s.Locked.Insert(0, 0));
            Rejects(s => s.PassRule = shape.Rules + 1);
            Rejects(s => s.Conversation = shape.Conversations.Length);
            Rejects(s => s.PendingReply = 40);
            Rejects(s => s.MapRequestAction = shape.Actions);
            Rejects(s => s.Timers[0] = new TimerState { Started = true, ValueMs = -5 });
            Rejects(s => s.Timers[0] = new TimerState { Running = true });
            Rejects(s => s.PlayerStatus[0] = 7);
            Rejects(s => s.ClockMs = -1);
            Rejects(s => s.Hidden[0] = true); // object 0 has an entity
            Rejects(s => s.EventMemory[3][0] = -2);
        }
    }
}
