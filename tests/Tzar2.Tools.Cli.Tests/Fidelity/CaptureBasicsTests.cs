using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Tzar2.Tests.Shared;
using Tzar2.Tools.Cli.Fidelity.Capture;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using Xunit;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

public class IniEditorTests
{
    [Fact]
    public void Replaces_inserts_and_appends_while_keeping_everything_else()
    {
        string ini = "[DEFAULTS]\r\nLAYOUT=LAYOUT1024L\r\n; comment=1\r\nDAYNIGHT=1\r\n\r\n[VIDEO]\r\nX=1\r\n";
        string result = IniEditor.Apply(ini, new Dictionary<string, string>
        {
            ["defaults.daynight"] = "0",
            ["DEFAULTS.WEATHER"] = "0",
            ["SOUND.MUSIC"] = "0",
        });
        Assert.Equal("[DEFAULTS]\r\nLAYOUT=LAYOUT1024L\r\n; comment=1\r\nDAYNIGHT=0\r\nWEATHER=0\r\n\r\n[VIDEO]\r\nX=1\r\n\r\n[SOUND]\r\nMUSIC=0\r\n", result);
        Assert.Equal("0", IniEditor.Get(result, "defaults", "WEATHER"));
        Assert.Null(IniEditor.Get(result, "VIDEO", "comment"));
        Assert.Equal("[A]\r\nb=1\r\n", IniEditor.Apply(string.Empty, new Dictionary<string, string> { ["A.b"] = "1" }));
        Assert.Throws<ArgumentException>(() => IniEditor.Apply(ini, new Dictionary<string, string> { ["nodot"] = "1" }));
    }

    [Fact]
    public void Cnc_ddraw_settings_are_overridable_and_pinned()
    {
        string text = CncDdraw.IniText(new Dictionary<string, string> { ["rgb555"] = "true", ["extra"] = "1" });
        Assert.Contains("[ddraw]\r\n", text, StringComparison.Ordinal);
        Assert.Equal("true", IniEditor.Get(text, "ddraw", "rgb555"));
        Assert.Equal("gdi", IniEditor.Get(text, "ddraw", "renderer"));
        Assert.Equal("true", IniEditor.Get(text, "ddraw", "windowed"));
        Assert.Equal("1", IniEditor.Get(text, "ddraw", "extra"));
        Assert.Equal(64, CncDdraw.ZipSha256.Length);
        Assert.StartsWith("https://github.com/FunkyFr3sh/cnc-ddraw/releases/download/v" + CncDdraw.Version + "/", CncDdraw.Url, StringComparison.Ordinal);

        // Without devmode the windowed wrapper swallowed the first click into the window (PLAY never registered).
        string defaults = CncDdraw.IniText(new Dictionary<string, string>());
        Assert.Equal("true", IniEditor.Get(defaults, "ddraw", "devmode"));
        Assert.Equal("true", IniEditor.Get(defaults, "ddraw", "adjmouse"));
    }
}

public class MachineSettingsTests
{
    [Fact]
    public void Device_counts_reproduce_the_values_the_games_setup_wrote()
    {
        // Operator PC, 2026-10-18: 2 waveOut, 3 DirectSound entries (primary driver included), 1 midiOut, one monitor;
        // the game's Setup.exe wrote 2 / 3 / 3 / 2.
        IReadOnlyDictionary<string, string> ini = MachineSettings.IniFor(new MachineDevices(2, 2, 3, 1, 4624));
        Assert.Equal("2", ini[MachineSettings.DirectDrawKey]);
        Assert.Equal("3", ini[MachineSettings.WaveKey]);
        Assert.Equal("3", ini[MachineSettings.DirectSoundKey]);
        Assert.Equal("2", ini[MachineSettings.MidiKey]);
        Assert.Equal("4624", ini[MachineSettings.CpuKey]);

        // GOG's shipped values (2 / 5 / 5 / 2) fit the same rule with four sound devices.
        IReadOnlyDictionary<string, string> gog = MachineSettings.IniFor(new MachineDevices(2, 4, 5, 1, null));
        Assert.Equal(["2", "5", "5", "2"], new[] { MachineSettings.DirectDrawKey, MachineSettings.WaveKey, MachineSettings.DirectSoundKey, MachineSettings.MidiKey }.Select(k => gog[k]));
        Assert.False(gog.ContainsKey(MachineSettings.CpuKey));
        Assert.Empty(MachineSettings.IniFor(new MachineDevices(null, null, null, null, null)));
    }

