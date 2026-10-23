using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.MapGen;

namespace Tzar2.Content.Editing;

/// <summary>The four kingdom resources in the order of <see cref="KingdomDef"/> (food, wood, gold, stone).</summary>
public enum KingdomResource : byte
{
    /// <summary>Food.</summary>
    Food,

    /// <summary>Wood.</summary>
    Wood,

    /// <summary>Gold.</summary>
    Gold,

    /// <summary>Stone.</summary>
    Stone,
}

/// <summary>Kingdom, diplomacy and starting-location edits (TDD §18.2 "Kingdoms", "Tools: starting locations").</summary>
public static class KingdomEdits
{
    /// <summary>The kingdom numbers the editor shows (1–8).</summary>
    public const int Kingdoms = 8;

    /// <summary>The kingdom <paramref name="number"/>, created with defaults when the scenario has none yet.</summary>
    public static KingdomDef KingdomOrDefault(this ScenarioModel m, int number)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m.Kingdom(number) ?? ScenarioModel.DefaultKingdom(number);
    }

    /// <summary>Replaces kingdom <paramref name="number"/> through <paramref name="change"/>.</summary>
    public static ScenarioModel UpdateKingdom(this ScenarioModel m, int number, Func<KingdomDef, KingdomDef> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return m.WithKingdom(change(m.KingdomOrDefault(number)) with { Number = number });
    }

    /// <summary>Sets the starting location of a kingdom (keys 1–8 in the editor) to a tile, or clears it with −1.</summary>
    public static ScenarioModel SetStart(this ScenarioModel m, int number, int tileX, int tileY)
        => m.UpdateKingdom(number, k => k with { StartTileX = tileX, StartTileY = tileY });

    /// <summary>One resource of a kingdom.</summary>
    public static int Get(this KingdomDef k, KingdomResource r)
    {
        ArgumentNullException.ThrowIfNull(k);
        return r switch { KingdomResource.Food => k.Food, KingdomResource.Wood => k.Wood, KingdomResource.Gold => k.Gold, _ => k.Stone };
    }

    /// <summary>A copy with one resource set.</summary>
    public static KingdomDef With(this KingdomDef k, KingdomResource r, int value)
    {
        ArgumentNullException.ThrowIfNull(k);
        int v = Math.Max(0, value);
        return r switch
        {
            KingdomResource.Food => k with { Food = v },
            KingdomResource.Wood => k with { Wood = v },
            KingdomResource.Gold => k with { Gold = v },
            _ => k with { Stone = v },
        };
    }

    /// <summary>
    /// Fills a kingdom's resources from a starting-resource level of the ruleset (Low / Medium / High: the ruleset's
    /// <see cref="GameplaySettings.StartingResources"/> levels in order).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The ruleset has no such level.</exception>
    public static ScenarioModel Fill(this ScenarioModel m, Ruleset ruleset, int number, int level)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ImmutableArray<StartingResources> levels = ruleset.Gameplay.StartingResources;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)level, (uint)levels.Length, nameof(level));
        ImmutableArray<int> amounts = levels[level].Amounts;
        int Of(string key) => ruleset.ResourceId(key) is >= 0 and var id && id < amounts.Length ? amounts[id] : 0;
        return m.UpdateKingdom(number, k => k with { Food = Of("food"), Wood = Of("wood"), Gold = Of("gold"), Stone = Of("stone") });
    }

    /// <summary>Equalize a column: every kingdom 1–8 gets kingdom <paramref name="from"/>'s amount of <paramref name="resource"/>.</summary>
    public static ScenarioModel EqualizeColumn(this ScenarioModel m, KingdomResource resource, int from)
    {
        int value = m.KingdomOrDefault(from).Get(resource);
        for (int k = 1; k <= Kingdoms; k++)
        {
            m = m.UpdateKingdom(k, d => d.With(resource, value));
        }

        return m;
    }

    /// <summary>Equalize a row: all four resources of kingdom <paramref name="number"/> get its amount of <paramref name="from"/>.</summary>
    public static ScenarioModel EqualizeRow(this ScenarioModel m, int number, KingdomResource from)
    {
        int value = m.KingdomOrDefault(number).Get(from);
        return m.UpdateKingdom(number, d => d.With(KingdomResource.Food, value).With(KingdomResource.Wood, value).With(KingdomResource.Gold, value).With(KingdomResource.Stone, value));
    }

    /// <summary>Equalize all: every resource of every kingdom gets kingdom <paramref name="number"/>'s amount of <paramref name="from"/>.</summary>
    public static ScenarioModel EqualizeAll(this ScenarioModel m, int number, KingdomResource from)
    {
        int value = m.KingdomOrDefault(number).Get(from);
        for (int k = 1; k <= Kingdoms; k++)
        {
            m = m.UpdateKingdom(k, d => d.With(KingdomResource.Food, value).With(KingdomResource.Wood, value).With(KingdomResource.Gold, value).With(KingdomResource.Stone, value));
        }

        return m;
    }

    /// <summary>Sets the diplomacy of a pair; with <paramref name="both"/> also the reverse pair.</summary>
    public static ScenarioModel SetDiplomacy(this ScenarioModel m, DiplomacyDef pair, bool both = true)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(pair);
        if (pair.From == pair.To)
        {
            throw new ArgumentException("A kingdom has no diplomacy with itself.", nameof(pair));
        }

        m = m.WithDiplomacy(pair);
        return both ? m.WithDiplomacy(pair with { From = pair.To, To = pair.From }) : m;
    }

    /// <summary>The effective stance of a pair (Enemy when not set, the original's default).</summary>
    public static DiplomacyDef DiplomacyOrDefault(this ScenarioModel m, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m.DiplomacyOf(from, to) ?? new DiplomacyDef(from, to, Stance.Enemy, false, false, false);
    }

    /// <summary>Building keys of the ruleset (Prohibited Buildings list).</summary>
    public static ImmutableArray<string> BuildingKeys(Ruleset ruleset) => Keys(ruleset, EntityCategory.Building);

    /// <summary>Unit keys of the ruleset (Prohibited Units list).</summary>
    public static ImmutableArray<string> UnitKeys(Ruleset ruleset) => Keys(ruleset, EntityCategory.Unit);

    /// <summary>Technology keys (Prohibited / Initial Researches lists).</summary>
    public static ImmutableArray<string> ResearchKeys(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        var b = ImmutableArray.CreateBuilder<string>();
        foreach (TechnologyDefinition t in ruleset.Technologies)
        {
            b.Add(t.Key);
        }

        return b.ToImmutable();
    }

    /// <summary>Upgrade keys (Prohibited Upgrades list).</summary>
    public static ImmutableArray<string> UpgradeKeys(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        var b = ImmutableArray.CreateBuilder<string>();
        foreach (UpgradeDefinition u in ruleset.Upgrades)
        {
            b.Add(u.Key);
        }

        return b.ToImmutable();
    }

    /// <summary>Adds or removes <paramref name="key"/> in a key list, keeping it sorted and free of repeats.</summary>
    public static EquatableArray<string> Toggle(EquatableArray<string> list, string key, bool on)
    {
        ArgumentNullException.ThrowIfNull(key);
        var set = new SortedSet<string>(list.Items, StringComparer.Ordinal);
        if (on)
        {
            set.Add(key);
        }
        else
        {
            set.Remove(key);
        }

        return EquatableArray<string>.From(set);
    }

    private static ImmutableArray<string> Keys(Ruleset ruleset, EntityCategory category)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        var b = ImmutableArray.CreateBuilder<string>();
        foreach (EntityTypeDefinition t in ruleset.EntityTypes)
        {
            if (t.Traits.Category == category)
            {
                b.Add(t.Key);
            }
        }

        return b.ToImmutable();
    }
}

