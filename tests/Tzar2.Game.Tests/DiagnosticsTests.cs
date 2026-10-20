using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tzar2.Core.Diagnostics;
using Tzar2.Game.Diagnostics;
using Tzar2.Game.Session;
using Tzar2.Sim.Events;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Game.Tests;

/// <summary>The session log, the trace settings and the match tracer (ADR 0176).</summary>
public class DiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public void Trace_settings_parse_a_level_for_all_areas_or_per_area_and_ignore_what_they_cannot_read()
    {
        Assert.All(Enum.GetValues<TraceArea>(), a => Assert.Equal(LogLevel.Debug, TraceSettings.Default[a]));

        (TraceSettings s, IReadOnlyList<string> errors) = TraceSettings.Parse("*=info, ai=verbose\nevents=warn # comment", TraceSettings.Default);
        Assert.Empty(errors);
        Assert.Equal(LogLevel.Verbose, s[TraceArea.Ai]);
        Assert.Equal(LogLevel.Warn, s[TraceArea.Events]);
        Assert.Equal(LogLevel.Info, s[TraceArea.Client]);

        (TraceSettings all, _) = TraceSettings.Parse("off", TraceSettings.Default);
        Assert.Equal(LogLevel.Off, all[TraceArea.Session]);

        (TraceSettings kept, IReadOnlyList<string> bad) = TraceSettings.Parse("loud,ai=shout,nowhere=debug", TraceSettings.Default);
        Assert.Equal(3, bad.Count);
        Assert.Equal(TraceSettings.Default.ToString(), kept.ToString());
        Assert.Equal(s.ToString(), TraceSettings.Parse(s.ToString(), TraceSettings.Uniform(LogLevel.Off)).Settings.ToString());
    }

    [Fact]
    public void The_session_log_keeps_records_by_area_and_level_and_writes_crash_reports()
    {
        string root = NewTempDir();
        try
        {
            (TraceSettings settings, _) = TraceSettings.Parse("*=info,ai=debug", TraceSettings.Default);
            string folder;
            using (SessionLog log = SessionLog.Open(root, settings, ["Tzar2 test"]))
            {
                folder = log.Folder;
                Assert.True(log.IsEnabled(TraceArea.Ai, LogLevel.Debug));
                Assert.False(log.IsEnabled(TraceArea.Events, LogLevel.Debug));
                log.Write(TraceArea.Ai, LogLevel.Debug, 1234, "p1 economy: kept");
                log.Write(TraceArea.Events, LogLevel.Debug, 1234, "dropped by level");
                log.Write(TraceArea.Session, LogLevel.Info, -1, "two\nlines");
                string? crash = log.WriteCrashReport("test", new InvalidOperationException("boom"));
                Assert.NotNull(crash);
                string report = File.ReadAllText(crash!);
                Assert.Contains("boom", report, StringComparison.Ordinal);
                Assert.Contains("p1 economy: kept", report, StringComparison.Ordinal);
            }

            string[] lines = File.ReadAllLines(Path.Combine(folder, "game.log"));
            foreach (string line in lines)
            {
                output.WriteLine(line);
            }

            Assert.Contains(lines, l => l.Contains("Tzar2 test", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("00001234 DEBUG ai: p1 economy: kept", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains("dropped by level", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.EndsWith("session: two | lines", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("ERROR session: crash: test: InvalidOperationException: boom", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Opening_a_run_keeps_only_the_latest_runs()
    {
        string root = NewTempDir();
        try
        {
            for (int i = 0; i < SessionLog.KeepRuns + 5; i++)
            {
                Directory.CreateDirectory(Path.Combine(root, string.Create(CultureInfo.InvariantCulture, $"20260101-{i:D6}")));
            }

            Directory.CreateDirectory(Path.Combine(root, "not-a-run"));
            using (SessionLog.Open(root, TraceSettings.Default, [], new DateTime(2026, 10, 19, 12, 0, 0)))
            {
            }

            string[] runs = [.. Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];
            Assert.Contains("not-a-run", runs);
            Assert.Contains("20261019-120000", runs);
            Assert.Equal(SessionLog.KeepRuns, runs.Count(r => char.IsAsciiDigit(r[0])));
            Assert.DoesNotContain("20260101-000000", runs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_traced_match_is_the_same_match_and_its_trace_tells_the_story()
    {
        const int ticks = 2400; // two minutes: a script pass, executor orders, deliveries, a summary
        ulong untraced = Play(null, ticks);
        var sink = new ListSink(LogLevel.Verbose);
        ulong traced = Play(sink, ticks);
        Assert.Equal(untraced, traced);

        string[] lines = [.. sink.Lines];
        output.WriteLine($"{lines.Length} records; first ones:");
        foreach (string line in lines.Where(l => !l.Contains("tick hash", StringComparison.Ordinal)).Take(60))
        {
            output.WriteLine(line);
        }

        Assert.Contains(lines, l => l.StartsWith("session Info: match started: map ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("session Info:   player 1: euro, Computer, Hard", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("commands Debug: p0 Harvest units ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("ai Debug: p1 script pass ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("ai Debug: p1 economy: p1 ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("events Debug: p0 delivered ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("session Info: summary at 1:40", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("session Info:   p0: population ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("sim Verbose: tick hash ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("session Info: match ended after 2:00", StringComparison.Ordinal));
    }

    [Fact]
    public void Events_are_traced_at_the_level_of_their_weight()
    {
        Assert.Equal(LogLevel.Info, MatchTracer.LevelOf(SimEventKind.ConstructionCompleted));
        Assert.Equal(LogLevel.Info, MatchTracer.LevelOf(SimEventKind.PlayerDefeated));
        Assert.Equal(LogLevel.Debug, MatchTracer.LevelOf(SimEventKind.UnitTrained));
        Assert.Equal(LogLevel.Warn, MatchTracer.LevelOf(SimEventKind.GoalUnreachable));
        Assert.Equal(LogLevel.Verbose, MatchTracer.LevelOf(SimEventKind.Damaged));
        Assert.Equal(LogLevel.Error, MatchTracer.LevelOf(SimEventKind.ScenarioFault));
    }

    // A human (peasants sent to the trees at tick 5) against a hard AI; the final full hash.
    private static ulong Play(ITraceSink? sink, int ticks)
    {
        using var session = new GameSession(HumanVsAiTests.Setup(), HumanVsAiTests.SyntheticRules);
        MatchTracer? tracer = sink is null ? null : MatchTracer.Attach(session, sink, "match");
        for (int t = 0; t < ticks; t++)
        {
            if (session.Simulation.World.Tick.Value == 5)
            {
                HumanVsAiTests.HumanHarvest(session);
            }

            session.Step(1);
        }

        tracer?.Dispose();
        return session.Simulation.ComputeFullHash();
    }

    private static string NewTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "tzar2-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // Keeps every record as "area Level: message".
    private sealed class ListSink(LogLevel level) : ITraceSink
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public bool IsEnabled(TraceArea area, LogLevel at) => at != LogLevel.Off && at <= level;

        public void Write(TraceArea area, LogLevel at, int tick, string message) => Lines.Enqueue($"{area.ToString().ToLowerInvariant()} {at}: {message}");
    }
}