    [Fact]
    public void Session_values_override_detected_ones_and_the_session_file_has_none()
    {
        var detected = new Dictionary<string, string> { [MachineSettings.WaveKey] = "3", [MachineSettings.MidiKey] = "2" };
        var session = new Dictionary<string, string> { [MachineSettings.WaveKey] = "4", ["SOUND.SOUND"] = "0" };
        IReadOnlyDictionary<string, string> merged = MachineSettings.Underlay(session, detected);
        Assert.Equal("4", merged[MachineSettings.WaveKey]);
        Assert.Equal("2", merged[MachineSettings.MidiKey]);
        Assert.Equal("0", merged["SOUND.SOUND"]);

        // Machine-specific values must never be committed in the session file.
        SessionConfig file = ScenarioLoader.LoadSession(Path.Combine(OriginPaths.RepositoryRoot, "tools", "capture", "session.yaml"));
        string[] machine = [MachineSettings.DirectDrawKey, MachineSettings.WaveKey, MachineSettings.DirectSoundKey, MachineSettings.MidiKey, MachineSettings.CpuKey];
        Assert.DoesNotContain(file.Ini.Keys, k => machine.Contains(k, StringComparer.OrdinalIgnoreCase));
        Assert.Equal("0", file.Ini["VIDEO.DIRECTXDEVICE"]);
    }

    [Fact]
    public void Compat_database_warning_names_the_workaround_without_acting()
    {
        Assert.Empty(MachineSettings.CompatDatabaseWarning([], "C:\\Games\\Tzar"));
        IReadOnlyList<string> lines = MachineSettings.CompatDatabaseWarning(["{a31e51c2-fa1b-4a9c-ba3b-601e9006ef54}", "{A31E51C2-FA1B-4A9C-BA3B-601E9006EF54}"], "C:\\Games\\Tzar");
        Assert.Single(lines, l => l.Contains("sdbinst -u -g {a31e51c2-fa1b-4a9c-ba3b-601e9006ef54}", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("goggame.sdb", StringComparison.Ordinal));
    }
}

public class WorkCopyTests
{
    [Fact]
    public void Sync_copies_changed_files_and_skips_runtime_folders()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(Path.Combine(src.Path, "Tzar.exe"), "exe");
        Directory.CreateDirectory(Path.Combine(src.Path, "DATA"));
        File.WriteAllText(Path.Combine(src.Path, "DATA", "a.ini"), "x");
        Directory.CreateDirectory(Path.Combine(src.Path, "ScrnShots"));
        File.WriteAllText(Path.Combine(src.Path, "ScrnShots", "tzar0000.bmp"), "shot");
        File.WriteAllText(Path.Combine(src.Path, "ddraw.dll"), "wrapper");

        WorkCopy.SyncResult first = WorkCopy.Sync(src.Path, dst.Path);
        Assert.Equal(3, first.Copied);
        Assert.False(File.Exists(Path.Combine(dst.Path, "ScrnShots", "tzar0000.bmp")));
        WorkCopy.SyncResult second = WorkCopy.Sync(src.Path, dst.Path);
        Assert.Equal(0, second.Copied);
        Assert.Equal(3, second.Unchanged);
        Assert.Equal(Path.Combine(dst.Path, "Tzar.exe"), WorkCopy.FindExe(dst.Path));
        Assert.Single(WorkCopy.WrapperFiles(dst.Path));
        Assert.Throws<ArgumentException>(() => WorkCopy.Sync(src.Path, src.Path));
    }

