using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Xunit;

namespace Tzar2.Architecture.Tests;

/// <summary>
/// TDD §7.2 at type level for the Godot client (ADR 0073): the project references are not enough, because Game exposes
/// its own references transitively (a client field of an AI type compiled fine). The client may use types of Game,
/// Content and Import, plus the presentation contract that Game hands out (REQ-ARCH-03: ids from Core; commands,
/// events and kingdom state records from Sim). AI, Net, Replay, Scripting, Formats, Server and the CLI stay behind Game.
/// </summary>
public class ClientTypeReferenceTests
{
    private static readonly string[] TypeLevelAllowed = ["Tzar2.Game", "Tzar2.Content", "Tzar2.Import", "Tzar2.Core", "Tzar2.Sim"];

    private static readonly string[] Forbidden = ["Tzar2.AI", "Tzar2.Net", "Tzar2.Replay", "Tzar2.Scripting", "Tzar2.Formats", "Tzar2.Server", "Tzar2.Tools"];

    [Fact]
    public void Client_sources_name_no_type_of_a_forbidden_assembly()
    {
        string client = Path.Combine(RepositoryPaths.Root, "src", "Tzar2.Client");
        var pattern = new Regex(@"\b(" + string.Join("|", Forbidden.Select(Regex.Escape)) + @")\.[A-Z]", RegexOptions.CultureInvariant);
        var hits = new List<string>();
        foreach (string file in Directory.EnumerateFiles(client, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}.godot{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                bool prose = line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('+') || line.StartsWith('"');
                if (!prose && pattern.IsMatch(lines[i]))
                {
                    hits.Add($"{Path.GetRelativePath(RepositoryPaths.Root, file)}:{i + 1}: {line}");
                }
            }
        }

        Assert.True(hits.Count == 0, "The client uses types it may not reference (§7.2):\n" + string.Join("\n", hits));
    }

    [Fact]
    public void The_compiled_client_references_only_allowed_tzar2_assemblies()
    {
        // The Godot SDK writes the client assembly here when the solution is built (CI's full build, local builds).
        string dll = Path.Combine(RepositoryPaths.Root, "src", "Tzar2.Client", ".godot", "mono", "temp", "bin", "Debug", "Tzar2.Client.dll");
        if (!File.Exists(dll))
        {
            return; // headless-only builds do not compile the client; the source check above still runs
        }

        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        MetadataReader md = pe.GetMetadataReader();
        var referenced = new SortedSet<string>(StringComparer.Ordinal);
        foreach (TypeReferenceHandle handle in md.TypeReferences)
        {
            TypeReference type = md.GetTypeReference(handle);
            if (type.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                referenced.Add(md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name));
            }
        }

        string[] bad = referenced.Where(n => n.StartsWith("Tzar2.", StringComparison.Ordinal) && !TypeLevelAllowed.Contains(n)).ToArray();
        Assert.True(bad.Length == 0, $"Tzar2.Client.dll uses types of {string.Join(", ", bad)} (§7.2).");
        Assert.Contains("Tzar2.Game", referenced);
    }
}
