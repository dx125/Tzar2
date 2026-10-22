using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.AI.Legacy.Loading;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Legacy.Syntax;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Rng;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Map;
using Tzar2.Sim.Scenarios;
using Xunit;

namespace Tzar2.AI.Tests;

/// <summary>
/// The script semantics of the interpreter (Appendix D.4, ADR 0050), run inside a real simulation with the executors
/// switched off (map settings "Economy AI" and "War AI" off), so only the script acts.
/// </summary>
public class AiInterpreterTests
{
    private const int Tile = MapState.UnitsPerTile;

    private sealed class Run : IDisposable
    {
        public Run(string script, MapData? map = null, AiDifficulty difficulty = AiDifficulty.Normal, string civ = "euro", AiOptions? options = null, IAiScriptSource? includes = null, int populationLimit = 200)
        {
            Program = AiCompiler.CompileText("t.ai", script, includes);
            Assert.False(Program.HasErrors, string.Join("\n", Program.Diagnostics));
            Simulation = new Simulation(AiFixtures.Classic, map ?? DefaultMap(), new SimulationConfig { PlayerCount = 2, MatchSeed = 11, StartingResources = 1, PopulationLimit = populationLimit });
            Ai = LegacyAiSystem.Attach(
                Simulation,
                [new AiPlayerSettings { Slot = 0, Program = Program, Civilization = civ, Difficulty = difficulty, EconomyAi = false, WarAi = false }],
                options ?? new AiOptions { ThinkIntervalTicks = 1 });
        }

        public AiProgram Program { get; }

        public Simulation Simulation { get; }

        public LegacyAiSystem Ai { get; }

        public AiPlayerState State => Ai.Players[0];

        public List<string> Chat { get; } = [];

        public Run Ticks(int n)
        {
            var none = new CommandList();
            for (int i = 0; i < n; i++)
            {
                Simulation.Tick(none);
                Chat.AddRange(Ai.LastChat.Select(c => c.Text));
            }

            return this;
        }

        public bool Flag(string name)
        {
            int index = Program.Flags.IndexOf(name.ToLowerInvariant());
            Assert.True(index >= 0, $"no flag {name}");
            return State.IsFlagSet(index);
        }

        public void Dispose() => Simulation.Dispose();
    }

    // Player 0: castle, barracks-free, 4 peons and a footman; player 1: a footman.
    private static MapData DefaultMap(bool barracks = false)
    {
        var objects = new List<MapObject>
        {
            MapObject.AtTile("euro.castle", 0, 2, 2),
            Unit("euro.peon", 0, 10, 2), Unit("euro.peon", 0, 10, 3), Unit("euro.peon", 0, 10, 4), Unit("euro.peon", 0, 10, 5),
            Unit("euro.footman", 0, 11, 6),
            Unit("euro.footman", 1, 30, 12),
        };
        if (barracks)
        {
            objects.Add(MapObject.AtTile("euro.barrack", 0, 14, 2));
        }

        return ScenarioMaps.WithObjects(ScenarioMaps.Build("t", 40, 20, new byte[40 * 20], []), objects);

        static MapObject Unit(string key, int owner, int tx, int ty) => new(key, (byte)owner, (tx * Tile) + (Tile / 2), (ty * Tile) + (Tile / 2));
    }

    [Fact]
    public void Flag_changes_are_seen_by_later_rules_of_the_same_pass()
    {
        // The reset-then-set idiom of the corpus (a flag is cleared at the top and set again lower down).
        using Run run = new Run("rule trigger !HasWorker, !Chain end\nrule when peon 2 trigger HasWorker end\nrule when trigger HasWorker trigger Chain end\nrule when not trigger HasWorker trigger Never end").Ticks(3);
        Assert.True(run.Flag("HasWorker"));
        Assert.True(run.Flag("chain")); // flags are case-insensitive
        Assert.False(run.Flag("Never"));
    }

    [Fact]
    public void A_failing_critical_rule_skips_the_rest_of_its_block_only()
    {
        const string Script = """
            { critical rule have 1 barracks end
              rule trigger Inside end
              { rule trigger Nested end }
            }
            rule trigger After end
            { critical rule when castle 1 end
              { critical rule when not castle 1 end
                rule trigger InnerGated end
              }
              rule trigger OuterOpen end
            }
            """;
        using (Run run = new Run(Script).Ticks(2))
        {
            Assert.False(run.Flag("Inside"));
            Assert.False(run.Flag("Nested"));
            Assert.True(run.Flag("After"));
            Assert.False(run.Flag("InnerGated"));
            Assert.True(run.Flag("OuterOpen"));
        }

        using (Run run = new Run(Script, DefaultMap(barracks: true)).Ticks(2))
        {
            Assert.True(run.Flag("Inside"));
            Assert.True(run.Flag("Nested"));
        }
    }