    [Fact]
    public void Environment_writes_tzar_ini_from_the_pristine_install_copy_and_ddraw_ini()
    {
        using var install = new TempDir();
        using var work = new TempDir();
        File.WriteAllText(Path.Combine(install.Path, "Tzar.exe"), "exe");
        File.WriteAllText(Path.Combine(install.Path, "tzar.ini"), "[DEFAULTS]\r\nDAYNIGHT=1\r\n");
        WorkCopy.Sync(install.Path, work.Path);
        var env = new GameEnvironment(install.Path, work.Path, RendererMode.CncDdraw, "test");
        env.ApplySettings(new Dictionary<string, string> { ["DEFAULTS.DAYNIGHT"] = "0" }, new Dictionary<string, string> { ["rgb555"] = "true" });
        env.ApplySettings(new Dictionary<string, string> { ["DEFAULTS.WEATHER"] = "0" }, new Dictionary<string, string>());
        string ini = File.ReadAllText(Path.Combine(work.Path, "tzar.ini"));
        // Each launch starts from the install's settings: the previous trial's override is gone.
        Assert.Equal("1", IniEditor.Get(ini, "DEFAULTS", "DAYNIGHT"));
        Assert.Equal("0", IniEditor.Get(ini, "DEFAULTS", "WEATHER"));
        Assert.Equal("false", IniEditor.Get(File.ReadAllText(Path.Combine(work.Path, "ddraw.ini")), "ddraw", "rgb555"));
        Assert.Contains(Path.Combine(work.Path, "ScrnShots"), env.SnapshotDirectories);
        Assert.Equal("[DEFAULTS]\r\nDAYNIGHT=1\r\n", File.ReadAllText(Path.Combine(install.Path, "tzar.ini")));
    }
}

public class CaptureImageTests
{
    [Theory]
    [InlineData(16, 0x7C00u, 0x03E0u, 0x001Fu)]
    [InlineData(16, 0xF800u, 0x07E0u, 0x001Fu)]
    [InlineData(32, 0xFF0000u, 0x00FF00u, 0x0000FFu)]
    public void Encodes_and_decodes_losslessly(int bits, uint r, uint g, uint b)
    {
        var format = new PixelFormat(bits, r, g, b);
        uint[] pixels = Enumerable.Range(0, 7 * 5).Select(i => (uint)((i * 2654435761u) & (r | g | b))).ToArray();
        var image = new CaptureImage(7, 5, format, pixels);
        CaptureImage back = CaptureImage.DecodeBmp(image.EncodeBmp());
        Assert.Equal(7, back.Width);
        Assert.Equal(5, back.Height);
        Assert.Equal(format.Name, back.Format.Name);
        Assert.Equal(pixels, back.Native);
    }

    [Fact]
    public void Decodes_bottom_up_555_and_24_bit_files()
    {
        // 2×2 BI_RGB 16-bit, stored bottom-up (the writer takes top-down pixels).
        byte[] bmp = SyntheticFormats.Bmp(2, 2, 16, [0x1F, 0x00, 0xE0, 0x03, 0x00, 0x7C, 0xFF, 0x7F]);
        CaptureImage image = CaptureImage.DecodeBmp(bmp);
        Assert.Equal("rgb555", image.Format.Name);
        Assert.Equal(0x001Fu, image.NativeAt(0, 0));
        Assert.Equal(0x7C00u, image.NativeAt(0, 1));
        Assert.Equal((31, 0, 0), image.Levels(image.NativeAt(0, 1)));
        byte[] rgb = SyntheticFormats.Bmp(1, 1, 24, [0x10, 0x20, 0xF8]);
        CaptureImage image24 = CaptureImage.DecodeBmp(rgb);
        Assert.Equal(0xF82010u, image24.NativeAt(0, 0));
        Assert.Equal((ushort)((31 << 10) | (4 << 5) | 2), image24.Canonical555(0));
        Assert.Throws<InvalidDataException>(() => CaptureImage.DecodeBmp([1, 2, 3]));
    }

