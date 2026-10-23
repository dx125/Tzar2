using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tzar2.Game.Diagnostics;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>Opt-in crash reporting over the Sentry protocol (ADR 0243).</summary>
public sealed class CrashReportingTests : IDisposable
{
    private const string Dsn = "https://abc123@crash.example.org/sentry/42";
    private readonly string _user = Path.Combine(Path.GetTempPath(), "tzar2-crash-" + Guid.NewGuid().ToString("N"));
    private readonly List<(Uri Endpoint, string Auth, byte[] Body)> _posts = [];
    private readonly List<CrashReporter> _reporters = [];
    private int _status = 200;

    public CrashReportingTests() => Directory.CreateDirectory(Path.Combine(_user, "logs"));

    private string Logs => Path.Combine(_user, "logs");

    public void Dispose()
    {
        _reporters.ForEach(r => r.Dispose());
        Directory.Delete(_user, recursive: true);
    }

    [Fact]
    public void A_dsn_gives_the_envelope_endpoint_and_the_auth_header()
    {
        SentryDsn dsn = SentryDsn.Parse(Dsn)!;
        Assert.Equal("https://crash.example.org/sentry/api/42/envelope/", dsn.EnvelopeEndpoint.ToString());
        Assert.Equal("Sentry sentry_version=7, sentry_client=tzar2/1.0, sentry_key=abc123", dsn.AuthHeader("1.0"));
        Assert.Equal("http://localhost:8000/api/7/envelope/", SentryDsn.Parse("http://key:secret@localhost:8000/7")!.EnvelopeEndpoint.ToString());
        Assert.Null(SentryDsn.Parse("https://crash.example.org/42"));
        Assert.Null(SentryDsn.Parse("not a dsn"));
        Assert.Null(SentryDsn.Parse(""));
    }

    [Fact]
    public void The_endpoint_comes_from_the_environment_then_the_settings_then_the_build()
    {
        string exe = Path.Combine(_user, "exe");
        Directory.CreateDirectory(exe);
        File.WriteAllText(Path.Combine(exe, CrashReportSettings.BuildDsnFile), "https://build@b.example/1\n");
        CrashReportSettings settings = CrashReportSettings.Load(_user);
        Assert.Equal(CrashReportConsent.Unasked, settings.Consent);
        Assert.Equal("https://build@b.example/1", settings.EffectiveDsn(exe, environmentValue: ""));
        settings.Dsn = "https://user@u.example/2";
        Assert.Equal("https://user@u.example/2", settings.EffectiveDsn(exe, environmentValue: ""));
        Assert.Equal("https://env@e.example/3", settings.EffectiveDsn(exe, environmentValue: "https://env@e.example/3"));
        Assert.Null(CrashReportSettings.Load(_user).EffectiveDsn(null, environmentValue: ""));

        settings.Consent = CrashReportConsent.Granted;
        settings.AttachReplay = true;
        Assert.True(settings.Save());
        CrashReportSettings again = CrashReportSettings.Load(_user);
        Assert.Equal((CrashReportConsent.Granted, true, "https://user@u.example/2"), (again.Consent, again.AttachReplay, again.Dsn));
        Assert.Equal("beta", CrashReportSettings.DefaultEnvironment("0.7.0-beta.12+abc"));
        Assert.Equal("development", CrashReportSettings.DefaultEnvironment("0.7.0+abc"));
    }

    [Fact]
    public void Paths_lose_the_user_name()
    {
        Assert.Equal(@"C:\Users\<user>\AppData\Roaming\Godot", SentryEnvelope.Scrub(@"C:\Users\Sasha Petrov\AppData\Roaming\Godot"));
        Assert.Equal("/home/<user>/.local x /Users/<user>/Library", SentryEnvelope.Scrub("/home/eldar/.local x /Users/eldar/Library"));
    }

