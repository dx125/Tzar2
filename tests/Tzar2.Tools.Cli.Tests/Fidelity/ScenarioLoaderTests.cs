using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Tests.Shared;
using Tzar2.Tools.Cli.Commands;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using Xunit;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

public class ScenarioLoaderTests
{
    private const string Header = "schema: tzar2-capture-scenario/1\nid: test\ntitle: Test\n";

    private static ScenarioSpec Parse(string body) => ScenarioLoader.ParseScenario(Header + body, "test.yaml");

    [Fact]
    public void The_repository_scenarios_and_session_are_valid()
    {
        (SessionConfig session, IReadOnlyList<ScenarioSpec> scenarios) = FidelityCommand.LoadSpecs(Path.Combine(OriginPaths.RepositoryRoot, "tools", "capture"));
        Assert.Equal("LAYOUT1024L", session.Layout.Name);
        Assert.Equal(new ScreenRect(150, 30, 874, 738), session.Layout.View);
        Assert.Equal("0", session.Ini["DEFAULTS.DAYNIGHT"]);
        Assert.Contains(scenarios, s => s.Id == "smoke" && s.Default);
        foreach (string id in new[] { "colour-depth", "terrain-tiles", "shadow-blend", "player-colours", "unit-speed", "attack-timing" })
        {
            ScenarioSpec s = Assert.Single(scenarios, s => s.Id == id);
            Assert.NotEmpty(s.Analysis);
            Assert.True(s.Minutes > 0);
        }

        Assert.False(scenarios.Single(s => s.Id == "combat-pairs").Default);
        // The default session must fit in about half an hour of the operator's time.
        Assert.InRange(scenarios.Where(s => s.Default).Sum(s => s.Minutes), 5, 35);

        // run-capture.ps1 -Batch A|B|C|D (ADR 0112): every id exists and every batch fits in half an hour.
        string script = File.ReadAllText(Path.Combine(OriginPaths.RepositoryRoot, "tools", "capture", "run-capture.ps1"));
        var batches = System.Text.RegularExpressions.Regex.Matches(script, "\"([ABCD])\" = @\\(([^)]*)\\)");
        Assert.Equal(4, batches.Count);
        foreach (System.Text.RegularExpressions.Match batch in batches)
        {
            string[] ids = batch.Groups[2].Value.Split(',').Select(x => x.Trim().Trim('"')).ToArray();
            double minutes = ids.Sum(id => Assert.Single(scenarios, s => s.Id == id).Minutes);
            Assert.InRange(minutes, 1, 30);
        }

        // What the first session (2026-10-18) established: only "-map <name>.wmp" loads a map, and it opens the setup
        // screen, so every scenario clicks PLAY; -explore / -nofog do nothing (the cheats reveal); a spawned unit is
        // placed with a click at the cursor.
        foreach (ScenarioSpec s in scenarios)
        {
            int map = s.Launch.ToList().IndexOf("-map");
            Assert.True(map >= 0 && map + 1 < s.Launch.Count, $"{s.Id}: no -map");
            Assert.True(s.Launch[map + 1].EndsWith(".wmp", System.StringComparison.Ordinal) || s.Trials.All(t => t.Vars["map"].EndsWith(".wmp", System.StringComparison.Ordinal)), $"{s.Id}: -map needs <name>.wmp");
            Assert.DoesNotContain("-explore", s.Launch);
            Assert.DoesNotContain("-nofog", s.Launch);
            Assert.Contains(s.Steps, step => step is ClickStep c && c.At == new ScreenPoint(772, 596));
            for (int i = 0; i < s.Steps.Count; i++)
            {
                if (s.Steps[i] is ChatStep chat && chat.Text.Contains("spawn", System.StringComparison.Ordinal))
                {
                    Assert.True(s.Steps.Skip(i + 1).OfType<ClickStep>().FirstOrDefault() is { Button: MouseButton.Left }, $"{s.Id}: line {chat.Line} spawns without a placing click");
                }
            }
        }
    }

    [Fact]
    public void Parses_every_step_kind()
    {
        ScenarioSpec s = Parse("""
            launch: [-nomovies, -map, "${map}"]
            trials:
              - name: one
                vars: {map: MP1}
                ini: {DEFAULTS.WEATHER: "1"}
                wrapper: {rgb555: "true"}
            steps:
              - wait: 1.5
              - key: F11
              - key: {key: C, ctrl: true, hold_ms: 100, repeat: 2}
              - text: abc
              - chat: hmreveal
              - note: hello
              - move: [10, 20]
              - minimap: [0.25, 0.75]
              - click: [5, 6]
              - click: {at: [7, 8], button: right, double: true}
              - drag: {from: [1, 2], to: [3, 4]}
              - snapshot: a
              - snapshot: {name: b, window: false, tags: {slot: "1"}}
              - burst: {name: c, count: 10, interval_ms: 50, region: [0, 0, 100, 100]}
              - memscan: {name: hp, value: 65, width: 2}
              - memscan: {name: hp2, from: hp, mode: decreased}
              - memsample: {name: series, scan: hp2, interval_ms: 10, duration_s: 2}
            analysis: [unit-speed]
            """);
        Assert.Equal(17, s.Steps.Count);
        Assert.Equal(["wait", "key", "key", "text", "chat", "note", "move", "minimap", "click", "click", "drag", "snapshot", "snapshot", "burst", "memscan", "memscan", "memsample"],
            s.Steps.Select(x => x.Kind));
        var key = Assert.IsType<KeyStep>(s.Steps[2]);
        Assert.True(key.Ctrl);
        Assert.Equal(2, key.Repeat);
        var click = Assert.IsType<ClickStep>(s.Steps[9]);
        Assert.Equal(MouseButton.Right, click.Button);
        Assert.True(click.Double);
        var snap = Assert.IsType<SnapshotStep>(s.Steps[12]);
        Assert.False(snap.Window);
        Assert.Equal("1", snap.Tags["slot"]);
        Assert.Equal(new ScreenRect(0, 0, 100, 100), Assert.IsType<BurstStep>(s.Steps[13]).Region);
        Assert.Equal(ScanMode.Decreased, Assert.IsType<MemScanStep>(s.Steps[15]).Mode);
        TrialSpec trial = Assert.Single(s.Trials);
        Assert.Equal("MP1", trial.Vars["map"]);
        Assert.Equal("1", trial.Ini["DEFAULTS.WEATHER"]);
        Assert.Equal("true", trial.Wrapper["rgb555"]);
        Assert.Equal(12, s.Steps[1].Line);
    }

