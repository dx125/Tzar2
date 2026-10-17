using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tzar2.Formats;
using Tzar2.Formats.Bmp;
using Tzar2.Formats.Ini;
using Tzar2.Formats.Text;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// One 32×32 cell of a terrain image (docs/formats/bmp.md §3). <see cref="File"/> is the image file, <see cref="Col"/>
/// and <see cref="Row"/> the cell in it. Base tiles (<c>NN.BMP</c>) are opaque; transition tiles (<c>AB.BMP</c>) have
/// key pixels (green <c>0x03E0</c> / magenta <c>0x7C1F</c>) where the base textures show through.
/// </summary>
internal sealed record TerrainTile(int Index, string File, IReadOnlyList<string> Codes, int Col, int Row, bool IsTransition, bool InDeclaredSize, ushort[] Pixels)
{
    public const int Size = 32;
    public const ushort Green = 0x03E0;
    public const ushort Magenta = 0x7C1F;

    /// <summary>The terrain index of a base tile (first digit of its code, e.g. 2 for <c>22</c>), or -1.</summary>
    public int BaseTerrain => IsTransition ? -1 : Codes[0][0] - '0';

    public string Label => $"{Path.GetFileNameWithoutExtension(File)}/c{Col}/r{Row}";

    /// <summary>The image file's number (<c>22</c> for <c>22.BMP</c>), or -1.</summary>
    public int ImageNumber => int.TryParse(Path.GetFileNameWithoutExtension(File), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1;
}

/// <summary>All 32×32 tiles of one terrain set, built from the install or (in tests) from synthetic tiles.</summary>
internal sealed class TerrainLibrary
{
    public static readonly string[] SetNames = ["grass", "summer", "desert", "snow"];

    public TerrainLibrary(string set, IReadOnlyList<TerrainTile> tiles)
    {
        Set = set;
        Tiles = tiles;
        var colours = new Dictionary<int, HashSet<ushort>>();
        foreach (TerrainTile t in tiles.Where(t => !t.IsTransition))
        {
            if (!colours.TryGetValue(t.BaseTerrain, out HashSet<ushort>? terrainColours))
            {
                colours[t.BaseTerrain] = terrainColours = [];
            }

            terrainColours.UnionWith(t.Pixels);
        }

        ColoursByTerrain = colours;
    }

    public string Set { get; }

    public IReadOnlyList<TerrainTile> Tiles { get; }

    /// <summary>Every colour used by the base tiles of each terrain index.</summary>
    public IReadOnlyDictionary<int, HashSet<ushort>> ColoursByTerrain { get; }

    /// <summary>
    /// Loads <c>IMAGES/TERRAIN/&lt;SET&gt;.TERRAIN.INI</c> and every image it lists. Images are cut into 32-px cells over
    /// their whole size; cells outside the declared <c>[terrainsize]</c> are kept and flagged, which answers whether
    /// the game uses them (docs/formats/bmp.md §3.1 open question).
    /// </summary>
    public static TerrainLibrary Load(string install, string set)
    {
        string terrainDir = FindPath(install, "IMAGES", "TERRAIN") ?? throw new DirectoryNotFoundException("IMAGES/TERRAIN not found in the install");
        string iniPath = FindPath(terrainDir, set + ".TERRAIN.INI") ?? throw new FileNotFoundException($"{set}.terrain.ini not found");
        IniDocument ini = IniDocument.Parse(File.ReadAllBytes(iniPath), CodePage.Windows1252);
        string dir = ini.FindSection("terrain")?.GetValue("dir") ?? set;
        string setDir = FindPath(terrainDir, dir) ?? throw new DirectoryNotFoundException($"terrain folder '{dir}' not found");
        var files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (IniEntry entry in ini.FindSection("terrainfiles")?.Entries ?? [])
        {
            if (entry.Key is { Length: 2 } code && code.All(char.IsAsciiDigit))
            {
                string file = entry.Value.Trim();
                if (!files.TryGetValue(file, out List<string>? codes))
                {
                    files[file] = codes = [];
                }

                codes.Add(code);
            }
        }

        var declared = new Dictionary<string, (int Cols, int Rows, int Frames)>(StringComparer.Ordinal);
        foreach (IniEntry entry in ini.FindSection("terrainsize")?.Entries ?? [])
        {
            string[] parts = entry.Value.Split(',', StringSplitOptions.TrimEntries);
            if (entry.Key is { Length: 2 } code && parts.Length >= 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cols) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows))
            {
                int frames = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) ? f : 0;
                declared[code] = (cols, rows, frames);
            }
        }

        var tiles = new List<TerrainTile>();
        foreach ((string file, List<string> codes) in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            string? path = FindPath(setDir, file);
            if (path is null)
            {
                continue;
            }

            BmpImage image;
            try
            {
                image = BmpImage.Parse(File.ReadAllBytes(path));
            }
            catch (LegacyFormatException)
            {
                continue;
            }

            if (image.BitsPerPixel != 16)
            {
                continue;
            }

            bool transition = codes.All(c => c[0] != c[1]);
            (int cols, int rows, int frames) = declared.TryGetValue(codes[0], out var d) ? d : (int.MaxValue, int.MaxValue, 0);
            int declaredCols = transition ? cols * Math.Max(1, frames) : cols;
            // Water (00) stacks its animation frames vertically: all rows of the image are in use.
            int declaredRows = !transition && codes[0] == "00" ? int.MaxValue : rows;
            AddTiles(tiles, image, file, codes, transition, declaredCols, declaredRows);
        }

        return new TerrainLibrary(set, tiles);
    }

    private static void AddTiles(List<TerrainTile> tiles, BmpImage image, string file, List<string> codes, bool transition, int declaredCols, int declaredRows)
    {
        for (int row = 0; row < image.Height / TerrainTile.Size; row++)
        {
            for (int col = 0; col < image.Width / TerrainTile.Size; col++)
            {
                ushort[] pixels = new ushort[TerrainTile.Size * TerrainTile.Size];
                for (int y = 0; y < TerrainTile.Size; y++)
                {
                    for (int x = 0; x < TerrainTile.Size; x++)
                    {
                        pixels[(y * TerrainTile.Size) + x] = (ushort)(image.Rgb555At((col * TerrainTile.Size) + x, (row * TerrainTile.Size) + y) & 0x7FFF);
                    }
                }

                tiles.Add(new TerrainTile(tiles.Count, file, codes, col, row, transition, col < declaredCols && row < declaredRows, pixels));
            }
        }
    }

    /// <summary>The terrain set named in a map header (a NUL-separated string equal to a set name), or null.</summary>
    public static string? SetFromMapHeader(ReadOnlySpan<byte> header)
    {
        foreach (string text in System.Text.Encoding.Latin1.GetString(header).Split('\0'))
        {
            string candidate = text.Trim().ToLowerInvariant();
            if (Array.IndexOf(SetNames, candidate) >= 0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Case-insensitive path lookup below a root (the install comes from Windows; the analysis may run elsewhere).</summary>
    public static string? FindPath(string root, params string[] parts)
    {
        string current = root;
        foreach (string part in parts)
        {
            if (!Directory.Exists(current))
            {
                return null;
            }

            string? next = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}
