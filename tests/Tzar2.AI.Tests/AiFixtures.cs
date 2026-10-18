using System;
using System.Text;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Legacy.Syntax;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;

namespace Tzar2.AI.Tests;

/// <summary>The classic ruleset compiled from the synthetic tables and the engine-default AI script (REQ-LEGAL-03).</summary>
internal static class AiFixtures
{
    private static readonly Lazy<Ruleset> LazyClassic = new(() => SyntheticLegacyContent.CompileRuleset());
    private static readonly Lazy<AiProgram> LazySkirmish = new(() => AiCompiler.CompileText("skirmish.ais", SkirmishText));

    public static Ruleset Classic => LazyClassic.Value;

    /// <summary>content/engine/ai/skirmish.ais as embedded in Tzar2.Content.</summary>
    public static string SkirmishText => Encoding.UTF8.GetString(new EngineDefaultsSource().ReadAllBytes("ai/skirmish.ais"));

    public static AiProgram Skirmish => LazySkirmish.Value;

    public static AiProgram Compile(string text, string name = "test.ais")
    {
        AiProgram program = AiCompiler.CompileText(name, text);
        foreach (AiDiagnostic d in program.Diagnostics)
        {
            if (d.Severity == AiDiagnosticSeverity.Error)
            {
                throw new InvalidOperationException(d.ToString());
            }
        }

        return program;
    }
}
