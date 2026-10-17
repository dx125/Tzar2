using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tzar2.Content.Rules;

/// <summary>A parsed YAML node: <see cref="YamlScalar"/>, <see cref="YamlSequence"/> or <see cref="YamlMapping"/>.</summary>
public abstract class YamlNode
{
    /// <summary>Creates a node.</summary>
    protected YamlNode(int line) => Line = line;

    /// <summary>1-based source line (0 for nodes built in code).</summary>
    public int Line { get; }
}

/// <summary>A scalar; <see cref="Quoted"/> scalars are never interpreted as numbers, booleans or null.</summary>
/// <param name="value">The text.</param>
/// <param name="quoted">Whether it was quoted in the source.</param>
/// <param name="line">1-based source line.</param>
public sealed class YamlScalar(string value, bool quoted, int line = 0) : YamlNode(line)
{
    /// <summary>The text.</summary>
    public string Value { get; } = value;

    /// <summary>Whether it was quoted in the source.</summary>
    public bool Quoted { get; } = quoted;

    /// <summary>An unquoted empty value, <c>~</c> or <c>null</c>.</summary>
    public bool IsNull => !Quoted && Value is "" or "~" or "null";

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A sequence.</summary>
/// <param name="items">The items in order.</param>
/// <param name="line">1-based source line.</param>
public sealed class YamlSequence(List<YamlNode> items, int line = 0) : YamlNode(line)
{
    /// <summary>The items in order.</summary>
    public IReadOnlyList<YamlNode> Items { get; } = items;
}

/// <summary>A mapping that keeps the source order of its keys. Duplicate keys are a parse error.</summary>
/// <param name="entries">The entries in source order.</param>
/// <param name="line">1-based source line.</param>
public sealed class YamlMapping(List<KeyValuePair<string, YamlNode>> entries, int line = 0) : YamlNode(line)
{
    /// <summary>The entries in source order.</summary>
    public IReadOnlyList<KeyValuePair<string, YamlNode>> Entries { get; } = entries;

    /// <summary>The value of <paramref name="key"/>, or null.</summary>
    public YamlNode? Get(string key)
    {
        foreach (KeyValuePair<string, YamlNode> entry in Entries)
        {
            if (entry.Key == key)
            {
                return entry.Value;
            }
        }

        return null;
    }
}

/// <summary>Malformed YAML, with the 1-based line of the problem.</summary>
public sealed class YamlException : Exception
{
    /// <summary>Creates the exception.</summary>
    public YamlException(string message, int line)
        : base(line > 0 ? $"line {line}: {message}" : message)
    {
        SourceLine = line;
    }

    /// <summary>Creates the exception.</summary>
    public YamlException()
        : this("malformed YAML", 0)
    {
    }

    /// <summary>Creates the exception.</summary>
    public YamlException(string message)
        : this(message, 0)
    {
    }

    /// <summary>Creates the exception.</summary>
    public YamlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>1-based line, 0 if unknown.</summary>
    public int SourceLine { get; }
}

/// <summary>
/// The YAML subset of the rules files (ADR 0031): block mappings and sequences by indentation, flow sequences
/// <c>[a, b]</c> and mappings <c>{a: 1}</c>, plain/single-quoted/double-quoted scalars, literal blocks (<c>|</c>) and
/// <c>#</c> comments. No anchors, tags, multi-documents or folded scalars. Ported from the capture harness reader
/// (ADR 0030); integer-only accessors, because the ruleset compiler is deterministic (ADR 0012). The capture harness
/// (Tzar2.Tools.Cli) uses this parser too.
/// </summary>
public static class RulesYaml
{
    private readonly record struct Line(int Number, int Indent, string Text);

    /// <summary>Parses <paramref name="text"/>; an empty document is an empty mapping.</summary>
    /// <exception cref="YamlException">The text is not in the subset.</exception>
    public static YamlNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<Line>();
        string[] raw = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < raw.Length; i++)
        {
            string line = raw[i];
            if (line.Contains('\t', StringComparison.Ordinal) && line.TrimStart(' ').StartsWith('\t'))
            {
                throw new YamlException("tabs are not allowed for indentation", i + 1);
            }

            lines.Add(new Line(i + 1, line.Length - line.TrimStart(' ').Length, line));
        }

        int index = 0;
        SkipBlank(lines, ref index);
        if (index >= lines.Count)
        {
            return new YamlMapping([], 1);
        }

        YamlNode root = ParseBlock(lines, ref index, lines[index].Indent, 0);
        SkipBlank(lines, ref index);
        if (index < lines.Count)
        {
            throw new YamlException("unexpected content (bad indentation?)", lines[index].Number);
        }