    [Theory]
    [InlineData("steps:\n  - jump: 1", "unknown step 'jump'")]
    [InlineData("steps:\n  - key: NotAKey", "unknown key 'NotAKey'")]
    [InlineData("steps:\n  - wait: 1\n    key: F1", "exactly one key")]
    [InlineData("steps: []\ncolour: red", "unknown key 'colour'")]
    [InlineData("steps:\n  - memsample: {name: a, scan: nope}", "does not name an earlier memscan")]
    [InlineData("steps:\n  - memscan: {name: a, mode: changed}", "needs 'from'")]
    [InlineData("steps:\n  - memscan: {name: a}", "needs a value")]
    [InlineData("steps:\n  - snapshot: a\n  - snapshot: a", "duplicate capture name")]
    [InlineData("steps:\n  - snapshot: Bad_Name", "lower-case")]
    [InlineData("steps:\n  - minimap: [2, 0]", "fractions")]
    [InlineData("steps: []\nanalysis: [magic]", "unknown analysis 'magic'")]
    [InlineData("steps: []\nini: {NOSECTION: 1}", "SECTION.KEY")]
    [InlineData("steps:\n  - burst: {name: b, count: 1, interval_ms: 50}", "count must be")]
    public void Rejects_mistakes_with_the_file_and_line(string body, string message)
    {
        var ex = Assert.Throws<ScenarioException>(() => Parse(body));
        Assert.Contains("test.yaml: line ", ex.Message, System.StringComparison.Ordinal);
        Assert.Contains(message, ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_wrong_schema()
    {
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.ParseScenario("schema: other/1\nid: a\ntitle: b\nsteps: []", "x.yaml"));
        Assert.Contains("schema", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Expands_variables_and_rejects_unknown_ones()
    {
        var vars = new Dictionary<string, string> { ["cheat"] = "hm", ["unit"] = "knight" };
        Assert.Equal("hmspawn knight", ScenarioLoader.Expand("${cheat}spawn ${unit}", vars));
        Assert.Equal(["cheat", "unit"], ScenarioLoader.VariablesIn("${cheat}spawn ${unit}"));
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Expand("${nope}", vars));
    }

    [Fact]
    public void Undefined_variables_fail_validation()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "scenarios"));
        File.Copy(Path.Combine(OriginPaths.RepositoryRoot, "tools", "capture", "session.yaml"), Path.Combine(dir.Path, "session.yaml"));
        File.WriteAllText(Path.Combine(dir.Path, "scenarios", "a.yaml"), Header + "trials:\n  - name: x\n    vars: {u: 1}\n  - name: y\nsteps:\n  - chat: \"${u}\"\n");
        var ex = Assert.Throws<ScenarioException>(() => FidelityCommand.LoadSpecs(dir.Path));
        Assert.Contains("${u}", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Select_picks_defaults_named_or_all()
    {
        (_, IReadOnlyList<ScenarioSpec> all) = FidelityCommand.LoadSpecs(Path.Combine(OriginPaths.RepositoryRoot, "tools", "capture"));
        Assert.DoesNotContain(FidelityCommand.Select(all, null), s => s.Id == "combat-pairs");
        Assert.Equal(all.Count, FidelityCommand.Select(all, "all").Count);
        Assert.Equal(["terrain-tiles", "smoke"], FidelityCommand.Select(all, "terrain-tiles, smoke").Select(s => s.Id));
    }

    [Fact]
    public void Scenarios_command_lists_and_capture_requires_windows_elsewhere()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(ExitCodes.Success, CliApp.Run(["fidelity", "scenarios"], output, error));
        Assert.Contains("terrain-tiles", output.ToString(), System.StringComparison.Ordinal);
        if (!System.OperatingSystem.IsWindows())
        {
            Assert.Equal(ExitCodes.UsageError, CliApp.Run(["fidelity", "capture", "--tzar", "."], output, error));
            Assert.Contains("requires Windows", error.ToString(), System.StringComparison.Ordinal);
        }

        Assert.Equal(ExitCodes.UsageError, CliApp.Run(["fidelity", "capture", "--scenarios", "nope", "--tzar", "."], output, error));
        Assert.Equal(ExitCodes.UsageError, CliApp.Run(["fidelity", "frobnicate"], output, error));
        Assert.Equal(ExitCodes.UsageError, CliApp.Run(["fidelity", "analyze"], output, error));
    }
}
