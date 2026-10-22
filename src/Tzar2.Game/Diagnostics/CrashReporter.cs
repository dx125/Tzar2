using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tzar2.Game.Diagnostics;

/// <summary>Posts an envelope; returns the HTTP status (0 when the network failed).</summary>
public delegate Task<int> CrashTransport(Uri endpoint, string authHeader, byte[] envelope, CancellationToken cancellation);

/// <summary>
/// Opt-in crash reporting over the Sentry protocol (TDD REQ-PLAT-04, Q-06; ADR 0243), the platform's crash reporter of
/// REQ-PLAT-01. Each run of the game marks its log folder (ADR 0176) while it runs:
/// <list type="bullet">
/// <item>a managed crash (an unhandled .NET exception) writes an event at once;</item>
/// <item>a .NET exception that Godot caught in one of our callbacks is an error event (at most
/// <see cref="MaxErrorsPerRun"/> per run, each text once);</item>
/// <item>a run whose mark is still there at the next start ended without quitting: a native (Godot) crash, a kill or a
/// power cut. Its event carries the tail of that run's log and of Godot's own log, where Godot's crash handler writes
/// its backtrace.</item>
/// </list>
/// Every run also ends as a session (exited, crashed or abnormal) for the crash-free rate. Envelopes wait in
/// <c>crash-reports/outbox</c> and are sent only when the player has agreed and an endpoint is configured; refusing
/// deletes them.
/// </summary>
public sealed class CrashReporter
{
    /// <summary>The mark of a running session in its log folder (content: session id and start, UTC).</summary>
    public const string RunningMark = "session.running";

    /// <summary>The mark of a session that crashed with a managed exception.</summary>
    public const string CrashedMark = "session.crashed";

    /// <summary>Godot-caught exceptions reported per run.</summary>
    public const int MaxErrorsPerRun = 10;

    /// <summary>Envelopes kept in the outbox; the oldest beyond go.</summary>
    public const int MaxOutbox = 100;

    /// <summary>Envelopes older than this are dropped unsent.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>Log lines sent with a report.</summary>
    public const int TailLines = 300;

    /// <summary>A replay larger than this is not attached.</summary>
    public const int MaxReplayBytes = 8 * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly object _gate = new();
    private readonly HashSet<string> _reportedErrors = new(StringComparer.Ordinal);
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly DateTime _started;
    private int _errors;
    private bool _crashed;

    private CrashReporter(string userDir, string runFolder, string? executableDir, SystemInfo system, DateTime nowUtc)
    {
        UserDir = userDir;
        RunFolder = runFolder;
        ExecutableDir = executableDir;
        Machine = system;
        _started = nowUtc;
        Settings = CrashReportSettings.Load(userDir);
        Release = "tzar2@" + GameInfo.Version;
        ReleaseEnvironment = Settings.Environment.Length > 0 ? Settings.Environment : CrashReportSettings.DefaultEnvironment(GameInfo.Version);
    }

    /// <summary>The user's data folder (Godot's <c>user://</c>).</summary>
    public string UserDir { get; }

    /// <summary>This run's log folder.</summary>
    public string RunFolder { get; }

    /// <summary>The folder of the executable (the build's default endpoint file), or null.</summary>
    public string? ExecutableDir { get; }

    /// <summary>The machine.</summary>
    public SystemInfo Machine { get; }

    /// <summary>The settings (consent, replay, endpoint); <see cref="SetConsent"/> changes them.</summary>
    public CrashReportSettings Settings { get; }

    /// <summary>The Sentry release (<c>tzar2@version</c>).</summary>
    public string Release { get; }

    /// <summary>The Sentry environment.</summary>
    public string ReleaseEnvironment { get; }

    /// <summary>Where envelopes wait.</summary>
    public string Outbox => Path.Combine(UserDir, "crash-reports", "outbox");

    /// <summary>Replaces the network for tests (null: HTTPS).</summary>
    public CrashTransport? Transport { get; set; }

    /// <summary>The endpoint in force, or null.</summary>
    public SentryDsn? Dsn => SentryDsn.Parse(Settings.EffectiveDsn(ExecutableDir));

