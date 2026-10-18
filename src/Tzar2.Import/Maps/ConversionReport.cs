using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tzar2.Import.Maps;

/// <summary>Severity of a conversion message (REQ-EDT-06).</summary>
public enum DiagnosticSeverity
{
    /// <summary>Information (counts, heuristics used).</summary>
    Info,

    /// <summary>Something was approximated, defaulted or dropped; the result is usable.</summary>
    Warning,

    /// <summary>The source could not be converted (or a part of it was lost); the result is not usable as is.</summary>
    Error,
}

/// <summary>One message of a conversion report.</summary>
/// <param name="Severity">Severity.</param>
/// <param name="Code">Stable short code (filterable), e.g. <c>object.type</c>.</param>
/// <param name="Message">Human-readable text.</param>
/// <param name="Location">Where in the source/result: e.g. <c>action 'Spawn group1'</c>, <c>object #12</c>.</param>
public sealed record ConversionDiagnostic(DiagnosticSeverity Severity, string Code, string Message, string Location);

/// <summary>
/// The conversion report of one map or campaign (REQ-EDT-06): provenance, detected code page and language, converted
/// counts, and diagnostics in the order they were found. Deterministic: no timestamps or machine data, so it can be
/// stored in the package (<c>conversion-report.json</c>).
/// </summary>
public sealed class ConversionReport
{
    private readonly List<ConversionDiagnostic> _diagnostics = [];
    private readonly SortedDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly List<ConversionReport> _children = [];

    /// <summary>Creates a report.</summary>
    /// <param name="kind"><c>map</c> or <c>campaign</c>.</param>
    /// <param name="source">Source file name (no directory).</param>
    public ConversionReport(string kind, string source)
    {
        Kind = kind;
        Source = source;
    }

    /// <summary>The converter version written into reports and packages.</summary>
    public const string ConverterVersion = "1.0.0";

    /// <summary><c>map</c> or <c>campaign</c>.</summary>
    public string Kind { get; }

    /// <summary>Source file name.</summary>
    public string Source { get; }

    /// <summary>Source size in bytes.</summary>
    public long SourceSize { get; set; }

    /// <summary>MD5 of the source (lower-case hex), empty for embedded maps.</summary>
    public string SourceMd5 { get; set; } = string.Empty;

    /// <summary>Code page used for text (e.g. <c>1252</c>).</summary>
    public int CodePage { get; set; }

    /// <summary>How the code page was chosen (<c>ascii</c>, <c>detected</c>, <c>override</c>, <c>inherited</c>).</summary>
    public string CodePageSource { get; set; } = string.Empty;

    /// <summary>Whether the detection was ambiguous.</summary>
    public bool CodePageAmbiguous { get; set; }

    /// <summary>Source language tag (heuristic, e.g. <c>en</c>, <c>ru</c>).</summary>
    public string Language { get; set; } = "en";

    /// <summary>Converted counts by name.</summary>
    public IReadOnlyDictionary<string, int> Counts => _counts;

    /// <summary>Diagnostics in order.</summary>
    public IReadOnlyList<ConversionDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Reports of the maps of a campaign.</summary>
    public IReadOnlyList<ConversionReport> Children => _children;

    /// <summary>Errors here and in children.</summary>
    public int ErrorCount => _diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) + _children.Sum(c => c.ErrorCount);

    /// <summary>Warnings here and in children.</summary>
    public int WarningCount => _diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning) + _children.Sum(c => c.WarningCount);

    /// <summary>Whether the conversion succeeded (no errors here or in children).</summary>
    public bool Succeeded => ErrorCount == 0;

    /// <summary>Adds a message.</summary>
    public void Add(DiagnosticSeverity severity, string code, string message, string location = "")
        => _diagnostics.Add(new ConversionDiagnostic(severity, code, message, location));

    /// <summary>Adds a warning.</summary>
    public void Warn(string code, string message, string location = "") => Add(DiagnosticSeverity.Warning, code, message, location);

    /// <summary>Adds an error.</summary>
    public void Error(string code, string message, string location = "") => Add(DiagnosticSeverity.Error, code, message, location);

    /// <summary>Adds information.</summary>
    public void Info(string code, string message, string location = "") => Add(DiagnosticSeverity.Info, code, message, location);

    /// <summary>Sets a count.</summary>
    public void Count(string name, int value) => _counts[name] = value;

    /// <summary>Adds to a count.</summary>
    public void Increment(string name, int by = 1) => _counts[name] = _counts.TryGetValue(name, out int v) ? v + by : by;

    /// <summary>Adds a child (map of a campaign).</summary>
    public void AddChild(ConversionReport child) => _children.Add(child);

    /// <summary>The report as indented UTF-8 JSON (LF newlines).</summary>
    public byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            Write(w);
        }

        byte[] bytes = buffer.WrittenSpan.ToArray();
        return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    /// <summary>A human-readable summary.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        WriteText(sb, string.Empty);
        return sb.ToString();
    }

    private void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("kind", Kind);
        w.WriteString("source", Source);
        w.WriteNumber("sourceSize", SourceSize);
        w.WriteString("sourceMd5", SourceMd5);
        w.WriteString("converterVersion", ConverterVersion);
        w.WriteNumber("codePage", CodePage);
        w.WriteString("codePageSource", CodePageSource);
        w.WriteBoolean("codePageAmbiguous", CodePageAmbiguous);
        w.WriteString("language", Language);
        w.WriteNumber("errors", ErrorCount);
        w.WriteNumber("warnings", WarningCount);
        w.WriteStartObject("counts");
        foreach (KeyValuePair<string, int> c in _counts)
        {
            w.WriteNumber(c.Key, c.Value);
        }

        w.WriteEndObject();
        w.WriteStartArray("diagnostics");
        foreach (ConversionDiagnostic d in _diagnostics)
        {
            w.WriteStartObject();
            w.WriteString("severity", d.Severity.ToString().ToLowerInvariant());
            w.WriteString("code", d.Code);
            w.WriteString("message", d.Message);
            w.WriteString("location", d.Location);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        if (_children.Count > 0)
        {
            w.WriteStartArray("maps");
            foreach (ConversionReport child in _children)
            {
                child.Write(w);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private void WriteText(StringBuilder sb, string indent)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        sb.Append(indent).Append(Kind).Append(' ').Append(Source).Append(": ")
            .Append(Succeeded ? "OK" : "FAILED").Append(ci, $" ({ErrorCount} errors, {WarningCount} warnings), code page {CodePage}")
            .Append(CodePageAmbiguous ? " (ambiguous)" : string.Empty).Append(", language ").Append(Language).Append('\n');
        if (_counts.Count > 0)
        {
            sb.Append(indent).Append("  counts: ").Append(string.Join(", ", _counts.Select(c => c.Key + " " + c.Value.ToString(ci)))).Append('\n');
        }

        foreach (ConversionDiagnostic d in _diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info))
        {
            sb.Append(indent).Append("  ").Append(d.Severity == DiagnosticSeverity.Error ? "error" : "warning").Append(' ')
                .Append(d.Code).Append(": ").Append(d.Message);
            if (d.Location.Length > 0)
            {
                sb.Append(" [").Append(d.Location).Append(']');
            }

            sb.Append('\n');
        }

        foreach (ConversionReport child in _children)
        {
            child.WriteText(sb, indent + "  ");
        }
    }
}
