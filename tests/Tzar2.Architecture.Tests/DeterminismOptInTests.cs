using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Tzar2.Core;
using Xunit;

namespace Tzar2.Architecture.Tests;

/// <summary>
/// The determinism-critical assemblies of TDD §8.8 must opt into Tzar2.Analyzers (ADRs 0004, 0012). Checked on the compiled
/// assemblies, so it does not matter where in the project (or in an imported file) the MSBuild property is set.
/// </summary>
public class DeterminismOptInTests
{
    /// <summary>Assembly name to (has [Tzar2Deterministic], has [Tzar2NoIO]).</summary>
    public static readonly Dictionary<string, (bool Deterministic, bool NoIO)> Expected = new(StringComparer.Ordinal)
    {
        ["Tzar2.Core"] = (true, false),
        ["Tzar2.Content"] = (true, false),
        ["Tzar2.Sim"] = (true, true),
        ["Tzar2.Scripting"] = (true, true),
        ["Tzar2.AI"] = (true, true),
        ["Tzar2.Replay"] = (true, false),
        ["Tzar2.Formats"] = (false, false),
        ["Tzar2.Import"] = (false, false),
        ["Tzar2.Net"] = (false, false),
        ["Tzar2.Game"] = (false, false),
        ["Tzar2.Server"] = (false, false),
        ["t2"] = (false, false),
    };

    public static TheoryData<string> Assemblies() => new(Expected.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Compiled_assembly_carries_the_expected_markers(string assemblyName)
    {
        Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
        (bool deterministic, bool noIO) = Expected[assemblyName];
        Assert.Equal(deterministic, assembly.GetCustomAttribute<Tzar2DeterministicAttribute>() is not null);
        Assert.Equal(noIO, assembly.GetCustomAttribute<Tzar2NoIOAttribute>() is not null);
    }

    [Fact]
    public void Every_compiled_source_assembly_has_an_expectation()
    {
        // The Godot client cannot be loaded here and the analyzer is not a runtime assembly; t2 is Tzar2.Tools.Cli.
        foreach (string name in DependencyRulesTests.Allowed.Keys.Except(["Tzar2.Client", "Tzar2.Analyzers", "Tzar2.Tools.Cli"]).Append("t2"))
        {
            Assert.True(Expected.ContainsKey(name), $"{name} has no determinism expectation.");
        }
    }
}