    /// <summary>
    /// Starts reporting for a run: marks <paramref name="runFolder"/> and turns the runs under <paramref name="logsRoot"/>
    /// that are still marked into reports. Sends nothing; call <see cref="SendPendingAsync"/>.
    /// </summary>
    public static CrashReporter Start(string userDir, string logsRoot, string runFolder, string? executableDir, SystemInfo system, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(userDir);
        ArgumentNullException.ThrowIfNull(logsRoot);
        ArgumentNullException.ThrowIfNull(runFolder);
        var reporter = new CrashReporter(userDir, runFolder, executableDir, system ?? SystemInfo.Unknown, nowUtc ?? DateTime.UtcNow);
        reporter.CollectUnfinishedRuns(logsRoot);
        Try(() => File.WriteAllText(Path.Combine(runFolder, RunningMark), reporter._sessionId + "\n" + reporter._started.ToString("O", CultureInfo.InvariantCulture)));
        if (reporter.Settings.Consent == CrashReportConsent.Denied)
        {
            reporter.ClearOutbox();
        }

        return reporter;
    }

    /// <summary>Records the player's answer; refusing deletes the waiting reports.</summary>
    public void SetConsent(CrashReportConsent consent, bool attachReplay)
    {
        Settings.Consent = consent;
        Settings.AttachReplay = attachReplay;
        Settings.Save();
        if (consent == CrashReportConsent.Denied)
        {
            ClearOutbox();
        }
    }

    /// <summary>A crash: an unhandled exception that ends the run. Queues the event and marks the session crashed.</summary>
    public string? CaptureCrash(string context, Exception? exception, IReadOnlyList<string> recentLines, string? replayPath, DateTime? nowUtc = null)
    {
        lock (_gate)
        {
            _crashed = true;
        }

        Try(() => File.WriteAllText(Path.Combine(RunFolder, CrashedMark), context));
        IReadOnlyList<ReportedException> chain = exception is null ? [] : SentryEnvelope.FromException(exception, "AppDomain.UnhandledException", handled: false);
        var attachments = new List<ReportAttachment> { SentryEnvelope.Text("game-log-tail.txt", string.Join('\n', Tail(recentLines))) };
        if (Settings.AttachReplay && replayPath is not null)
        {
            AddReplay(attachments, replayPath);
        }

        return Queue("fatal", exception is null ? context : string.Empty, chain, new Dictionary<string, string> { ["context"] = context, ["kind"] = "managed-crash" }, attachments, nowUtc);
    }

    /// <summary>
    /// A logged error: when it is a .NET exception's text (one Godot caught in our callback), queues it as an error
    /// event, each text once and at most <see cref="MaxErrorsPerRun"/> per run. Returns whether it was queued.
    /// </summary>
    public bool CaptureLoggedError(string text, DateTime? nowUtc = null)
    {
        if (SentryEnvelope.FromText(text, "Godot.callback", handled: true) is not { } reported)
        {
            return false;
        }

        string key = reported.Type + ":" + reported.Message + ":" + (reported.Frames.Count > 0 ? reported.Frames[^1].Function : string.Empty);
        lock (_gate)
        {
            if (_reportedErrors.Count >= MaxErrorsPerRun || !_reportedErrors.Add(key))
            {
                return false;
            }

            _errors++;
        }

        return Queue("error", string.Empty, [reported], new Dictionary<string, string> { ["kind"] = "godot-caught-exception" }, [], nowUtc) is not null;
    }

    /// <summary>The run quit normally: removes its mark and queues the session as exited (or crashed).</summary>
    public void EndRun(DateTime? nowUtc = null)
    {
        bool crashed;
        int errors;
        lock (_gate)
        {
            crashed = _crashed;
            errors = _errors;
        }

        QueueSession(_sessionId, _started, nowUtc ?? DateTime.UtcNow, crashed ? "crashed" : "exited", errors + (crashed ? 1 : 0));
        Try(() => File.Delete(Path.Combine(RunFolder, RunningMark)));
        Try(() => File.Delete(Path.Combine(RunFolder, CrashedMark)));
    }

