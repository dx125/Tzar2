using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Tzar2.Content.Assets;
using Tzar2.Content.Packages;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Client.Tests;

/// <summary>Runs only where a Godot 4.7 .NET editor binary is available (local development; CI has none yet).</summary>
public sealed class GodotFactAttribute : FactAttribute
{
    public GodotFactAttribute()
    {
        if (GodotRunner.Executable is null)
        {
            Skip = "Godot 4.7 .NET not found (set TZAR2_GODOT to the editor binary to run the client smoke tests).";
        }
    }
}

/// <summary>Finds and runs the Godot editor binary on src/Tzar2.Client.</summary>
internal static class GodotRunner
{
    private static readonly object BuildLock = new();
    private static bool s_built;

    public static string? Executable { get; } = Find();

    public static string RepositoryRoot { get; } = FindRoot();

    private static string? Find()
    {
        foreach (string? candidate in new[]
        {
            Environment.GetEnvironmentVariable("TZAR2_GODOT"),
            Environment.GetEnvironmentVariable("GODOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/Godot_mono.app/Contents/MacOS/Godot"),
            "/Applications/Godot_mono.app/Contents/MacOS/Godot",
        })
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tzar2.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find Tzar2.sln above " + AppContext.BaseDirectory);
    }

    /// <summary>The dotnet host running the tests (Godot needs the SDK to load the client's assemblies).</summary>
    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    /// <summary>Builds the client in Debug, the configuration the Godot editor binary loads.</summary>
    public static void EnsureClientBuilt()
    {
        lock (BuildLock)
        {
            if (s_built)
            {
                return;
            }

            (int exit, string output) = Run(Dotnet, ["build", Path.Combine(RepositoryRoot, "src/Tzar2.Client/Tzar2.Client.csproj"), "-c", "Debug", "-nologo", "-v", "q"], TimeSpan.FromMinutes(5));
            Assert.True(exit == 0, "Building the client failed:\n" + output);
            s_built = true;
        }
    }

    public static (int Exit, string Output) RunClient(params string[] userArgs)
    {
        EnsureClientBuilt();
        string[] args = ["--headless", "--path", Path.Combine(RepositoryRoot, "src/Tzar2.Client"), "--", .. userArgs];
        return Run(Executable!, args, TimeSpan.FromMinutes(3));
    }

    private static (int Exit, string Output) Run(string file, string[] args, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        string dotnetDir = Path.GetDirectoryName(Path.GetFullPath(Dotnet)) ?? string.Empty;
        if (Path.IsPathRooted(Dotnet))
        {
            info.Environment["DOTNET_ROOT"] = dotnetDir;
            info.Environment["PATH"] = dotnetDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            return (-1, output + "\n(timed out)");
        }

        process.WaitForExit();
        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }
}

