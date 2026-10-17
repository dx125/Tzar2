using System;
using System.Collections.Generic;

namespace Tzar2.Client.Logic;

/// <summary>
/// Command-line options of the client after Godot's <c>--</c> separator (<c>OS.GetCmdlineUserArgs()</c>). Godot-free so
/// that it is unit-tested headless (tests/Tzar2.Client.Tests links this file).
/// </summary>
public sealed record ViewerArgs
{
    /// <summary>Usage text.</summary>
    public const string Usage =
        "Tzar2 client (M1: asset viewer)\n"
        + "  --package <path>     imported package (.t2pkg from 't2 import', or an unpacked folder)\n"
        + "  --soundfont <path>   General MIDI SoundFont (.sf2) for music playback\n"
        + "  --config <path>      settings file (default user://viewer.cfg)\n"
        + "  --select <name>      open this IMAGES.INI entry in the sprite browser\n"
        + "  --capture <dir>      render the scripted screenshot set into <dir> and quit\n"
        + "  --smoke              load everything once and quit (exit code 0 = OK)\n";

    /// <summary>Package path given on the command line, or null.</summary>
    public string? Package { get; init; }

    /// <summary>SoundFont path given on the command line, or null.</summary>
    public string? SoundFont { get; init; }

    /// <summary>Settings file, or null for the default.</summary>
    public string? Config { get; init; }

    /// <summary>Sprite entry to select at start, or null.</summary>
    public string? Select { get; init; }

    /// <summary>Screenshot output folder, or null.</summary>
    public string? CaptureDir { get; init; }

    /// <summary>Smoke-test mode.</summary>
    public bool Smoke { get; init; }

    /// <summary>Show the usage and quit.</summary>
    public bool Help { get; init; }

    /// <summary>Problems found while parsing (unknown options, missing values).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Parses the user arguments.</summary>
    public static ViewerArgs Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var errors = new List<string>();
        var result = new ViewerArgs();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? Value()
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return args[++i];
                }

                errors.Add($"{arg} needs a value");
                return null;
            }

            switch (arg)
            {
                case "--package":
                    result = result with { Package = Value() };
                    break;
                case "--soundfont":
                    result = result with { SoundFont = Value() };
                    break;
                case "--config":
                    result = result with { Config = Value() };
                    break;
                case "--select":
                    result = result with { Select = Value() };
                    break;
                case "--capture":
                    result = result with { CaptureDir = Value() };
                    break;
                case "--smoke":
                    result = result with { Smoke = true };
                    break;
                case "--viewer":
                    // The asset viewer is the only client mode in M1; accepted for forward compatibility.
                    break;
                case "-h" or "--help":
                    result = result with { Help = true };
                    break;
                default:
                    errors.Add($"unknown option '{arg}'");
                    break;
            }
        }

        return result with { Errors = errors };
    }
}

/// <summary>Chooses the SoundFont to use: command line, then settings, then environment, then the dev download folder.</summary>
public static class SoundFontLocator
{
    /// <summary>Environment variable that may name a SoundFont.</summary>
    public const string EnvironmentVariable = "TZAR2_SOUNDFONT";

    /// <summary>The first candidate that exists according to <paramref name="exists"/>, or null.</summary>
    public static string? Choose(Func<string, bool> exists, params string?[] candidates)
    {
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
