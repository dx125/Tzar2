using System;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace Tzar2.Architecture.Tests;

/// <summary>The determinism-critical assemblies of TDD §8.8 must opt into Tzar2.Analyzers (ADR 0004).</summary>
public class DeterminismOptInTests
{
    [Theory]
    [InlineData("Tzar2.Core", true, false)]
    [InlineData("Tzar2.Sim", true, true)]
    [InlineData("Tzar2.Scripting", true, true)]
    [InlineData("Tzar2.AI", true, true)]
    [InlineData("Tzar2.Replay", true, false)]
    public void Project_opts_into_the_analyzers(string project, bool deterministic, bool noIO)
    {
        XDocument csproj = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", project, project + ".csproj"));
        Assert.Equal(deterministic, string.Equals((string?)csproj.Root!.Element("PropertyGroup")?.Element("Tzar2Deterministic"), "true", StringComparison.Ordinal));
        Assert.Equal(noIO, string.Equals((string?)csproj.Root!.Element("PropertyGroup")?.Element("Tzar2NoIO"), "true", StringComparison.Ordinal));
    }
}
