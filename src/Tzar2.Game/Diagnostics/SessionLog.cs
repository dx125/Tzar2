using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tzar2.Core.Diagnostics;

namespace Tzar2.Game.Diagnostics;

/// <summary>
/// The diagnostic log of one run of the game (ADR 0176): a folder <c>logs/yyyyMMdd-HHmmss</c> under the user's data with
/// <c>game.log</c> (one line per record, written by a background thread so the sim thread never waits on the disk),
/// <c>crash-*.txt</c> reports and whatever the host adds (replays). The latest records stay in memory for crash
/// reports. Older run folders beyond <see cref="KeepRuns"/> are deleted when a new one opens.
/// </summary>
public sealed class SessionLog : ITraceSink, IDisposable
{
    /// <summary>Run folders kept under the logs root.</summary>
    public const int KeepRuns = 30;

    /// <summary>The log file is renamed to <c>game.1.log</c> (replacing an older one) when it grows past this.</summary>
    public const long RotateBytes = 100L * 1024 * 1024;

    /// <summary>Records waiting for the writer before new ones are dropped (and counted).</summary>
    public const int QueueCapacity = 200_000;

    /// <summary>Latest lines kept for crash reports.</summary>
    public const int RecentLines = 2000;

    private const string LogName = "game.log";

    private readonly BlockingCollection<string> _queue = new(QueueCapacity);
    private readonly Queue<string> _recent = new();
    private readonly Thread _writer;
    private readonly string _file;
    private long _dropped;
    private bool _disposed;

    private SessionLog(string folder, TraceSettings settings)
    {
        Folder = folder;
        Settings = settings;
        _file = Path.Combine(folder, LogName);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Tzar2 log writer" };
        _writer.Start();
    }

    /// <summary>The run's folder.</summary>
    public string Folder { get; }

    /// <summary>The levels in force.</summary>
    public TraceSettings Settings { get; }

    /// <summary>Records lost because the writer fell behind.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Opens a new run folder under <paramref name="logsRoot"/>, deletes the oldest beyond <see cref="KeepRuns"/> and
    /// writes <paramref name="header"/> lines first (product, version, settings, machine).
    /// </summary>
    public static SessionLog Open(string logsRoot, TraceSettings settings, IEnumerable<string> header, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(logsRoot);
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(logsRoot);
        DateTime time = now ?? DateTime.Now;
        string name = time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string folder = Path.Combine(logsRoot, name);
        for (int n = 2; Directory.Exists(folder); n++)
        {
            folder = Path.Combine(logsRoot, string.Create(CultureInfo.InvariantCulture, $"{name}-{n}"));
        }

        Directory.CreateDirectory(folder);
        Prune(logsRoot, folder);
        var log = new SessionLog(folder, settings);
        foreach (string line in header ?? [])
        {
            log.Write(TraceArea.Session, LogLevel.Info, -1, line);
        }

        log.Write(TraceArea.Session, LogLevel.Info, -1, "trace levels: " + settings);
        return log;
    }

    /// <inheritdoc />
    public bool IsEnabled(TraceArea area, LogLevel level) => level != LogLevel.Off && level <= Settings[area];

    /// <inheritdoc />
    public void Write(TraceArea area, LogLevel level, int tick, string message)
    {
        if (_disposed || !IsEnabled(area, level))
        {
            return;
        }

        string line = Format(DateTime.Now, area, level, tick, message);
        lock (_recent)
        {
            _recent.Enqueue(line);
            if (_recent.Count > RecentLines)
            {
                _recent.Dequeue();
            }
        }

        try
        {
            if (!_queue.TryAdd(line))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Closing on another thread: the record stays in the recent lines only.
        }
    }

    /// <summary>Writes a record outside a match (tick -1).</summary>
    public void Log(TraceArea area, LogLevel level, string message) => Write(area, level, -1, message);

    /// <summary>One line of the log: local time, tick, level, area, message (line breaks folded).</summary>
    public static string Format(DateTime time, TraceArea area, LogLevel level, int tick, string message)
    {
        string tickText = tick < 0 ? "       -" : tick.ToString("D8", CultureInfo.InvariantCulture);
        string text = (message ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " | ", StringComparison.Ordinal);
        return string.Create(CultureInfo.InvariantCulture, $"{time:yyyy-MM-dd HH:mm:ss.fff} {tickText} {LevelText(level)} {area.ToString().ToLowerInvariant()}: {text}");
    }

    /// <summary>The latest lines, oldest first.</summary>
    public IReadOnlyList<string> Recent()
    {
        lock (_recent)
        {
            return [.. _recent];
        }
    }

    /// <summary>
    /// Writes <c>crash-yyyyMMdd-HHmmss.txt</c> with the context, the exception and the latest lines, and records it in the
    /// log; returns its path, or null when the disk refused.
    /// </summary>
    public string? WriteCrashReport(string context, Exception? exception)
    {
        Write(TraceArea.Session, LogLevel.Error, -1, $"crash: {context}: {exception?.GetType().Name}: {exception?.Message}");
        try
        {
            string path = Path.Combine(Folder, string.Create(CultureInfo.InvariantCulture, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));
            var text = new StringBuilder();
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Tzar2 crash report, {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
            text.AppendLine("Context: " + context);
            text.AppendLine();
            text.AppendLine(exception?.ToString() ?? "(no exception)");
            text.AppendLine();
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Latest {RecentLines} log lines:"));
            foreach (string line in Recent())
            {
                text.AppendLine(line);
            }

            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            Flush();
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Waits (up to <paramref name="timeoutMs"/>) until the writer has written everything queued so far.</summary>
    public void Flush(int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (_queue.Count > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        Thread.Sleep(20); // the line taken last is being written
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (Dropped > 0)
        {
            _queue.TryAdd(Format(DateTime.Now, TraceArea.Session, LogLevel.Warn, -1, string.Create(CultureInfo.InvariantCulture, $"{Dropped} records dropped (the writer fell behind)")));
        }

        _disposed = true;
        _queue.CompleteAdding();
        _writer.Join(5000);
        _queue.Dispose();
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN ",
        LogLevel.Info => "INFO ",
        LogLevel.Debug => "DEBUG",
        _ => "VERB ",
    };

    private void WriteLoop()
    {
        StreamWriter? writer = null;
        long written = 0;
        try
        {
            writer = Open(_file);
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                writer.WriteLine(line);
                written += line.Length + 2;
                if (written > RotateBytes)
                {
                    writer.Dispose();
                    string old = Path.Combine(Folder, "game.1.log");
                    File.Move(_file, old, overwrite: true);
                    writer = Open(_file);
                    written = 0;
                }

                if (_queue.Count == 0)
                {
                    writer.Flush();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // The disk refused: the game goes on without its log (the recent lines stay in memory for crash reports).
        }
        finally
        {
            writer?.Dispose();
        }

        static StreamWriter Open(string path) => new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
    }

    private static void Prune(string root, string keep)
    {
        try
        {
            string[] runs = [.. Directory.GetDirectories(root)
                .Where(d => Path.GetFileName(d) is { Length: >= 15 } n && char.IsAsciiDigit(n[0]) && n[8] == '-')
                .Order(StringComparer.Ordinal)];
            foreach (string old in runs.Take(Math.Max(0, runs.Length - KeepRuns)))
            {
                if (!string.Equals(old, keep, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(old, recursive: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An old folder in use (an open log viewer) stays.
        }
    }
}
