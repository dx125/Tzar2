using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tzar2.Tools.Cli.Fidelity.Capture;

/// <summary>How the game draws to the screen during a session.</summary>
internal enum RendererMode
{
    /// <summary>The pinned cnc-ddraw release in windowed GDI mode (default: capturable window, exact pixels).</summary>
    CncDdraw,

    /// <summary>Whatever DirectDraw wrapper (or none) the install already has; its settings are left alone.</summary>
    Existing,

    /// <summary>No wrapper: the system's DirectDraw (full-screen 16-bit; window captures may be black).</summary>
    Native,
}

/// <summary>The work copy prepared for a session (see <see cref="WorkCopy"/>, <see cref="CncDdraw"/>).</summary>
internal sealed class GameEnvironment : IGameEnvironment
{
    private readonly string _pristineIni;
    private readonly RendererMode _renderer;

    public GameEnvironment(string install, string workDir, RendererMode renderer, string rendererDescription)
    {
        WorkingDirectory = workDir;
        ExePath = WorkCopy.FindExe(workDir) ?? throw new FileNotFoundException("Tzar.exe not found in the work copy", Path.Combine(workDir, "Tzar.exe"));
        string iniPath = IniPath(install);
        _pristineIni = File.Exists(iniPath) ? File.ReadAllText(iniPath) : string.Empty;
        _renderer = renderer;
        RendererDescription = rendererDescription;
        // The game's F11 folder (and the other names it may use), in the copy and, in case the game resolves its folder
        // through the registry, in the install.
        SnapshotDirectories = new[] { workDir, install }
            .SelectMany(root => new[] { "ScrnShots", "Shots" }.Select(name => Path.Combine(root, name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string ExePath { get; }

    public string WorkingDirectory { get; }

    public IReadOnlyList<string> SnapshotDirectories { get; }

    public string RendererDescription { get; }

    public void ApplySettings(IReadOnlyDictionary<string, string> ini, IReadOnlyDictionary<string, string> wrapper)
    {
        File.WriteAllText(IniPath(WorkingDirectory), IniEditor.Apply(_pristineIni, ini));
        if (_renderer == RendererMode.CncDdraw)
        {
            File.WriteAllText(Path.Combine(WorkingDirectory, "ddraw.ini"), CncDdraw.IniText(wrapper));
        }
    }

    /// <summary>The settings file next to the executable (any letter case; <c>tzar.ini</c> when absent).</summary>
    public static string IniPath(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root).FirstOrDefault(f => string.Equals(Path.GetFileName(f), "tzar.ini", StringComparison.OrdinalIgnoreCase))
          ?? Path.Combine(root, "tzar.ini")
        : Path.Combine(root, "tzar.ini");
}
