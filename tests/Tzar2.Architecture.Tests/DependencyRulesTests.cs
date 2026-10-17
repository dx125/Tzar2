using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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

    /// <summary>
    /// TDD §7.2 / §24 (DEC-09): the NuGet packages each source project may reference directly. Test projects are free.
    /// Core's "BCL only" admits the out-of-band BCL package System.IO.Hashing (ADR 0011).
    /// </summary>
    public static readonly Dictionary<string, string[]> AllowedPackages = new(StringComparer.Ordinal)
    {
        ["Tzar2.Core"] = ["System.IO.Hashing"],
        ["Tzar2.Formats"] = [],
        ["Tzar2.Content"] = ["YamlDotNet", "Vecc.YamlDotNet.Analyzers.StaticGenerator"],
        ["Tzar2.Import"] = [],
        ["Tzar2.Sim"] = [],
        ["Tzar2.Scripting"] = ["LuaCSharp"],
        ["Tzar2.AI"] = [],
        ["Tzar2.Net"] = ["LiteNetLib"],
        ["Tzar2.Replay"] = [],
        ["Tzar2.Game"] = [],
        // MeltySynth: MIDI music (REQ-AUD-02, ADR 0020).
        ["Tzar2.Client"] = ["GodotSharp", "GodotSharpEditor", "Godot.SourceGenerators", "MeltySynth"],
        ["Tzar2.Server"] = [],
        ["Tzar2.Tools.Cli"] = [],
        ["Tzar2.Analyzers"] = ["Microsoft.CodeAnalysis.CSharp", "Microsoft.CodeAnalysis.Analyzers"],
    };

    /// <summary>Assembly names shipped by the allowed packages that compiled assemblies may reference.</summary>
    private static readonly Dictionary<string, string> PackageAssemblies = new(StringComparer.Ordinal)
    {
        ["System.IO.Hashing"] = "System.IO.Hashing",
        ["YamlDotNet"] = "YamlDotNet",
        ["LuaCSharp"] = "Lua",
        ["LiteNetLib"] = "LiteNetLib",
        ["MeltySynth"] = "MeltySynth",
    };

    /// <summary>Compiled assembly name to project name, where they differ.</summary>
    private static readonly Dictionary<string, string> ProjectOfAssembly = new(StringComparer.Ordinal)
    {
        ["t2"] = "Tzar2.Tools.Cli",
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

    [Fact]
    public void Every_source_project_has_a_package_rule()
    {
        foreach (string project in ProjectNames)
        {
            Assert.True(AllowedPackages.ContainsKey(project), $"src/{project} is not listed in DependencyRulesTests.AllowedPackages.");
        }
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Package_references_follow_the_allowed_package_map(string project)
    {
        string[] forbidden = ForbiddenPackages(project, LoadProject(project));
        Assert.True(forbidden.Length == 0, $"{project} references the package(s) {string.Join(", ", forbidden)}, which DependencyRulesTests.AllowedPackages does not allow.");
    }

    [Fact]
    public void Package_rule_rejects_an_unlisted_package()
    {
        XDocument csproj = XDocument.Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="System.IO.Hashing" />
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);
        Assert.Equal(["Newtonsoft.Json"], ForbiddenPackages("Tzar2.Core", csproj));
        Assert.Equal(["Newtonsoft.Json", "System.IO.Hashing"], ForbiddenPackages("Tzar2.Sim", csproj));
    }

    [Fact]
    public void Shared_build_files_add_no_packages_to_source_projects()
    {
        // Root-level Directory.Build.* files apply to every src project, bypassing the per-project map.
        foreach (string file in new[] { "Directory.Build.props", "Directory.Build.targets", "src/Directory.Build.props", "src/Directory.Build.targets" })
        {
            string path = Path.Combine(RepositoryPaths.Root, file);
            if (File.Exists(path))
            {
                Assert.DoesNotContain(XDocument.Load(path).Descendants(), e => e.Name.LocalName is "PackageReference" or "PackageDownload");
            }
        }
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Restored_direct_dependencies_follow_the_allowed_package_map(string project)
    {
        // project.assets.json reflects the evaluated project, including anything an SDK or imported file adds.
        // Godot.NET.Sdk moves the intermediate directory to .godot/mono/temp/obj.
        string assets = new[] { "obj", ".godot/mono/temp/obj" }
            .Select(dir => Path.Combine(RepositoryPaths.Root, "src", project, dir, "project.assets.json"))
            .FirstOrDefault(File.Exists) ?? Path.Combine(RepositoryPaths.Root, "src", project, "obj", "project.assets.json");
        if (!File.Exists(assets))
        {
            // Only the Godot client is not built by the headless solution (Tzar2.Headless.slnf); every other project is a
            // (transitive) reference of this test project and therefore restored.
            Assert.True(project == "Tzar2.Client", $"{assets} is missing; build the solution first.");
            return;
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(assets));
        foreach (JsonProperty framework in json.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject())
        {
            if (!framework.Value.TryGetProperty("dependencies", out JsonElement dependencies))
            {
                continue;
            }

            foreach (JsonProperty dependency in dependencies.EnumerateObject())
            {
                bool autoReferenced = dependency.Value.TryGetProperty("autoReferenced", out JsonElement auto) && auto.GetBoolean();
                Assert.True(
                    autoReferenced || AllowedPackages[project].Contains(dependency.Name, StringComparer.OrdinalIgnoreCase),
                    $"{project} ({framework.Name}) depends on the package {dependency.Name}, which DependencyRulesTests.AllowedPackages does not allow.");
            }
        }
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
            Assembly.Load(new AssemblyName("Tzar2.Server")),
            Assembly.Load(new AssemblyName("t2")),
        ];

        Assert.Equal(
            Allowed.Keys.Except(["Tzar2.Client", "Tzar2.Analyzers"]).Order(StringComparer.Ordinal),
            assemblies.Select(a => ProjectName(a.GetName().Name!)).Order(StringComparer.Ordinal));

        foreach (Assembly assembly in assemblies)
        {
            string name = ProjectName(assembly.GetName().Name!);
            string[] packageAssemblies = AllowedPackages[name].Where(PackageAssemblies.ContainsKey).Select(p => PackageAssemblies[p]).ToArray();
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                string referenced = reference.Name!;
                Assert.False(referenced.StartsWith("Godot", StringComparison.Ordinal), $"{name} references {referenced}.");
                if (referenced.StartsWith("Tzar2.", StringComparison.Ordinal) || referenced == "t2")
                {
                    Assert.True(Allowed[name].Contains(ProjectName(referenced)), $"{name} references {referenced}, which TDD §7.2 does not allow.");
                }
                else
                {
                    bool framework = referenced is "mscorlib" or "netstandard"
                        || referenced.StartsWith("System.", StringComparison.Ordinal)
                        || referenced.StartsWith("Microsoft.", StringComparison.Ordinal);
                    Assert.True(
                        framework || packageAssemblies.Contains(referenced, StringComparer.Ordinal),
                        $"{name} references {referenced}, which is neither the framework nor an allowed package.");
                }
            }
        }
    }

    private static string ProjectName(string assemblyName)
        => ProjectOfAssembly.TryGetValue(assemblyName, out string? project) ? project : assemblyName;

    private static string[] ForbiddenPackages(string project, XDocument csproj)
        => csproj.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "PackageDownload")
            .Select(e => (e.Attribute("Include") ?? e.Attribute("Update"))?.Value ?? string.Empty)
            .Where(id => !AllowedPackages[project].Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

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
