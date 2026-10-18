using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.AI.Legacy.Loading;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Legacy.Syntax;
using Tzar2.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.AI.Tests;

/// <summary>
/// REQ-AI-01 against the user's own install (never in the repository, REQ-LEGAL-01): every original AI script parses
/// with zero errors and every strategy script compiles with its includes. Skipped without <c>TZAR2_ORIGIN</c>.
/// </summary>
public class OriginalCorpusTests(ITestOutputHelper output)
{
    private static string AiFolder => Path.Combine(OriginPaths.RequireOrigin(), "DATA", "AI");

    internal static MemoryAiScriptSource LoadCorpus(out List<string> names)
    {
        var source = new MemoryAiScriptSource();
        names = [];
        foreach (string path in Directory.EnumerateFiles(AiFolder).Order(StringComparer.Ordinal))
        {
            string ext = Path.GetExtension(path).ToUpperInvariant();
            if (ext is ".AI" or ".AIS" or ".BAK")
            {
                source.Add(Path.GetFileName(path), File.ReadAllText(path));
                names.Add(AiScriptNames.Normalize(Path.GetFileName(path)));
            }
        }

        return source;
    }

    [OriginFact]
    public void Every_original_script_parses_without_errors()
    {
        MemoryAiScriptSource source = LoadCorpus(out List<string> names);
        Assert.True(names.Count >= 37, $"only {names.Count} scripts found in {AiFolder}");
        int rules = 0;
        foreach (string name in names)
        {
            Assert.True(source.TryRead(name, out string? text));
            AiScriptSyntax syntax = AiParser.Parse(name, text);
            foreach (AiDiagnostic d in syntax.Diagnostics)
            {
                output.WriteLine(d.ToString());
            }

            Assert.DoesNotContain(syntax.Diagnostics, d => d.Severity == AiDiagnosticSeverity.Error);
            rules += CountRules(syntax.Items);
        }

        output.WriteLine($"{names.Count} files, {rules} rules parsed");
    }

    [OriginFact]
    public void Every_original_strategy_script_compiles_with_its_includes()
    {
        MemoryAiScriptSource source = LoadCorpus(out List<string> names);
        foreach (string name in names.Where(n => n.EndsWith(".ais", StringComparison.Ordinal) || n == "def.ai"))
        {
            AiProgram program = AiCompiler.Compile(name, source);
            foreach (AiDiagnostic d in program.Diagnostics)
            {
                output.WriteLine(d.ToString());
            }

            Assert.Empty(program.Diagnostics);
            output.WriteLine($"{name}: {program.Rules.Length} rules, {program.Files.Length} files, {program.Symbols.Length} names, {program.Flags.Length} flags");
        }
    }

    [OriginTheory]
    [InlineData("random.ais", Runtime.AiDifficulty.Hard)]
    [InlineData("random.ais", Runtime.AiDifficulty.Normal)]
    [InlineData("random.ais", Runtime.AiDifficulty.Easy)]
    [InlineData("dm.ais", Runtime.AiDifficulty.Hard)]
    public void The_original_strategy_scripts_play_a_skirmish(string script, Runtime.AiDifficulty difficulty)
    {
        AiProgram program = AiCompiler.Compile(script, LoadCorpus(out _));
        using var scenario = new Scenarios.AiSkirmishScenario(AiFixtures.Classic, program, new Scenarios.AiSkirmishOptions { Players = 2, MapSize = 96, Difficulty = difficulty });
        for (int t = 0; t < 10 * 60 * 20; t++)
        {
            scenario.RunTick();
        }

        output.WriteLine(scenario.Describe());
        output.WriteLine(Runtime.AiDebugView.Describe(scenario.Simulation.World, scenario.Ai, 0));
        for (int p = 0; p < 2; p++)
        {
            Assert.True(scenario.Delivered(p) > 0 && scenario.Built(p) > 0 && scenario.Trained(p) > 0, scenario.Describe());
        }
    }

    [OriginFact]
    public void Every_original_strategy_script_binds_for_every_civilization()
    {
        MemoryAiScriptSource source = LoadCorpus(out List<string> names);
        var model = new Runtime.AiRulesetModel(AiFixtures.Classic);
        foreach (string name in names.Where(n => n.EndsWith(".ais", StringComparison.Ordinal)))
        {
            AiProgram program = AiCompiler.Compile(name, source);
            foreach (string civ in new[] { "european", "asian", "arabian" })
            {
                Runtime.AiBinding binding = Runtime.AiBinding.Bind(program, model, civ);
                Assert.Equal(program.Symbols.Length, binding.Symbols.Length);
                Assert.DoesNotContain(binding.Diagnostics, d => d.Severity == AiDiagnosticSeverity.Error);
            }
        }
    }

    [OriginFact]
    public void Mutated_original_scripts_never_crash_the_toolchain()
    {
        MemoryAiScriptSource source = LoadCorpus(out List<string> names);
        var random = new Random(20261017);
        var model = new Runtime.AiRulesetModel(AiFixtures.Classic);
        for (int round = 0; round < 300; round++)
        {
            string name = names[random.Next(names.Count)];
            Assert.True(source.TryRead(name, out string? text));
            var mutated = new System.Text.StringBuilder(text);
            for (int e = 0, n = 1 + random.Next(10); e < n && mutated.Length > 0; e++)
            {
                int at = random.Next(mutated.Length);
                if (random.Next(2) == 0)
                {
                    mutated.Remove(at, Math.Min(mutated.Length - at, 1 + random.Next(30)));
                }
                else
                {
                    mutated.Insert(at, "{}\",%!;\n"[random.Next(8)]);
                }
            }

            var layered = new MemoryAiScriptSource();
            foreach (string other in names)
            {
                source.TryRead(other, out string? t);
                layered.Add(other, other == name ? mutated.ToString() : t!);
            }

            AiProgram program = AiCompiler.Compile("random.ais", layered);
            Runtime.AiBinding.Bind(program, model, "euro");
        }
    }

    private static int CountRules(IEnumerable<AiItem> items)
        => items.Sum(i => i switch { AiRuleItem => 1, AiBlockItem b => CountRules(b.Items), _ => 0 });
}
