using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Tzar2.Core.Diagnostics;
using Tzar2.Game;
using Tzar2.Game.Diagnostics;
using Tzar2.Game.Session;

namespace Tzar2.Client;

/// <summary>
/// The client's diagnostic log (ADR 0176): one <see cref="SessionLog"/> per run in <c>user://logs/</c>, levels from
/// <c>TZAR2_TRACE</c> or <c>user://trace.txt</c> (default: debug everywhere). Godot's own output and errors, including the
/// exceptions it catches in our callbacks, arrive through <see cref="GodotLogger"/>; unhandled .NET exceptions write a
/// crash report and the running match's replay into the run's folder.
/// </summary>
internal static class ClientLog
{
    private static GodotLogger? _logger;
    private static GameSession? _session;

    /// <summary>The run's log, or null before <see cref="Open"/>.</summary>
    public static SessionLog? Sink { get; private set; }

    /// <summary>Opens the run's log in <paramref name="userDir"/>/logs and hooks Godot's and .NET's error reporting.</summary>
    public static void Open(string userDir, IReadOnlyList<string> args)
    {
        if (Sink is not null)
        {
            return;
        }

        string settingsFile = Path.Combine(userDir, "trace.txt");
        string? text = System.Environment.GetEnvironmentVariable(TraceSettings.EnvironmentVariable);
        string source = text is null ? settingsFile : TraceSettings.EnvironmentVariable;
        try
        {
            text ??= File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            text = null;
        }

        (TraceSettings settings, IReadOnlyList<string> errors) = TraceSettings.Parse(text, TraceSettings.Default);
        try
        {
            Sink = SessionLog.Open(Path.Combine(userDir, "logs"), settings,
            [
                $"{GameInfo.Codename} {GameInfo.Version}",
                $"OS {OS.GetName()} {OS.GetVersion()}, Godot {Engine.GetVersionInfo()["string"]}, .NET {System.Environment.Version}",
                string.Create(CultureInfo.InvariantCulture, $"CPU {OS.GetProcessorName()} ({OS.GetProcessorCount()} threads), video {RenderingServer.GetVideoAdapterName()} ({RenderingServer.GetVideoAdapterVendor()})"),
                "arguments: " + string.Join(" ", args),
                $"user data {userDir}; Godot's own log: {ProjectSettings.GlobalizePath("user://logs/godot.log")}",
                $"trace settings from {(text is null ? "defaults" : source)}",
            ]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PrintErr("No session log: " + e.Message);
            return;
        }

        foreach (string error in errors)
        {
            Sink.Log(TraceArea.Session, LogLevel.Warn, $"trace settings: ignored '{error}'");
        }

        _logger = new GodotLogger { Log = Sink };
        OS.AddLogger(_logger);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash("unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error("unobserved task exception: " + e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>The match whose replay a crash report saves (the running game screen's), or null.</summary>
    public static void TrackSession(GameSession? session) => _session = session;

    /// <summary>A client record at Debug.</summary>
    public static void Debug(string message) => Sink?.Log(TraceArea.Client, LogLevel.Debug, message);

    /// <summary>A client record at Info.</summary>
    public static void Info(string message) => Sink?.Log(TraceArea.Client, LogLevel.Info, message);

    /// <summary>A client record at Warn.</summary>
    public static void Warn(string message) => Sink?.Log(TraceArea.Client, LogLevel.Warn, message);

    /// <summary>A client record at Error.</summary>
    public static void Error(string message) => Sink?.Log(TraceArea.Client, LogLevel.Error, message);

    /// <summary>Writes a crash report and the tracked match's replay; returns the report's path.</summary>
    public static string? Crash(string context, Exception? exception)
    {
        if (Sink is not { } log)
        {
            return null;
        }

        string? report = log.WriteCrashReport(context, exception);
        if (_session is { IsRecording: true } session)
        {
            try
            {
                session.Stop();
                if (session.FinishReplay() is { } replay)
                {
                    string path = Path.Combine(log.Folder, "crash-replay.t2rep");
                    File.WriteAllBytes(path, replay);
                    log.Log(TraceArea.Session, LogLevel.Error, "the crashed match's replay: " + path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.Log(TraceArea.Session, LogLevel.Error, "the crashed match's replay was not saved: " + e.Message);
            }
        }

        log.Flush();
        return report;
    }

    /// <summary>Closes the log (on quit).</summary>
    public static void Close()
    {
        if (_logger is not null)
        {
            OS.RemoveLogger(_logger);
            _logger = null;
        }

        Sink?.Log(TraceArea.Session, LogLevel.Info, "quit");
        Sink?.Dispose();
        Sink = null;
    }
}

/// <summary>
/// Copies Godot's output and errors into the session log (Godot 4.5+ <c>OS.add_logger</c>). Godot may call it from any
/// thread; it only writes to the thread-safe log and never prints (which would call it again).
/// </summary>
internal sealed partial class GodotLogger : Logger
{
    /// <summary>The log to write to.</summary>
    public SessionLog? Log { get; init; }

    /// <inheritdoc/>
    public override void _LogMessage(string message, bool error) =>
        Log?.Write(TraceArea.Client, error ? LogLevel.Error : LogLevel.Info, -1, "godot: " + message.TrimEnd());

    /// <inheritdoc/>
    public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        LogLevel level = errorType == (int)ErrorType.Warning ? LogLevel.Warn : LogLevel.Error;
        string text = string.IsNullOrEmpty(rationale) ? code : rationale + (string.IsNullOrEmpty(code) ? string.Empty : " | " + code);
        Log?.Write(TraceArea.Client, level, -1, string.Create(CultureInfo.InvariantCulture, $"godot {(ErrorType)errorType}: {text} ({function} at {file}:{line})"));
    }
}
