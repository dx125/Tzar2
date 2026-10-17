using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Tzar2.Tools.Cli.Fidelity.Scenarios;

namespace Tzar2.Tools.Cli.Fidelity.Capture;

internal sealed class StepRecord(int index, int line, string kind, double timeMs)
{
    public int Index { get; } = index;

    public int Line { get; } = line;

    public string Kind { get; } = kind;

    public double TimeMs { get; } = timeMs;

    public string Status { get; set; } = "pending";

    public string? Error { get; set; }
}

/// <summary>One capture of a trial: a game snapshot, a window capture, a burst, a memory scan or a memory sample file.</summary>
internal sealed record CaptureRecord(string Kind, string Name, string File, double TimeMs, IReadOnlyDictionary<string, string> Tags,
    (double U, double V)? Camera, int Width, int Height, string Format)
{
    public IReadOnlyList<(string File, double TimeMs)> Frames { get; init; } = [];

    public ScreenRect? Region { get; init; }
}

/// <summary>Everything about one trial, written as <c>trial.json</c> (schema <c>tzar2-capture-trial/1</c>).</summary>
internal sealed class TrialRecord(string scenario, string trial, IReadOnlyDictionary<string, string> vars, IReadOnlyDictionary<string, string> ini,
    IReadOnlyDictionary<string, string> wrapper, IReadOnlyList<string> launch)
{
    public const string Schema = "tzar2-capture-trial/1";

    public long Epoch { get; set; }

    public int ClientWidth { get; set; }

    public int ClientHeight { get; set; }

    public int? ExitCode { get; set; }

    public string Status { get; set; } = "ok";

    public string? Error { get; set; }

    public List<StepRecord> Steps { get; } = [];

    public List<CaptureRecord> Captures { get; } = [];

    public List<string> Notes { get; } = [];

    public void Write(string path)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("schema", Schema);
        w.WriteString("scenario", scenario);
        w.WriteString("trial", trial);
        w.WriteString("status", Status);
        if (Error is not null)
        {
            w.WriteString("error", Error);
        }

        if (ExitCode is { } code)
        {
            w.WriteNumber("exit_code", code);
        }

        w.WriteStartObject("client");
        w.WriteNumber("width", ClientWidth);
        w.WriteNumber("height", ClientHeight);
        w.WriteEndObject();
        WriteMap(w, "vars", vars);
        WriteMap(w, "ini", ini);
        WriteMap(w, "wrapper", wrapper);
        w.WriteStartArray("launch");
        foreach (string a in launch)
        {
            w.WriteStringValue(a);
        }

        w.WriteEndArray();
        w.WriteStartArray("steps");
        foreach (StepRecord s in Steps)
        {
            w.WriteStartObject();
            w.WriteNumber("index", s.Index);
            w.WriteNumber("line", s.Line);
            w.WriteString("kind", s.Kind);
            w.WriteNumber("t_ms", s.TimeMs);
            w.WriteString("status", s.Status);
            if (s.Error is not null)
            {
                w.WriteString("error", s.Error);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("captures");
        foreach (CaptureRecord c in Captures)
        {
            w.WriteStartObject();
            w.WriteString("kind", c.Kind);
            w.WriteString("name", c.Name);
            w.WriteString("file", c.File.Replace('\\', '/'));
            w.WriteNumber("t_ms", c.TimeMs);
            w.WriteNumber("width", c.Width);
            w.WriteNumber("height", c.Height);
            w.WriteString("format", c.Format);
            if (c.Camera is { } cam)
            {
                w.WriteStartArray("minimap");
                w.WriteNumberValue(cam.U);
                w.WriteNumberValue(cam.V);
                w.WriteEndArray();
            }

            if (c.Region is { } r)
            {
                w.WriteStartArray("region");
                w.WriteNumberValue(r.X);
                w.WriteNumberValue(r.Y);
                w.WriteNumberValue(r.Width);
                w.WriteNumberValue(r.Height);
                w.WriteEndArray();
            }

            WriteMap(w, "tags", c.Tags);
            if (c.Frames.Count > 0)
            {
                w.WriteStartArray("frames");
                foreach ((string file, double t) in c.Frames)
                {
                    w.WriteStartObject();
                    w.WriteString("file", file.Replace('\\', '/'));
                    w.WriteNumber("t_ms", t);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("notes");
        foreach (string n in Notes)
        {
            w.WriteStringValue(n);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteMap(Utf8JsonWriter w, string name, IReadOnlyDictionary<string, string> map)
    {
        w.WriteStartObject(name);
        foreach ((string k, string v) in map)
        {
            w.WriteString(k, v);
        }

        w.WriteEndObject();
    }
}
