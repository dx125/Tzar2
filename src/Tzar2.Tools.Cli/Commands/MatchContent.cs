using System;
using System.IO;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Tzar2.Game.Session;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// The ruleset and AI script a command runs matches with: the user's imported base package (<c>--package</c> or
/// <c>TZAR2_PACKAGE</c>), or the synthetic fixture tables (<c>--synthetic</c>, public CI; REQ-LEGAL-03).
/// </summary>
internal sealed record MatchContent(Ruleset Ruleset, SkirmishAi Ai, string Description)
{
    /// <summary>Loads the content; <paramref name="package"/> null means synthetic.</summary>
    /// <exception cref="IOException">The package cannot be read.</exception>
    /// <exception cref="RulesException">The rules do not compile.</exception>
    public static MatchContent Load(string? package)
    {
        if (package is null)
        {
            return new MatchContent(SyntheticLegacyContent.CompileRuleset(), SkirmishAi.EngineDefault, "synthetic tables, engine-default AI");
        }

        using PackageSource source = PackageSource.Open(package);
        Ruleset ruleset = RulesetLoader.Compile(source).Ruleset;
        SkirmishAi ai = SkirmishAi.Resolve(source);
        return new MatchContent(ruleset, ai, $"package {Path.GetFileName(package)}, {(ai.IsOriginal ? "original AI" : "engine-default AI")}");
    }

    /// <summary>The package named by the environment (<c>TZAR2_PACKAGE</c>), if any.</summary>
    public static string? EnvironmentPackage() => Environment.GetEnvironmentVariable("TZAR2_PACKAGE") is { Length: > 0 } p ? p : null;
}
