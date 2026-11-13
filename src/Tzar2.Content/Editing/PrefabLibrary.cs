using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Editing;

/// <summary>A group of objects to paste (the clipboard's format, ADR 0292).</summary>
/// <param name="Name">The name shown.</param>
/// <param name="Description">What it holds.</param>
/// <param name="Text">The clipboard text (<see cref="ObjectClipboard"/>).</param>
/// <param name="Count">Its objects.</param>
/// <param name="Path">The file of a player's prefab, or null for a ready-made one.</param>
public sealed record Prefab(string Name, string Description, string Text, int Count, string? Path = null);

/// <summary>
/// The editor's prefab library (ADR 0292): ready-made groups of objects for a civilization and a kingdom — a village, a
/// walled yard with a gate, a watch post, a war band, a mining camp — and the player's own prefabs (the clipboard's
/// format, saved in the user folder <see cref="Folder"/>). The ready-made ones are laid out by the editor's own placement
/// rules on open land from the ruleset's traits, never from type names, so any ruleset gets them (a ruleset without, say,
/// walls gets no walled yard).
/// </summary>
public static class PrefabLibrary
{
    /// <summary>The user folder of the player's prefabs.</summary>
    public const string Folder = "prefabs";

    /// <summary>The extension of a prefab file.</summary>
    public const string Extension = ".t2prefab.json";

    private const int Side = 40;

    /// <summary>The ready-made prefabs of <paramref name="civilization"/> for kingdom slot <paramref name="owner"/> (0-7).</summary>
    public static ImmutableArray<Prefab> BuiltIn(Ruleset ruleset, string civilization, byte owner)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(civilization);
        var kit = new Kit(ruleset, civilization);
        var b = ImmutableArray.CreateBuilder<Prefab>();
        void Add(string name, string description, Action<Layout> build)
        {
            var layout = new Layout(ruleset, owner);
            build(layout);
            if (layout.Ids.Count > 0)
            {
                b.Add(new Prefab(name, description, ObjectClipboard.Copy(layout.State, layout.Ids, name), layout.Ids.Count));
            }
        }