    [Fact]
    public void Canonical_555_keeps_the_top_bits_of_any_format()
    {
        Assert.Equal((ushort)0x7FFF, CaptureImage.To555(0xFFFF, PixelFormat.Rgb565));
        Assert.Equal((ushort)((10 << 10) | (20 << 5) | 30), CaptureImage.To555(SyntheticWorld.To565((ushort)((10 << 10) | (20 << 5) | 30)), PixelFormat.Rgb565));
        Assert.Equal((ushort)((10 << 10) | (20 << 5) | 30), CaptureImage.To555(SyntheticWorld.To888((ushort)((10 << 10) | (20 << 5) | 30)), PixelFormat.Rgb888));
    }
}

public class MemoryProbeTests
{
    private static FakeMemory Memory()
    {
        var memory = new FakeMemory();
        byte[] a = new byte[4096];
        byte[] b = new byte[3 << 20];
        BitConverter.GetBytes(65).CopyTo(a, 100);
        BitConverter.GetBytes(65).CopyTo(b, 2_500_000);
        BitConverter.GetBytes((short)65).CopyTo(b, 8);
        BitConverter.GetBytes((short)1).CopyTo(b, 10); // as an int32 this reads 65 + 65536
        memory.Regions.Add((0x10000, a));
        memory.Regions.Add((0x400000, b));
        return memory;
    }

    [Fact]
    public void Scans_filters_and_samples()
    {
        FakeMemory memory = Memory();
        MemoryScanResult scan = MemoryProbe.Scan(memory, "hp", 65, 4, 100);
        Assert.Equal([0x10000ul + 100, 0x400000ul + 2_500_000], scan.Addresses);
        Assert.Equal((4096 + (3 << 20)), scan.BytesScanned);
        Assert.Equal(2, MemoryProbe.Scan(memory, "hp16", 65, 2, 100).Addresses.Count(a => a is 0x10000ul + 100 or 0x400000ul + 8));
        Assert.True(MemoryProbe.Scan(memory, "zero", 0, 4, 10).Truncated);

        BitConverter.GetBytes(50).CopyTo(memory.Regions[0].Data, 100);
        Assert.Equal([0x10000ul + 100], MemoryProbe.Filter(memory, "d", scan, ScanMode.Decreased, null, 10).Addresses);
        Assert.Equal([0x400000ul + 2_500_000], MemoryProbe.Filter(memory, "u", scan, ScanMode.Unchanged, null, 10).Addresses);
        Assert.Equal([0x10000ul + 100], MemoryProbe.Filter(memory, "e", scan, ScanMode.Exact, 50, 10).Addresses);
        Assert.Empty(MemoryProbe.Filter(memory, "i", scan, ScanMode.Increased, null, 10).Addresses);

        using var stream = new MemoryStream();
        int samples = MemoryProbe.Sample(memory, scan.Addresses, 4, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(30), stream,
            System.Diagnostics.Stopwatch.GetTimestamp(), CancellationToken.None);
        Assert.True(samples >= 2);
        MemoryProbe.SampleSeries series = MemoryProbe.ReadSamples(stream.ToArray());
        Assert.Equal(scan.Addresses, series.Addresses);
        Assert.Equal(samples, series.TimesMicroseconds.Count);
        Assert.All(series.Values[0], v => Assert.Equal(50, v));
        Assert.All(series.Values[1], v => Assert.Equal(65, v));
        Assert.Throws<InvalidDataException>(() => MemoryProbe.ReadSamples([1, 2, 3, 4]));
    }
}