/// <summary>Kinds of user resources (TDD §18.2 "User resources").</summary>
public enum AssetKind : byte
{
    /// <summary>Images (BMP, PNG).</summary>
    Image,

    /// <summary>Sounds (WAV, OGG).</summary>
    Sound,

    /// <summary>Music (MIDI, OGG).</summary>
    Music,
}

/// <summary>User resources: import, export, delete; custom mini-map and title image (TDD §18.2).</summary>
public static class AssetEdits
{
    /// <summary>Path of the custom mini-map (the original's <c>Minimap.bmp</c>, stored as PNG).</summary>
    public const string MinimapPath = "user/minimap.png";

    /// <summary>Path of the custom title image (the original's <c>Maptitle.bmp</c>, stored as PNG).</summary>
    public const string TitlePath = "user/maptitle.png";

    /// <summary>The kind of a file by its extension, or null when the editor does not take it.</summary>
    public static AssetKind? KindOf(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".bmp" or ".png" => AssetKind.Image,
            ".wav" => AssetKind.Sound,
            ".mid" or ".midi" => AssetKind.Music,
            ".ogg" => AssetKind.Sound,
            _ => null,
        };
    }

    /// <summary>The package path a file is imported to (<c>user/&lt;name&gt;</c>, normalised).</summary>
    public static string PathFor(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return ContentPath.Normalize("user/" + System.IO.Path.GetFileName(fileName));
    }

    /// <summary>Adds or replaces a user resource.</summary>
    public static DocumentState Import(this DocumentState state, string path, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(state);
        string key = ContentPath.Normalize(path);
        return state with { Assets = state.Assets.SetItem(key, data.ToImmutableArray()) };
    }

    /// <summary>Deletes a user resource (references to it become "missing resource" warnings).</summary>
    public static DocumentState DeleteAsset(this DocumentState state, string path)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Assets = state.Assets.Remove(ContentPath.Normalize(path)) };
    }

    /// <summary>The bytes of a user resource, or null.</summary>
    public static byte[]? Export(this DocumentState state, string path)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Assets.TryGetValue(ContentPath.Normalize(path), out ImmutableArray<byte> bytes) ? bytes.AsSpan().ToArray() : null;
    }

    /// <summary>Paths of the user resources of one kind, sorted.</summary>
    public static ImmutableArray<string> Of(this DocumentState state, AssetKind kind)
    {
        ArgumentNullException.ThrowIfNull(state);
        var b = ImmutableArray.CreateBuilder<string>();
        foreach (KeyValuePair<string, ImmutableArray<byte>> a in state.Assets)
        {
            if (KindOf(a.Key) == kind)
            {
                b.Add(a.Key);
            }
        }

        return b.ToImmutable();
    }
}

