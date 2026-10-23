using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Tzar2.Formats;
using Tzar2.Formats.Pal;
using Tzar2.Tests.Shared;
using Tzar2.Tools.Cli.Fidelity.Analysis;
using Tzar2.Tools.Cli.Fidelity.Capture;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using Tzar2.Content.Rules;
using Tzar2.Tools.Cli.Fidelity.Yaml;
using Xunit;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

/// <summary>
/// The whole pipeline on synthetic data: scenarios run against a fake game that draws a synthetic world, the
/// session is zipped, and the analysis must recover the numbers the fake was built with (expansion rules, map
/// alignment, shadow factor, player ramps, speeds, attack period).
/// </summary>
public class EndToEndTests
{
    private static readonly SyntheticWorld World = new();
    private static readonly GamePalette Palette = GamePalette.Parse(SyntheticFormats.Pal());

    /// <summary>A clock that only moves when a (fake) sleep advances it, so timing-based scenarios are exact and instant.</summary>
    private sealed class VirtualClock : TimeProvider
    {
        private long _ns;

        public override long TimestampFrequency => 1_000_000_000;

        public override long GetTimestamp() => Interlocked.Read(ref _ns);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ns, by.Ticks * 100);
    }

    private sealed class TimedMemory(VirtualClock clock) : IMemoryReader
    {
        private readonly long _start = clock.GetTimestamp();

        public IReadOnlyList<MemoryRegion> WritableRegions() => [new MemoryRegion(0x2000, 64)];

        public int Read(ulong address, Span<byte> buffer)
        {
            buffer.Clear();
            long hp = Math.Max(0, 65 - (5 * (long)(clock.GetElapsedTime(_start).TotalMilliseconds / 150)));
            if (address <= 0x2008 && address + (ulong)buffer.Length >= 0x200C)
            {
                BitConverter.GetBytes((int)hp).CopyTo(buffer[(int)(0x2008 - address)..]);
            }

            return address >= 0x2000 && address < 0x2040 ? buffer.Length : 0;
        }
    }

    /// <summary>Camera (world pixel at the view's top-left) for a click on the minimap, as the fake game moves it.</summary>
    private static (int X, int Y) CameraFor(ScreenPoint click)
    {
        ScreenRect m = SyntheticWorld.Layout.Minimap, v = SyntheticWorld.Layout.View;
        double u = (click.X - m.X) / (double)m.Width, w = (click.Y - m.Y) / (double)m.Height;
        int worldPx = SyntheticWorld.Side * 32;
        return (Math.Clamp((int)((u * worldPx) - (v.Width / 2)), 0, worldPx - v.Width), Math.Clamp((int)((w * worldPx) - (v.Height / 2)), 0, worldPx - v.Height));
    }

    private static FakeGameDriver WorldDriver(string snapshotDir, Func<int, int, ushort, ushort>? overlay = null)
    {
        (int X, int Y) camera = (300, 300);
        var driver = new FakeGameDriver(snapshotDir);
        driver.OnLeftClick = p =>
        {
            if (SyntheticWorld.Layout.Minimap.Contains(p.X, p.Y))
            {
                camera = CameraFor(p);
            }
        };
        driver.Screen = _ => World.Render(camera.X, camera.Y, overlay);
        return driver;
    }

    private static void Run(string sessionDir, string root, string body, string id, Func<string, FakeGameDriver> driver, VirtualClock? clock = null)
    {
        // Always a virtual clock (issue #179): with the real one and no sleep, the runner's deadlines (the F11 snapshot,
        // the load wait) ran in wall time and a loaded CI runner failed a step now and then.
        clock ??= new VirtualClock();
        var env = new FakeEnvironment(root);
        using var log = new CaptureLog(TextWriter.Null, null);
        var runner = new CaptureRunner(CaptureRunnerTests.Session(), env, () => driver(env.SnapshotDirectories[0]), sessionDir, log, clock.Advance, clock);
        Assert.All(runner.Run([CaptureRunnerTests.Scenario(body, id)]), r => Assert.Equal("ok", r.Status));
    }

    private static ushort Ramp555(int ramp, int k)
    {
        Rgb24 c = Palette.PlayerRamp(ramp)[k];
        return (ushort)(((c.R >> 3) << 10) | ((c.G >> 3) << 5) | (c.B >> 3));
    }

    [Fact]
    public void Capture_zip_analyze_recovers_the_synthetic_truth()
    {
        using var dir = new TempDir();
        string sessionDir = Path.Combine(dir.Path, "capture-test");

        Run(sessionDir, dir.Path, """
            trials:
              - name: grass
                vars: {map: SYN}
            steps:
              - minimap: [0.3, 0.3]
              - snapshot: a
              - minimap: [0.6, 0.7]
              - snapshot: b
            analysis: [colour-depth, terrain-tiles]
            """, "colour-depth", d => WorldDriver(d));

        // Shadows: every fourth band of 6 rows darkened by floor(c * 20 / 32); a few spots twice (overlaps).
        static ushort Dark(ushort v) => (ushort)(((((v >> 10) & 31) * 20 / 32) << 10) | ((((v >> 5) & 31) * 20 / 32) << 5) | ((v & 31) * 20 / 32));
        Run(sessionDir, dir.Path, """
            trials:
              - name: grass
                vars: {map: SYN}
            steps:
              - minimap: [0.5, 0.5]
              - snapshot: {name: a, window: false}
              - minimap: [0.4, 0.6]
              - snapshot: {name: b, window: false}
            analysis: [shadow-blend]
            """, "shadow-blend", d =>
        {
            FakeGameDriver driver = WorldDriver(d, (x, y, v) =>
                !SyntheticWorld.Layout.View.Contains(x, y) || (y / 6) % 4 != 0 ? v : x % 50 < 8 ? Dark(Dark(v)) : Dark(v));
            driver.SnapshotFormat = PixelFormat.Rgb555;
            return driver;
        });

        // Player colours: a block of ramp colours at the view centre; slot 3 deliberately uses ramp 5.
        int[] rampOfSlot = [0, 0, 1, 4];
        int slot = 0;
        Run(sessionDir, dir.Path, """
            trials:
              - name: main
                vars: {map: SYN}
            steps:
              - note: one
              - snapshot: {name: s1, tags: {slot: "1"}, window: false}
              - note: two
              - snapshot: {name: s2, tags: {slot: "2"}, window: false}
              - note: three
              - snapshot: {name: s3, tags: {slot: "3"}, window: false}
            analysis: [player-colours]
            """, "player-colours", d =>
        {
            var driver = new FakeGameDriver(d);
            driver.Screen = _ =>
            {
                slot++;
                return World.Render(300, 300, (x, y, v) => Math.Abs(x - 587) < 30 && Math.Abs(y - 399) < 30 ? Ramp555(rampOfSlot[slot], 6 + ((x + y) % 10)) : v);
            };
            return driver;
        });

        // Unit speed: a red square moving at 120 px/s (knight, SPEED 6) and 80 px/s (peon, SPEED 4), on a virtual clock.
        ushort[] background = World.Render(300, 300);
        var speeds = new Dictionary<string, double> { ["knight"] = 120, ["peon"] = 80 };
        string trialName = "knight";
        var clock = new VirtualClock();
        Run(sessionDir, dir.Path, """
            trials:
              - name: knight
                vars: {unit: knight, speed_stat: "6"}
              - name: peon
                vars: {unit: peon, speed_stat: "4"}
            steps:
              - note: "${unit}"
              - burst: {name: move, count: 80, interval_ms: 40, region: [150, 360, 874, 60]}   # 3.2 s: a clean fit needs 3 s (ADR 0171)
            analysis: [unit-speed]
            """, "unit-speed", d =>
        {
            var driver = new FakeGameDriver(d);
            long start = 0;
            double speed = speeds[trialName];
            trialName = "peon";
            driver.Screen = _ =>
            {
                if (start == 0)
                {
                    start = clock.GetTimestamp();
                }

                int x0 = 300 + (int)(speed * clock.GetElapsedTime(start).TotalSeconds);
                ushort[] frame = (ushort[])background.Clone();
                for (int y = 380; y < 400; y++)
                {
                    Array.Fill(frame, (ushort)0x7C00, (y * 1024) + x0, 20);
                }

                return frame;
            };
            return driver;
        }, clock);

        // Attack timing: hit points 65, -5 every 150 ms of virtual time.
        Run(sessionDir, dir.Path, """
            trials:
              - name: main
                vars: {attacker: knight, defender: footman}
            steps:
              - memscan: {name: hp, value: 65}
              - memsample: {name: series, scan: hp, interval_ms: 5, duration_s: 1}
            analysis: [attack-timing]
            """, "attack-timing", d => new FakeGameDriver(d) { Memory = new TimedMemory(clock) }, clock);

        File.WriteAllText(Path.Combine(sessionDir, "session.json"),
            """{"session_id": "capture-test", "started_utc": "2026-10-17T10:00:00Z", "install": {"edition": "GOG v1.10 English", "matches_reference": true, "exe_md5": "d178304615a9058739ebafe3393fafb3"}, "harness": {"version": "test", "commit": "abc"}, "renderer": "fake", "machine": {"os": "test"}}""");
        string zip = sessionDir + ".zip";
        ZipFile.CreateFromDirectory(sessionDir, zip);

        using CaptureSession session = CaptureSession.Open(zip);
        Assert.Equal(6, session.Trials.Count);
        var ctx = new AnalysisContext(session, null, SyntheticWorld.Layout) { InjectedPalette = Palette };
        ctx.InjectedLibraries["grass"] = World.Library;
        ctx.InjectedMaps["SYN"] = World.Map;
        string factsDir = Path.Combine(dir.Path, "facts");
        IReadOnlyList<FactsFile> files = FidelityAnalyzer.Analyze(ctx, factsDir, null, TextWriter.Null);
        Assert.Equal(5, files.Count);
        foreach (FactsFile f in files)
        {
            Assert.All(f.Statuses, s => Assert.True(s.Value == "ok", $"{f.ScenarioId}/{s.Key}: {s.Value}\n{File.ReadAllText(f.Path)}"));
        }

        YamlMapping Facts(string scenario, string analysis)
        {
            var doc = (YamlMapping)MiniYaml.Parse(File.ReadAllText(Path.Combine(factsDir, scenario + ".yaml")));
            Assert.Equal(FidelityAnalyzer.FactsSchema, ((YamlScalar)doc.Get("schema")!).Value);
            Assert.Equal("GOG v1.10 English", Scalar(doc, "provenance", "edition"));
            Assert.Equal("true", Scalar(doc, "provenance", "install_matches_reference"));
            return (YamlMapping)((YamlMapping)((YamlMapping)doc.Get("analyses")!).Get(analysis)!).Get("facts")!;
        }

        // UNK-39: surface 565, display replicate (red), art -> surface replicate (green 5 -> 6).
        YamlMapping colour = (YamlMapping)((YamlSequence)Facts("colour-depth", "colour-depth").Get("trials")!).Items[0];
        Assert.Equal("rgb565", Scalar(colour, "game_surface"));
        Assert.Equal("replicate", Scalar(colour, "display", "red", "model"));
        Assert.Equal("replicate", Scalar(colour, "art_to_surface", "green", "model"));
        Assert.Equal("6", Scalar(colour, "art_to_surface", "green", "to_bits"));

        // UNK-10: every snapshot placed on the map; each word shows exactly one tile.
        YamlMapping terrain = (YamlMapping)((YamlSequence)Facts("colour-depth", "terrain-tiles").Get("trials")!).Items[0];
        Assert.Equal("2", Scalar(terrain, "aligned_snapshots"));
        Assert.Equal("0", Scalar(terrain, "words_with_several_tiles"));
        Assert.Equal("0", Scalar(terrain, "cells", "partial"));

        // UNK-03: the shadow rule and the double application on overlaps.
        YamlMapping shadow = Facts("shadow-blend", "shadow-blend");
        Assert.Equal("floor(c*20/32)", Scalar(shadow, "model"));
        Assert.True(int.Parse(Scalar(shadow, "double_application_pairs"), System.Globalization.CultureInfo.InvariantCulture) > 0);

        // Player ramps: slot 1 -> 1, slot 2 -> 2, slot 3 -> 5 (not an identity mapping).
        var slots = ((YamlSequence)Facts("player-colours", "player-colours").Get("slots")!).Items.Cast<YamlMapping>().ToList();
        Assert.Equal(["1", "2", "5"], slots.Select(s => Scalar(s, "ramp")));
        Assert.Equal("false", Scalar(Facts("player-colours", "player-colours"), "identity_mapping"));

        // Speeds: 120 and 80 px/s -> 20 px/s per SPEED point.
        YamlMapping speed = Facts("unit-speed", "unit-speed");
        var measured = ((YamlSequence)speed.Get("measurements")!).Items.Cast<YamlMapping>().ToDictionary(m => Scalar(m, "unit"), m => double.Parse(Scalar(m, "px_per_s"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(measured["knight"], 100, 140);
        Assert.InRange(measured["peon"], 65, 95);
        Assert.InRange(double.Parse(Scalar(speed, "speed_stat_relation", "px_per_s_per_speed"), System.Globalization.CultureInfo.InvariantCulture), 10, 30);

        // Attack period 150 ms, 5 damage per hit.
        YamlMapping attack = (YamlMapping)((YamlSequence)Facts("attack-timing", "attack-timing").Get("measurements")!).Items[0];
        Assert.InRange(double.Parse(Scalar(attack, "period_ms_median"), System.Globalization.CultureInfo.InvariantCulture), 130, 170);
        Assert.StartsWith("5x", ((YamlSequence)attack.Get("damage_values")!).Items.Select(i => ((YamlScalar)i).Value).Single(), StringComparison.Ordinal);

        // The facts contain numbers and names, never image data.
        foreach (FactsFile f in files)
        {
            Assert.True(new FileInfo(f.Path).Length < 200_000);
        }

        // check: the same session reads as usable.
        var output = new StringWriter();
        Assert.Equal(ExitCodes.Success, CliApp.Run(["fidelity", "check", zip], output, new StringWriter()));
        Assert.Contains("usable", output.ToString(), StringComparison.Ordinal);
    }

    private static string Scalar(YamlMapping map, params string[] path)
    {
        YamlNode node = map;
        foreach (string p in path)
        {
            node = ((YamlMapping)node).Get(p) ?? throw new KeyNotFoundException(string.Join('.', path));
        }

        return ((YamlScalar)node).Value;
    }
}
