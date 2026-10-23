using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tzar2.Game.Diagnostics;

/// <summary>The machine a report comes from (no personal data: no machine or user name).</summary>
public sealed record SystemInfo(
    string OsName,
    string OsVersion,
    string Cpu,
    int CpuThreads,
    string Gpu,
    string GpuVendor,
    string Godot,
    string Renderer)
{
    /// <summary>What a test or a headless run reports.</summary>
    public static SystemInfo Unknown { get; } = new(System.Environment.OSVersion.Platform.ToString(), System.Environment.OSVersion.VersionString, "?", System.Environment.ProcessorCount, "?", "?", "?", "?");
}

/// <summary>One exception of a report: its type, message and frames (oldest call first, as Sentry wants them).</summary>
public sealed record ReportedException(string Type, string Message, IReadOnlyList<ReportedFrame> Frames, string Mechanism, bool Handled);

/// <summary>A stack frame of a report.</summary>
public sealed record ReportedFrame(string Function, string? File, int Line, bool InApp);

/// <summary>A file sent with a report.</summary>
public sealed record ReportAttachment(string FileName, string ContentType, byte[] Data);

/// <summary>
/// Builds Sentry envelopes (https://develop.sentry.dev/sdk/envelopes/; ADR 0243): an event item with its attachments, or
/// a session item for the crash-free rate. Text is scrubbed of the user's folder names before it goes in.
/// </summary>
public static class SentryEnvelope
{
    /// <summary>The logger and SDK name in events.</summary>
    public const string Sdk = "tzar2";

