using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Tzar2.Tools.Cli.Fidelity.Capture;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using Xunit;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

public class CaptureRunnerTests
{
    internal static SessionConfig Session(int loadSeconds = 0) => new(
        new Dictionary<string, string> { ["DEFAULTS.DAYNIGHT"] = "0", ["DEFAULTS.LAYOUT"] = "X" },
        new Dictionary<string, string> { ["rgb555"] = "false" },
        new Dictionary<string, string> { ["cheat"] = "hm" },
        SyntheticWorld.Layout,
        new SessionTiming(KeyHoldMs: 0, StepGapMs: 0, ChatOpenMs: 0, SnapshotTimeoutMs: 3000, WindowTimeoutSeconds: 1, LoadSeconds: loadSeconds, CloseTimeoutSeconds: 1));

    internal static ScenarioSpec Scenario(string body, string id = "test") =>
        ScenarioLoader.ParseScenario($"schema: tzar2-capture-scenario/1\nid: {id}\ntitle: T\n" + body, id + ".yaml");

    private const string Body = """
        ini: {DEFAULTS.LAYOUT: Y}
        launch: [-nomovies, "${flag}", -map, "${map}"]
        trials:
          - name: one
            vars: {map: MP1, flag: ""}
            wrapper: {rgb555: "true"}
        steps:
          - chat: "${cheat}reveal"
          - minimap: [0.5, 0.5]
          - snapshot: {name: a, tags: {slot: "2"}}
          - key: {key: C, ctrl: true}
          - burst: {name: b, count: 3, interval_ms: 10, region: [150, 30, 100, 50]}
          - memscan: {name: hp, value: 65}
          - memsample: {name: s, scan: hp, interval_ms: 5, duration_s: 0.05}
          - note: done
        """;

