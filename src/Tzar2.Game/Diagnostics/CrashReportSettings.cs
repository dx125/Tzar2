using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Tzar2.Game.Diagnostics;

/// <summary>The player's answer to "send crash reports?" (ADR 0243). Nothing is sent unless it is <see cref="Granted"/>.</summary>
public enum CrashReportConsent
{
    /// <summary>Not asked yet: reports wait on this PC.</summary>
    Unasked,

    /// <summary>Reports and session ends are sent to the configured endpoint.</summary>
    Granted,

    /// <summary>Nothing is sent and waiting reports are deleted.</summary>
    Denied,
}

/// <summary>
/// The crash-reporting settings (ADR 0243) in <c>crash-reporting.cfg</c> under the user's data, one <c>key=value</c> per
/// line: <c>consent</c> (unasked, granted, denied), <c>attach_replay</c> (0/1, the crashed match's replay holds the
/// players' in-game names, so it has its own opt-in), <c>dsn</c> (the endpoint) and <c>environment</c>. The endpoint can
/// also come from <c>TZAR2_CRASH_DSN</c> or from <c>crash-reporting.dsn</c> next to the executable (written by the
/// packaging job); an empty one keeps every report on this PC.
/// </summary>
public sealed class CrashReportSettings
{
    /// <summary>The file name under the user's data.</summary>
    public const string FileName = "crash-reporting.cfg";

    /// <summary>The environment variable that overrides the endpoint.</summary>
    public const string DsnVariable = "TZAR2_CRASH_DSN";

    /// <summary>The file next to the executable that holds the build's default endpoint.</summary>
    public const string BuildDsnFile = "crash-reporting.dsn";

    /// <summary>What the player is asked on the first start (and what the settings row explains).</summary>
    public const string Prompt =
        "Send crash reports?\n\n"
        + "When Tzar2 crashes or quits unexpectedly, it can send a report to the developers: the error, the last lines of "
        + "this run's log, the game version, the operating system, processor and graphics card, and whether each session "
        + "ended normally (for the crash-free rate). Paths are shortened so that your user name is not included. "
        + "No account, saved games or other files of yours are included.\n\n"
        + "Reports wait on this PC until you agree. You can change this at any time in Settings.";

    private readonly string _path;

    private CrashReportSettings(string path) => _path = path;

    /// <summary>The player's answer.</summary>
    public CrashReportConsent Consent { get; set; }

    /// <summary>Whether a crash report also carries the crashed match's replay.</summary>
    public bool AttachReplay { get; set; }

    /// <summary>The endpoint from the settings file (empty: none).</summary>
    public string Dsn { get; set; } = string.Empty;

    /// <summary>The release environment reported (empty: <see cref="DefaultEnvironment"/>).</summary>
    public string Environment { get; set; } = string.Empty;

    /// <summary>Loads <see cref="FileName"/> from <paramref name="userDir"/> (missing or unreadable: defaults).</summary>
    public static CrashReportSettings Load(string userDir)
    {
        ArgumentNullException.ThrowIfNull(userDir);
        var settings = new CrashReportSettings(Path.Combine(userDir, FileName));
        string text;
        try
        {
            text = File.Exists(settings._path) ? File.ReadAllText(settings._path) : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            text = string.Empty;
        }

        foreach (KeyValuePair<string, string> pair in Parse(text))
        {
            switch (pair.Key)
            {
                case "consent":
                    settings.Consent = pair.Value switch
                    {
                        "granted" => CrashReportConsent.Granted,
                        "denied" => CrashReportConsent.Denied,
                        _ => CrashReportConsent.Unasked,
                    };
                    break;
                case "attach_replay":
                    settings.AttachReplay = pair.Value == "1";
                    break;
                case "dsn":
                    settings.Dsn = pair.Value;
                    break;
                case "environment":
                    settings.Environment = pair.Value;
                    break;
            }
        }

        return settings;
    }

    /// <summary>The environment for a version: <c>beta</c> for a <c>-beta</c> build, otherwise <c>development</c>.</summary>
    public static string DefaultEnvironment(string version)
        => (version ?? string.Empty).Contains("-beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "development";

    /// <summary>
    /// The endpoint in force: <c>TZAR2_CRASH_DSN</c>, else the settings file's, else the build's file in
    /// <paramref name="executableDir"/>; null when there is none.
    /// </summary>
    public string? EffectiveDsn(string? executableDir, string? environmentValue = null)
    {
        environmentValue ??= System.Environment.GetEnvironmentVariable(DsnVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue.Trim();
        }

        if (!string.IsNullOrWhiteSpace(Dsn))
        {
            return Dsn.Trim();
        }

        if (executableDir is null)
        {
            return null;
        }

        try
        {
            string file = Path.Combine(executableDir, BuildDsnFile);
            string? value = File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the settings back; false when the disk refused.</summary>
    public bool Save()
    {
        var text = new StringBuilder();
        text.Append("consent=").Append(Consent.ToString().ToLowerInvariant()).Append('\n');
        text.Append("attach_replay=").Append(AttachReplay ? '1' : '0').Append('\n');
        text.Append("dsn=").Append(Dsn).Append('\n');
        text.Append("environment=").Append(Environment).Append('\n');
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, text.ToString(), new UTF8Encoding(false));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> Parse(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line[0] == '#' || eq <= 0)
            {
                continue;
            }

            yield return new(line[..eq].Trim().ToLower(CultureInfo.InvariantCulture), line[(eq + 1)..].Trim());
        }
    }
}

/// <summary>A Sentry DSN (<c>https://key@host[:port][/prefix]/project</c>) and where it takes envelopes.</summary>
public sealed record SentryDsn(string PublicKey, Uri EnvelopeEndpoint, string Text)
{
    /// <summary>Parses <paramref name="dsn"/>; null when it is not a DSN.</summary>
    public static SentryDsn? Parse(string? dsn)
    {
        if (string.IsNullOrWhiteSpace(dsn) || !Uri.TryCreate(dsn.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        string key = uri.UserInfo.Split(':')[0];
        string path = uri.AbsolutePath.TrimEnd('/');
        int slash = path.LastIndexOf('/');
        string project = path[(slash + 1)..];
        if (key.Length == 0 || project.Length == 0)
        {
            return null;
        }

        string prefix = path[..slash];
        var endpoint = new UriBuilder(uri.Scheme, uri.Host, uri.Port, $"{prefix}/api/{project}/envelope/").Uri;
        return new SentryDsn(key, endpoint, dsn.Trim());
    }

    /// <summary>The <c>X-Sentry-Auth</c> header for a client version.</summary>
    public string AuthHeader(string clientVersion)
        => $"Sentry sentry_version=7, sentry_client=tzar2/{clientVersion}, sentry_key={PublicKey}";
}
