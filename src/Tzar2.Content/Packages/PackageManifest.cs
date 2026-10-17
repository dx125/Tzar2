using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Tzar2.Content.Packages;

/// <summary>Package kinds of TDD REQ-DATA-01.</summary>
public enum PackageKind
{
    /// <summary>Imported original content.</summary>
    Base,

    /// <summary>Imported or official expansions.</summary>
    Expansion,

    /// <summary>A user mod.</summary>
    Mod,

    /// <summary>A map package.</summary>
    Map,

    /// <summary>A campaign package.</summary>
    Campaign,

    /// <summary>Art or sound replacements.</summary>
    AssetPack,

    /// <summary>A translation.</summary>
    Language,
}

/// <summary>
/// The <c>package.yaml</c> manifest (TDD REQ-DATA-01). Written and read as a small, fixed YAML subset
/// (<c>key: scalar</c>, double-quoted strings, flow lists), so neither direction needs a YAML library or reflection.
/// </summary>
public sealed record PackageManifest
{
    /// <summary>Unique package id, e.g. <c>base.original</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Package version (semantic).</summary>
    public required string Version { get; init; }

    /// <summary>Package kind.</summary>
    public PackageKind Kind { get; init; } = PackageKind.Base;

    /// <summary>Engine API level the package targets.</summary>
    public int EngineApi { get; init; } = 1;

    /// <summary>Ids of packages this one depends on.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Ids of packages this one cannot be combined with.</summary>
    public IReadOnlyList<string> ConflictsWith { get; init; } = [];

    /// <summary>Content categories provided.</summary>
    public IReadOnlyList<string> Provides { get; init; } = [];

    /// <summary>Canonical hash of gameplay-relevant content, <c>sha256:&lt;hex&gt;</c>, or null.</summary>
    public string? GameplayHash { get; init; }

    /// <summary>Edition the content was imported from (base packages), or null.</summary>
    public string? SourceEdition { get; init; }

    /// <summary>Serialises to YAML with LF line endings and a fixed key order.</summary>
    public string ToYaml()
    {
        var sb = new StringBuilder();
        Line(sb, "id", Plain(Id));
        Line(sb, "name", Quote(Name));
        Line(sb, "version", Plain(Version));
        Line(sb, "kind", KindName(Kind));
        Line(sb, "engineApi", EngineApi.ToString(CultureInfo.InvariantCulture));
        Line(sb, "dependsOn", List(DependsOn));
        Line(sb, "conflictsWith", List(ConflictsWith));
        Line(sb, "provides", List(Provides));
        if (GameplayHash is not null)
        {
            Line(sb, "gameplayHash", Quote(GameplayHash));
        }

        if (SourceEdition is not null)
        {
            Line(sb, "sourceEdition", Quote(SourceEdition));
        }

        return sb.ToString();
    }

    /// <summary>Parses the YAML subset written by <see cref="ToYaml"/> (unknown keys are ignored).</summary>
    /// <exception cref="InvalidDataException">A required key is missing or a value is malformed.</exception>
    public static PackageManifest Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string rawLine in yaml.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                throw new InvalidDataException($"package.yaml:{lineNumber}: expected 'key: value'.");
            }

            if (!values.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim()))
            {
                throw new InvalidDataException($"package.yaml:{lineNumber}: duplicate key '{line[..colon].Trim()}'.");
            }
        }

        string Required(string key) => values.TryGetValue(key, out string? v)
            ? Scalar(v, key)
            : throw new InvalidDataException($"package.yaml: missing '{key}'.");
        string? Optional(string key) => values.TryGetValue(key, out string? v) ? Scalar(v, key) : null;
        IReadOnlyList<string> ListOf(string key) => values.TryGetValue(key, out string? v) ? ParseList(v, key) : [];

        string kindText = Optional("kind") ?? "base";
        string engineText = Optional("engineApi") ?? "1";
        if (!int.TryParse(engineText, NumberStyles.None, CultureInfo.InvariantCulture, out int engineApi))
        {
            throw new InvalidDataException($"package.yaml: invalid engineApi '{engineText}'.");
        }

        return new PackageManifest
        {
            Id = Required("id"),
            Name = Required("name"),
            Version = Required("version"),
            Kind = ParseKind(kindText),
            EngineApi = engineApi,
            DependsOn = ListOf("dependsOn"),
            ConflictsWith = ListOf("conflictsWith"),
            Provides = ListOf("provides"),
            GameplayHash = Optional("gameplayHash"),
            SourceEdition = Optional("sourceEdition"),
        };
    }

    private static void Line(StringBuilder sb, string key, string value) => sb.Append(key).Append(": ").Append(value).Append('\n');

    private static string KindName(PackageKind kind) => kind switch
    {
        PackageKind.Base => "base",
        PackageKind.Expansion => "expansion",
        PackageKind.Mod => "mod",
        PackageKind.Map => "map",
        PackageKind.Campaign => "campaign",
        PackageKind.AssetPack => "asset-pack",
        PackageKind.Language => "language",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static PackageKind ParseKind(string text)
    {
        foreach (PackageKind kind in Enum.GetValues<PackageKind>())
        {
            if (KindName(kind) == text)
            {
                return kind;
            }
        }

        throw new InvalidDataException($"package.yaml: unknown kind '{text}'.");
    }

    private static string Plain(string value)
    {
        foreach (char c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                return Quote(value);
            }
        }

        return value.Length == 0 ? "\"\"" : value;
    }

    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static string List(IReadOnlyList<string> items)
    {
        var parts = new string[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            parts[i] = Plain(items[i]);
        }

        return "[" + string.Join(", ", parts) + "]";
    }

    private static string Scalar(string value, string key)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return Unquote(value[1..^1], key);
        }

        int comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? value[..comment] : value).Trim();
    }

    private static string Unquote(string body, string key)
    {
        var sb = new StringBuilder(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            if (++i >= body.Length)
            {
                throw new InvalidDataException($"package.yaml: bad escape in '{key}'.");
            }

            switch (body[i])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'n': sb.Append('\n'); break;
                case 'u' when i + 4 < body.Length
                    && int.TryParse(body.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: throw new InvalidDataException($"package.yaml: bad escape in '{key}'.");
            }
        }

        return sb.ToString();
    }

    private static List<string> ParseList(string value, string key)
    {
        if (value.Length < 2 || value[0] != '[' || value[^1] != ']')
        {
            throw new InvalidDataException($"package.yaml: '{key}' must be a flow list [a, b].");
        }

        var items = new List<string>();
        string body = value[1..^1].Trim();
        if (body.Length == 0)
        {
            return items;
        }

        // Split at commas outside double quotes (Plain() quotes items that contain commas).
        int start = 0;
        bool quoted = false;
        for (int i = 0; i <= body.Length; i++)
        {
            if (i < body.Length && body[i] == '\\' && quoted)
            {
                i++;
                continue;
            }

            if (i < body.Length && body[i] == '"')
            {
                quoted = !quoted;
            }
            else if (i == body.Length || (body[i] == ',' && !quoted))
            {
                items.Add(Scalar(body[start..i].Trim(), key));
                start = i + 1;
            }
        }

        if (quoted)
        {
            throw new InvalidDataException($"package.yaml: unterminated quote in '{key}'.");
        }

        return items;
    }
}