    private static readonly Regex UserFolder = new(@"(?i)([\\/](?:Users|home|Documents and Settings)[\\/])[^\\/\r\n:*?""<>|]+", RegexOptions.CultureInvariant);
    private static readonly Regex FrameLine = new(@"^\s*at (?<fn>.+?)(?: in (?<file>.+):line (?<line>\d+))?\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex HeadLine = new(@"^(?<type>[A-Za-z_][\w.`+]*(?:Exception|Error))(?:: (?<msg>.*))?$", RegexOptions.CultureInvariant);

    /// <summary>Replaces the user's folder name in paths (<c>C:\Users\name\…</c>, <c>/home/name/…</c>) with <c>&lt;user&gt;</c>.</summary>
    public static string Scrub(string? text) => UserFolder.Replace(text ?? string.Empty, "$1<user>");

    /// <summary>The chain of <paramref name="exception"/>, innermost (the cause) first, as Sentry orders them.</summary>
    public static IReadOnlyList<ReportedException> FromException(Exception exception, string mechanism, bool handled)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var chain = new List<ReportedException>();
        for (Exception? e = exception; e is not null && chain.Count < 10; e = e.InnerException)
        {
            chain.Add(new ReportedException(e.GetType().FullName ?? e.GetType().Name, Scrub(e.Message), Frames(e.StackTrace), mechanism, handled));
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// An exception from its text (<c>Exception.ToString()</c>, as Godot logs the exceptions it catches in our
    /// callbacks): the first line's type and message and the <c>at …</c> frames; null when the text is not one.
    /// </summary>
    public static ReportedException? FromText(string text, string mechanism, bool handled)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        Match head = HeadLine.Match(lines[0].Trim());
        if (!head.Success)
        {
            return null;
        }

        return new ReportedException(head.Groups["type"].Value, Scrub(head.Groups["msg"].Value), Frames(text), mechanism, handled);
    }

    /// <summary>Whether a logged error is a .NET exception's text (see <see cref="FromText"/>).</summary>
    public static bool IsExceptionText(string? text)
        => !string.IsNullOrWhiteSpace(text) && HeadLine.IsMatch(text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')[0].Trim());

    /// <summary>The frames of a .NET stack trace text, oldest call first.</summary>
    public static IReadOnlyList<ReportedFrame> Frames(string? stackTrace)
    {
        var frames = new List<ReportedFrame>();
        foreach (string line in (stackTrace ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            Match m = FrameLine.Match(line);
            if (!m.Success)
            {
                continue;
            }

            string function = m.Groups["fn"].Value;
            string? file = m.Groups["file"].Success ? Path.GetFileName(m.Groups["file"].Value.Replace('\\', '/')) : null;
            int lineNo = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture) : 0;
            frames.Add(new ReportedFrame(function, file, lineNo, function.StartsWith("Tzar2.", StringComparison.Ordinal)));
        }

        frames.Reverse();
        return frames;
    }

    /// <summary>An envelope with one event (a crash, an error or an abnormal exit) and its attachments.</summary>
    public static byte[] Event(
        string eventId,
        DateTime timeUtc,
        string level,
        string message,
        IReadOnlyList<ReportedException> exceptions,
        string release,
        string environment,
        SystemInfo system,
        IReadOnlyDictionary<string, string> tags,
        IReadOnlyList<ReportAttachment> attachments,
        string? dsn)
    {
        byte[] payload = Json(w =>
        {
            w.WriteString("event_id", eventId);
            w.WriteString("timestamp", Iso(timeUtc));
            w.WriteString("platform", "csharp");
            w.WriteString("level", level);
            w.WriteString("logger", Sdk);
            w.WriteString("release", release);
            w.WriteString("environment", environment);
            w.WriteStartObject("sdk");
            w.WriteString("name", Sdk);
            w.WriteString("version", GameInfo.Version);
            w.WriteEndObject();
            if (message.Length > 0)
            {
                w.WriteStartObject("message");
                w.WriteString("formatted", Scrub(message));
                w.WriteEndObject();
            }

            if (exceptions.Count > 0)
            {
                w.WriteStartObject("exception");
                w.WriteStartArray("values");
                foreach (ReportedException e in exceptions)
                {
                    w.WriteStartObject();
                    w.WriteString("type", e.Type);
                    w.WriteString("value", e.Message);
                    w.WriteStartObject("mechanism");
                    w.WriteString("type", e.Mechanism);
                    w.WriteBoolean("handled", e.Handled);
                    w.WriteEndObject();
                    if (e.Frames.Count > 0)
                    {
                        w.WriteStartObject("stacktrace");
                        w.WriteStartArray("frames");
                        foreach (ReportedFrame f in e.Frames)
                        {
                            w.WriteStartObject();
                            w.WriteString("function", f.Function);
                            if (f.File is not null)
                            {
                                w.WriteString("filename", f.File);
                            }

                            if (f.Line > 0)
                            {
                                w.WriteNumber("lineno", f.Line);
                            }

                            w.WriteBoolean("in_app", f.InApp);
                            w.WriteEndObject();
                        }

                        w.WriteEndArray();
                        w.WriteEndObject();
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteStartObject("contexts");
            w.WriteStartObject("os");
            w.WriteString("name", system.OsName);
            w.WriteString("version", system.OsVersion);
            w.WriteEndObject();
            w.WriteStartObject("runtime");
            w.WriteString("name", ".NET");
            w.WriteString("version", System.Environment.Version.ToString());
            w.WriteEndObject();
            w.WriteStartObject("device");
            w.WriteString("cpu_description", system.Cpu);
            w.WriteNumber("processor_count", system.CpuThreads);
            w.WriteEndObject();
            w.WriteStartObject("gpu");
            w.WriteString("name", system.Gpu);
            w.WriteString("vendor_name", system.GpuVendor);
            w.WriteEndObject();
            w.WriteStartObject("app");
            w.WriteString("app_name", GameInfo.Codename);
            w.WriteString("app_version", GameInfo.Version);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("tags");
            w.WriteString("godot", system.Godot);
            w.WriteString("renderer", system.Renderer);
            foreach (KeyValuePair<string, string> tag in tags)
            {
                w.WriteString(tag.Key, Scrub(tag.Value));
            }

            w.WriteEndObject();
        });

        using var envelope = new MemoryStream();
        WriteLine(envelope, Json(w =>
        {
            w.WriteString("event_id", eventId);
            if (dsn is not null)
            {
                w.WriteString("dsn", dsn);
            }
        }));
        WriteItem(envelope, "event", payload, null, null);
        foreach (ReportAttachment attachment in attachments)
        {
            WriteItem(envelope, "attachment", attachment.Data, attachment.FileName, attachment.ContentType);
        }

        return envelope.ToArray();
    }

    /// <summary>
    /// An envelope with one finished session (Sentry release health: <paramref name="status"/> is <c>exited</c>,
    /// <c>crashed</c> or <c>abnormal</c>), which is what the beta's crash-free rate counts.
    /// </summary>
    public static byte[] Session(string sessionId, DateTime startedUtc, DateTime endedUtc, string status, int errors, string release, string environment, string? dsn)
    {
        byte[] payload = Json(w =>
        {
            w.WriteString("sid", sessionId);
            w.WriteBoolean("init", true);
            w.WriteString("started", Iso(startedUtc));
            w.WriteString("timestamp", Iso(endedUtc));
            w.WriteString("status", status);
            w.WriteNumber("errors", errors);
            w.WriteNumber("duration", Math.Max(0, (endedUtc - startedUtc).TotalSeconds));
            w.WriteStartObject("attrs");
            w.WriteString("release", release);
            w.WriteString("environment", environment);
            w.WriteEndObject();
        });

        using var envelope = new MemoryStream();
        WriteLine(envelope, Json(w =>
        {
            if (dsn is not null)
            {
                w.WriteString("dsn", dsn);
            }
        }));
        WriteItem(envelope, "session", payload, null, null);
        return envelope.ToArray();
    }

    private static void WriteItem(Stream envelope, string type, byte[] payload, string? fileName, string? contentType)
    {
        WriteLine(envelope, Json(w =>
        {
            w.WriteString("type", type);
            w.WriteNumber("length", payload.Length);
            if (fileName is not null)
            {
                w.WriteString("filename", fileName);
                w.WriteString("content_type", contentType ?? "application/octet-stream");
            }
        }));
        WriteLine(envelope, payload);
    }

    private static void WriteLine(Stream stream, byte[] data)
    {
        stream.Write(data);
        stream.WriteByte((byte)'\n');
    }

    private static byte[] Json(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A UTF-8 text attachment, scrubbed.</summary>
    public static ReportAttachment Text(string fileName, string text) => new(fileName, "text/plain", new UTF8Encoding(false).GetBytes(Scrub(text)));
}
