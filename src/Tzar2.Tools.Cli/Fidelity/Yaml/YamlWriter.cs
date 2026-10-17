using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Tzar2.Tools.Cli.Fidelity.Yaml;

/// <summary>
/// Builds YAML documents for fidelity facts (TDD §22.3). Values are added in order; nested mappings and lists are
/// <see cref="YamlWriter"/> blocks or <see cref="Flow"/> lists of scalars. The output parses with <see cref="MiniYaml"/>.
/// </summary>
internal sealed class YamlWriter
{
    private readonly List<(string Key, object? Value, string? Comment)> _entries = [];

    /// <summary>A list written in flow style on one line (<c>[1, 2, 3]</c>); items are scalars.</summary>
    public sealed record Flow(IReadOnlyList<object?> Items);

    public YamlWriter Add(string key, object? value, string? comment = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_entries.Exists(e => e.Key == key))
        {
            throw new ArgumentException($"Duplicate key '{key}'.", nameof(key));
        }

        _entries.Add((key, value, comment));
        return this;
    }

    public YamlWriter Map(string key, Action<YamlWriter> build, string? comment = null)
    {
        var child = new YamlWriter();
        build(child);
        return Add(key, child, comment);
    }

    public bool IsEmpty => _entries.Count == 0;

    public string ToYaml(string? headerComment = null)
    {
        var sb = new StringBuilder();
        if (headerComment is not null)
        {
            foreach (string line in headerComment.Split('\n'))
            {
                sb.Append("# ").Append(line).Append('\n');
            }
        }

        Write(sb, 0);
        return sb.ToString();
    }

    private void Write(StringBuilder sb, int indent)
    {
        string pad = new(' ', indent);
        foreach ((string key, object? value, string? comment) in _entries)
        {
            string k = FormatKey(key);
            switch (value)
            {
                case YamlWriter { IsEmpty: true }:
                    sb.Append(pad).Append(k).Append(": {}");
                    AppendComment(sb, comment);
                    break;
                case YamlWriter child:
                    sb.Append(pad).Append(k).Append(':');
                    AppendComment(sb, comment);
                    child.Write(sb, indent + 2);
                    continue;
                case IReadOnlyList<YamlWriter> list when list.Count == 0:
                    sb.Append(pad).Append(k).Append(": []");
                    AppendComment(sb, comment);
                    break;
                case IReadOnlyList<YamlWriter> list:
                    sb.Append(pad).Append(k).Append(':');
                    AppendComment(sb, comment);
                    foreach (YamlWriter item in list)
                    {
                        var itemText = new StringBuilder();
                        item.Write(itemText, indent + 4);
                        string text = itemText.ToString();
                        // "- " replaces the first two of the item's indentation spaces on its first line.
                        sb.Append(pad).Append("  - ").Append(text.Length == 0 ? "{}\n" : text.AsSpan(indent + 4));
                    }

                    continue;
                case IReadOnlyList<Flow> flows when flows.Count == 0:
                    sb.Append(pad).Append(k).Append(": []");
                    AppendComment(sb, comment);
                    break;
                case IReadOnlyList<Flow> flows:
                    sb.Append(pad).Append(k).Append(':');
                    AppendComment(sb, comment);
                    foreach (Flow item in flows)
                    {
                        sb.Append(pad).Append("  - ").Append(FormatScalar(item, inFlow: true)).Append('\n');
                    }

                    continue;
                case Flow flow:
                    sb.Append(pad).Append(k).Append(": [");
                    for (int i = 0; i < flow.Items.Count; i++)
                    {
                        sb.Append(i == 0 ? string.Empty : ", ").Append(FormatScalar(flow.Items[i], inFlow: true));
                    }

                    sb.Append(']');
                    AppendComment(sb, comment);
                    break;
                default:
                    sb.Append(pad).Append(k).Append(": ").Append(FormatScalar(value, inFlow: false));
                    AppendComment(sb, comment);
                    break;
            }
        }
    }

    private static void AppendComment(StringBuilder sb, string? comment)
    {
        if (comment is not null)
        {
            sb.Append("  # ").Append(comment.Replace('\n', ' '));
        }

        sb.Append('\n');
    }

    private static string FormatKey(string key) => NeedsQuotes(key, inFlow: false) ? Quote(key) : key;

    internal static string FormatScalar(object? value, bool inFlow) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        uint u => u.ToString(CultureInfo.InvariantCulture),
        ulong u => u.ToString(CultureInfo.InvariantCulture),
        double d => FormatDouble(d),
        float f => FormatDouble(f),
        Flow f => "[" + string.Join(", ", f.Items.Select(i => FormatScalar(i, inFlow: true))) + "]",
        Hex h => "0x" + h.Value.ToString(h.Digits > 0 ? "X" + h.Digits.ToString(CultureInfo.InvariantCulture) : "X", CultureInfo.InvariantCulture),
        string s => NeedsQuotes(s, inFlow) ? Quote(s) : s,
        _ => Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>An integer written in hexadecimal (<c>0x03E0</c>).</summary>
    public readonly record struct Hex(long Value, int Digits = 4);

    private static string FormatDouble(double d)
    {
        if (!double.IsFinite(d))
        {
            return "null";
        }

        // Round to 6 significant decimals so facts files do not churn on float noise.
        return Math.Round(d, 6).ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static bool NeedsQuotes(string s, bool inFlow)
    {
        if (s.Length == 0 || s != s.Trim() || s is "null" or "~" or "true" or "false" or "yes" or "no" or "on" or "off")
        {
            return true;
        }

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ("-?:,[]{}#&*!|>'\"%@`".Contains(s[0], StringComparison.Ordinal))
        {
            return true;
        }

        foreach (char c in s)
        {
            if (c < ' ' || c == '"' || (inFlow && c is ',' or '[' or ']' or '{' or '}'))
            {
                return true;
            }
        }

        return s.Contains(": ", StringComparison.Ordinal) || s.Contains(" #", StringComparison.Ordinal) || s.EndsWith(':');
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }
}
