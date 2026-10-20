using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Xunit;

namespace Tzar2.Architecture.Tests;

/// <summary>Runs only where the Godot SDK has compiled the client assembly (a full solution build).</summary>
public sealed class ClientAssemblyFactAttribute : FactAttribute
{
    public ClientAssemblyFactAttribute()
    {
        if (!File.Exists(ClientTypeReferenceTests.ClientAssembly))
        {
            Skip = $"The compiled client assembly is not here ({ClientTypeReferenceTests.ClientAssembly}): build the full solution "
                + "with the Godot .NET SDK (headless-only builds do not compile the client); the source check still runs.";
        }
    }
}

/// <summary>
/// TDD §7.2 at type level for the Godot client (ADR 0073): project references are not enough, because Game exposes its
/// own references transitively. Of the Tzar2 assemblies the client may use Game, Content and Import, and, as the
/// presentation contract that Game hands out (REQ-ARCH-03), only the namespaces <c>Tzar2.Core.Ids</c>,
/// <c>Tzar2.Sim.Commands</c> and <c>Tzar2.Sim.Events</c>, plus <c>Tzar2.Core.Diagnostics</c> (the trace interface and
/// levels the client logs through, ADR 0176): never the sim's world, simulation, components or kingdom state, nor AI,
/// Net, Replay, Scripting, Formats, Server or the CLI.
/// </summary>
public class ClientTypeReferenceTests
{
    /// <summary>Where the Godot SDK writes the client assembly.</summary>
    public static readonly string ClientAssembly = Path.Combine(RepositoryPaths.Root, "src", "Tzar2.Client", ".godot", "mono", "temp", "bin", "Debug", "Tzar2.Client.dll");

    private static readonly string[] AllowedAssemblies = ["Tzar2.Game", "Tzar2.Content", "Tzar2.Import"];

    private static readonly string[] ContractNamespaces = ["Tzar2.Core.Ids", "Tzar2.Core.Diagnostics", "Tzar2.Sim.Commands", "Tzar2.Sim.Events"];

    /// <summary>Whether the client may use a type of namespace <paramref name="ns"/>.</summary>
    public static bool IsAllowed(string ns)
        => !ns.StartsWith("Tzar2.", StringComparison.Ordinal) || ns.StartsWith("Tzar2.Client", StringComparison.Ordinal)
            || Array.Exists(AllowedAssemblies, a => ns == a || ns.StartsWith(a + ".", StringComparison.Ordinal))
            || Array.IndexOf(ContractNamespaces, ns) >= 0;

    [Fact]
    public void The_rule_admits_the_contract_and_rejects_sim_state()
    {
        Assert.True(IsAllowed("Tzar2.Sim.Commands"));
        Assert.True(IsAllowed("Tzar2.Sim.Events"));
        Assert.True(IsAllowed("Tzar2.Core.Ids"));
        Assert.True(IsAllowed("Tzar2.Core.Diagnostics"));
        Assert.True(IsAllowed("Tzar2.Game.Session"));
        Assert.True(IsAllowed("Godot"));
        Assert.False(IsAllowed("Tzar2.Sim"));
        Assert.False(IsAllowed("Tzar2.Sim.Components"));
        Assert.False(IsAllowed("Tzar2.Sim.Kingdoms"));
        Assert.False(IsAllowed("Tzar2.Core.Numerics"));
        Assert.False(IsAllowed("Tzar2.AI.Legacy.Program"));
        Assert.False(IsAllowed("Tzar2.Replay"));
    }

    [Fact]
    public void Client_sources_name_only_allowed_namespaces()
    {
        string client = Path.Combine(RepositoryPaths.Root, "src", "Tzar2.Client");
        var reference = new Regex(@"\b(Tzar2(?:\.[A-Z][A-Za-z0-9]*)+)", RegexOptions.CultureInvariant);
        var hits = new List<string>();
        foreach (string file in Directory.EnumerateFiles(client, "*.cs", SearchOption.AllDirectories))
        {
            string sep = Path.DirectorySeparatorChar.ToString();
            if (file.Contains(sep + ".godot" + sep, StringComparison.Ordinal) || file.Contains(sep + "obj" + sep, StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('+') || line.StartsWith('"'))
                {
                    continue; // prose and strings
                }

                foreach (Match m in reference.Matches(lines[i]))
                {
                    // "using Tzar2.Sim.Events;" names a namespace; "Tzar2.Sim.Kingdoms.KingdomState" a type in Tzar2.Sim.Kingdoms.
                    string name = m.Groups[1].Value;
                    if (!IsAllowed(name) && !IsAllowed(Namespace(name)))
                    {
                        hits.Add($"{Path.GetRelativePath(RepositoryPaths.Root, file)}:{i + 1}: {line}");
                    }
                }
            }
        }

        Assert.True(hits.Count == 0, "The client names types it may not use (§7.2):\n" + string.Join("\n", hits));
    }

    [ClientAssemblyFact]
    public void The_compiled_client_uses_only_allowed_namespaces()
    {
        // Read a private copy: a parallel build may hold or rewrite the DLL while this test runs (CI flake on #37).
        using var stream = new MemoryStream(ReadShared(ClientAssembly));
        using var pe = new PEReader(stream);
        MetadataReader md = pe.GetMetadataReader();
        var bad = new SortedSet<string>(StringComparer.Ordinal);
        foreach (TypeReferenceHandle handle in md.TypeReferences)
        {
            TypeReference type = md.GetTypeReference(handle);
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference)
            {
                continue; // nested types resolve through their declaring type
            }

            string ns = md.GetString(type.Namespace);
            if (!IsAllowed(ns))
            {
                bad.Add($"{ns}.{md.GetString(type.Name)}");
            }
        }

        Assert.True(bad.Count == 0, "Tzar2.Client.dll uses types it may not (§7.2): " + string.Join(", ", bad));
    }

    private static string Namespace(string qualified)
    {
        int dot = qualified.LastIndexOf('.');
        return dot > 0 ? qualified[..dot] : qualified;
    }

    private static byte[] ReadShared(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                file.CopyTo(copy);
                return copy.ToArray();
            }
            catch (IOException) when (attempt < 5)
            {
                System.Threading.Thread.Sleep(200 * attempt);
            }
        }
    }
}
