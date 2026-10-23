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
        + "  --import <folder>    import the original game from <folder> into the user's data first (first-run import)\n"
        + "  --viewer             open the asset viewer instead of the game\n"
        + "  --skirmish           start a skirmish directly (skips the menus)\n"
        + "  --editor [file]      open the scenario editor (optionally a .t2map)\n"
        + "  --editor-smoke       drive the editor headless (edit, validate, save, reopen) and quit (exit code 0 = OK)\n"
        + "  --editor-import <f>  a .wmp/.wcm the editor smoke test imports through the converter UI\n"
        + "  --editor [file]      open the scenario editor (optionally a .t2map)\n"
        + "  --editor-smoke       drive the editor headless (edit, validate, save, reopen) and quit (exit code 0 = OK)\n"
        + "  --editor-import <f>  a .wmp/.wcm the editor smoke test imports through the converter UI\n"
        + "  --autoplay <dir>     play a scripted skirmish, write screenshots into <dir> and quit\n"
        + "  --game-smoke         run a skirmish headless for --ticks ticks and quit (exit code 0 = OK)\n"
        + "  --bench-render       the S2 render bench (ADR 0244): 512² map, 8 Hard AIs, 4,000 units; prints one RENDER-BENCH line\n"
        + "  --bench-seconds <n>  seconds the render bench measures (default 120, after 15 s of warm-up)\n"
        + "  --resolution <WxH>   window size of the render bench (default 2560x1440, the S2 resolution)\n"
        + "  --fullscreen         the render bench in exclusive fullscreen at the monitor's resolution\n"
        + "  --mp-smoke           host a LAN match against a computer headless, then play its replay, and quit\n"
        + "  --resource-soak <f>  play --matches skirmishes of --ticks ticks each at top speed, back to the menu after each,\n"
        + "                       and write the engine's memory, object and video-memory counters over time to <f> (TSV)\n"
        + "  --matches <n>        matches of --resource-soak (default 3)\n"
        + "  --ui-smoke           open every screen and dialog at every display of the UI review, audit the layout, quit\n"
        + "  --ui-shots <dir>     with --ui-smoke: also save a screenshot of each (needs a renderer, not --headless)\n"
        + "  --ui-only <text>     with --ui-smoke: only the displays and screens whose names contain <text>\n"
        + "  --ticks <n>          ticks for --game-smoke, --mp-smoke and --resource-soak (default 300)\n"
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

    /// <summary>An original install to import before anything else (<c>--import</c>), or null.</summary>
    public string? Import { get; init; }

    /// <summary>Whether a scripted run (smoke, capture, autoplay, bench, soak) is the mode: nothing asks the player anything.</summary>
    public bool IsAutomated => Smoke || CaptureDir is not null || AutoplayDir is not null || GameSmoke || MultiplayerSmoke
        || EditorSmoke || UiSmoke || BenchRender || ResourceSoak is not null;

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

    /// <summary>Resource soak output file (TSV), or null.</summary>
    public string? ResourceSoak { get; init; }

    /// <summary>Matches of the resource soak.</summary>
    public int Matches { get; init; } = 3;

    /// <summary>The S2 render bench (<c>--bench-render</c>, ADR 0244).</summary>
    public bool BenchRender { get; init; }

    /// <summary>Seconds the render bench measures.</summary>
    public int BenchSeconds { get; init; } = 120;

    /// <summary>The render bench's window size (default 2560 × 1440).</summary>
    public (int Width, int Height) Resolution { get; init; } = (2560, 1440);

    /// <summary>The render bench in exclusive fullscreen.</summary>
    public bool Fullscreen { get; init; }

    /// <summary>The UI smoke test (ADR 0200): every screen at every display, audited.</summary>
    public bool UiSmoke { get; init; }

    /// <summary>Screenshot folder of the UI smoke test, or null.</summary>
    public string? UiShots { get; init; }

    /// <summary>Only the UI smoke cases whose display or screen name contains this, or null for all.</summary>
    public string? UiOnly { get; init; }

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

    /// <summary>A legacy file the editor smoke test imports, or null.</summary>
    public string? EditorImport { get; init; }

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
                case "--import":
                    result = result with { Import = Value() };
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
                case "--editor-import":
                    result = result with { EditorImport = Value() };
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
                case "--bench-render":
                    result = result with { BenchRender = true };
                    break;
                case "--bench-seconds":
                    result = result with { BenchSeconds = Number(Value(), 1, 3600, 120) };
                    break;
                case "--fullscreen":
                    result = result with { Fullscreen = true };
                    break;
                case "--resolution":
                    string? resolution = Value();
                    string[] parts = (resolution ?? string.Empty).Split('x', 'X');
                    if (parts.Length == 2
                        && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int w)
                        && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int h)
                        && w is >= 640 and <= 7680 && h is >= 480 and <= 4320)
                    {
                        result = result with { Resolution = (w, h) };
                    }
                    else if (resolution is not null)
                    {
                        errors.Add($"--resolution needs WIDTHxHEIGHT, not '{resolution}'");
                    }

                    break;
                case "--mp-smoke":
                    result = result with { MultiplayerSmoke = true };
                    break;
                case "--resource-soak":
                    result = result with { ResourceSoak = Value(), Skirmish = true };
                    break;
                case "--matches":
                    result = result with { Matches = Number(Value(), 1, 1000, 3) };
                    break;
                case "--ui-smoke":
                    result = result with { UiSmoke = true };
                    break;
                case "--ui-shots":
                    result = result with { UiShots = Value() };
                    break;
                case "--ui-only":
                    result = result with { UiOnly = Value() };
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