    /// <summary>
    /// Sends the waiting envelopes when the player agreed and an endpoint is configured; returns how many were
    /// delivered. Without consent nothing leaves this PC. Sent and rejected (4xx) envelopes are deleted; the others wait.
    /// </summary>
    public async Task<int> SendPendingAsync(CancellationToken cancellation = default)
    {
        if (Settings.Consent != CrashReportConsent.Granted || Dsn is not { } dsn)
        {
            return 0;
        }

        CrashTransport transport = Transport ?? PostAsync;
        string auth = dsn.AuthHeader(GameInfo.Version);
        int sent = 0;
        foreach (string file in PendingFiles())
        {
            byte[] envelope;
            try
            {
                envelope = await File.ReadAllBytesAsync(file, cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            int status = await transport(dsn.EnvelopeEndpoint, auth, WithDsn(envelope, dsn), cancellation).ConfigureAwait(false);
            if (status is >= 200 and < 300)
            {
                sent++;
                Try(() => File.Delete(file));
            }
            else if (status is >= 400 and < 500 and not 429)
            {
                Try(() => File.Delete(file));
            }
            else
            {
                break; // offline, rate-limited or the server failed: try again next start
            }
        }

        return sent;
    }

    /// <summary>The waiting envelopes, oldest first.</summary>
    public IReadOnlyList<string> PendingFiles()
    {
        try
        {
            return Directory.Exists(Outbox) ? [.. Directory.GetFiles(Outbox, "*.envelope").Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task<int> PostAsync(Uri endpoint, string authHeader, byte[] envelope, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(envelope) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sentry-envelope");
            request.Headers.TryAddWithoutValidation("X-Sentry-Auth", authHeader);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
            return (int)response.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return 0;
        }
    }

    /// <summary>Envelopes are stored without an endpoint; the one in force when sending goes into the header.</summary>
    private static byte[] WithDsn(byte[] envelope, SentryDsn dsn)
    {
        int end = Array.IndexOf(envelope, (byte)'\n');
        string header = Encoding.UTF8.GetString(envelope, 0, end < 0 ? envelope.Length : end).TrimEnd();
        if (header.Contains("\"dsn\"", StringComparison.Ordinal))
        {
            return envelope;
        }

        string escaped = System.Text.Json.JsonEncodedText.Encode(dsn.Text).ToString();
        string withDsn = header == "{}" ? $"{{\"dsn\":\"{escaped}\"}}" : header[..^1] + $",\"dsn\":\"{escaped}\"}}";
        byte[] head = Encoding.UTF8.GetBytes(withDsn);
        return [.. head, .. envelope.AsSpan(end < 0 ? envelope.Length : end)];
    }

    private static IEnumerable<string> Tail(IReadOnlyList<string> lines) => lines.Skip(Math.Max(0, lines.Count - TailLines));

    private static string ReadTail(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var tail = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                tail.Enqueue(line);
                if (tail.Count > TailLines)
                {
                    tail.Dequeue();
                }
            }

            return string.Join('\n', tail);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private void CollectUnfinishedRuns(string logsRoot)
    {
        string[] runs;
        try
        {
            runs = Directory.Exists(logsRoot) ? [.. Directory.GetDirectories(logsRoot).Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string run in runs)
        {
            string mark = Path.Combine(run, RunningMark);
            if (string.Equals(Path.GetFullPath(run), Path.GetFullPath(RunFolder), StringComparison.OrdinalIgnoreCase) || !File.Exists(mark))
            {
                continue;
            }

            (string sessionId, DateTime started) = ReadMark(mark);
            DateTime ended = LastWrite(run);
            if (File.Exists(Path.Combine(run, CrashedMark)))
            {
                // The managed crash queued its event already.
                QueueSession(sessionId, started, ended, "crashed", 1);
            }
            else
            {
                string godotLog = GodotLogOf(logsRoot, started);
                string godotTail = godotLog.Length > 0 ? ReadTail(godotLog) : string.Empty;
                bool nativeCrash = godotTail.Contains("crashed with signal", StringComparison.OrdinalIgnoreCase)
                    || godotTail.Contains("CrashHandlerException", StringComparison.Ordinal)
                    || godotTail.Contains("Dumping the backtrace", StringComparison.Ordinal);
                var attachments = new List<ReportAttachment> { SentryEnvelope.Text("game-log-tail.txt", ReadTail(Path.Combine(run, "game.log"))) };
                if (godotTail.Length > 0)
                {
                    attachments.Add(SentryEnvelope.Text("godot-log-tail.txt", godotTail));
                }

                string message = nativeCrash
                    ? "Godot crashed (native crash; Godot's backtrace is in godot-log-tail.txt)"
                    : "The game ended without quitting (killed, hung, power loss, or a crash without a log)";
                Queue("fatal", message, [], new Dictionary<string, string> { ["kind"] = nativeCrash ? "native-crash" : "abnormal-exit", ["run"] = Path.GetFileName(run) }, attachments, ended);
                QueueSession(sessionId, started, ended, nativeCrash ? "crashed" : "abnormal", 1);
            }

            Try(() => File.Delete(mark));
            Try(() => File.Delete(Path.Combine(run, CrashedMark)));
        }
    }

    /// <summary>Godot's log of a run that started at <paramref name="startedUtc"/>: the oldest <c>godot*.log</c> written since.</summary>
    private static string GodotLogOf(string logsRoot, DateTime startedUtc)
    {
        try
        {
            return Directory.GetFiles(logsRoot, "godot*.log")
                .Select(f => (File: f, Time: File.GetLastWriteTimeUtc(f)))
                .Where(f => f.Time >= startedUtc.AddMinutes(-1))
                .OrderBy(f => f.Time)
                .Select(f => f.File)
                .FirstOrDefault() ?? string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static (string SessionId, DateTime StartedUtc) ReadMark(string mark)
    {
        try
        {
            string[] lines = File.ReadAllLines(mark);
            DateTime started = lines.Length > 1 && DateTime.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t)
                ? t.ToUniversalTime()
                : File.GetLastWriteTimeUtc(mark);
            return (lines.Length > 0 && Guid.TryParse(lines[0], out Guid id) ? id.ToString() : Guid.NewGuid().ToString(), started);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (Guid.NewGuid().ToString(), DateTime.UtcNow);
        }
    }

    private static DateTime LastWrite(string run)
    {
        try
        {
            return Directory.GetFiles(run).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(run)).Max();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow;
        }
    }

    private static void AddReplay(List<ReportAttachment> attachments, string replayPath)
    {
        try
        {
            if (File.Exists(replayPath) && new FileInfo(replayPath).Length <= MaxReplayBytes)
            {
                attachments.Add(new ReportAttachment(Path.GetFileName(replayPath), "application/octet-stream", File.ReadAllBytes(replayPath)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The report goes without it.
        }
    }

    private string? Queue(string level, string message, IReadOnlyList<ReportedException> exceptions, IReadOnlyDictionary<string, string> tags, IReadOnlyList<ReportAttachment> attachments, DateTime? nowUtc)
    {
        if (Settings.Consent == CrashReportConsent.Denied)
        {
            return null;
        }

        string id = Guid.NewGuid().ToString("N");
        DateTime time = nowUtc ?? DateTime.UtcNow;
        byte[] envelope = SentryEnvelope.Event(id, time, level, message, exceptions, Release, ReleaseEnvironment, Machine, tags, attachments, dsn: null);
        return Store(time, "event-" + id, envelope);
    }

    private void QueueSession(string sessionId, DateTime started, DateTime ended, string status, int errors)
    {
        if (Settings.Consent != CrashReportConsent.Denied)
        {
            Store(ended, "session-" + sessionId, SentryEnvelope.Session(sessionId, started, ended, status, errors, Release, ReleaseEnvironment, dsn: null));
        }
    }

    private string? Store(DateTime time, string name, byte[] envelope)
    {
        string path = Path.Combine(Outbox, string.Create(CultureInfo.InvariantCulture, $"{time:yyyyMMdd-HHmmss-fff}-{name}.envelope"));
        bool stored = Try(() =>
        {
            Directory.CreateDirectory(Outbox);
            File.WriteAllBytes(path, envelope);
        });
        Prune();
        return stored ? path : null;
    }

    private void Prune()
    {
        IReadOnlyList<string> files = PendingFiles();
        DateTime cutoff = DateTime.UtcNow - MaxAge;
        for (int i = 0; i < files.Count; i++)
        {
            string file = files[i];
            if (i < files.Count - MaxOutbox || File.GetLastWriteTimeUtc(file) < cutoff)
            {
                Try(() => File.Delete(file));
            }
        }
    }

    private void ClearOutbox()
    {
        foreach (string file in PendingFiles())
        {
            Try(() => File.Delete(file));
        }
    }

    private static bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
