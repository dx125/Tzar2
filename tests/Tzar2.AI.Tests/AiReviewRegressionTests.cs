using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Tzar2.AI.Legacy.Loading;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Legacy.Syntax;
using Tzar2.AI.Runtime;
using Tzar2.AI.Scenarios;
using Tzar2.Core.Serialization;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.AI.Tests;

/// <summary>Regressions of the PR #20 review: hostile scripts cannot exceed the budgets; state and attach are strict.</summary>
public class AiReviewRegressionTests(ITestOutputHelper output)
{
    private static string Repeat(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    private static AiSkirmishScenario Scenario(AiProgram p, AiOptions? ai = null, int players = 2)
        => new(AiFixtures.Classic, p, new AiSkirmishOptions { Players = players, MapSize = 96, TreesPerPlayer = 40, Ai = ai ?? AiOptions.Default });

    [Fact]
    public void Say_and_forget_count_toward_the_parts_of_a_rule()
    {
        AiProgram p = AiCompiler.CompileText("t.ais", "rule " + Repeat("say \"a\" ", 5000) + Repeat("forget THIS ", 5000) + "end");
        Assert.True(p.Rules[0].ActionCount <= AiParseLimits.Default.MaxClausesPerRule);
        Assert.Contains(p.Diagnostics, d => d.Code == AiDiagnosticCode.RuleTooLarge);
    }

    [Fact]
    public void Forget_fan_out_is_capped_and_charged()
    {
        var src = new MemoryAiScriptSource().Add("m.ai", Repeat("rule x end\n", 60000));
        AiProgram p = AiCompiler.CompileText("t.ais", "include \"m.ai\"\nrule " + Repeat("forget x ", 60) + "end", src);
        Assert.Equal(AiParseLimits.Default.MaxRulesPerName, p.RulesByName[p.RuleNames.IndexOf("x")].Length);
        Assert.Contains(p.Diagnostics, d => d.Code == AiDiagnosticCode.ProgramTooLarge);

        // The rule of 60 forgets costs 1 + 60 × (1 + 64) instructions: a budget of 64 per tick spreads the pass out.
        using AiSkirmishScenario s = Scenario(p, new AiOptions { InstructionBudgetPerTick = 64, ThinkIntervalTicks = 1 }, players: 1);
        for (int t = 0; t < 200; t++)
        {
            s.RunTick();
        }

        Assert.True(s.Ai.Players[0].PassCount < 200);
    }

    [Fact]
    public void Extern_expressions_are_bounded_at_compile_time_and_correct_below_the_bound()
    {
        AiProgram tooLong = AiCompiler.CompileText("t.ais", $"rule when extern \"{string.Join("+", Enumerable.Repeat("1", 300))} == 300\" trigger Ok end");
        Assert.Contains(tooLong.Diagnostics, d => d.Code == AiDiagnosticCode.InvalidExpression);
        Assert.Null(tooLong.Externs[0]);

        AiProgram fine = AiCompiler.CompileText("t.ais", $"rule when extern \"{string.Join("+", Enumerable.Repeat("1", 100))} == 100\" trigger Ok end");
        Assert.Empty(fine.Diagnostics);
        Assert.Equal(1 + (201 / 8), fine.ExternCosts[0]);
        using AiSkirmishScenario s = Scenario(fine, new AiOptions { ThinkIntervalTicks = 1 }, players: 1);
        s.RunTick();
        s.RunTick();
        Assert.True(s.Ai.Players[0].IsFlagSet(fine.Flags.IndexOf("ok")));
    }

    [Fact]
    public void Expanding_includes_is_bounded_in_items()
    {
        var src = new MemoryAiScriptSource().Add("a.ai", Repeat("{}", 200_000));
        var sw = Stopwatch.StartNew();
        AiProgram p = AiCompiler.CompileText("t.ais", Repeat("include \"a.ai\"\n", 400), src);
        output.WriteLine($"{sw.ElapsedMilliseconds} ms");
        Assert.Contains(p.Diagnostics, d => d.Code == AiDiagnosticCode.ProgramTooLarge);
    }

    [Fact]
    public void Past_the_rule_limit_no_block_end_is_rewritten()
    {
        AiProgram p = AiCompiler.CompileText("t.ais", "rule have house end\n{ rule have farm end rule have farm end rule have farm end }\nrule have house end", limits: new AiParseLimits { MaxRules = 2 });
        Assert.Equal(2, p.Rules.Length);
        Assert.All(p.Rules, r => Assert.InRange(r.BlockEnd, 0, p.Rules.Length));
    }

    [Theory]
    [InlineData("../secret.ai")]
    [InlineData("sub/../../x.ai")]
    [InlineData("c:/x.ai")]
    public void Includes_cannot_leave_the_ai_folder(string path)
    {
        var src = new MemoryAiScriptSource().Add("secret.ai", "rule have house end");
        AiProgram p = AiCompiler.CompileText("t.ais", $"include \"{path}\"", src);
        Assert.Empty(p.Rules);
        Assert.Contains(p.Diagnostics, d => d.Code == AiDiagnosticCode.IncludeNotFound && d.Severity == AiDiagnosticSeverity.Error);
        Assert.Equal("a/b.ai", AiScriptNames.Normalize("./A/./B.AI"));
    }

    [Fact]
    public void A_rejected_attach_leaves_the_simulation_untouched()
    {
        using AiSkirmishScenario s = Scenario(AiFixtures.Skirmish);
        for (int t = 0; t < 50; t++)
        {
            s.RunTick();
        }

        byte[] snapshot = s.Simulation.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, AiFixtures.Classic, s.Map);
        var players = AiSkirmishScenario.PlayerSettings(AiFixtures.Skirmish, s.Options).Select(x => x.Slot == 1 ? x with { Difficulty = AiDifficulty.Easy } : x).ToList();
        Assert.Throws<InvalidDataException>(() => LegacyAiSystem.Attach(restored, players, s.Options.Ai));
        Assert.True(restored.World.Extensions.HasPending(LegacyAiSystem.ExtensionKey));
        restored.Systems.Seal();
        Assert.DoesNotContain(restored.Systems.Ordered, x => x is LegacyAiSystem);
    }