    [Fact]
    public void An_exception_chain_is_reported_cause_first_with_its_frames_oldest_first()
    {
        Exception thrown;
        try
        {
            Thrower.Outer();
            throw new InvalidOperationException("unreachable");
        }
        catch (InvalidOperationException e)
        {
            thrown = e;
        }

        IReadOnlyList<ReportedException> chain = SentryEnvelope.FromException(thrown, "test", handled: false);
        Assert.Equal(["System.ArgumentException", "System.InvalidOperationException"], chain.Select(e => e.Type));
        Assert.Contains(chain[0].Frames, f => f.Function.Contains("Thrower.Inner", StringComparison.Ordinal) && f.InApp);
        Assert.Contains("Thrower.Inner", chain[0].Frames[^1].Function, StringComparison.Ordinal);

        ReportedException parsed = SentryEnvelope.FromText(thrown.InnerException!.ToString(), "Godot.callback", handled: true)!;
        Assert.Equal("System.ArgumentException", parsed.Type);
        Assert.Equal("inner failure", parsed.Message);
        Assert.NotEmpty(parsed.Frames);
        Assert.True(SentryEnvelope.IsExceptionText("System.NullReferenceException: Object reference not set"));
        Assert.False(SentryEnvelope.IsExceptionText("Condition \"!is_inside_tree()\" is true."));
    }

    [Fact]
    public void An_event_envelope_holds_the_event_and_its_attachments_with_their_lengths()
    {
        byte[] envelope = SentryEnvelope.Event(
            "0123456789abcdef0123456789abcdef", new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc), "fatal", string.Empty,
            [new ReportedException("System.Exception", "boom", [new ReportedFrame("Tzar2.Game.X.Y()", "X.cs", 12, true)], "test", false)],
            "tzar2@0.7.0", "beta", SystemInfo.Unknown, new Dictionary<string, string> { ["kind"] = "managed-crash" },
            [SentryEnvelope.Text("game-log-tail.txt", @"line in C:\Users\Sasha\x")], Dsn);