        if (kit.House is { } house)
        {
            Add("Village", "Four houses round a square, with workers in it", l =>
            {
                int step = Math.Max(3, house.FootprintWidth + 2);
                for (int i = 0; i < 4; i++)
                {
                    l.Place(house.Key, 4 + (i % 2 * step * 2), 4 + (i / 2 * step * 2));
                }

                if (kit.Worker is { } worker)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        l.Place(worker.Key, 4 + step + i - 1, 4 + step);
                    }
                }
            });
        }

        if (kit.Wall is { } wall)
        {
            Add("Walled yard", "A square of walls with a gate on its south side", l =>
            {
                const int size = 10;
                int x0 = 4, y0 = 4, x1 = x0 + size - 1, y1 = y0 + size - 1;
                l.Wall(wall.Key, x0, y0, x1, y0);
                l.Wall(wall.Key, x0, y0, x0, y1);
                l.Wall(wall.Key, x1, y0, x1, y1);
                if (kit.Gate is { } gate)
                {
                    int gx = x0 + ((size - gate.FootprintWidth) / 2);
                    l.Wall(wall.Key, x0, y1, gx - 1, y1);
                    l.Wall(wall.Key, gx + gate.FootprintWidth, y1, x1, y1);
                    l.Place(gate.Key, gx, y1 - ((gate.FootprintHeight - 1) / 2));
                }
                else
                {
                    l.Wall(wall.Key, x0, y1, x1, y1);
                }
            });
        }

        if (kit.Tower is { } tower)
        {
            Add("Watch post", "Two towers with archers before them", l =>
            {
                int gap = tower.FootprintWidth + 3;
                l.Place(tower.Key, 4, 4);
                l.Place(tower.Key, 4 + gap, 4);
                if (kit.Archer is { } archer)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        l.Place(archer.Key, 4 + (i * gap / 3), 4 + tower.FootprintHeight + 1);
                    }
                }
            });
        }

        if (!kit.Soldiers.IsEmpty)
        {
            Add("War band", "Three of each of the first soldiers, in ranks", l =>
            {
                for (int row = 0; row < kit.Soldiers.Length; row++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        l.Place(kit.Soldiers[row].Key, 4 + i, 4 + row, facing: 4);
                    }
                }
            });
        }

        if (kit.Mine is { } mine && kit.Worker is { } miner)
        {
            Add("Mining camp", "A gold mine with workers round it", l =>
            {
                l.Place(mine.Key, 6, 6, owner: MapObject.Nature);
                int w = mine.FootprintWidth, h = mine.FootprintHeight;
                foreach ((int x, int y) in new[] { (5, 6), (6 + w, 6), (6, 5), (6, 6 + h) })
                {
                    l.Place(miner.Key, x, y);
                }
            });
        }

        return b.ToImmutable();
    }

    /// <summary>A player's prefab from its file text.</summary>
    /// <exception cref="FormatException">Not a prefab.</exception>
    public static Prefab FromFile(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using JsonDocument json = JsonDocument.Parse(text);
            JsonElement root = json.RootElement;
            if (root.GetProperty("kind").GetString() != "tzar2.objects")
            {
                throw new FormatException("Not a Tzar2 prefab.");
            }

            string name = root.TryGetProperty("name", out JsonElement n) && n.GetString() is { Length: > 0 } given ? given : NameOf(path);
            int count = root.GetProperty("objects").GetArrayLength();
            return new Prefab(name, "Saved by you", text, count, path);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("Not a Tzar2 prefab: " + e.Message, e);
        }
    }

    /// <summary>A prefab's name from its file name.</summary>
    public static string NameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string file = System.IO.Path.GetFileName(path);
        return file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? file[..^Extension.Length] : System.IO.Path.GetFileNameWithoutExtension(file);
    }

    /// <summary>The prefab text with every object a kingdom owns given to kingdom slot <paramref name="owner"/> (Nature's stay).</summary>
    /// <exception cref="FormatException">Not a prefab.</exception>
    public static string WithOwner(string text, byte owner)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            JsonNode root = JsonNode.Parse(text) ?? throw new FormatException("Empty prefab.");
            foreach (JsonNode? o in root["objects"]?.AsArray() ?? [])
            {
                if (o?["owner"] is { } current && current.GetValue<int>() != MapObject.Nature)
                {
                    o["owner"] = owner;
                }
            }

            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new FormatException("Not a Tzar2 prefab: " + e.Message, e);
        }
    }

    // The ruleset's types a civilization's prefabs are made of, by trait.
    private sealed class Kit
    {
        public Kit(Ruleset rules, string civ)
        {
            var produced = new HashSet<int>();
            foreach (EntityTypeDefinition t in rules.EntityTypes)
            {
                foreach (ushort id in t.Traits.Producer?.Produces ?? [])
                {
                    produced.Add(id);
                }
            }

            string CivOf(EntityTypeDefinition t) => t.Traits.Civilization.Length > 0 ? t.Traits.Civilization : EditorPalette.CivOf(t.Key);
            bool Own(EntityTypeDefinition t) => string.Equals(CivOf(t), civ, StringComparison.Ordinal);

            // Walls, gates and towers may be every civilization's (the original's are common): the civilization's own first.
            bool Shared(EntityTypeDefinition t) => Own(t) || string.Equals(CivOf(t), "comm", StringComparison.Ordinal);
            IEnumerable<EntityTypeDefinition> Strongest(Func<EntityTypeDefinition, bool> what) => rules.EntityTypes.Where(t => Shared(t) && what(t))
                .OrderBy(t => Own(t) ? 0 : 1).ThenByDescending(t => t.MaxHealth).ThenBy(t => t.Key, StringComparer.Ordinal);
            int Cost(EntityTypeDefinition t) => t.Traits.Cost.IsDefaultOrEmpty ? 0 : t.Traits.Cost.Sum();
            IEnumerable<EntityTypeDefinition> Ours(Func<EntityTypeDefinition, bool> what) => rules.EntityTypes.Where(t => Own(t) && what(t)).OrderBy(Cost).ThenBy(t => t.Key, StringComparer.Ordinal);
            bool Building(EntityTypeDefinition t) => t.Traits.Category == EntityCategory.Building && t.HasFootprint && t.Traits.Placement == PlacementKind.Land;
            bool Soldier(EntityTypeDefinition t) => t.Traits.Category == EntityCategory.Unit && t.Traits.Armament is not null && t.Traits.Harvester is null && produced.Contains(t.Id) && t.Locomotor != Locomotor.Ship;

            House = Ours(t => Building(t) && t.Traits.PopulationProvided > 0 && !t.Traits.IsSeat && !t.Traits.IsWall && t.Traits.Gate is null).FirstOrDefault();
            Worker = Ours(t => t.Traits.IsStartingWorker).FirstOrDefault();
            Wall = Strongest(t => t.Traits.IsWall && t.Traits.Gate is null).FirstOrDefault();
            Gate = Strongest(t => t.Traits.Gate is not null && t.HasFootprint).FirstOrDefault();
            Tower = rules.EntityTypes.Where(t => Shared(t) && Building(t) && t.Traits.Armament is not null && !t.Traits.IsWall && t.Traits.Gate is null && !t.Traits.IsSeat)
                .OrderBy(t => Own(t) ? 0 : 1).ThenBy(Cost).ThenBy(t => t.Key, StringComparer.Ordinal).FirstOrDefault();
            Archer = Ours(t => Soldier(t) && t.Traits.Armament!.Projectile >= 0).FirstOrDefault();
            Soldiers = [.. Ours(Soldier).Take(4)];
            int gold = rules.ResourceId("gold");
            Mine = rules.EntityTypes.Where(t => t.Traits.ResourceNode is { } n && n.Resource == gold && t.HasFootprint).OrderBy(t => t.Key, StringComparer.Ordinal).FirstOrDefault();
        }

        public EntityTypeDefinition? House { get; }

        public EntityTypeDefinition? Worker { get; }

        public EntityTypeDefinition? Wall { get; }

        public EntityTypeDefinition? Gate { get; }

        public EntityTypeDefinition? Tower { get; }

        public EntityTypeDefinition? Archer { get; }

        public ImmutableArray<EntityTypeDefinition> Soldiers { get; }

        public EntityTypeDefinition? Mine { get; }
    }

    // Places objects on an empty scratch map by the editor's rules; what does not fit is left out.
    private sealed class Layout
    {
        private readonly Ruleset _rules;
        private readonly byte _owner;
        private readonly MapDocument _doc;
        private readonly Occupancy _occupancy;

        public Layout(Ruleset rules, byte owner)
        {
            _rules = rules;
            _owner = owner;
            _doc = MapDocument.CreateEmpty("prefab", Side, Side, TerrainRules.WithoutSet(rules));
            _occupancy = new Occupancy(_doc, rules);
        }

        public List<int> Ids { get; } = [];

        public DocumentState State => _doc.State;

        public void Place(string key, int x, int y, byte? owner = null, byte facing = 0)
        {
            using EditTransaction tx = _doc.Begin("prefab");
            int id = PlaceTools.Place(tx, _occupancy, PlaceTools.Placement(_rules, key, owner ?? _owner, x, y, facing: facing), null, out _);
            tx.Commit();
            if (id > 0)
            {
                Ids.Add(id);
            }
        }

        public void Wall(string key, int x0, int y0, int x1, int y1)
        {
            if (x1 < x0 || y1 < y0)
            {
                return;
            }

            using EditTransaction tx = _doc.Begin("prefab");
            Ids.AddRange(WallTool.Drag(tx, _occupancy, key, _owner, x0, y0, x1, y1));
            tx.Commit();
        }
    }
}