/// <summary>
/// The headless client smoke test of the M1 viewer: a synthetic package (generated here, no original data) is loaded,
/// every page is exercised once, and the client must exit with 0.
/// </summary>
public sealed class GodotSmokeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tzar2-client-smoke-" + Guid.NewGuid().ToString("N"));

    public GodotSmokeTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [GodotFact]
    public void Viewer_loads_a_synthetic_package_and_exits_cleanly()
    {
        string package = Path.Combine(_dir, "synthetic.t2pkg");
        SyntheticPackage.Build().WriteTo(package);
        (int exit, string output) = GodotRunner.RunClient("--config", Path.Combine(_dir, "viewer.cfg"), "--package", package, "--smoke", "--select", "bldg");
        _output.WriteLine(output);
        Assert.Equal(0, exit);
        string line = output.Split('\n').Single(l => l.StartsWith("VIEWER SMOKE OK", StringComparison.Ordinal));
        // Two IMAGES.INI entries (their _s/_c companions are shown with them) plus the three terrain bitmaps as raw files.
        Assert.Contains("sprites=5 [unit, bldg, art/terrain/test/00.t2img", line, StringComparison.Ordinal);
        Assert.Contains("terrain=3", line, StringComparison.Ordinal);
        Assert.Contains("fonts=1", line, StringComparison.Ordinal);
        Assert.Contains("sounds=1", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR", output, StringComparison.Ordinal);
    }

    [GodotFact]
    public void A_skirmish_runs_300_ticks_headless_on_a_synthetic_package()
    {
        // The synthetic art plus the synthetic legacy tables (CI never sees original data): the classic ruleset
        // compiles, the test map is generated, the whole game screen (world renderer, HUD, audio) runs on the dummy
        // renderer while the sim thread plays 300 ticks with a computer opponent.
        string package = Path.Combine(_dir, "game.t2pkg");
        var writer = new PackageWriter();
        var tables = Tzar2.Content.Rules.SyntheticLegacyContent.CreateTables();
        foreach (string path in tables.Paths)
        {
            writer.Add(path, tables.ReadAllBytes(path));
        }

        foreach (KeyValuePair<string, byte[]> entry in SyntheticPackage.Build().Entries)
        {
            if (!writer.Contains(entry.Key))
            {
                writer.Add(entry.Key, entry.Value);
            }
        }

        writer.WriteTo(package);
        (int exit, string output) = GodotRunner.RunClient("--config", Path.Combine(_dir, "game.cfg"), "--package", package, "--game-smoke", "--ticks", "300", "--map-size", "48");
        _output.WriteLine(output);
        Assert.Equal(0, exit);
        string line = output.Split('\n').Single(l => l.StartsWith("GAME SMOKE OK", StringComparison.Ordinal));
        Assert.Contains("ticks=3", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR", output, StringComparison.Ordinal);
    }

    [GodotFact]
    public void The_editor_edits_validates_saves_and_reopens_a_map_headless()
    {
        // TDD §18 (M5): the editor mode on the synthetic package: brush strokes, placement, walls, trees, starting
        // locations, rubber band and Alt-drag through the real screen, then save → reopen → byte-identical.
        string package = Path.Combine(_dir, "editor.t2pkg");
        GamePackage().WriteTo(package);

        // An invented legacy map the smoke run imports through the converter UI (§18.5).
        var legacy = new Tzar2.Tests.Shared.SyntheticMap { Name = "Smoke", Side = 12, Title = "Smoke import" };
        legacy.Objects.Add(new Tzar2.Tests.Shared.SyntheticObject { ClassName = "peon", X = 3, Y = 3, Owner = 1, Race = 1, Id = 1 });
        string wmp = Path.Combine(_dir, "smoke.wmp");
        File.WriteAllBytes(wmp, legacy.Build());
        (int exit, string output) = GodotRunner.RunClient("--config", Path.Combine(_dir, "editor.cfg"), "--package", package, "--editor-smoke", "--editor-import", wmp);
        _output.WriteLine(output);
        Assert.Equal(0, exit);
        string line = output.Split('\n').Single(l => l.StartsWith("EDITOR SMOKE OK", StringComparison.Ordinal));
        Assert.Contains("blocking=0", line, StringComparison.Ordinal);
        Assert.Contains("playtest=", line, StringComparison.Ordinal);
        Assert.Contains("import=smoke.t2map", line, StringComparison.Ordinal);
        Assert.Contains("campaign=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR", output, StringComparison.Ordinal);
    }

    [GodotFact]
    public void A_lan_match_against_a_computer_and_its_replay_run_headless()
    {
        // ADR 0168: the multiplayer menu, a LAN host (embedded relay), the lobby, the lockstep match against a Hard
        // computer with the overlay, then the replay viewer at 16x with a backward seek, all on the dummy renderer.
        string package = Path.Combine(_dir, "mp.t2pkg");
        GamePackage().WriteTo(package);
        (int exit, string output) = GodotRunner.RunClient("--config", Path.Combine(_dir, "mp.cfg"), "--package", package, "--mp-smoke", "--ticks", "300");
        _output.WriteLine(output);
        Assert.Equal(0, exit);
        string line = output.Split('\n').Single(l => l.StartsWith("MP SMOKE OK", StringComparison.Ordinal));
        Assert.Contains("generation=1", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR", output, StringComparison.Ordinal);
    }

    // The synthetic art plus the synthetic legacy tables, so the classic ruleset compiles.
    private static PackageWriter GamePackage()
    {
        var writer = new PackageWriter();
        var tables = Tzar2.Content.Rules.SyntheticLegacyContent.CreateTables();
        foreach (string path in tables.Paths)
        {
            writer.Add(path, tables.ReadAllBytes(path));
        }

        foreach (KeyValuePair<string, byte[]> entry in SyntheticPackage.Build().Entries)
        {
            if (!writer.Contains(entry.Key))
            {
                writer.Add(entry.Key, entry.Value);
            }
        }

        return writer;
    }

    [GodotFact]
    public void Without_a_package_the_first_run_screen_is_shown()
    {
        (int exit, string output) = GodotRunner.RunClient("--config", Path.Combine(_dir, "empty.cfg"), "--smoke");
        _output.WriteLine(output);
        Assert.Equal(0, exit);
        Assert.Contains("VIEWER SMOKE FIRST-RUN", output, StringComparison.Ordinal);
    }

    [GodotFact]
    public void A_broken_package_path_falls_back_to_the_first_run_screen()
    {
        (int exit, string output) = GodotRunner.RunClient("--config", Path.Combine(_dir, "c.cfg"), "--package", Path.Combine(_dir, "missing.t2pkg"), "--smoke");
        _output.WriteLine(output);
        Assert.Equal(0, exit);
        Assert.Contains("Cannot open package", output, StringComparison.Ordinal);
        Assert.Contains("VIEWER SMOKE FIRST-RUN", output, StringComparison.Ordinal);
    }
}

/// <summary>A tiny package with every manifest the viewer reads, made of generated art (no original data).</summary>
internal static class SyntheticPackage
{
    public static PackageWriter Build()
    {
        var p = new PackageWriter();
        p.AddText(PlayerPalette.PackagePath, "{\"entries\":[" + string.Join(",", Enumerable.Range(0, 256).Select(i => $"\"{i:x2}{i:x2}{i:x2}\"")) + "],\"playerRampBase\":112}");

        // An 8-direction indexed unit (2 frames, 4×4 cells) with player pixels and a baked-shadow index.
        Rgba32[] palette = Enumerable.Range(0, 256).Select(i => new Rgba32((byte)i, 0, 0, i == 0 ? (byte)0 : (byte)255)).ToArray();
        var cells = Enumerable.Range(0, 16).Select(i => new SpriteCell(1, 1, 2, 2, i * 4)).ToArray();
        p.Add("art/unit/test unit.t2spr", new SpriteSheet
        {
            Format = PixelFormat.Indexed8, SourceKind = 1, Directions = 8, FramesPerDirection = 2, CellWidth = 4, CellHeight = 4,
            Palette = palette, PlayerFirst = 240, PlayerCount = 16, ShadowIndices = [3], Cells = cells,
            PixelData = Enumerable.Range(0, 64).Select(i => (byte)(i % 4 == 0 ? 240 + (i % 16) : i % 4 == 1 ? 3 : 9)).ToArray(),
        }.ToBytes());

        // A building (RGB5A1), its shadow (R8) and a 2-frame indexed _c overlay.
        p.Add("art/building/test bldg.t2spr", new SpriteSheet
        {
            Format = PixelFormat.Rgb5A1, SourceKind = 3, Directions = 1, FramesPerDirection = 1, CellWidth = 8, CellHeight = 8,
            Cells = [new SpriteCell(0, 0, 8, 8, 0)], PixelData = Enumerable.Range(0, 64).SelectMany(_ => new byte[] { 0x1F, 0x80 }).ToArray(),
        }.ToBytes());
        p.Add("art/building/test bldg_s.t2spr", new SpriteSheet
        {
            Format = PixelFormat.R8, SourceKind = 7, Directions = 1, FramesPerDirection = 1, CellWidth = 8, CellHeight = 8,
            Cells = [new SpriteCell(4, 4, 4, 4, 0)], PixelData = Enumerable.Repeat((byte)1, 16).ToArray(),
        }.ToBytes());
        p.Add("art/building/mask/test bldg c.t2img", new ImageAsset
        {
            Format = PixelFormat.Indexed8, Width = 2, Height = 4, Palette = palette, Pixels = [240, 245, 250, 255, 0, 241, 250, 9],
        }.ToBytes());
        p.AddText("manifests/sprite-index.json", """
            [{"name":"unit","ini":"data/images.ini","asset":"art/unit/test unit.t2spr","properties":{"rows":["2"],"cols":["8"],"anim_time":["300"],"object":["0,0,4,4"]}},
             {"name":"bldg","ini":"data/images.ini","asset":"art/building/test bldg.t2spr","properties":{}},
             {"name":"bldg_s","ini":"data/images.ini","asset":"art/building/test bldg_s.t2spr","properties":{}},
             {"name":"bldg_c","ini":"data/images.ini","asset":"art/building/mask/test bldg c.t2img","properties":{"rows":["2"],"object":["3,2"],"fps":["4"]}}]
            """);

        // Terrain: animated water (2 frames of one tile), dirt, and a water/dirt transition (2 variants × 14 shapes).
        p.Add("art/terrain/test/00.t2img", Rgb(32, 64, (x, y) => 0x8000 | (y < 32 ? 0x001F : 0x0010)));
        p.Add("art/terrain/test/11.t2img", Rgb(32, 32, (x, y) => 0x8000 | 0x2A0A));
        p.Add("art/terrain/test/01.t2img", Rgb(64, 448, (x, y) => (x % 32) < 16 ? 0x7C1F : (x % 32) == 31 ? 0x8000 | 0x7FFF : 0x03E0));
        p.AddText("manifests/terrain.json", """
            [{"set":"test","name":"Test","table":"t","folder":"art/terrain/test/","terrains":[{"index":"0","name":"Water"},{"index":"1","name":"Dirt"}],
              "files":[{"pair":"00","asset":"art/terrain/test/00.t2img","size":"1,1,0"},{"pair":"11","asset":"art/terrain/test/11.t2img","size":"1,1,0"},
                       {"pair":"01","asset":"art/terrain/test/01.t2img","size":"2,14,0"}],
              "light":null,"trees":null,"treeShadows":null}]
            """);

        // A 2-glyph font.
        p.Add("fonts/test.t2img", new ImageAsset { Format = PixelFormat.R8, Width = 3, Height = 6, Pixels = [1, 0, 1, 0, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 0, 1] }.ToBytes());
        p.AddText("fonts/test.json", "{\"strip\":\"fonts/test.t2img\",\"face\":\"Test\",\"cellWidth\":3,\"cellHeight\":3,\"glyphCount\":2,\"characters\":[[63,63,0,3],[65,65,1,3]]}");
        p.AddText("manifests/fonts.json", "[{\"asset\":\"fonts/test.json\",\"strip\":\"fonts/test.t2img\",\"face\":\"Test\"}]");

        // A 0.1 s 22.05 kHz mono sine-ish WAV and a one-note MIDI file.
        p.Add("sound/test/beep.wav", Wave(Enumerable.Range(0, 2205).SelectMany(i => BitConverter.GetBytes((short)((i % 50) * 400 - 10000))).ToArray()));
        p.AddText("manifests/sounds.json", "[{\"asset\":\"sound/test/beep.wav\",\"channels\":1,\"sampleRate\":22050,\"bitsPerSample\":16,\"sampleFrames\":2205,\"durationMs\":100}]");
        p.Add("music/test.mid", Midi());
        p.AddText("manifests/music.json", "[{\"asset\":\"music/test.mid\",\"smfType\":0,\"tracks\":1,\"division\":96}]");
        return p;
    }

    private static byte[] Rgb(int width, int height, Func<int, int, int> word)
    {
        byte[] pixels = new byte[width * height * 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int w = word(x, y);
                pixels[2 * ((y * width) + x)] = (byte)w;
                pixels[(2 * ((y * width) + x)) + 1] = (byte)(w >> 8);
            }
        }

        return new ImageAsset { Format = PixelFormat.Rgb5A1, Width = width, Height = height, Pixels = pixels }.ToBytes();
    }

    private static byte[] Wave(byte[] data)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(22050);
        w.Write(44100);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        return stream.ToArray();
    }

    private static byte[] Midi()
    {
        // SMF type 0, 96 ticks per quarter: program change, note on (C4) for one quarter, end of track.
        byte[] track = [0x00, 0xC0, 0x00, 0x00, 0x90, 0x3C, 0x64, 0x60, 0x80, 0x3C, 0x00, 0x00, 0xFF, 0x2F, 0x00];
        byte[] header = [(byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, 0, 0, 1, 0, 96];
        byte[] trackHeader = [(byte)'M', (byte)'T', (byte)'r', (byte)'k', 0, 0, 0, (byte)track.Length];
        return [.. header, .. trackHeader, .. track];
    }
}
