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
        "Tzar2 client (M3: skirmish; asset viewer)\n"
        + "  --package <path>     imported package (.t2pkg from 't2 import', or an unpacked folder)\n"
        + "  --viewer             open the asset viewer instead of the game\n"
        + "  --skirmish           start a skirmish directly (skips the menus)\n"
        + "  --editor [file]      open the scenario editor (optionally a .t2map)\n"
        + "  --editor-smoke       drive the editor headless (edit, validate, save, reopen) and quit (exit code 0 = OK)\n"
        + "  --editor [file]      open the scenario editor (optionally a .t2map)\n"
        + "  --editor-smoke       drive the editor headless (edit, validate, save, reopen) and quit (exit code 0 = OK)\n"
        + "  --autoplay <dir>     play a scripted skirmish, write screenshots into <dir> and quit\n"
        + "  --game-smoke         run a skirmish headless for --ticks ticks and quit (exit code 0 = OK)\n"
        + "  --mp-smoke           host a LAN match against a computer headless, then play its replay, and quit\n"
        + "  --ticks <n>          ticks for --game-smoke and --mp-smoke (default 300)\n"
        + "  --seed <n>           match seed\n"
        + "  --map-type <type>    random map type (random, archipelagos, island, continental, lakes, dry)\n"
        + "  --map-size <size>    random map size (tiny ... enormous), or a number: the test map's side in tiles\n"
        + "  --players <n>        kingdoms in the skirmish (2-8, default 2)\n"
        + "  --terrain <set>      terrain set (grass, summer, desert, snow)\n"
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

    /// <summary>Open the asset viewer (also implied by <see cref="Smoke"/> and <see cref="CaptureDir"/>).</summary>
    public bool Viewer { get; init; }

    /// <summary>Start a skirmish directly.</summary>
    public bool Skirmish { get; init; }

    /// <summary>Open the campaign screen directly (<c>--campaign</c>), or start its current mission (<c>--campaign play</c>).</summary>
    public string? Campaign { get; init; }

    /// <summary>Autoplay screenshot folder, or null.</summary>
    public string? AutoplayDir { get; init; }

    /// <summary>Headless game smoke test.</summary>
    public bool GameSmoke { get; init; }

    /// <summary>Headless multiplayer smoke test: a LAN host, a match against a computer, then its replay (ADR 0168).</summary>
    public bool MultiplayerSmoke { get; init; }

    /// <summary>Ticks of the game smoke test.</summary>
    public int Ticks { get; init; } = 300;

    /// <summary>Match seed, or null for the default.</summary>
    public ulong? Seed { get; init; }

    /// <summary>Test-map side in tiles, or 0 for a random map (the default).</summary>
    public int MapSize { get; init; }

    /// <summary>Random map type name, or null for the default.</summary>
    public string? MapType { get; init; }

    /// <summary>Random map size name, or null for the default.</summary>
    public string? MapSizeName { get; init; }

    /// <summary>Kingdoms in the skirmish.</summary>
    public int Players { get; init; } = 2;

    /// <summary>Terrain set id, or null for the default.</summary>
    public string? Terrain { get; init; }

    /// <summary>Whether the asset viewer is the mode.</summary>
    public bool IsViewerMode => Viewer || Smoke || CaptureDir is not null;

    /// <summary>Open the scenario editor.</summary>
    public bool Editor { get; init; }

    /// <summary>A <c>.t2map</c> to open in the editor, or null.</summary>
    public string? EditorFile { get; init; }

    /// <summary>Run the editor smoke test and quit.</summary>
    public bool EditorSmoke { get; init; }

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
                    result = result with { Viewer = true };
                    break;
                case "--skirmish":
                    result = result with { Skirmish = true };
                    break;
                case "--editor":
                    result = result with { Editor = true, EditorFile = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null };
                    break;
                case "--editor-smoke":
                    result = result with { Editor = true, EditorSmoke = true };
                    break;
                case "--campaign":
                    result = result with { Campaign = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "screen" };
                    break;
                case "--autoplay":
                    result = result with { AutoplayDir = Value(), Skirmish = true };
                    break;
                case "--game-smoke":
                    result = result with { GameSmoke = true, Skirmish = true };
                    break;
                case "--mp-smoke":
                    result = result with { MultiplayerSmoke = true };
                    break;
                case "--ticks":
                    result = result with { Ticks = Number(Value(), 1, 1_000_000, 300) };
                    break;
                case "--seed":
                    result = result with { Seed = ulong.TryParse(Value(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out ulong seed) ? seed : null };
                    break;
                case "--map-size":
                    string? size = Value();
                    result = size is not null && size.Length > 0 && char.IsAsciiDigit(size[0])
                        ? result with { MapSize = Number(size, 48, 512, 96) }
                        : result with { MapSizeName = size };
                    break;
                case "--map-type":
                    result = result with { MapType = Value() };
                    break;
                case "--players":
                    result = result with { Players = Number(Value(), 2, 8, 2) };
                    break;
                case "--terrain":
                    result = result with { Terrain = Value() };
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

        int Number(string? text, int min, int max, int fallback)
        {
            if (text is null)
            {
                return fallback;
            }

            if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) || n < min || n > max)
            {
                errors.Add($"'{text}' is not a number from {min} to {max}");
                return fallback;
            }

            return n;
        }
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
