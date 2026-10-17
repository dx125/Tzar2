using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Tools.Cli.Fidelity.Capture;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

/// <summary>A game stand-in: records input, renders a frame per capture, writes an F11 BMP when F11 is released.</summary>
internal sealed class FakeGameDriver(string snapshotDir) : IGameDriver
{
    public List<string> Events { get; } = [];

    public Func<int, ushort[]> Screen { get; set; } = _ => new ushort[1024 * 768];

    /// <summary>Pixel format of the F11 snapshots (the game's surface).</summary>
    public PixelFormat SnapshotFormat { get; set; } = PixelFormat.Rgb565;

    /// <summary>Called on every left-button press with the last mouse position (e.g. minimap clicks move the camera).</summary>
    public Action<ScreenPoint>? OnLeftClick { get; set; }

    private ScreenPoint _mouse;

    public bool ShowWindow { get; set; } = true;

    /// <summary>Exit after this many input events (simulated crash), or never.</summary>
    public int ExitAfterEvents { get; set; } = int.MaxValue;

    public IMemoryReader? Memory { get; set; }

    public int Captures { get; private set; }

    public List<IReadOnlyList<string>> Launches { get; } = [];

    private bool _exited;
    private int _snapshot;

    public void Launch(string exePath, string workingDirectory, IReadOnlyList<string> arguments) => Launches.Add(arguments);

    public bool HasExited => _exited;

    public int? ExitCode => _exited ? -1073741819 : null;

    public bool WaitForWindow(TimeSpan timeout) => ShowWindow;

    public (int Width, int Height) ClientSize => (1024, 768);

    public bool Focus() => true;

    public void Key(ushort virtualKey, bool down)
    {
        Record($"key {virtualKey:X2} {(down ? "down" : "up")}");
        if (virtualKey == 0x7A && !down)
        {
            Directory.CreateDirectory(snapshotDir);
            ushort[] frame = Screen(Captures++);
            CaptureImage image = SnapshotFormat.GreenBits == 6
                ? SyntheticWorld.Snapshot565(frame)
                : new CaptureImage(1024, 768, PixelFormat.Rgb555, Array.ConvertAll(frame, v => (uint)v));
            File.WriteAllBytes(Path.Combine(snapshotDir, $"tzar{_snapshot++:D4}.bmp"), image.EncodeBmp());
        }
    }

    public void Type(string text) => Record($"type {text}");

    public void MouseMove(ScreenPoint point)
    {
        _mouse = point;
        Record($"move {point.X},{point.Y}");
    }

    public void MouseButton(MouseButton button, bool down)
    {
        if (button == Cli.Fidelity.Scenarios.MouseButton.Left && down)
        {
            OnLeftClick?.Invoke(_mouse);
        }

        Record($"{button} {(down ? "down" : "up")}".ToLowerInvariant());
    }

    public CaptureImage? CaptureWindow() => _exited ? null : SyntheticWorld.Window888(Screen(Captures++));

    public void Close(TimeSpan timeout) => Events.Add("close");

    public void Dispose()
    {
    }

    private void Record(string e)
    {
        Events.Add(e);
        if (Events.Count >= ExitAfterEvents)
        {
            _exited = true;
        }
    }
}

internal sealed class FakeEnvironment(string root) : IGameEnvironment
{
    public string ExePath => Path.Combine(root, "Tzar.exe");

    public string WorkingDirectory => root;

    public IReadOnlyList<string> SnapshotDirectories => [Path.Combine(root, "ScrnShots")];

    public string RendererDescription => "fake";

    public List<(IReadOnlyDictionary<string, string> Ini, IReadOnlyDictionary<string, string> Wrapper)> Applied { get; } = [];

    public void ApplySettings(IReadOnlyDictionary<string, string> ini, IReadOnlyDictionary<string, string> wrapper) => Applied.Add((ini, wrapper));
}

/// <summary>Memory of a fake process: a few regions of bytes.</summary>
internal sealed class FakeMemory : IMemoryReader
{
    public List<(ulong Base, byte[] Data)> Regions { get; } = [];

    public IReadOnlyList<MemoryRegion> WritableRegions() => Regions.ConvertAll(r => new MemoryRegion(r.Base, r.Data.Length));

    public int Read(ulong address, Span<byte> buffer)
    {
        foreach ((ulong b, byte[] data) in Regions)
        {
            if (address >= b && address + (ulong)buffer.Length <= b + (ulong)data.Length)
            {
                data.AsSpan((int)(address - b), buffer.Length).CopyTo(buffer);
                return buffer.Length;
            }

            if (address >= b && address < b + (ulong)data.Length)
            {
                int n = (int)(b + (ulong)data.Length - address);
                data.AsSpan((int)(address - b), n).CopyTo(buffer);
                return n;
            }
        }

        return 0;
    }
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "t2-fidelity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