        return root;
    }

    /// <summary>Deepest nesting of collections accepted (rules and mods are untrusted input: bounded recursion).</summary>
    public const int MaxDepth = 64;

    private static void CheckDepth(int depth, int line)
    {
        if (depth > MaxDepth)
        {
            throw new YamlException($"collections nested deeper than {MaxDepth} levels", line);
        }
    }

    private static bool IsBlank(string text)
    {
        string trimmed = text.TrimStart(' ');
        return trimmed.Length == 0 || trimmed[0] == '#';
    }

    private static void SkipBlank(List<Line> lines, ref int index)
    {
        while (index < lines.Count && IsBlank(lines[index].Text))
        {
            index++;
        }
    }

    private static YamlNode ParseBlock(List<Line> lines, ref int index, int indent, int depth)
    {
        CheckDepth(depth, lines[index].Number);
        string content = lines[index].Text[indent..];
        return content.StartsWith("- ", StringComparison.Ordinal) || content == "-"
            ? ParseSequence(lines, ref index, indent, depth)
            : ParseMapping(lines, ref index, indent, depth);
    }

    private static YamlSequence ParseSequence(List<Line> lines, ref int index, int indent, int depth)
    {
        var items = new List<YamlNode>();
        int startLine = lines[index].Number;
        while (true)
        {
            SkipBlank(lines, ref index);
            if (index >= lines.Count || lines[index].Indent < indent)
            {
                break;
            }

            Line line = lines[index];
            if (line.Indent > indent)
            {
                throw new YamlException("unexpected indentation in a sequence", line.Number);
            }

            string content = line.Text[indent..];
            if (!(content.StartsWith("- ", StringComparison.Ordinal) || content == "-"))
            {
                break;
            }

            string rest = StripComment(content[1..]).Trim();
            int itemIndent = indent + 2 + (content.Length > 2 ? content[2..].Length - content[2..].TrimStart(' ').Length : 0);
            if (rest.Length == 0)
            {
                index++;
                SkipBlank(lines, ref index);
                if (index < lines.Count && lines[index].Indent > indent)
                {
                    items.Add(ParseBlock(lines, ref index, lines[index].Indent, depth + 1));
                }
                else
                {
                    items.Add(new YamlScalar(string.Empty, false, line.Number));
                }
            }
            else if (FindMappingColon(rest) >= 0 && rest[0] is not ('[' or '{' or '"' or '\''))
            {
                // "- key: value" starts a mapping whose further keys are indented like the first key.
                // Rewrite the line as if the dash were a space, then parse the mapping at the key's column.
                lines[index] = new Line(line.Number, itemIndent, new string(' ', itemIndent) + content[(itemIndent - indent)..]);
                items.Add(ParseMapping(lines, ref index, itemIndent, depth + 1));
            }
            else
            {
                index++;
                items.Add(ParseInline(rest, line.Number, lines, ref index, indent, depth + 1));
            }
        }

        return new YamlSequence(items, startLine);
    }

    private static YamlMapping ParseMapping(List<Line> lines, ref int index, int indent, int depth)
    {
        var entries = new List<KeyValuePair<string, YamlNode>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int startLine = lines[index].Number;
        while (true)
        {
            SkipBlank(lines, ref index);
            if (index >= lines.Count || lines[index].Indent < indent)
            {
                break;
            }

            Line line = lines[index];
            if (line.Indent > indent)
            {
                throw new YamlException("unexpected indentation in a mapping", line.Number);
            }

            string content = StripComment(line.Text[indent..]);
            if (content.StartsWith("- ", StringComparison.Ordinal) || content == "-")
            {
                break;
            }

            int colon = FindMappingColon(content);
            if (colon <= 0)
            {
                throw new YamlException($"expected 'key: value', found '{content.Trim()}'", line.Number);
            }

            string key = UnquoteKey(content[..colon].Trim(), line.Number);
            if (!seen.Add(key))
            {
                throw new YamlException($"duplicate key '{key}'", line.Number);
            }

            string rest = content[(colon + 1)..].Trim();
            index++;
            YamlNode value;
            if (rest.Length == 0)
            {
                SkipBlank(lines, ref index);
                if (index < lines.Count && (lines[index].Indent > indent ||
                    (lines[index].Indent == indent && lines[index].Text[indent..].StartsWith('-'))))
                {
                    value = ParseBlock(lines, ref index, lines[index].Indent, depth + 1);
                }
                else
                {
                    value = new YamlScalar(string.Empty, false, line.Number);
                }
            }
            else
            {
                value = ParseInline(rest, line.Number, lines, ref index, indent, depth + 1);
            }

            entries.Add(new KeyValuePair<string, YamlNode>(key, value));
        }

        return new YamlMapping(entries, startLine);
    }

    private static YamlNode ParseInline(string rest, int lineNumber, List<Line> lines, ref int index, int parentIndent, int depth)
    {
        if (rest is "|" or "|-")
        {
            return ParseLiteralBlock(lines, ref index, parentIndent, lineNumber, keepFinalNewline: rest == "|");
        }

        if (rest[0] is '[' or '{')
        {
            int pos = 0;
            YamlNode node = ParseFlow(rest, ref pos, lineNumber, depth);
            if (rest[pos..].Trim().Length != 0)
            {
                throw new YamlException($"unexpected text after flow collection: '{rest[pos..].Trim()}'", lineNumber);
            }

            return node;
        }

        return ParseScalar(rest, lineNumber);
    }

    private static YamlScalar ParseLiteralBlock(List<Line> lines, ref int index, int parentIndent, int lineNumber, bool keepFinalNewline)
    {
        var text = new StringBuilder();
        int blockIndent = -1;
        while (index < lines.Count)
        {
            Line line = lines[index];
            bool blank = line.Text.Trim().Length == 0;
            if (!blank && line.Indent <= parentIndent)
            {
                break;
            }

            if (!blank && blockIndent < 0)
            {
                blockIndent = line.Indent;
            }

            text.Append(blank ? string.Empty : line.Text[Math.Min(blockIndent, line.Indent)..]).Append('\n');
            index++;
        }

        string value = text.ToString().TrimEnd('\n');
        return new YamlScalar(keepFinalNewline && value.Length > 0 ? value + "\n" : value, true, lineNumber);
    }

    private static YamlNode ParseFlow(string text, ref int pos, int line, int depth)
    {
        CheckDepth(depth, line);
        SkipSpaces(text, ref pos);
        if (pos >= text.Length)
        {
            throw new YamlException("unterminated flow collection", line);
        }

        char open = text[pos];
        if (open is '[' or '{')
        {
            pos++;
            char close = open == '[' ? ']' : '}';
            var items = new List<YamlNode>();
            var entries = new List<KeyValuePair<string, YamlNode>>();
            SkipSpaces(text, ref pos);
            if (pos < text.Length && text[pos] == close)
            {
                pos++;
                return open == '[' ? new YamlSequence(items, line) : new YamlMapping(entries, line);
            }

            while (true)
            {
                if (open == '{')
                {
                    YamlNode keyNode = ParseFlowScalar(text, ref pos, line, untilColon: true);
                    SkipSpaces(text, ref pos);
                    if (pos >= text.Length || text[pos] != ':')
                    {
                        throw new YamlException("expected ':' in flow mapping", line);
                    }

                    pos++;
                    string key = ((YamlScalar)keyNode).Value;
                    if (entries.Exists(e => e.Key == key))
                    {
                        throw new YamlException($"duplicate key '{key}'", line);
                    }

                    entries.Add(new KeyValuePair<string, YamlNode>(key, ParseFlowValue(text, ref pos, line, depth + 1)));
                }
                else
                {
                    items.Add(ParseFlowValue(text, ref pos, line, depth + 1));
                }

                SkipSpaces(text, ref pos);
                if (pos >= text.Length)
                {
                    throw new YamlException("unterminated flow collection", line);
                }

                if (text[pos] == ',')
                {
                    pos++;
                    continue;
                }

                if (text[pos] == close)
                {
                    pos++;
                    return open == '[' ? new YamlSequence(items, line) : new YamlMapping(entries, line);
                }

                throw new YamlException($"unexpected '{text[pos]}' in flow collection", line);
            }
        }

        return ParseFlowScalar(text, ref pos, line, untilColon: false);
    }

    private static YamlNode ParseFlowValue(string text, ref int pos, int line, int depth)
    {
        SkipSpaces(text, ref pos);
        return pos < text.Length && text[pos] is '[' or '{' ? ParseFlow(text, ref pos, line, depth) : ParseFlowScalar(text, ref pos, line, untilColon: false);
    }

    private static YamlScalar ParseFlowScalar(string text, ref int pos, int line, bool untilColon)
    {
        SkipSpaces(text, ref pos);
        if (pos < text.Length && text[pos] is '"' or '\'')
        {
            int end = FindClosingQuote(text, pos, line);
            YamlScalar quoted = ParseScalar(text[pos..(end + 1)], line);
            pos = end + 1;
            return quoted;
        }

        int start = pos;
        while (pos < text.Length && text[pos] is not (',' or ']' or '}') && !(untilColon && text[pos] == ':'))
        {
            pos++;
        }

        return ParseScalar(text[start..pos].Trim(), line);
    }

    private static int FindClosingQuote(string text, int start, int line)
    {
        char quote = text[start];
        for (int i = start + 1; i < text.Length; i++)
        {
            if (quote == '"' && text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == quote)
            {
                if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                return i;
            }
        }

        throw new YamlException("unterminated quoted string", line);
    }

    private static void SkipSpaces(string text, ref int pos)
    {
        while (pos < text.Length && text[pos] == ' ')
        {
            pos++;
        }
    }

    private static YamlScalar ParseScalar(string text, int line)
    {
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            return new YamlScalar(text[1..^1].Replace("''", "'", StringComparison.Ordinal), true, line);
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            var sb = new StringBuilder();
            for (int i = 1; i < text.Length - 1; i++)
            {
                char c = text[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (++i >= text.Length - 1)
                {
                    throw new YamlException("dangling escape in a double-quoted string", line);
                }

                sb.Append(text[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    _ => throw new YamlException($"unsupported escape '\\{text[i]}'", line),
                });
            }

            return new YamlScalar(sb.ToString(), true, line);
        }

        if (text.Length > 0 && text[0] is '"' or '\'')
        {
            throw new YamlException("unterminated quoted string", line);
        }

        if (text.Length > 0 && text[0] is '&' or '*' or '!' or '%' or '@' or '`')
        {
            throw new YamlException($"unsupported YAML feature '{text[0]}'", line);
        }

        return new YamlScalar(text, false, line);
    }

    /// <summary>Index of the ':' that separates key and value (followed by a space or the end), outside quotes.</summary>
    private static int FindMappingColon(string text)
    {
        char quote = '\0';
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'' when i == 0:
                    quote = c;
                    break;
                case '[' or '{':
                    depth++;
                    break;
                case ']' or '}':
                    depth--;
                    break;
                case ':' when depth == 0 && (i + 1 == text.Length || text[i + 1] == ' '):
                    return i;
            }
        }

        return -1;
    }

    private static string UnquoteKey(string key, int line)
    {
        YamlScalar scalar = ParseScalar(key, line);
        if (scalar.Value.Length == 0)
        {
            throw new YamlException("empty key", line);
        }

        return scalar.Value;
    }

    /// <summary>Removes a trailing <c># comment</c> (a '#' at the start or after a space, outside quotes).</summary>
    private static string StripComment(string text)
    {
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'' && (i == 0 || text[i - 1] is ' ' or '[' or '{' or ','))
            {
                quote = c;
            }
            else if (c == '#' && (i == 0 || text[i - 1] == ' '))
            {
                return text[..i].TrimEnd();
            }
        }

        return text.TrimEnd();
    }

    // ---- typed accessors -------------------------------------------------------------------------------------------

    /// <summary>The scalar value of <paramref name="node"/>.</summary>
    /// <exception cref="YamlException">It is not of that kind; the message names <paramref name="what"/>.</exception>
    public static string AsString(YamlNode node, string what) => node is YamlScalar s
        ? s.Value
        : throw new YamlException($"{what}: expected a scalar", node.Line);

    /// <summary>The integer value (decimal or <c>0x</c> hex) of <paramref name="node"/>.</summary>
    /// <exception cref="YamlException">It is not of that kind; the message names <paramref name="what"/>.</exception>
    public static int AsInt(YamlNode node, string what)
    {
        string text = AsString(node, what);
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(hex ? text[2..] : text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new YamlException($"{what}: '{text}' is not an integer", node.Line);
    }

    /// <summary>The boolean value (<c>true/yes/on</c>, <c>false/no/off</c>) of <paramref name="node"/>.</summary>
    /// <exception cref="YamlException">It is not of that kind; the message names <paramref name="what"/>.</exception>
    public static bool AsBool(YamlNode node, string what) => AsString(node, what) switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        { } other => throw new YamlException($"{what}: '{other}' is not true/false", node.Line),
    };

    /// <summary><paramref name="node"/> as a mapping.</summary>
    /// <exception cref="YamlException">It is not of that kind; the message names <paramref name="what"/>.</exception>
    public static YamlMapping AsMapping(YamlNode node, string what) => node as YamlMapping
        ?? throw new YamlException($"{what}: expected a mapping", node.Line);

    /// <summary><paramref name="node"/> as a sequence.</summary>
    /// <exception cref="YamlException">It is not of that kind; the message names <paramref name="what"/>.</exception>
    public static YamlSequence AsSequence(YamlNode node, string what) => node as YamlSequence
        ?? throw new YamlException($"{what}: expected a list", node.Line);
}