/// <summary>Decorations (TDD §18.2 "Decorations"): ground patches stack anywhere on land, ground objects on land, water objects on water.</summary>
public static class DecorationEdits
{
    /// <summary>Whether a decoration of <paramref name="class"/> may lie on tile (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static bool Allowed(MapDocument doc, string @class, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(@class);
        if (!doc.ContainsTile(x, y))
        {
            return false;
        }

        return @class switch
        {
            "water_object" => doc.IsWaterTile(x, y),
            _ => doc.IsLandTile(x, y),
        };
    }

    /// <summary>Adds a decoration where it is allowed (patches stack; there is no occupancy check).</summary>
    public static DocumentState AddDecoration(this DocumentState state, MapDocument doc, MapDecoration d, out bool added)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(d);
        added = Allowed(doc, d.Class, d.TileX, d.TileY);
        return added ? state with { Decorations = state.Decorations.Add(d) } : state;
    }

    /// <summary>Removes the decorations on a tile.</summary>
    public static DocumentState RemoveDecorationsAt(this DocumentState state, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Decorations = state.Decorations.RemoveAll(d => d.TileX == x && d.TileY == y) };
    }

    /// <summary>The next shape of a decoration group (TAB cycles shapes): the image after <paramref name="image"/> in <paramref name="shapes"/>.</summary>
    public static string NextShape(IReadOnlyList<string> shapes, string image, bool backwards = false)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        if (shapes.Count == 0)
        {
            return image;
        }

        int i = 0;
        for (int k = 0; k < shapes.Count; k++)
        {
            if (string.Equals(shapes[k], image, StringComparison.Ordinal))
            {
                i = k;
                break;
            }
        }

        return shapes[((i + (backwards ? -1 : 1)) % shapes.Count + shapes.Count) % shapes.Count];
    }
}