    [Fact]
    public void Runs_a_trial_and_records_everything()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir.Path);
        var memory = new FakeMemory();
        byte[] data = new byte[64];
        BitConverter.GetBytes(65).CopyTo(data, 16);
        memory.Regions.Add((0x1000, data));
        var driver = new FakeGameDriver(env.SnapshotDirectories[0]) { Memory = memory };
        string sessionDir = Path.Combine(dir.Path, "session");
        using var log = new CaptureLog(TextWriter.Null, null);
        var runner = new CaptureRunner(Session(), env, () => driver, sessionDir, log, _ => { });

        TrialResult result = Assert.Single(runner.Run([Scenario(Body)]));

        Assert.Equal("ok", result.Status);
        Assert.Equal(8, result.StepsOk);
        Assert.Equal(["-nomovies", "-map", "MP1"], driver.Launches.Single());
        (IReadOnlyDictionary<string, string> ini, IReadOnlyDictionary<string, string> wrapper) = env.Applied.Single();
        Assert.Equal("Y", ini["DEFAULTS.LAYOUT"]);
        Assert.Equal("0", ini["DEFAULTS.DAYNIGHT"]);
        Assert.Equal("true", wrapper["rgb555"]);

        // chat = Enter, text, Enter; minimap centre = (4 + 64, 31 + 64); Ctrl wraps C.
        int enter = driver.Events.IndexOf("key 0D down");
        Assert.True(enter >= 0);
        Assert.Equal("type hmreveal", driver.Events.Skip(enter).First(e => e.StartsWith("type", StringComparison.Ordinal)));
        Assert.Contains("move 68,95", driver.Events);
        int ctrl = driver.Events.IndexOf("key 11 down");
        Assert.True(ctrl >= 0 && driver.Events[ctrl + 1] == "key 43 down" && driver.Events.IndexOf("key 11 up") > ctrl);
        Assert.Contains("close", driver.Events);

        string trialDir = Path.Combine(sessionDir, "scenarios", "test", "one");
        Assert.True(File.Exists(Path.Combine(trialDir, "window", "a.bmp")));
        Assert.True(File.Exists(Path.Combine(trialDir, "game", "a.bmp")));
        Assert.Empty(Directory.GetFiles(env.SnapshotDirectories[0]));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(trialDir, "burst", "b")).Length);
        Assert.True(File.Exists(Path.Combine(trialDir, "mem", "hp.json")));
        Assert.True(File.Exists(Path.Combine(sessionDir, "scenarios", "test", "scenario.yaml")));

        using JsonDocument trial = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(trialDir, "trial.json")));
        JsonElement root = trial.RootElement;
        Assert.Equal("tzar2-capture-trial/1", root.GetProperty("schema").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        var captures = root.GetProperty("captures").EnumerateArray().ToList();
        Assert.Equal(["window", "game", "burst", "memscan", "memsample"], captures.Select(c => c.GetProperty("kind").GetString()));
        JsonElement game = captures[1];
        Assert.Equal("rgb565", game.GetProperty("format").GetString());
        Assert.Equal("2", game.GetProperty("tags").GetProperty("slot").GetString());
        Assert.Equal(0.5, game.GetProperty("minimap")[0].GetDouble());
        Assert.Equal(100, captures[2].GetProperty("region")[2].GetInt32());
        Assert.Equal(3, captures[2].GetProperty("frames").GetArrayLength());
        MemoryProbe.SampleSeries series = MemoryProbe.ReadSamples(File.ReadAllBytes(Path.Combine(trialDir, "mem", "s.bin")));
        Assert.Equal([0x1010ul], series.Addresses);
        Assert.NotEmpty(series.TimesMicroseconds);
    }

    [Fact]
    public void A_crash_ends_the_trial_and_the_session_continues()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir.Path);
        int created = 0;
        IGameDriver Factory() => new FakeGameDriver(env.SnapshotDirectories[0]) { ExitAfterEvents = created++ == 0 ? 3 : int.MaxValue };
        using var log = new CaptureLog(TextWriter.Null, null);
        var runner = new CaptureRunner(Session(), env, Factory, Path.Combine(dir.Path, "s"), log, _ => { });
        ScenarioSpec spec = Scenario("steps:\n  - chat: abc\n  - snapshot: x\n  - note: n\n");

        IReadOnlyList<TrialResult> results = runner.Run([spec, Scenario("steps:\n  - note: second\n", "second")]);

        Assert.Equal("crashed", results[0].Status);
        Assert.True(results[0].StepsSkipped >= 1);
        Assert.Equal("ok", results[1].Status);
        Assert.Equal(1, log.Errors);
    }

    [Fact]
    public void A_missing_window_fails_the_trial_without_running_steps()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir.Path);
        using var log = new CaptureLog(TextWriter.Null, null);
        var driver = new FakeGameDriver(env.SnapshotDirectories[0]) { ShowWindow = false };
        var runner = new CaptureRunner(Session(), env, () => driver, Path.Combine(dir.Path, "s"), log, _ => { });

        TrialResult result = Assert.Single(runner.Run([Scenario("steps:\n  - note: n\n")]));

        Assert.Equal("failed", result.Status);
        Assert.Contains("no game window", result.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dir.Path, "s", "scenarios", "test", "main", "trial.json")));
    }

    [Fact]
    public void Failing_steps_are_logged_and_the_trial_is_partial()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir.Path);
        using var log = new CaptureLog(TextWriter.Null, null);
        // No memory reader: the memscan step fails, the next step still runs.
        var driver = new FakeGameDriver(env.SnapshotDirectories[0]);
        var runner = new CaptureRunner(Session(), env, () => driver, Path.Combine(dir.Path, "s"), log, _ => { });

        TrialResult result = Assert.Single(runner.Run([Scenario("steps:\n  - memscan: {name: m, value: 1}\n  - note: after\n")]));

        Assert.Equal("partial", result.Status);
        Assert.Equal(1, result.StepsFailed);
        Assert.Equal(1, result.StepsOk);
        Assert.Equal(1, log.Warnings);
    }

    [Fact]
    public void Cancellation_skips_the_remaining_trials()
    {
        using var dir = new TempDir();
        var env = new FakeEnvironment(dir.Path);
        using var log = new CaptureLog(TextWriter.Null, null);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var runner = new CaptureRunner(Session(), env, () => new FakeGameDriver(env.SnapshotDirectories[0]), Path.Combine(dir.Path, "s"), log, _ => { })
        {
            Cancellation = cancel.Token,
        };

        Assert.All(runner.Run([Scenario("steps:\n  - note: n\n")]), r => Assert.Equal("cancelled", r.Status));
    }
}
