using System;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;

namespace Tzar2.Game.Session;

/// <summary>Compiles a match's ruleset from the engine defaults and the user's imported base package (ADR 0032).</summary>
public static class RulesetLoader
{
    /// <summary>Compiles ruleset <paramref name="name"/> with <paramref name="package"/> mounted over the engine defaults.</summary>
    /// <exception cref="RulesException">The rules do not compile (for <c>classic</c>: no imported tables).</exception>
    public static CompiledRuleset Compile(IContentSource package, string name = "classic")
    {
        ArgumentNullException.ThrowIfNull(package);
        var vfs = new VirtualFileSystem();
        vfs.Mount(new EngineDefaultsSource());
        vfs.Mount(package);
        return RulesetCompiler.Compile(vfs, name);
    }
}
