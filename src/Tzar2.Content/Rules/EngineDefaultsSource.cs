using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Tzar2.Content.Vfs;

namespace Tzar2.Content.Rules;

/// <summary>
/// The engine-default package (REQ-DATA-02: mounted first, below the imported base): the rules files of the built-in
/// rulesets (<c>rules/classic.yaml</c>, <c>rules/remastered.yaml</c>) and Tzar2's own default AI script
/// (<c>ai/skirmish.ais</c>, ADR 0052) from <c>content/engine/</c> in the repository, embedded in this assembly. They
/// contain no original data: <c>classic</c> names the tables to import, the numbers come from the user's own install
/// (DEC-05).
/// </summary>
public sealed class EngineDefaultsSource : IContentSource
{
    private const string Prefix = "engine/";

    private readonly SortedDictionary<string, string> _resources = new(StringComparer.Ordinal);

    /// <summary>Indexes the embedded files.</summary>
    public EngineDefaultsSource()
    {
        Assembly assembly = typeof(EngineDefaultsSource).Assembly;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            string logical = name.Replace('\\', '/');
            if (logical.StartsWith(Prefix, StringComparison.Ordinal))
            {
                _resources[ContentPath.Normalize(logical[Prefix.Length..])] = name;
            }
        }

        var paths = new List<string>(_resources.Keys);
        Paths = paths;
    }

    /// <inheritdoc />
    public string Name => "engine defaults";

    /// <inheritdoc />
    public IReadOnlyList<string> Paths { get; }

    /// <inheritdoc />
    public bool Contains(string path) => _resources.ContainsKey(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path)
    {
        if (!_resources.TryGetValue(path, out string? name))
        {
            throw new FileNotFoundException($"'{path}' is not an engine default.", path);
        }

        using Stream stream = typeof(EngineDefaultsSource).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded resource '{name}' is missing.", path);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <inheritdoc />
    public long GetSize(string path) => ReadAllBytes(path).LongLength;
}
