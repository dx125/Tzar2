using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tzar2.Architecture.Tests;

/// <summary>
/// ADR 0150: the editor model lives in the <c>Editing</c> namespaces of Content (document, undo, validation, packages),
/// Import (converter integration) and Game (playtest, trigger debugger); each may use only what its layer allows, and the
/// Godot client's editor mode stays a view over them (its namespaces are checked by <see cref="ClientTypeReferenceTests"/>).
/// </summary>
public class EditorModelTests
{
    /// <summary>Folder of each editing namespace and the Tzar2 namespaces its sources must not name.</summary>
    public static readonly Dictionary<string, string[]> Forbidden = new(StringComparer.Ordinal)
    {
        // The document model takes compiled data only: no rules compiler, no file system, no layers above Content.
        ["src/Tzar2.Content/Editing"] = ["Tzar2.Content.Rules", "Tzar2.Content.Vfs", "Tzar2.Import", "Tzar2.Formats", "Tzar2.Game", "Tzar2.Sim", "Tzar2.Scripting", "Tzar2.Client"],
        ["src/Tzar2.Import/Editing"] = ["Tzar2.Game", "Tzar2.Sim", "Tzar2.Scripting", "Tzar2.AI", "Tzar2.Client"],
        ["src/Tzar2.Game/Editing"] = ["Tzar2.Import", "Tzar2.Formats", "Tzar2.Client"],
    };

    public static TheoryData<string> Folders() => new(Forbidden.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void The_document_model_exists_in_content()
        => Assert.True(Directory.Exists(Path.Combine(RepositoryPaths.Root, "src/Tzar2.Content/Editing")), "ADR 0150: Tzar2.Content.Editing holds the document model.");

    [Theory]
    [MemberData(nameof(Folders))]
    public void Editing_sources_use_only_their_layer(string folder)
    {
        string dir = Path.Combine(RepositoryPaths.Root, folder);
        if (!Directory.Exists(dir))
        {
            return; // the layer has no editing code yet
        }

        var reference = new Regex(@"\b(Tzar2(?:\.[A-Z][A-Za-z0-9]*)+)", RegexOptions.CultureInvariant);
        var hits = new List<string>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match m in reference.Matches(lines[i]))
                {
                    string name = m.Groups[1].Value;
                    if (Forbidden[folder].Any(f => name == f || name.StartsWith(f + ".", StringComparison.Ordinal)))
                    {
                        hits.Add($"{Path.GetRelativePath(RepositoryPaths.Root, file)}:{i + 1}: {line}");
                    }
                }
            }
        }

        Assert.True(hits.Count == 0, "ADR 0150: editing code names a layer it may not use:\n" + string.Join("\n", hits));
    }
}