    [Fact]
    public void A_top_level_critical_rule_of_an_include_gates_the_including_block()
    {
        var includes = new MemoryAiScriptSource().Add("M.AI", "critical rule when not castle 1 end\nrule trigger Gated end");
        using Run run = new Run("{ critical rule when castle 1 end\n  include \"m.ai\"\n  rule trigger SameBlock end\n}\nrule trigger Outside end", includes: includes).Ticks(2);
        Assert.False(run.Flag("Gated"));
        Assert.False(run.Flag("SameBlock")); // textual inclusion: the include's critical rule gates the rest of the block
        Assert.True(run.Flag("Outside"));
    }

    [Fact]
    public void Forget_disables_named_rules_and_this()
    {
        using Run run = new Run("rule ST when castle trigger Seen end\nrule when trigger Seen forget ST trigger !Seen end\nrule trigger Once forget THIS end\nrule trigger !Once end").Ticks(4);
        Assert.True(run.State.IsForgotten(0));
        Assert.True(run.State.IsForgotten(2));
        Assert.False(run.State.IsForgotten(3));
        Assert.False(run.Flag("Seen")); // ST no longer sets it once forgotten
        Assert.False(run.Flag("Once"));
    }

    [Fact]
    public void Say_speaks_when_its_rule_becomes_active()
    {
        using Run run = new Run("rule when castle say \"I have a castle\" end\nrule when not time 2000 say \"early\" end\nrule when time 2000 say \"late\" end").Ticks(80);
        Assert.Equal(["I have a castle", "early", "late"], run.Chat);
    }

    [Fact]
    public void Percentages_are_shares_of_the_population_limit_and_goals_round_up()
    {
        // Player 0 has 5 units (4 peasants, 1 footman) and a population limit of 20.
        using Run run = new Run("rule when peon 20% trigger P20 end\nrule when peon 21% trigger P21 end\nrule when units 5, not units 6 trigger Five end\nrule have 12% footman end", populationLimit: 20).Ticks(2);
        Assert.True(run.Flag("P20"));
        Assert.False(run.Flag("P21"));
        Assert.True(run.Flag("Five"));
        AiGoal goal = Assert.Single(run.State.Goals); // 12 % of 20 rounds up to 3 footmen
        Assert.Equal((AiGoalKind.Object, 12, true), (goal.Kind, goal.Number, goal.Percent));
        Assert.Equal(3, AiInterpreter.ObjectTarget(12, true, 20));
        Assert.Equal(0, AiInterpreter.ObjectTarget(40, true, 0));
        Assert.Equal(1, AiInterpreter.ObjectTarget(-1, false, 9));
    }

    [Fact]
    public void Met_goals_are_not_collected_and_unmet_ones_are_in_script_order()
    {
        using Run run = new Run("rule have castle end\nrule have 4 peons end\nrule have 6 peons end\nrule have 2 houses end\nrule have 1 gold end\nrule have 99999 wood end").Ticks(2);
        Assert.Equal(["peons", "houses", "wood"], run.State.Goals.Select(g => run.Program.Symbols[g.Symbol]));
    }

    [Fact]
    public void Needs_locked_saves_the_unmet_resources_for_the_goals_after_it()
    {
        using Run run = new Run("rule have 2 houses end\nrule have 99999 wood, 1 gold end\nrule have needs_locked end\nrule have 3 houses end").Ticks(2);
        int wood = AiFixtures.Classic.ResourceId("wood");
        AiGoal[] goals = [.. run.State.Goals];
        Assert.Equal(0, goals[0].LockedResources);
        Assert.Equal(1 << wood, goals[^1].LockedResources); // gold is stocked, so only wood is saved
    }

    [Fact]
    public void Difficulty_civilization_time_and_enemy_conditions()
    {
        const string Script = "rule when difficulty hard trigger H end\nrule when difficulty easy trigger E end\nrule when civ european trigger Eu end\nrule when civ asian trigger As end\nrule when time 1000 trigger T end\nrule when enemy footman, not enemy footman 2 trigger OneEnemy end\nrule when enemy peon trigger EnemyPeon end";
        using (Run run = new Run(Script, difficulty: AiDifficulty.Hard).Ticks(10))
        {
            Assert.True(run.Flag("H"));
            Assert.False(run.Flag("E"));
            Assert.True(run.Flag("Eu"));
            Assert.False(run.Flag("As"));
            Assert.False(run.Flag("T"));
            Assert.True(run.Flag("OneEnemy"));
            Assert.False(run.Flag("EnemyPeon"));
            run.Ticks(15);
            Assert.True(run.Flag("T"));
        }

        using (Run asian = new Run(Script, civ: "asian").Ticks(2))
        {
            Assert.True(asian.Flag("As"));
            Assert.False(asian.Flag("Eu"));
        }
    }

