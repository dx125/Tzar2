using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Tzar2.Content.Compiled;
using Tzar2.Content.MapGen;
using Tzar2.Tools.Cli.Imaging;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// <c>t2 mapgen</c>: generates a random map (GAME-18, ADR 0060) and writes a debug dump (<c>.t2mapgen</c>), a summary
/// (<c>.json</c>) and a PNG preview with invented colours (no original art). <c>--sweep</c> generates every type × set
/// of the size and reports the times.
/// </summary>
internal static class MapGenCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var options = new MapGenOptions { Seed = 1 };
        string? outDir = null;
        int scale = 0;
        bool sweep = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "--help")
            {
                PrintUsage(stdout);
                return ExitCodes.Success;
            }

            if (arg == "--sweep")
            {
                sweep = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                stderr.WriteLine($"t2 mapgen: unknown or incomplete option '{arg}'.");
                return ExitCodes.UsageError;
            }

            string value = args[++i];
            bool ok = true;
            switch (arg)
            {
                case "--type": ok = RandomMapNames.TryParse(value, out RandomMapType type); options = options with { Type = type }; break;
                case "--set": ok = RandomMapNames.TryParse(value, out MapTerrainSet set); options = options with { TerrainSet = set }; break;
                case "--size": ok = RandomMapNames.TryParse(value, out RandomMapSize size); options = options with { Size = size }; break;
                case "--seed":
                    ok = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? ulong.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong seed)
                        : ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seed);
                    options = options with { Seed = seed };
                    break;
                case "--players": ok = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int players) && players is >= 1 and <= MapGenOptions.MaxPlayers; options = options with { Players = players }; break;
                case "--teams": ok = TryInts(value, out ImmutableArray<int> teams); options = options with { Teams = teams }; break;
                case "--civs": options = options with { Civilizations = [.. value.Split(',', StringSplitOptions.TrimEntries)] }; break;
                case "--resources":
                    ok = Enum.TryParse(value, ignoreCase: true, out MapResourceLevel level) && Enum.IsDefined(level);
                    options = options with { Resources = level };
                    break;
                case "--features": ok = TryFeatures(value, out MapFeatures features); options = options with { Features = features }; break;
                case "--scale": ok = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out scale) && scale is >= 1 and <= 16; break;
                case "--out": outDir = value; break;
                default:
                    stderr.WriteLine($"t2 mapgen: unknown option '{arg}'.");
                    return ExitCodes.UsageError;
            }

            if (!ok)
            {
                stderr.WriteLine($"t2 mapgen: invalid value '{value}' for {arg}.");
                return ExitCodes.UsageError;
            }
        }

        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
        }

        var jobs = new List<MapGenOptions>();
        if (sweep)
        {
            for (RandomMapType t = RandomMapType.Archipelagos; t <= RandomMapType.Dry; t++)
            {
                for (MapTerrainSet s = MapTerrainSet.Grass; s <= MapTerrainSet.Winter; s++)
                {
                    jobs.Add(options with { Type = t, TerrainSet = s });
                }
            }
        }
        else
        {
            jobs.Add(options);
        }

        // Warm the JIT up once so the reported times are steady-state.
        MapGenerator.Generate(options with { Size = RandomMapSize.Tiny, Players = Math.Min(options.Players, 2), Teams = [], Civilizations = [] });
        long worst = 0;
        foreach (MapGenOptions job in jobs)
        {
            GeneratedMap map;
            var watch = Stopwatch.StartNew();
            try
            {
                map = MapGenerator.Generate(job);
            }
            catch (ArgumentException ex)
            {
                stderr.WriteLine($"t2 mapgen: {ex.Message}");
                return ExitCodes.UsageError;
            }
            catch (MapGenException ex)
            {
                stderr.WriteLine($"t2 mapgen: {ex.Message}");
                return ExitCodes.Mismatch;
            }

            long ms = watch.ElapsedMilliseconds;
            worst = Math.Max(worst, ms);
            MapGenStats st = map.Stats;
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{map.Map.Name}: {map.Map.Width}x{map.Map.Height} {RandomMapNames.Name(map.ResolvedType)} hash={map.Map.ContentHash:X16} {ms} ms attempts={map.Attempts} land={st.LandTiles} water={st.WaterTiles} cliffs={st.CliffTiles} trees={st.Count(MapObjectRole.Tree)} gold={st.Count(MapObjectRole.Gold)} stone={st.Count(MapObjectRole.Stone)} fish={st.Count(MapObjectRole.FishPatch)} objects={map.Map.Objects.Length}"));
            foreach (string note in map.Notes)
            {
                stdout.WriteLine($"  note: {note}");
            }

            if (outDir is not null)
            {
                string stem = Path.Combine(outDir, map.Map.Name);
                WriteDump(stem + ".t2mapgen", map.Map);
                WriteSummary(stem + ".json", map, ms);
                int s = scale > 0 ? scale : Math.Clamp(1024 / map.Map.Width, 1, 8);
                byte[] rgba = Preview.Render(map, s);
                PngWriter.Write(stem + ".png", map.Map.Width * s, map.Map.Height * s, rgba);
            }
        }

        if (jobs.Count > 1)
        {
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{jobs.Count} maps, slowest {worst} ms"));
        }

        return ExitCodes.Success;
    }

    private static bool TryInts(string text, out ImmutableArray<int> values)
    {
        var list = ImmutableArray.CreateBuilder<int>();
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int v))
            {
                values = [];
                return false;
            }

            list.Add(v);
        }

        values = list.ToImmutable();
        return true;
    }

    private static bool TryFeatures(string text, out MapFeatures features)
    {
        features = MapFeatures.None;
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Enum.TryParse(part, ignoreCase: true, out MapFeatures f) || (f & ~MapFeatures.All) != 0)
            {
                return false;
            }

            features |= f;
        }

        return true;
    }

    // Debug dump, little-endian: "T2MG", u16 version 1, u16 width, u16 height, name, terrain set (u16 length + UTF-8),
    // ruleset terrain corners, set corners, cliff tiles, u32 object count, objects (key, u8 owner, i32 x, i32 y),
    // u64 content hash. Not the .t2map package (TDD §18.4).
    private static void WriteDump(string path, MapData map)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(file, Encoding.UTF8);
        w.Write("T2MG"u8);
        w.Write((ushort)1);
        w.Write((ushort)map.Width);
        w.Write((ushort)map.Height);
        WriteString(w, map.Name);
        WriteString(w, map.TerrainSet ?? string.Empty);
        w.Write(map.TerrainCorners);
        w.Write(map.SetTerrainCorners);
        w.Write(map.CliffTiles);
        w.Write((uint)map.Objects.Length);
        foreach (MapObject o in map.Objects)
        {
            WriteString(w, o.TypeKey);
            w.Write(o.Owner);
            w.Write(o.X);
            w.Write(o.Y);
        }

        w.Write(map.ContentHash);
    }

    private static void WriteString(BinaryWriter w, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    private static void WriteSummary(string path, GeneratedMap map, long ms)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        MapGenOptions o = map.Options;
        json.WriteStartObject();
        json.WriteString("name", map.Map.Name);
        json.WriteString("hash", map.Map.ContentHash.ToString("X16", CultureInfo.InvariantCulture));
        json.WriteString("type", RandomMapNames.Name(map.ResolvedType));
        json.WriteString("set", RandomMapNames.Name(o.TerrainSet));
        json.WriteString("size", RandomMapNames.Name(o.Size));
        json.WriteNumber("side", map.Map.Width);
        json.WriteString("seed", o.Seed.ToString("X16", CultureInfo.InvariantCulture));
        json.WriteString("resources", o.Resources.ToString());
        json.WriteString("features", o.Features.ToString());
        json.WriteNumber("attempts", map.Attempts);
        json.WriteNumber("milliseconds", ms);
        json.WriteStartArray("starts");
        foreach (StartPosition s in map.Starts)
        {
            json.WriteStartObject();
            json.WriteNumber("player", s.Player);
            json.WriteNumber("x", s.TileX);
            json.WriteNumber("y", s.TileY);
            json.WriteNumber("team", s.Team);
            json.WriteString("civilization", s.Civilization);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartObject("counts");
        json.WriteNumber("landTiles", map.Stats.LandTiles);
        json.WriteNumber("waterTiles", map.Stats.WaterTiles);
        json.WriteNumber("cliffTiles", map.Stats.CliffTiles);
        for (MapObjectRole r = MapObjectRole.Castle; r <= MapObjectRole.GroundPatch; r++)
        {
            json.WriteNumber(r.ToString(), map.Stats.Count(r));
        }

        json.WriteEndObject();
        json.WriteStartArray("notes");
        foreach (string note in map.Notes)
        {
            json.WriteStringValue(note);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Usage: t2 mapgen [options]");
        output.WriteLine();
        output.WriteLine("Generates a random map (GAME-18) and prints its hash and counts.");
        output.WriteLine();
        output.WriteLine("  --type <t>        random|archipelagos|island|continental|lakes|dry (default continental)");
        output.WriteLine("  --set <s>         grass|summer|desert|winter (default grass)");
        output.WriteLine("  --size <z>        tiny|small|medium|large|huge|enormous|real-world, or 64…512 (default medium)");
        output.WriteLine("  --seed <n>        decimal or 0x hex (default 1)");
        output.WriteLine("  --players <n>     1…8 (tiny: 4, small: 6; default 2)");
        output.WriteLine("  --teams a,b,…     team per player (default: everyone alone)");
        output.WriteLine("  --civs a,b,…      euro|asia|arab|random per player (default random)");
        output.WriteLine("  --resources <r>   low|normal|high (default normal)");
        output.WriteLine("  --features a,b,…  trees,rocks,patches,animals,cliffs,rivers,guardedtowns,gateways,magicitems, all, none, default");
        output.WriteLine("  --out <dir>       write <name>.t2mapgen (debug dump), <name>.json and <name>.png");
        output.WriteLine("  --scale <n>       preview pixels per tile (default 1024 / side)");
        output.WriteLine("  --sweep           every type × terrain set of the size; reports the slowest time");
    }

    /// <summary>The PNG preview: invented colours per terrain-set index and object role.</summary>
    internal static class Preview
    {
        private static readonly byte[][] Players =
        [
            [40, 80, 230], [220, 40, 40], [40, 190, 60], [160, 60, 200], [240, 220, 40], [40, 210, 220], [250, 140, 30], [150, 150, 150],
        ];

        public static byte[] Render(GeneratedMap map, int scale)
        {
            MapData m = map.Map;
            int w = m.Width, h = m.Height, cs = w + 1;
            byte[][] palette = Palette(map.Options.TerrainSet);
            byte[] tile = new byte[w * h * 3];
            ReadOnlySpan<byte> set = m.SetTerrainCorners;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int c = (y * cs) + x;
                    for (int k = 0; k < 3; k++)
                    {
                        tile[(((y * w) + x) * 3) + k] = (byte)((palette[set[c]][k] + palette[set[c + 1]][k] + palette[set[c + cs]][k] + palette[set[c + cs + 1]][k]) / 4);
                    }

                    if (m.CliffTiles[(y * w) + x] != 0)
                    {
                        Paint(tile, w, x, y, [70, 45, 30]);
                    }
                }
            }

            var roles = new Dictionary<string, MapObjectRole>(StringComparer.Ordinal);
            for (MapObjectRole r = MapObjectRole.Castle; r <= MapObjectRole.GroundPatch; r++)
            {
                if (map.Options.Catalog.Kind(r) is { } kind)
                {
                    foreach (string civ in map.Options.Catalog.Civilizations)
                    {
                        roles.TryAdd(kind.KeyFor(civ), r);
                    }
                }
            }

            foreach (MapObject o in m.Objects)
            {
                MapObjectRole role = roles[o.TypeKey];
                MapObjectKind kind = map.Options.Catalog.Kind(role)!.Value;
                int tx = o.X / MapData.UnitsPerTile, ty = o.Y / MapData.UnitsPerTile;
                byte[] colour = role switch
                {
                    MapObjectRole.Castle or MapObjectRole.Peasant => Players[o.Owner % Players.Length],
                    MapObjectRole.Tree => [15, 75, 20],
                    MapObjectRole.Gold => [250, 210, 0],
                    MapObjectRole.Stone => [175, 175, 185],
                    MapObjectRole.FishPatch => [0, 230, 230],
                    MapObjectRole.Cow => [255, 255, 255],
                    MapObjectRole.Bear => [80, 40, 15],
                    MapObjectRole.Gateway => [230, 0, 230],
                    MapObjectRole.Tower => [200, 30, 30],
                    MapObjectRole.TownHouse => [210, 130, 40],
                    MapObjectRole.Item => [255, 90, 210],
                    MapObjectRole.GroundObject => [95, 95, 95],
                    _ => [0, 0, 0],
                };
                if (role == MapObjectRole.GroundPatch)
                {
                    continue;
                }

                for (int y = ty; y < ty + Math.Max(1, kind.Height); y++)
                {
                    for (int x = tx; x < tx + Math.Max(1, kind.Width); x++)
                    {
                        Paint(tile, w, x, y, colour);
                    }
                }
            }

            byte[] rgba = new byte[w * scale * h * scale * 4];
            for (int py = 0; py < h * scale; py++)
            {
                for (int px = 0; px < w * scale; px++)
                {
                    int t = (((py / scale) * w) + (px / scale)) * 3;
                    int o = ((py * w * scale) + px) * 4;
                    rgba[o] = tile[t];
                    rgba[o + 1] = tile[t + 1];
                    rgba[o + 2] = tile[t + 2];
                    rgba[o + 3] = 255;
                }
            }

            return rgba;
        }

        private static void Paint(byte[] tile, int w, int x, int y, byte[] colour)
        {
            int o = ((y * w) + x) * 3;
            tile[o] = colour[0];
            tile[o + 1] = colour[1];
            tile[o + 2] = colour[2];
        }

        private static byte[][] Palette(MapTerrainSet set) => set switch
        {
            MapTerrainSet.Desert => [[35, 75, 150], [220, 200, 140], [200, 180, 120], [170, 130, 80], [150, 110, 70], [110, 155, 75]],
            MapTerrainSet.Winter => [[35, 70, 140], [235, 240, 245], [115, 85, 55], [205, 215, 230], [95, 90, 75]],
            MapTerrainSet.Summer => [[35, 75, 160], [160, 125, 70], [110, 150, 55], [95, 140, 50], [135, 130, 115], [80, 130, 45], [115, 110, 100], [125, 105, 65]],
            _ => [[35, 75, 160], [150, 115, 65], [70, 140, 50], [90, 160, 60], [130, 130, 120], [60, 120, 40], [110, 110, 100], [120, 100, 60]],
        };
    }
}
