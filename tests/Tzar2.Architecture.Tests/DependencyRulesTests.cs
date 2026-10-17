using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace Tzar2.Architecture.Tests;

/// <summary>Enforces the "May reference" column of TDD §7.2 and REQ-ARCH-01 on the project files and the compiled assemblies.</summary>
public class DependencyRulesTests
{
    private static readonly string[] NonGodotLibraries =
    [
        "Tzar2.Core", "Tzar2.Formats", "Tzar2.Content", "Tzar2.Import", "Tzar2.Sim", "Tzar2.Scripting",
        "Tzar2.AI", "Tzar2.Net", "Tzar2.Replay", "Tzar2.Game",
    ];

    /// <summary>TDD §7.2: the Tzar2 projects each project may reference directly.</summary>
    public static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["Tzar2.Core"] = [],
        ["Tzar2.Formats"] = ["Tzar2.Core"],
        ["Tzar2.Content"] = ["Tzar2.Core"],
        ["Tzar2.Import"] = ["Tzar2.Core", "Tzar2.Formats", "Tzar2.Content"],
        ["Tzar2.Sim"] = ["Tzar2.Core", "Tzar2.Content"],
        ["Tzar2.Scripting"] = ["Tzar2.Core", "Tzar2.Sim"],
        ["Tzar2.AI"] = ["Tzar2.Core", "Tzar2.Sim"],
        ["Tzar2.Net"] = ["Tzar2.Core"],
        ["Tzar2.Replay"] = ["Tzar2.Core", "Tzar2.Sim"],
        ["Tzar2.Game"] = ["Tzar2.Core", "Tzar2.Content", "Tzar2.Sim", "Tzar2.Scripting", "Tzar2.AI", "Tzar2.Net", "Tzar2.Replay"],
        ["Tzar2.Client"] = ["Tzar2.Game", "Tzar2.Content", "Tzar2.Import"],
        ["Tzar2.Server"] = ["Tzar2.Core", "Tzar2.Net"],
        ["Tzar2.Tools.Cli"] = NonGodotLibraries,
        ["Tzar2.Analyzers"] = [],
    };

    private static string[] ProjectNames { get; } = Directory.GetDirectories(Path.Combine(RepositoryPaths.Root, "src"))
        .Select(dir => Path.GetFileName(dir))
        .Order(StringComparer.Ordinal)
        .ToArray();

    public static TheoryData<string> SourceProjects() => new(ProjectNames);

    [Fact]
    public void Every_source_project_has_a_rule()
    {
        Assert.NotEmpty(ProjectNames);
        foreach (string project in ProjectNames)
        {
            Assert.True(Allowed.ContainsKey(project), $"src/{project} is not listed in TDD §7.2 / DependencyRulesTests.Allowed.");
        }
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Project_references_follow_the_may_reference_rules(string project)
    {
        XDocument csproj = LoadProject(project);
        string[] references = csproj.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .ToArray();
        string[] forbidden = references.Except(Allowed[project], StringComparer.Ordinal).ToArray();
        Assert.True(forbidden.Length == 0, $"{project} references {string.Join(", ", forbidden)}, which TDD §7.2 does not allow.");
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Only_the_client_uses_godot(string project)
    {
        XDocument csproj = LoadProject(project);
        string sdk = csproj.Root!.Attribute("Sdk")?.Value ?? string.Empty;
        bool usesGodot = sdk.StartsWith("Godot.", StringComparison.Ordinal)
            || csproj.Descendants("PackageReference").Any(e => e.Attribute("Include")!.Value.StartsWith("Godot", StringComparison.Ordinal));
        Assert.Equal(project == "Tzar2.Client", usesGodot);
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void No_raw_assembly_references(string project)
        => Assert.Empty(LoadProject(project).Descendants("Reference"));

    [Fact]
    public void Simulation_layers_do_not_reach_formats_import_or_godot_transitively()
    {
        // REQ-ARCH-01: Sim, Scripting and AI never see legacy formats, the importer or the engine.
        foreach (string project in new[] { "Tzar2.Sim", "Tzar2.Scripting", "Tzar2.AI", "Tzar2.Replay", "Tzar2.Game" })
        {
            HashSet<string> closure = TransitiveReferences(project);
            Assert.DoesNotContain("Tzar2.Formats", closure);
            Assert.DoesNotContain("Tzar2.Import", closure);
            Assert.DoesNotContain("Tzar2.Client", closure);
        }
    }

    [Fact]
    public void Compiled_assemblies_reference_only_allowed_assemblies()
    {
        Assembly[] assemblies =
        [
            typeof(Core.Tzar2DeterministicAttribute).Assembly,
            typeof(Formats.FormatsAssembly).Assembly,
            typeof(Content.ContentAssembly).Assembly,
            typeof(Import.Editions.InstallVerifier).Assembly,
            typeof(Sim.SimAssembly).Assembly,
            typeof(Scripting.ScriptingAssembly).Assembly,
            typeof(AI.AIAssembly).Assembly,
            typeof(Net.NetAssembly).Assembly,
            typeof(Replay.ReplayAssembly).Assembly,
            typeof(Game.GameInfo).Assembly,
        ];

        foreach (Assembly assembly in assemblies)
        {
            string name = assembly.GetName().Name!;
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                string referenced = reference.Name!;
                Assert.False(referenced.StartsWith("Godot", StringComparison.Ordinal), $"{name} references {referenced}.");
                if (referenced.StartsWith("Tzar2.", StringComparison.Ordinal))
                {
                    Assert.True(Allowed[name].Contains(referenced), $"{name} references {referenced}, which TDD §7.2 does not allow.");
                }
            }
        }
    }

    private static XDocument LoadProject(string project)
        => XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", project, project + ".csproj"));

    private static HashSet<string> TransitiveReferences(string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([project]);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (XElement reference in LoadProject(current).Descendants("ProjectReference"))
            {
                string name = Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/'));
                if (seen.Add(name))
                {
                    pending.Push(name);
                }
            }
        }

        return seen;
    }
}