    [Fact]
    public void Extern_expressions_see_the_kingdom_and_the_stockpiles()
    {
        using Run run = new Run("rule when extern \"@wood($aik) >= 100\" trigger W end\nrule when extern \"@wood($aik) > 100000\" trigger Rich end\nrule when extern \"$aik == 1 && (7 / 0) == 0 && -2 * 3 == 0 - 6 && !0\" trigger Math end\nrule when extern \"@gold(9) == 0\" trigger NoSuchKingdom end").Ticks(2);
        Assert.True(run.Flag("W"));
        Assert.False(run.Flag("Rich"));
        Assert.True(run.Flag("Math"));
        Assert.True(run.Flag("NoSuchKingdom"));
    }

    [Fact]
    public void Random_draws_from_the_players_ai_stream_only()
    {
        const string Script = "rule when random 50 trigger A end\nrule when not trigger A, random 50% trigger B end\nrule when random 0 trigger Never end\nrule when random 100 trigger Always end";
        ulong[] flags = new ulong[2];
        for (int i = 0; i < 2; i++)
        {
            using Run run = new Run(Script).Ticks(1);
            flags[i] = (run.Flag("A") ? 1UL : 0) | (run.Flag("B") ? 2UL : 0);
            Assert.False(run.Flag("Never"));
            Assert.True(run.Flag("Always"));
            World world = run.Simulation.World;
            Assert.NotEqual(RngStreams.Derive(11, RngStreamKind.Ai, 0).State, world.Rng.Ai(0).State);
            Assert.Equal(RngStreams.Derive(11, RngStreamKind.Ai, 1).State, world.Rng.Ai(1).State);
            Assert.Equal(RngStreams.Derive(11, RngStreamKind.Combat, 0).State, world.Rng.Combat.State);
        }

        Assert.Equal(flags[0], flags[1]);
    }

    [Fact]
    public void A_pass_spans_ticks_when_the_budget_runs_out_and_publishes_goals_at_its_end()
    {
        string script = string.Concat(Enumerable.Range(0, 60).Select(i => $"rule when castle have {i + 10} peons end\n"));
        using Run run = new Run(script, options: new AiOptions { InstructionBudgetPerTick = 30, ThinkIntervalTicks = 1000 });
        run.Ticks(3);
        Assert.True(run.State.InPass);
        Assert.Equal(0, run.State.PassCount);
        Assert.Empty(run.State.Goals);
        run.Ticks(10);
        Assert.False(run.State.InPass);
        Assert.Equal(1, run.State.PassCount);
        Assert.Equal(60, run.State.Goals.Count);
    }

    [Fact]
    public void Unknown_names_count_zero_and_their_goals_are_ignored()
    {
        using Run run = new Run("rule when dragonz trigger D end\nrule when not dragonz trigger NoD end\nrule have 5 dragonz end\nrule when needs_locked trigger Misused end").Ticks(2);
        Assert.False(run.Flag("D"));
        Assert.True(run.Flag("NoD"));
        Assert.Empty(run.State.Goals);
        Assert.False(run.Flag("Misused"));
        Assert.Contains(run.Ai.Diagnostics, d => d.Code == AiDiagnosticCode.UnknownName && d.Message.Contains("dragonz", StringComparison.Ordinal));
        Assert.Contains(run.Ai.Diagnostics, d => d.Code == AiDiagnosticCode.MisusedName);
    }

    private sealed class FixedTechnologies(AiTechnologyState state) : IAiTechnologies
    {
        public AiTechnologyState StateOf(World world, byte player, string key, int technologyId) => state;
    }

    [Fact]
    public void Technologies_the_sim_does_not_model_count_as_researched()
    {
        const string Script = "rule when te_ploughing trigger T end\n{ critical rule have te_ploughing end\n  rule trigger After end\n}";
        using (Run run = new Run(Script).Ticks(2))
        {
            Assert.True(run.Flag("T"));
            Assert.True(run.Flag("After"));
            Assert.Empty(run.State.Goals);
        }

        using (Run run = new Run(Script, options: new AiOptions { ThinkIntervalTicks = 1, Technologies = new FixedTechnologies(AiTechnologyState.NotResearched) }).Ticks(2))
        {
            Assert.False(run.Flag("T"));
            Assert.False(run.Flag("After"));
            Assert.Equal(AiGoalKind.Technology, Assert.Single(run.State.Goals).Kind);
        }
    }

    [Fact]
    [Trait("Rule", "DIP-009")]
    public void A_resign_the_script_asks_for_is_carried_out()
    {
        using Run run = new Run("rule when not barracks have resign trigger Resigned end").Ticks(3);
        Assert.True(run.State.ResignRequested);
        Assert.True(run.Flag("Resigned"));
        PlayerFlags flags = run.Simulation.World.Players[0].Flags;
        Assert.True((flags & PlayerFlags.Resigned) != 0, "the kingdom resigned (RESIGN.AI, 1.09)");
        Assert.True((flags & PlayerFlags.Defeated) != 0);
    }

    [Fact]
    public void Executors_off_means_no_commands()
    {
        using Run run = new Run("rule have 9 peons, 3 houses end").Ticks(60);
        Assert.Equal(0, run.Ai.Issued(0));
    }
}