        (JsonElement header, List<(JsonElement Header, byte[] Payload)> items) = ReadEnvelope(envelope);
        Assert.Equal("0123456789abcdef0123456789abcdef", header.GetProperty("event_id").GetString());
        Assert.Equal(Dsn, header.GetProperty("dsn").GetString());
        Assert.Equal(["event", "attachment"], items.Select(i => i.Header.GetProperty("type").GetString()));
        JsonElement ev = JsonDocument.Parse(items[0].Payload).RootElement;
        Assert.Equal("fatal", ev.GetProperty("level").GetString());
        Assert.Equal("2026-10-22T12:00:00.000Z", ev.GetProperty("timestamp").GetString());
        Assert.Equal("tzar2@0.7.0", ev.GetProperty("release").GetString());
        JsonElement value = ev.GetProperty("exception").GetProperty("values")[0];
        Assert.Equal("boom", value.GetProperty("value").GetString());
        Assert.Equal(12, value.GetProperty("stacktrace").GetProperty("frames")[0].GetProperty("lineno").GetInt32());
        Assert.False(ev.TryGetProperty("server_name", out _));
        Assert.False(ev.TryGetProperty("user", out _));
        Assert.Equal("game-log-tail.txt", items[1].Header.GetProperty("filename").GetString());
        Assert.Equal(@"line in C:\Users\<user>\x", Encoding.UTF8.GetString(items[1].Payload));
    }

    [Fact]
    public async Task Nothing_is_sent_without_consent_or_without_an_endpoint()
    {
        CrashReporter unasked = Start(dsn: Dsn);
        unasked.CaptureCrash("test", new InvalidOperationException("x"), ["a line"], null);
        Assert.Single(unasked.PendingFiles());
        Assert.Equal(0, await unasked.SendPendingAsync());
        Assert.Empty(_posts);

        CrashReporter noEndpoint = Start(dsn: null);
        noEndpoint.SetConsent(CrashReportConsent.Granted, attachReplay: false);
        Assert.Equal(0, await noEndpoint.SendPendingAsync());
        Assert.Empty(_posts);

        CrashReporter refusing = Start(dsn: Dsn);
        refusing.SetConsent(CrashReportConsent.Denied, attachReplay: false);
        Assert.Empty(refusing.PendingFiles());
        Assert.Null(refusing.CaptureCrash("test", new InvalidOperationException("y"), [], null));
        refusing.EndRun();
        Assert.Empty(refusing.PendingFiles());
        Assert.Equal(0, await refusing.SendPendingAsync());
        Assert.Empty(_posts);
    }

    [Fact]
    public async Task With_consent_waiting_envelopes_are_sent_and_deleted_and_server_failures_wait()
    {
        CrashReporter reporter = Start(dsn: Dsn);
        reporter.CaptureCrash("test", new InvalidOperationException("x"), ["a line"], null);
        reporter.EndRun();
        Assert.Equal(2, reporter.PendingFiles().Count);
        reporter.SetConsent(CrashReportConsent.Granted, attachReplay: false);

        _status = 503;
        Assert.Equal(0, await reporter.SendPendingAsync());
        Assert.Equal(2, reporter.PendingFiles().Count);

        _status = 200;
        Assert.Equal(2, await reporter.SendPendingAsync());
        Assert.Empty(reporter.PendingFiles());
        Assert.All(_posts, p => Assert.Equal("https://crash.example.org/sentry/api/42/envelope/", p.Endpoint.ToString()));
        Assert.All(_posts, p => Assert.Equal(Dsn, ReadEnvelope(p.Body).Header.GetProperty("dsn").GetString()));
        Assert.Contains(_posts, p => ReadEnvelope(p.Body).Items[0].Header.GetProperty("type").GetString() == "session");
    }

    [Fact]
    public void A_run_that_never_quit_becomes_a_native_crash_report_and_a_crashed_session()
    {
        string previous = Path.Combine(Logs, "20261021-100000");
        Directory.CreateDirectory(previous);
        var started = DateTime.UtcNow.AddMinutes(-30);
        File.WriteAllText(Path.Combine(previous, CrashReporter.RunningMark), Guid.NewGuid() + "\n" + started.ToString("O"));
        File.WriteAllText(Path.Combine(previous, "game.log"), "2026-10-21 10:00:00.000 00000100 INFO  session: match started\n");
        File.WriteAllText(Path.Combine(Logs, "godot2026-10-21T10.30.00.log"), "CrashHandlerException: Program crashed with signal 11\nDumping the backtrace.\n[1] ???\n");
        File.SetLastWriteTimeUtc(Path.Combine(Logs, "godot2026-10-21T10.30.00.log"), started.AddMinutes(10));

        CrashReporter reporter = Start(dsn: Dsn);

        Assert.False(File.Exists(Path.Combine(previous, CrashReporter.RunningMark)));
        Assert.True(File.Exists(Path.Combine(reporter.RunFolder, CrashReporter.RunningMark)));
        var envelopes = reporter.PendingFiles().Select(f => ReadEnvelope(File.ReadAllBytes(f))).ToList();
        var ev = envelopes.Single(e => e.Items[0].Header.GetProperty("type").GetString() == "event");
        JsonElement payload = JsonDocument.Parse(ev.Items[0].Payload).RootElement;
        Assert.Equal("native-crash", payload.GetProperty("tags").GetProperty("kind").GetString());
        Assert.Equal(["game-log-tail.txt", "godot-log-tail.txt"], ev.Items.Skip(1).Select(i => i.Header.GetProperty("filename").GetString()));
        var session = envelopes.Single(e => e.Items[0].Header.GetProperty("type").GetString() == "session");
        Assert.Equal("crashed", JsonDocument.Parse(session.Items[0].Payload).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void A_clean_quit_is_an_exited_session_and_godot_caught_exceptions_are_errors_once_each()
    {
        CrashReporter reporter = Start(dsn: Dsn);
        string text = "System.NullReferenceException: Object reference not set to an instance of an object.\n   at Tzar2.Client.Game.Hud.Draw() in C:\\Users\\Sasha\\src\\Hud.cs:line 10";
        Assert.True(reporter.CaptureLoggedError(text));
        Assert.False(reporter.CaptureLoggedError(text));
        Assert.False(reporter.CaptureLoggedError("Condition \"p_node\" is null."));
        for (int i = 0; i < CrashReporter.MaxErrorsPerRun + 5; i++)
        {
            reporter.CaptureLoggedError($"System.InvalidOperationException: case {i}");
        }

        reporter.EndRun();
        Assert.False(File.Exists(Path.Combine(reporter.RunFolder, CrashReporter.RunningMark)));
        var envelopes = reporter.PendingFiles().Select(f => ReadEnvelope(File.ReadAllBytes(f))).ToList();
        Assert.Equal(CrashReporter.MaxErrorsPerRun, envelopes.Count(e => e.Items[0].Header.GetProperty("type").GetString() == "event"));
        JsonElement session = JsonDocument.Parse(envelopes.Single(e => e.Items[0].Header.GetProperty("type").GetString() == "session").Items[0].Payload).RootElement;
        Assert.Equal("exited", session.GetProperty("status").GetString());
        Assert.Equal(CrashReporter.MaxErrorsPerRun, session.GetProperty("errors").GetInt32());

        // The next start finds no unfinished run.
        CrashReporter next = Start(dsn: Dsn);
        Assert.Equal(envelopes.Count, next.PendingFiles().Count);
    }

    [Fact]
    public void Another_copy_of_the_game_that_is_still_running_is_not_reported()
    {
        CrashReporter running = Start(dsn: Dsn);
        CrashReporter second = Start(dsn: Dsn);
        Assert.True(File.Exists(Path.Combine(running.RunFolder, CrashReporter.RunningMark)));
        Assert.Empty(second.PendingFiles());

        // Once the first copy is gone without ending its run (as after a crash), the next start reports it.
        running.Dispose();
        CrashReporter third = Start(dsn: Dsn);
        Assert.Contains(third.PendingFiles(), f => f.Contains("-event-", StringComparison.Ordinal));
    }

    private CrashReporter Start(string? dsn)
    {
        CrashReportSettings settings = CrashReportSettings.Load(_user);
        settings.Dsn = dsn ?? string.Empty;
        settings.Save();

        string run = Path.Combine(Logs, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(run);
        CrashReporter reporter = CrashReporter.Start(_user, Logs, run, executableDir: null, SystemInfo.Unknown);
        reporter.Transport = Post;
        _reporters.Add(reporter);
        return reporter;
    }

    private Task<int> Post(Uri endpoint, string auth, byte[] body, CancellationToken cancellation)
    {
        _posts.Add((endpoint, auth, body));
        return Task.FromResult(_status);
    }

    private static (JsonElement Header, List<(JsonElement Header, byte[] Payload)> Items) ReadEnvelope(byte[] envelope)
    {
        int at = 0;
        JsonElement header = JsonDocument.Parse(Line(envelope, ref at)).RootElement;
        var items = new List<(JsonElement, byte[])>();
        while (at < envelope.Length)
        {
            byte[] line = Line(envelope, ref at);
            if (line.Length == 0)
            {
                continue;
            }

            JsonElement item = JsonDocument.Parse(line).RootElement;
            int length = item.GetProperty("length").GetInt32();
            items.Add((item, envelope.AsSpan(at, length).ToArray()));
            at += length + 1;
        }

        return (header, items);
    }

    private static byte[] Line(byte[] data, ref int at)
    {
        int end = Array.IndexOf(data, (byte)'\n', at);
        end = end < 0 ? data.Length : end;
        byte[] line = data.AsSpan(at, end - at).ToArray();
        at = end + 1;
        return line;
    }

    private static class Thrower
    {
        public static void Outer()
        {
            try
            {
                Inner();
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException("outer failure", e);
            }
        }

        public static void Inner() => throw new ArgumentException("inner failure");
    }
}
