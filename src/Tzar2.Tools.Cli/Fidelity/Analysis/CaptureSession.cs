using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Tzar2.Tools.Cli.Fidelity.Imaging;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>A capture of a trial as recorded in <c>trial.json</c>.</summary>
internal sealed record CaptureData(string Kind, string Name, string File, double TimeMs, IReadOnlyDictionary<string, string> Tags,
    (double U, double V)? Minimap, int Width, int Height, string Format, IReadOnlyList<(string File, double TimeMs)> Frames,
    (int X, int Y, int W, int H)? Region);

/// <summary>A trial of a capture session (<c>scenarios/&lt;id&gt;/&lt;trial&gt;/trial.json</c>).</summary>
internal sealed record TrialData(string Scenario, string Trial, string Status, IReadOnlyDictionary<string, string> Vars,
    IReadOnlyDictionary<string, string> Ini, IReadOnlyDictionary<string, string> Wrapper, int ClientWidth, int ClientHeight,
    IReadOnlyList<CaptureData> Captures, string Directory)
{
    public string? Var(string name) => Vars.TryGetValue(name, out string? v) ? v : null;

    public IEnumerable<CaptureData> Of(string kind) => Captures.Where(c => c.Kind == kind);
}

/// <summary>
/// Read access to a capture session: the zip the operator sends back, or the session directory before zipping.
/// Paths inside are forward-slash relative paths.
/// </summary>
internal sealed class CaptureSession : IDisposable
{
    private readonly ZipArchive? _zip;
    private readonly string? _directory;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);

    private CaptureSession(ZipArchive? zip, string? directory)
    {
        _zip = zip;
        _directory = directory;
        if (zip is not null)
        {
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                _entries[e.FullName.Replace('\\', '/')] = e;
            }
        }

        Manifest = Exists("session.json") ? JsonDocument.Parse(ReadAllBytes("session.json")) : JsonDocument.Parse("{}");
        Trials = LoadTrials();
    }

    public JsonDocument Manifest { get; }

    public IReadOnlyList<TrialData> Trials { get; }

    public string Name { get; private set; } = "session";

    public static CaptureSession Open(string path)
    {
        if (Directory.Exists(path))
        {
            return new CaptureSession(null, path) { Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) };
        }

        ZipArchive zip = ZipFile.OpenRead(path);
        try
        {
            return new CaptureSession(zip, null) { Name = Path.GetFileNameWithoutExtension(path) };
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    public bool Exists(string relative) => _zip is not null ? _entries.ContainsKey(relative) : File.Exists(Path.Combine(_directory!, relative));

    public byte[] ReadAllBytes(string relative)
    {
        if (_zip is null)
        {
            return File.ReadAllBytes(Path.Combine(_directory!, relative));
        }

        if (!_entries.TryGetValue(relative, out ZipArchiveEntry? entry))
        {
            throw new FileNotFoundException($"'{relative}' is not in the capture archive");
        }

        using Stream s = entry.Open();
        using var memory = new MemoryStream();
        s.CopyTo(memory);
        return memory.ToArray();
    }

    public string? ReadText(string relative) => Exists(relative) ? System.Text.Encoding.UTF8.GetString(ReadAllBytes(relative)) : null;

    /// <summary>Decodes a BMP capture of a trial (path relative to the trial directory).</summary>
    public CaptureImage ReadImage(TrialData trial, string file) => CaptureImage.DecodeBmp(ReadAllBytes(trial.Directory + "/" + file));

    public byte[] ReadTrialFile(TrialData trial, string file) => ReadAllBytes(trial.Directory + "/" + file);

    /// <summary>A string property of <c>session.json</c> by dotted path, or null.</summary>
    public string? ManifestString(string dottedPath)
    {
        JsonElement e = Manifest.RootElement;
        foreach (string part in dottedPath.Split('.'))
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(part, out e))
            {
                return null;
            }
        }

        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private IEnumerable<string> AllFiles() => _zip is not null
        ? _entries.Keys
        : System.IO.Directory.EnumerateFiles(_directory!, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_directory!, f).Replace('\\', '/'));

    private List<TrialData> LoadTrials()
    {
        var trials = new List<TrialData>();
        foreach (string file in AllFiles().Where(f => f.StartsWith("scenarios/", StringComparison.Ordinal) && f.EndsWith("/trial.json", StringComparison.Ordinal))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(ReadAllBytes(file));
            JsonElement r = doc.RootElement;
            var captures = new List<CaptureData>();
            foreach (JsonElement c in r.GetProperty("captures").EnumerateArray())
            {
                (double, double)? minimap = c.TryGetProperty("minimap", out JsonElement mm) ? (mm[0].GetDouble(), mm[1].GetDouble()) : null;
                (int, int, int, int)? region = c.TryGetProperty("region", out JsonElement rg) ? (rg[0].GetInt32(), rg[1].GetInt32(), rg[2].GetInt32(), rg[3].GetInt32()) : null;
                var frames = c.TryGetProperty("frames", out JsonElement fr)
                    ? fr.EnumerateArray().Select(f => (f.GetProperty("file").GetString()!, f.GetProperty("t_ms").GetDouble())).ToList()
                    : [];
                captures.Add(new CaptureData(
                    c.GetProperty("kind").GetString()!,
                    c.GetProperty("name").GetString()!,
                    c.GetProperty("file").GetString()!,
                    c.GetProperty("t_ms").GetDouble(),
                    Map(c, "tags"),
                    minimap,
                    c.GetProperty("width").GetInt32(),
                    c.GetProperty("height").GetInt32(),
                    c.GetProperty("format").GetString()!,
                    frames,
                    region));
            }

            JsonElement client = r.GetProperty("client");
            trials.Add(new TrialData(
                r.GetProperty("scenario").GetString()!,
                r.GetProperty("trial").GetString()!,
                r.GetProperty("status").GetString()!,
                Map(r, "vars"),
                Map(r, "ini"),
                Map(r, "wrapper"),
                client.GetProperty("width").GetInt32(),
                client.GetProperty("height").GetInt32(),
                captures,
                file[..^"/trial.json".Length]));
        }

        return trials;
    }

    private static Dictionary<string, string> Map(JsonElement parent, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parent.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in e.EnumerateObject())
            {
                result[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            }
        }

        return result;
    }

    public void Dispose()
    {
        Manifest.Dispose();
        _zip?.Dispose();
    }

    internal static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
