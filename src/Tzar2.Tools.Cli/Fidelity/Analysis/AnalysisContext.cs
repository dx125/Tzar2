using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Formats;
using Tzar2.Formats.Hpfs;
using Tzar2.Formats.Pal;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>Result of one analysis: status (<c>ok</c>, <c>inconclusive</c>, <c>skipped</c>), facts and notes.</summary>
internal sealed record AnalysisOutcome(string Status, YamlWriter Facts, IReadOnlyList<string> Notes)
{
    public static AnalysisOutcome Skipped(string reason) => new("skipped", new YamlWriter(), [reason]);
}

/// <summary>A map of the install: its <c>tiles</c> words and the terrain set named in its header.</summary>
internal sealed record MapInfo(string Name, int Side, ushort[] Words, string? TerrainSet);

/// <summary>
/// Shared inputs of the analyses: the capture session and, optionally, the original install (terrain images,
/// <c>GAME.PAL</c>, maps), which the analysis reads locally and never copies into its output (REQ-LEGAL-01).
/// </summary>
internal sealed class AnalysisContext(CaptureSession session, string? install, GameLayout layout)
{
    private readonly Dictionary<string, TerrainLibrary?> _libraries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TileMatcher> _matchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MapInfo?> _maps = new(StringComparer.OrdinalIgnoreCase);
    private GamePalette? _palette;
    private bool _paletteLoaded;

    public CaptureSession Session { get; } = session;

    public string? Install { get; } = install;

    public GameLayout Layout { get; } = layout;

    /// <summary>Overrides for tests: libraries and maps that do not come from an install.</summary>
    public Dictionary<string, TerrainLibrary> InjectedLibraries { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, MapInfo> InjectedMaps { get; } = new(StringComparer.OrdinalIgnoreCase);

    public GamePalette? InjectedPalette { get; set; }

    public TerrainLibrary? Library(string set)
    {
        if (InjectedLibraries.TryGetValue(set, out TerrainLibrary? injected))
        {
            return injected;
        }

        if (!_libraries.TryGetValue(set, out TerrainLibrary? library))
        {
            try
            {
                library = Install is null ? null : TerrainLibrary.Load(Install, set);
            }
            catch (Exception ex) when (ex is IOException or LegacyFormatException)
            {
                library = null;
            }

            _libraries[set] = library;
        }

        return library;
    }

    public TileMatcher? Matcher(string set)
    {
        if (_matchers.TryGetValue(set, out TileMatcher? matcher))
        {
            return matcher;
        }

        TerrainLibrary? library = Library(set);
        return library is null ? null : _matchers[set] = new TileMatcher(library);
    }

    public IEnumerable<string> AvailableSets => TerrainLibrary.SetNames.Where(s => Library(s) is not null);

    public GamePalette? Palette
    {
        get
        {
            if (InjectedPalette is not null)
            {
                return InjectedPalette;
            }

            if (!_paletteLoaded)
            {
                _paletteLoaded = true;
                string? path = Install is null ? null : TerrainLibrary.FindPath(Install, "IMAGES", "GAME.PAL");
                try
                {
                    _palette = path is null ? null : GamePalette.Parse(File.ReadAllBytes(path));
                }
                catch (Exception ex) when (ex is IOException or LegacyFormatException)
                {
                    _palette = null;
                }
            }

            return _palette;
        }
    }

    /// <summary>The install's <c>Maps/&lt;name&gt;.wmp</c>, or null.</summary>
    public MapInfo? Map(string name)
    {
        if (InjectedMaps.TryGetValue(name, out MapInfo? injected))
        {
            return injected;
        }

        if (_maps.TryGetValue(name, out MapInfo? cached))
        {
            return cached;
        }

        MapInfo? info = null;
        string? path = Install is null ? null : TerrainLibrary.FindPath(Install, "Maps", name + ".wmp");
        if (path is not null)
        {
            try
            {
                info = ReadMap(name, File.ReadAllBytes(path));
            }
            catch (Exception ex) when (ex is IOException or LegacyFormatException)
            {
                info = null;
            }
        }

        _maps[name] = info;
        return info;
    }

    /// <summary>Reads the <c>tiles</c> member (side² uint16) and the terrain set from the header (docs/formats/hpfs.md §5).</summary>
    public static MapInfo? ReadMap(string name, byte[] data)
    {
        IReadOnlyList<HpfsEntry> entries = HpfsArchive.Open(data).ReadAll();
        HpfsEntry? tiles = entries.FirstOrDefault(e => !e.IsDirectory && e.Path.EndsWith(".tiles", StringComparison.OrdinalIgnoreCase));
        HpfsEntry? header = entries.FirstOrDefault(e => !e.IsDirectory && e.Path.EndsWith(".header", StringComparison.OrdinalIgnoreCase));
        if (tiles is null)
        {
            return null;
        }

        int side = (int)Math.Round(Math.Sqrt(tiles.Data.Length / 2.0));
        if (side * side * 2 != tiles.Data.Length)
        {
            return null;
        }

        ushort[] words = new ushort[side * side];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = (ushort)(tiles.Data[2 * i] | (tiles.Data[(2 * i) + 1] << 8));
        }

        return new MapInfo(name, side, words, header is null ? null : TerrainLibrary.SetFromMapHeader(header.Data));
    }

    /// <summary>The image of a game snapshot (preferred) or window capture; null when undecodable.</summary>
    public CaptureImage? TryImage(TrialData trial, CaptureData capture)
    {
        try
        {
            return Session.ReadImage(trial, capture.File);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            return null;
        }
    }
}
