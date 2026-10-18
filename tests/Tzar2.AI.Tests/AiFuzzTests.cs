using System;
using System.Collections.Generic;
using System.Text;
using FsCheck.Xunit;
using Tzar2.AI.Legacy.Loading;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Legacy.Syntax;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Scenarios;
using Xunit;

namespace Tzar2.AI.Tests;

/// <summary>
/// User and mod scripts are untrusted input: whatever the text, the toolchain reports diagnostics and the interpreter
/// runs what is left; nothing throws, nothing recurses without bound, and the work is linear (TDD §22.1, §22.6).
/// </summary>
public class AiFuzzTests
{
    private static readonly string[] Vocabulary =
    [
        "rule", "critical", "end", "when", "have", "trigger", "forget", "say", "include", "not", "enemy", "this", "random",
        "time", "difficulty", "civ", "extern", "units", "castle", "barracks", "peon", "peons", "footman", "house", "gold",
        "wood", "food", "stone", "te_fields", "convert", "needs_locked", "resign", "easy", "hard", "european", "Flag",
        "X1", "{", "}", ",", "%", "!", "0", "1", "15", "100", "99999", "\"hi\"", "\"x.ai\"", "\"@gold($aik) >= 1\"",
        "\"(1+\"", "\n", " ", ";c\n", "\t",
    ];

    [Property(MaxTest = 400)]
    public void Random_text_never_crashes(string? text)
    {
        AiProgram program = AiCompiler.CompileText("r.ai", text ?? string.Empty);
        AiBinding.Bind(program, Model, "euro");
    }

    [Property(MaxTest = 300)]
    public void Token_soup_compiles_binds_and_runs(int seed)
    {
        var random = new Random(seed);
        var text = new StringBuilder();
        int n = random.Next(200);
        for (int i = 0; i < n; i++)
        {
            text.Append(Vocabulary[random.Next(Vocabulary.Length)]).Append(random.Next(3) == 0 ? "\n" : " ");
        }

        AiProgram program = AiCompiler.CompileText("soup.ai", text.ToString(), new MemoryAiScriptSource().Add("x.ai", text.ToString()));
        RunBriefly(program, ticks: 30);
    }

    [Property(MaxTest = 200)]
    public void Mutated_scripts_compile_bind_and_run(int seed)
    {
        var random = new Random(seed);
        string mutated = Mutate(AiFixtures.SkirmishText, random);
        AiProgram program = AiCompiler.CompileText("skirmish.ais", mutated);
        RunBriefly(program, ticks: 30);
    }

    [Fact]
    public void Deep_expressions_and_long_inputs_stay_bounded()
    {
        string deep = "rule when extern \"" + new string('(', 5000) + "1" + new string(')', 5000) + "\" end";
        Assert.Contains(AiParser.Parse("d.ai", deep).Diagnostics, d => d.Code == AiDiagnosticCode.InvalidExpression);
        string bangs = "rule when extern \"" + new string('!', 5000) + "1\" end";
        Assert.Contains(AiParser.Parse("d.ai", bangs).Diagnostics, d => d.Code == AiDiagnosticCode.InvalidExpression);

        var big = new StringBuilder();
        for (int i = 0; i < 15_000; i++)
        {
            big.Append("rule when castle, gold ").Append(i).Append(" have ").Append(i % 7).Append(" house trigger F").Append(i % 13).Append(" end\n");
        }

        AiProgram program = AiCompiler.CompileText("big.ai", big.ToString());
        Assert.Empty(program.Diagnostics);
        Assert.Equal(15_000, program.Rules.Length);
    }

    [Fact]
    public void Exponential_include_graphs_are_cut_off()
    {
        // Each file includes the next one ten times: 10^15 expansions without a limit.
        var source = new MemoryAiScriptSource();
        for (int i = 0; i < 15; i++)
        {
            var text = new StringBuilder("rule have house end\n");
            for (int k = 0; k < 10; k++)
            {
                text.Append("include \"f").Append(i + 1).Append(".ai\"\n");
            }

            source.Add($"f{i}.ai", text.ToString());
        }

        AiProgram program = AiCompiler.Compile("f0.ai", source);
        Assert.Contains(program.Diagnostics, d => d.Code == AiDiagnosticCode.ProgramTooLarge);
        Assert.True(program.Rules.Length <= AiParseLimits.Default.MaxIncludes + 1);
    }

    private static AiRulesetModel Model { get; } = new(AiFixtures.Classic);

    private static readonly Lazy<MapData> SmallMap = new(() =>
        AiSkirmishMapForTests(AiFixtures.Classic));

    private static MapData AiSkirmishMapForTests(Ruleset ruleset)
        => Scenarios.AiSkirmishScenario.CreateMap(ruleset, new Scenarios.AiSkirmishOptions { Players = 2, MapSize = 64, TreesPerPlayer = 20 });

    private static void RunBriefly(AiProgram program, int ticks)
    {
        using var simulation = new Simulation(AiFixtures.Classic, SmallMap.Value, new SimulationConfig { PlayerCount = 2, MatchSeed = 7 });
        LegacyAiSystem.Attach(simulation, [new AiPlayerSettings { Slot = 0, Program = program, Civilization = "euro" }], new AiOptions { InstructionBudgetPerTick = 64, ExecutorIntervalTicks = 3 });
        var none = new CommandList();
        for (int t = 0; t < ticks; t++)
        {
            simulation.Tick(none);
        }
    }

    private static string Mutate(string seed, Random random)
    {
        var text = new StringBuilder(seed);
        int edits = 1 + random.Next(12);
        for (int i = 0; i < edits && text.Length > 0; i++)
        {
            int at = random.Next(text.Length);
            switch (random.Next(4))
            {
                case 0: text.Remove(at, Math.Min(text.Length - at, 1 + random.Next(20))); break;
                case 1: text.Insert(at, Vocabulary[random.Next(Vocabulary.Length)]); break;
                case 2: text[at] = (char)random.Next(0x20, 0x7F); break;
                default:
                    int start = random.Next(text.Length);
                    text.Insert(at, text.ToString(start, Math.Min(text.Length - start, random.Next(40))));
                    break;
            }
        }

        return text.ToString();
    }
}