    [Fact]
    public void A_restore_with_other_ai_options_is_rejected()
    {
        using AiSkirmishScenario s = Scenario(AiFixtures.Skirmish);
        for (int t = 0; t < 30; t++)
        {
            s.RunTick();
        }

        byte[] snapshot = s.Simulation.CaptureSnapshot();
        foreach (AiOptions other in new[]
        {
            new AiOptions { ThinkIntervalTicks = 7 },
            new AiOptions { MaxCommandsPerRun = 3 },
            new AiOptions { Military = new Executors.MilitaryOptions { FirstWaveSize = 11 } },
            new AiOptions { Economy = new Executors.EconomyOptions { SurplusStock = 900 } },
        })
        {
            using Simulation restored = Simulation.Restore(snapshot, AiFixtures.Classic, s.Map);
            Assert.Throws<InvalidDataException>(() => LegacyAiSystem.Attach(restored, AiSkirmishScenario.PlayerSettings(AiFixtures.Skirmish, s.Options), other));
        }
    }

    [Fact]
    public void Bits_beyond_the_flag_count_are_rejected()
    {
        using AiSkirmishScenario s = Scenario(AiFixtures.Compile("rule trigger A end"));
        var writer = new BinaryBufferWriter();
        s.Ai.WriteTo(writer);
        byte[] state = writer.ToArray();

        // Header: version (1), options fingerprint (8), player count (1); player: version, slot, program hash (8),
        // difficulty, civilization string (length + 4), flags byte, then the flag bit set (length 1, one word).
        int flagsWord = 1 + 8 + 1 + 1 + 1 + 8 + 1 + 5 + 1 + 1;
        Assert.Equal(1, state[flagsWord - 1]);
        state[flagsWord + 7] = 0x80; // bit 63, beyond the single flag
        Assert.Throws<InvalidDataException>(() => s.Ai.ReadFrom(state));
    }

    [Fact]
    public void A_goal_repeated_by_many_rules_is_worked_on_like_one()
    {
        // Duplicates are skipped per class and run, so 1,000 copies of a goal that can never be placed cost what one
        // costs: the commands are identical. (Passes complete in one tick here, so both runs see the same goals.)
        var options = new AiOptions { InstructionBudgetPerTick = 1_000_000 };
        string goal = "rule have 99 houses end\nrule have 30 peons end\nrule have 500 wood end\n";
        AiProgram one = AiFixtures.Compile(goal);
        AiProgram many = AiFixtures.Compile(Repeat(goal, 1000));
        ulong Commands(AiProgram p)
        {
            using var s = new AiSkirmishScenario(AiFixtures.Classic, p, new AiSkirmishOptions { Players = 1, MapSize = 40, TreesPerPlayer = 400, Ai = options });
            var hash = new Tzar2.Core.Hashing.StateHasher();
            var wire = new BinaryBufferWriter();
            for (int t = 0; t < 1500; t++)
            {
                s.RunTick();
                wire.Clear();
                CommandCodec.WriteBatch(wire, s.Ai.LastCommands);
                hash.Add(wire.WrittenSpan);
            }

            Assert.True(s.Ai.Issued(0) > 10);
            return hash.GetCurrentHash();
        }

        Assert.Equal(Commands(one), Commands(many));
    }
}