/// <summary>Options of a new empty map (TDD §18.2 "New map": start terrain, patches, rocks, animals).</summary>
/// <param name="Name">Map name.</param>
/// <param name="Size">Side in tiles (64–512 classic, up to 1,024 extended).</param>
/// <param name="TerrainSet">Terrain set id, or null for a map without a set.</param>
/// <param name="Fill">Start terrain (palette value), or null for the default land.</param>
/// <param name="PatchesPercent">Ground patches per 100 tiles (0–20).</param>
/// <param name="RocksPerKingdom">Gold and stone rocks per player (0–8).</param>
/// <param name="Animals">Animals scattered on the map (0–64).</param>
/// <param name="Seed">Seed of the scattering.</param>
public sealed record NewMapOptions(string Name, int Size, string? TerrainSet, int? Fill = null, int PatchesPercent = 0, int RocksPerKingdom = 0, int Animals = 0, ulong Seed = 1);

/// <summary>Creating maps (TDD §18.2 "New map").</summary>
public static class NewMap
{
    /// <summary>
    /// An empty map of one terrain, optionally scattered with ground patches, rocks and animals (Nature owns them). The
    /// scatter is deterministic in <see cref="NewMapOptions.Seed"/>.
    /// </summary>
    public static MapDocument Empty(NewMapOptions options, TerrainRules rules, Ruleset? ruleset)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rules);
        MapDocument doc = MapDocument.CreateEmpty(options.Name, options.Size, options.Size, rules, options.Fill);
        doc.Apply("New map", s => s.WithScenario(m => m with { Properties = m.Properties with { Title = options.Name } }));
        if (ruleset is null || (options.PatchesPercent <= 0 && options.RocksPerKingdom <= 0 && options.Animals <= 0))
        {
            doc.History.Clear();
            return doc;
        }

        var rng = Core.Rng.Xoshiro256StarStar.FromSeed(Core.Rng.SplitMix64.Mix(options.Seed));
        var occupancy = new Occupancy(doc, ruleset);
        using (EditTransaction tx = doc.Begin("Scatter"))
        {
            DocumentState s = tx.State;
            int tiles = options.Size * options.Size;
            int patches = Math.Clamp(options.PatchesPercent, 0, 20) * tiles / 100;
            for (int i = 0; i < patches; i++)
            {
                int x = rng.NextInt(options.Size), y = rng.NextInt(options.Size);
                s = s.AddDecoration(doc, new MapDecoration("ground_patch", "patch", x, y, -1, -1), out _);
            }

            void Scatter(string key, int count)
            {
                if (ruleset.EntityTypeId(key) < 0)
                {
                    return;
                }

                for (int i = 0, tries = 0; i < count && tries < count * 20; tries++)
                {
                    MapObject o = PlaceTools.Placement(ruleset, key, MapObject.Nature, rng.NextInt(options.Size), rng.NextInt(options.Size));
                    if (occupancy.Check(o).Ok)
                    {
                        s = s.Add(o, null, out int id);
                        occupancy.Add(id, o);
                        i++;
                    }
                }
            }

            int rocks = Math.Clamp(options.RocksPerKingdom, 0, 8) * 2;
            Scatter("comm.res_gold", rocks);
            Scatter("comm.res_stone", rocks);
            Scatter("comm.cow", Math.Clamp(options.Animals, 0, 64) / 2);
            Scatter("comm.bear", Math.Clamp(options.Animals, 0, 64) - (Math.Clamp(options.Animals, 0, 64) / 2));
            tx.State = s;
            tx.Commit();
        }

        doc.History.Clear();
        return doc;
    }

    /// <summary>
    /// A random map from the random-map generator (ADR 0060) with the kingdoms the generator placed: per-player
    /// civilization, starting location, Human-or-AI for player 1 and AI for the others.
    /// </summary>
    /// <exception cref="MapGenException">The generator found no valid layout.</exception>
    public static MapDocument Random(MapGenOptions options, TerrainRules rules, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rules);
        GeneratedMap generated = MapGenerator.Generate(options);
        MapDocument doc = MapDocument.FromCompiled(generated.Map, null, rules);
        doc.Apply("Random map", s => s.WithScenario(m =>
        {
            m = m with { Properties = m.Properties with { Title = name ?? generated.Map.Name } };
            foreach (StartPosition p in generated.Starts)
            {
                m = m.UpdateKingdom(p.Player + 1, k => k with { Civilization = p.Civilization, StartTileX = p.TileX, StartTileY = p.TileY });
            }

            return m;
        }));
        doc.History.Clear();
        return doc;
    }
}
