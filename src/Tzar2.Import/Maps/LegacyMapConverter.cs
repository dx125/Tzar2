using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using Tzar2.Content;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Formats;
using Tzar2.Formats.Maps;
using Tzar2.Formats.Text;

namespace Tzar2.Import.Maps;

/// <summary>
/// Converts a legacy map (<c>.wmp</c>) into <see cref="MapData"/> + <see cref="ScenarioData"/> (TDD §18.5,
/// REQ-EDT-05…10, ADR 0121). The one converter shared by the CLI, the first-run importer and the editor. Deterministic:
/// the same bytes and options give the same result. Never throws for bad input: problems go to the report.
/// </summary>
public static class LegacyMapConverter
{
    /// <summary>Order names by legacy order code (docs/formats/wmp.md §6.5).</summary>
    public static IReadOnlyList<string> OrderKeys { get; } =
        ["default", "move", "attack", "capture", "load", "unload", "pickitem", "patrol", "moveadvance", "guard", "standground", "attackground", "stop", "dropitems"];

    /// <summary>Decoration classes: drawn on the terrain, not simulated.</summary>
    private static readonly HashSet<string> DecorationClasses = new(StringComparer.Ordinal) { "ground_object", "ground_patch", "animation" };

    /// <summary>Converts a top-level map file.</summary>
    /// <param name="data">The <c>.wmp</c> bytes.</param>
    /// <param name="fileName">Source file name (for the report).</param>
    /// <param name="options">Options, or null for the defaults.</param>
    public static MapConversion Convert(byte[] data, string fileName, LegacyConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var report = new ConversionReport("map", fileName) { SourceSize = data.Length, SourceMd5 = Md5(data) };
        return Convert(data, report, options ?? new LegacyConversionOptions(), null);
    }

    /// <summary>Converts a map image (also used for maps embedded in a campaign).</summary>
    internal static MapConversion Convert(byte[] data, ConversionReport report, LegacyConversionOptions options, CodePage? inherited)
    {
        CodePage codePage;
        try
        {
            if (options.CodePage is CodePage forced)
            {
                codePage = forced;
                report.CodePageSource = "override";
            }
            else
            {
                CodePageGuess guess = CodePageDetector.Detect(LegacyMapReader.TextSamples(data));
                codePage = guess.HighBytes == 0 && inherited is CodePage parent ? parent : guess.CodePage;
                report.CodePageSource = guess.HighBytes == 0 ? (inherited is null ? "ascii" : "inherited") : "detected";
                report.CodePageAmbiguous = guess.Ambiguous;
                report.Language = guess.Language;
                if (guess.Ambiguous)
                {
                    report.Warn("text.codepage", $"code page guessed as {(int)guess.CodePage} from only {guess.HighBytes} non-ASCII bytes; override with --codepage if text looks wrong");
                }
            }

            report.CodePage = (int)codePage;
            LegacyMap map = LegacyMapReader.ReadMap(data, codePage);
            return new MapBuilder(map, report, options, codePage).Build();
        }
        catch (LegacyFormatException e)
        {
            report.Error("format", e.Message);
            return new MapConversion { MapName = string.Empty, Report = report };
        }
    }

    /// <summary>MD5 of the source as lower-case hex (provenance, REQ-EDT-11).</summary>
    internal static string Md5(byte[] data) => System.Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    /// <summary>Normalises a legacy next-map reference (<c>MAPS\name.wmp</c>, <c>name</c>) to the map name.</summary>
    public static string MapNameOf(string reference)
    {
        string s = reference.Trim().Replace('/', '\\');
        int slash = s.LastIndexOf('\\');
        if (slash >= 0)
        {
            s = s[(slash + 1)..];
        }

        return s.EndsWith(".wmp", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
    }

    /// <summary>The package path of a legacy resource reference (<c>USER\X.bmp</c> → <c>user/x.png</c>).</summary>
    public static string AssetPathOf(string legacyPath, bool convertImages = true)
    {
        string p = legacyPath.Trim().Replace('\\', '/');
        if (convertImages && p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            p = p[..^4] + ".png";
        }

        return ContentPath.Normalize(p);
    }

    private sealed class MapBuilder
    {
        private readonly LegacyMap _map;
        private readonly ConversionReport _report;
        private readonly LegacyConversionOptions _options;
        private readonly LegacyTypeResolver _types;
        private readonly Dictionary<int, int> _indexById = [];
        private readonly SortedDictionary<int, int> _legacyIndices = [];
        private readonly List<MapObject> _objects = [];
        private readonly List<ObjectInfo> _infos = [];
        private readonly List<Decoration> _decorations = [];
        private readonly List<Placeholder> _placeholders = [];
        private readonly List<MapString> _strings = [];
        private readonly List<UserResource> _resources = [];
        private readonly Dictionary<string, string> _assets = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missingAssets = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(int, CampaignEdgeKind, string)> _transitions = [];
        private readonly HashSet<string> _unknownNames = new(StringComparer.OrdinalIgnoreCase);
        private Scope _scope = null!;

        public MapBuilder(LegacyMap map, ConversionReport report, LegacyConversionOptions options, CodePage codePage)
        {
            _map = map;
            _report = report;
            _options = options;
            _types = new LegacyTypeResolver(options.Ruleset, options.Mapping);
            _ = codePage;
        }

        public MapConversion Build()
        {
            foreach (FormatWarning w in _map.Warnings)
            {
                _report.Warn("format", w.Message);
            }

            CollectResources();
            string title = _map.Header.Title.Length > 0 ? _map.Header.Title : _map.MapName;
            MapData? mapData = BuildMap(title);
            ScenarioData? scenario = null;
            if (mapData is not null)
            {
                scenario = BuildScenario(title);
            }

            foreach (string missing in _missingAssets)
            {
                _report.Warn("resource.missing", $"user resource '{missing}' is referenced but not in the map");
            }

            Count();
            return new MapConversion
            {
                MapName = _map.MapName,
                Map = mapData,
                Scenario = scenario,
                Report = _report,
                Resources = _resources,
                Decorations = _decorations,
                Placeholders = _placeholders,
                Strings = _strings,
                Transitions = _transitions,
                LegacyObjectIndices = _legacyIndices,
            };

            void Count()
            {
                _report.Count("objects", _objects.Count);
                _report.Count("placeholders", _placeholders.Count);
                _report.Count("decorations", _decorations.Count);
                _report.Count("kingdoms", scenario?.Kingdoms.Length ?? 0);
                _report.Count("events", scenario?.Events.Length ?? 0);
                _report.Count("actions", scenario?.Actions.Length ?? 0);
                _report.Count("rules", scenario?.Rules.Length ?? 0);
                _report.Count("variables", scenario?.Variables.Length ?? 0);
                _report.Count("conversations", scenario?.Conversations.Length ?? 0);
                int phrases = 0, quests = 0, timers = 0, expressions = 0;
                if (scenario is not null)
                {
                    foreach (ConversationDef c in scenario.Conversations)
                    {
                        phrases += c.Phrases.Length;
                    }

                    foreach (ActionDef a in scenario.Actions)
                    {
                        quests += a is AddQuestAction ? 1 : 0;
                        timers += a is SetTimerAction ? 1 : 0;
                    }
                }

                expressions = _scope?.Expressions ?? 0;
                _report.Count("phrases", phrases);
                _report.Count("quests", quests);
                _report.Count("timers", timers);
                _report.Count("expressions", expressions);
                _report.Count("resources", _resources.Count);
                _report.Count("music", scenario?.Music.Length ?? 0);
                _report.Count("sounds", scenario?.Sounds.Length ?? 0);
            }
        }

        // ---- resources ----------------------------------------------------------------------------------------------

        private void CollectResources()
        {
            foreach (LegacyFile f in _map.Files)
            {
                string upper = f.Path.ToUpperInvariant();
                if (upper.StartsWith("MAPS/", StringComparison.Ordinal))
                {
                    continue;
                }

                string legacyKey = f.Path.Replace('/', '\\');
                string path;
                byte[] bytes = f.Data;
                if (_options.ConvertImages && upper.EndsWith(".BMP", StringComparison.Ordinal))
                {
                    try
                    {
                        bytes = PngEncoder.FromBmp(f.Data);
                        path = AssetPathOf(f.Path);
                    }
                    catch (LegacyFormatException e)
                    {
                        _report.Warn("resource.image", $"'{f.Path}' is not a readable BMP ({e.Message}); kept as is", f.Path);
                        path = AssetPathOf(f.Path, convertImages: false);
                    }
                }
                else
                {
                    path = AssetPathOf(f.Path, convertImages: false);
                }

                if (_assets.TryAdd(legacyKey, path))
                {
                    _resources.Add(new UserResource(path, f.Path, bytes));
                }
            }
        }

        private ResourceRef? Asset(string legacyPath)
        {
            if (string.IsNullOrWhiteSpace(legacyPath))
            {
                return null;
            }

            string key = legacyPath.Trim().Replace('/', '\\');
            if (_assets.TryGetValue(key, out string? path))
            {
                return new ResourceRef(path);
            }

            _missingAssets.Add(legacyPath.Trim());
            return new ResourceRef(AssetPathOf(key, _options.ConvertImages));
        }

        // ---- terrain and objects ------------------------------------------------------------------------------------

        private MapData? BuildMap(string title)
        {
            int side = _map.Side;
            int cs = side + 1;
            byte[] setCorners = new byte[cs * cs];
            byte[] cliffs = new byte[side * side];
            int conflicts = CornerGrid(setCorners, cliffs);
            if (conflicts > 0)
            {
                _report.Info("terrain.corners", $"{conflicts} corners disagree between neighbouring tiles; the majority was used (UNK-10)");
            }

            byte[] terrain = new byte[setCorners.Length];
            for (int i = 0; i < terrain.Length; i++)
            {
                terrain[i] = _types.TerrainId(setCorners[i]);
            }

            ConvertObjects(side);
            int stumps = PlaceTrees(side);
            if (stumps > 0)
            {
                _report.Info("terrain.stumps", $"{stumps} tree stumps kept as decorations");
            }

            string set = _map.Header.TerrainSet.Trim().ToLowerInvariant();
            if (set is not ("grass" or "summer" or "desert" or "snow"))
            {
                _report.Warn("terrain.set", $"unknown terrain set '{_map.Header.TerrainSet}'");
            }

            try
            {
                return new MapData(title, side, side, terrain, [], cliffs, _objects, set.Length > 0 ? set : null, set.Length > 0 ? setCorners : default);
            }
            catch (ArgumentException e)
            {
                _report.Error("map", e.Message);
                return null;
            }
        }

        // Derives the (side+1)² corner grid from the per-tile words (docs/formats/wmp.md §5): bits 4–9 hold the terrain
        // pair a·8+b, bits 0–3 the transition shape s (corner mask s+1, bits TL 1, TR 2, BR 4, BL 8; a set bit = a).
        // A cliff tile (bit 10) stores a cliff piece in bits 0–9 instead, so it casts no vote; corners only cliffs touch
        // take the terrain of the nearest voted corner (breadth-first, row-major order).
        private int CornerGrid(byte[] corners, byte[] cliffs)
        {
            int side = _map.Side;
            int cs = side + 1;
            var votes = new byte[corners.Length * 8];
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    ushort w = _map.Tiles[(y * side) + x];
                    if ((w & 0x0400) != 0)
                    {
                        cliffs[(y * side) + x] = 1;
                        continue;
                    }

                    int code = (w >> 4) & 63;
                    int hi = code >> 3, lo = code & 7;
                    int mask = hi == lo ? 15 : (w & 15) + 1;
                    Vote(votes, (y * cs) + x, (mask & 1) != 0 ? hi : lo);
                    Vote(votes, (y * cs) + x + 1, (mask & 2) != 0 ? hi : lo);
                    Vote(votes, ((y + 1) * cs) + x + 1, (mask & 4) != 0 ? hi : lo);
                    Vote(votes, ((y + 1) * cs) + x, (mask & 8) != 0 ? hi : lo);
                }
            }

            int conflicts = 0;
            var known = new bool[corners.Length];
            var queue = new Queue<int>();
            for (int c = 0; c < corners.Length; c++)
            {
                int best = 0, kinds = 0;
                for (int t = 0; t < 8; t++)
                {
                    int v = votes[(c * 8) + t];
                    kinds += v > 0 ? 1 : 0;
                    if (v > votes[(c * 8) + best])
                    {
                        best = t;
                    }
                }

                corners[c] = (byte)best;
                conflicts += kinds > 1 ? 1 : 0;
                if (kinds > 0)
                {
                    known[c] = true;
                    queue.Enqueue(c);
                }
            }

            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                int cx = c % cs, cy = c / cs;
                foreach ((int dx, int dy) in Neighbours)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if ((uint)nx < (uint)cs && (uint)ny < (uint)cs && !known[(ny * cs) + nx])
                    {
                        known[(ny * cs) + nx] = true;
                        corners[(ny * cs) + nx] = corners[c];
                        queue.Enqueue((ny * cs) + nx);
                    }
                }
            }

            return conflicts;
        }

        private static readonly (int Dx, int Dy)[] Neighbours = [(0, -1), (-1, 0), (1, 0), (0, 1)];

        private static void Vote(byte[] votes, int corner, int terrain) => votes[(corner * 8) + terrain]++;

        private void ConvertObjects(int side)
        {
            // Decorations stay decorations unless a trigger or conversation refers to them (signs, ruins that vanish).
            HashSet<int> referenced = ReferencedIds();
            var extras = new Dictionary<int, LegacyObjectExtra>();
            foreach (LegacyObjectExtra e in _map.ObjectExtras)
            {
                if (!extras.TryAdd(e.ObjectId, e))
                {
                    _report.Warn("object.extra", $"object id {e.ObjectId} has two title records; the first is used");
                }
            }

            foreach (LegacyObject o in _map.Objects)
            {
                string at = $"object #{o.Index} ({o.ClassName}, id {o.Id})";
                if ((uint)o.X >= (uint)side || (uint)o.Y >= (uint)side)
                {
                    _report.Warn("object.position", $"position ({o.X}, {o.Y}) is off the {side}×{side} map; dropped", at);
                    continue;
                }

                if (DecorationClasses.Contains(o.ClassName) && !referenced.Contains(o.Id))
                {
                    (int px, int py) = o.PixelPosition ?? (-1, -1);
                    _decorations.Add(new Decoration(o.ClassName, o.ImageName ?? string.Empty, o.X, o.Y, px, py));
                    continue;
                }

                string? key = _types.Resolve(o.ClassName, o.Race);
                if (key is null)
                {
                    if (o.ImageName is { Length: > 0 })
                    {
                        _decorations.Add(new Decoration(o.ClassName, o.ImageName, o.X, o.Y, -1, -1));
                    }
                    else
                    {
                        _placeholders.Add(new Placeholder(o.Index, o.ClassName, o.Race, o.Owner, o.X, o.Y, o.Id));
                        _report.Warn("object.type", $"class '{o.ClassName}' (civ {LegacyTypeResolver.CivOf(o.Race)}) has no entity type; kept as a placeholder", at);
                    }

                    continue;
                }

                byte owner = o.Owner is >= 1 and <= 8 ? (byte)(o.Owner - 1) : MapObject.Nature;
                if (o.Owner > LegacyObject.OwnerNature)
                {
                    _report.Warn("object.owner", $"unknown owner {o.Owner}; Nature used", at);
                }

                int index = _objects.Count;
                // A rock's or fish patch's own amount (REQ-EDT-03 lossless; ADR 0152); items keep theirs in ItemInfo.
                int amount = o.Amount is int a && a > 0 && o.ClassName is "res_gold" or "res_stone" or "fishpatch" ? a : 0;
                _objects.Add(new MapObject(key, owner, (o.X * MapData.UnitsPerTile) + (MapData.UnitsPerTile / 2), (o.Y * MapData.UnitsPerTile) + (MapData.UnitsPerTile / 2)) { Amount = amount, Flags = PlacementFlags(o), Facing = FacingOf(o) });
                _legacyIndices[o.Index] = index;
                _infos.Add(Info(o, extras.TryGetValue(o.Id, out LegacyObjectExtra? extra) ? extra : null));
                if (o.Id != 0xFFFF && !_indexById.TryAdd(o.Id, index))
                {
                    _report.Warn("object.id", $"object id {o.Id} is used twice; triggers refer to the first", at);
                }
            }
        }

        private HashSet<int> ReferencedIds()
        {
            var ids = new HashSet<int>();
            foreach (LegacyTrigger t in _map.Events.AddRange(_map.Actions))
            {
                if (t.Fields.IsDefault)
                {
                    continue;
                }

                foreach (object f in t.Fields)
                {
                    switch (f)
                    {
                        case LegacyObjectSpec spec:
                            foreach (ushort id in spec.ObjectIds)
                            {
                                ids.Add(id);
                            }

                            break;
                        case LegacyArea { IsObject: true } area:
                            ids.Add(area.ObjectId);
                            break;
                        case LegacyHolder { Typed: false } holder:
                            ids.Add(holder.ObjectId);
                            break;
                    }
                }
            }

            foreach (LegacyConversation c in _map.Conversations)
            {
                foreach (LegacyPhrase p in c.Phrases)
                {
                    ids.Add(p.SourceObjectId);
                }
            }

            ids.Remove(0xFFFF);
            return ids;
        }

        // Gates: extension byte 0 bit 1 = vertical (Rotated), bit 0 = the other side (Mirrored); byte 1 = open.
        // Bridges: extension byte 1 = running north-south (Rotated). Both checked against the walls and the water around
        // every gate and bridge of the reference build (docs/formats/wmp.md §4.4); the types are defined horizontal.
        private static MapObjectFlags PlacementFlags(LegacyObject o)
        {
            if (o.Shape is not int shape)
            {
                return MapObjectFlags.None;
            }

            return o.ClassName switch
            {
                "gate_stone" or "gate_wood" => ((shape & 2) != 0 ? MapObjectFlags.Rotated : 0)
                    | ((shape & 1) != 0 ? MapObjectFlags.Mirrored : 0)
                    | ((shape >> 8) != 0 ? MapObjectFlags.Open : 0),
                "bridge" => (shape & 1) != 0 ? MapObjectFlags.Rotated : MapObjectFlags.None,
                _ => MapObjectFlags.None,
            };
        }

        // Legacy facing 0–7 runs counter-clockwise from south (the sprite direction order, docs/formats/rle.md); ours runs
        // clockwise from east (ADR 0005). ⚠ UNVERIFIED that the map field uses the sprite order (UNK-82).
        private static byte FacingOf(LegacyObject o) => o.Direction is >= 0 and <= 7 ? (byte)((2 - o.Direction) & 7) : (byte)0;

        private ObjectInfo Info(LegacyObject o, LegacyObjectExtra? extra)
        {
            ObjectFlags flags = ObjectFlags.None;
            flags |= (o.Flags & LegacyObjectFlags.NotAppear) != 0 ? ObjectFlags.NotAppear : 0;
            flags |= (o.Flags & LegacyObjectFlags.Persistent) != 0 ? ObjectFlags.Persistent : 0;
            flags |= (o.Flags & LegacyObjectFlags.NoAi) != 0 ? ObjectFlags.NoAi : 0;
            flags |= o.Owner == LegacyObject.OwnerRescue ? ObjectFlags.Rescue : 0;
            string name = extra?.Title ?? string.Empty;
            if (name.Length > 0)
            {
                _strings.Add(new MapString($"object/{o.Id}/title", name));
            }

            ItemInfo? item = null;
            if (o.Item is LegacyItem li)
            {
                item = new ItemInfo(
                    li.Name,
                    li.Value,
                    li.Amount,
                    li.MagicType,
                    li.NeedsMana,
                    li.CooldownSeconds,
                    li.Charges,
                    li.Text,
                    li.Image is null ? null : Asset(li.Image),
                    li.Sound is null ? null : Asset(li.Sound),
                    li.Music is null ? null : Asset(li.Music),
                    li.ScrollTimeMs,
                    li.AutoClose,
                    li.LimitedCharges);
                if (li.Text is { Length: > 0 } text)
                {
                    _strings.Add(new MapString($"object/{o.Id}/scroll", text));
                }
            }

            bool custom(LegacyObjectFlags bit) => (o.Flags & bit) == 0;
            return new ObjectInfo(
                name,
                name,
                flags,
                custom(LegacyObjectFlags.DefaultHealth) && o.Health > 0 ? o.Health : null,
                custom(LegacyObjectFlags.DefaultMaxHealth) && o.MaxHealth > 0 ? o.MaxHealth : null,
                custom(LegacyObjectFlags.DefaultAttack) && o.Attack > 0 ? o.Attack : null,
                custom(LegacyObjectFlags.DefaultDefence) && o.Defence > 0 ? o.Defence : null,
                custom(LegacyObjectFlags.DefaultMana) && o.Mana > 0 ? o.Mana : null,
                custom(LegacyObjectFlags.DefaultLevelOrResistance) && o.Level > 0 ? o.Level : null,
                null,
                extra is { Icon.Length: > 0 } ? Asset(extra.Icon) : null,
                extra is { Icon.Length: > 0 } ? Asset(SmallIcon(extra.Icon)) : null,
                item);
        }

        // The original keeps a portrait "X.bmp" and its small variant "X small.bmp" (docs/formats/wmp.md §4.3).
        private string SmallIcon(string icon)
        {
            int dot = icon.LastIndexOf('.');
            string small = dot > 0 ? icon[..dot] + " small" + icon[dot..] : icon + " small";
            return _assets.ContainsKey(small.Replace('/', '\\')) ? small : icon;
        }

        private int PlaceTrees(int side)
        {
            int stumps = 0;
            string? tree = _types.Resolve("tree", 0);
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    int t = _map.Tiles[(y * side) + x] >> 11;
                    if (t == 0)
                    {
                        continue;
                    }

                    if (t <= 3)
                    {
                        stumps++;
                        _decorations.Add(new Decoration("tree", "stump " + t.ToString(CultureInfo.InvariantCulture), x, y, -1, -1));
                        continue;
                    }

                    if (tree is null)
                    {
                        _placeholders.Add(new Placeholder(-1, "tree", 0, 0, x, y, t));
                        continue;
                    }

                    _objects.Add(new MapObject(tree, MapObject.Nature, (x * MapData.UnitsPerTile) + (MapData.UnitsPerTile / 2), (y * MapData.UnitsPerTile) + (MapData.UnitsPerTile / 2)));
                    _infos.Add(ObjectInfo.Plain);
                }
            }

            if (tree is null)
            {
                _report.Warn("object.type", "the ruleset has no tree type; trees kept as placeholders");
            }

            return stumps;
        }

        // ---- scenario -----------------------------------------------------------------------------------------------

        private ScenarioData? BuildScenario(string title)
        {
            LegacyMapHeader h = _map.Header;
            _strings.Add(new MapString("map/title", title));
            if (_map.Description.Length > 0)
            {
                _strings.Add(new MapString("map/description", _map.Description));
            }

            var properties = new MapProperties(title, h.Author, _map.Description, GameType.Standard, VictoryCondition.Standard, h.PopulationLimit, h.Difficulty, false, false);
            ImmutableArray<KingdomDef> kingdoms = Kingdoms(out ImmutableArray<DiplomacyDef> diplomacy);

            var eventNames = new List<string>();
            foreach (LegacyTrigger e in _map.Events)
            {
                eventNames.Add(e.Name);
            }

            var timerNames = new List<string>();
            var actionIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _map.Actions.Length; i++)
            {
                LegacyTrigger a = _map.Actions[i];
                timerNames.Add(a.Type == (int)LegacyTriggerType.SetTimer ? a.Name : string.Empty);
                if (!actionIndex.TryAdd(a.Name, i))
                {
                    _report.Warn("trigger.name", $"two actions are named '{a.Name}'; rules use the first");
                }
            }

            _scope = new Scope(eventNames, timerNames);
            var conversationIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _map.Conversations.Length; i++)
            {
                conversationIndex.TryAdd(_map.Conversations[i].Name, i);
            }

            var events = ImmutableArray.CreateBuilder<EventDef>(_map.Events.Length);
            foreach (LegacyTrigger e in _map.Events)
            {
                events.Add(Event(e));
            }

            var actions = ImmutableArray.CreateBuilder<ActionDef>(_map.Actions.Length);
            for (int i = 0; i < _map.Actions.Length; i++)
            {
                actions.Add(Action(_map.Actions[i], i, actionIndex, conversationIndex));
            }

            var rules = ImmutableArray.CreateBuilder<RuleDef>(_map.Rules.Length);
            foreach (LegacyRule r in _map.Rules)
            {
                string at = $"rule '{r.Name}'";
                var indices = ImmutableArray.CreateBuilder<int>(r.ActionNames.Length);
                foreach (string name in r.ActionNames)
                {
                    if (actionIndex.TryGetValue(name, out int ai))
                    {
                        indices.Add(ai);
                    }
                    else
                    {
                        _report.Warn("trigger.action", $"action '{name}' does not exist; dropped from the rule", at);
                    }
                }

                rules.Add(new RuleDef(r.Name, Expr(r.Condition, at + " condition"), indices.ToImmutable()));
            }

            var conversations = ImmutableArray.CreateBuilder<ConversationDef>(_map.Conversations.Length);
            foreach (LegacyConversation c in _map.Conversations)
            {
                conversations.Add(Conversation(c));
            }

            var music = ImmutableArray.CreateBuilder<ResourceRef>();
            foreach (string song in _map.Music)
            {
                if (Asset(song) is ResourceRef r)
                {
                    music.Add(r);
                }
            }

            foreach (string name in _unknownNames)
            {
                _report.Warn("expression.name", $"'{name}' is neither an event, a timer nor a known function; it evaluates to 0");
            }

            try
            {
                return new ScenarioData(
                    properties,
                    _objects.Count,
                    _infos.ToImmutableArray(),
                    kingdoms,
                    diplomacy,
                    _scope.Variables.ToImmutableArray(),
                    events.MoveToImmutable(),
                    actions.MoveToImmutable(),
                    rules.MoveToImmutable(),
                    conversations.MoveToImmutable(),
                    EquatableArray<CustomSoundDef>.Empty,
                    music.ToImmutable());
            }
            catch (ArgumentException e)
            {
                _report.Error("scenario", e.Message);
                return null;
            }
        }

        private ImmutableArray<KingdomDef> Kingdoms(out ImmutableArray<DiplomacyDef> diplomacy)
        {
            var result = ImmutableArray.CreateBuilder<KingdomDef>(_map.Kingdoms.Length);
            var pairs = ImmutableArray.CreateBuilder<DiplomacyDef>();
            int n = _map.Kingdoms.Length;
            foreach (LegacyKingdom k in _map.Kingdoms)
            {
                int i = k.Number - 1;
                uint self = _map.Diplomacy[(i * n) + i];
                // The original's race numbers (LegacyMap.Race): 1 European, 2 Arabian, 3 Asian, else random.
                string civ = k.Race switch
                {
                    1 => "euro",
                    2 => "arab",
                    3 => "asia",
                    _ => string.Empty,
                };
                PlayerType type = k.PlayerType switch
                {
                    1 => PlayerType.AiOnly,
                    2 => PlayerType.Rescue,
                    _ => PlayerType.HumanOrAi,
                };
                if (k.Name.Length > 0)
                {
                    _strings.Add(new MapString($"kingdom/{k.Number}/name", k.Name));
                }

                result.Add(new KingdomDef(
                    k.Number,
                    k.Name,
                    k.TzarName,
                    civ,
                    type,
                    k.Active,
                    k.Resources[2],
                    k.Resources[3],
                    k.Resources[0],
                    k.Resources[1],
                    k.AiScript,
                    (k.AiFlags & 1) != 0,
                    (k.AiFlags & 2) != 0,
                    (self & 0x08) != 0,
                    k.StartX,
                    k.StartY,
                    _map.ForbiddenBuildings[i],
                    _map.ForbiddenUnits[i],
                    _map.ForbiddenResearches[i],
                    _map.ForbiddenUpgrades[i],
                    _map.InitialResearches[i]));
                for (int j = 0; j < n; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    uint f = _map.Diplomacy[(i * n) + j];
                    pairs.Add(new DiplomacyDef(k.Number, j + 1, StanceOf(f), (f & 1) != 0, (f & 2) != 0, (f & 4) != 0));
                }
            }

            diplomacy = pairs.ToImmutable();
            return result.MoveToImmutable();
        }

        private static Stance StanceOf(uint flags) => (flags & 0x40) != 0 ? Stance.Ally : (flags & 0x20) != 0 ? Stance.Neutral : Stance.Enemy;

        private EventDef Event(LegacyTrigger e)
        {
            string at = $"event '{e.Name}'";
            if (!e.IsKnown || e.Fields.IsDefaultOrEmpty)
            {
                _report.Warn("trigger.unknown", $"event type {e.Type} is not understood; kept raw and always false", at);
                return new UnknownEvent(e.Name, e.Type, e.Payload);
            }

            switch ((LegacyTriggerType)e.Type)
            {
                case LegacyTriggerType.ObjectExist:
                    return new ObjectExistEvent(e.Name, Objects(e.Field<LegacyObjectSpec>(0), at));
                case LegacyTriggerType.ObjectDestroyed:
                    return new ObjectDestroyedEvent(e.Name, Objects(e.Field<LegacyObjectSpec>(0), at));
                case LegacyTriggerType.ObjectsEnterHolder:
                {
                    (int holder, ObjectSpec? holders) = Holder(e.Field<LegacyHolder>(1), at);
                    return new ObjectsEnterHolderEvent(e.Name, Objects(e.Field<LegacyObjectSpec>(0), at), holder, holders);
                }

                case LegacyTriggerType.ObjectLeaveHolder:
                {
                    (int holder, ObjectSpec? holders) = Holder(e.Field<LegacyHolder>(1), at);
                    return new ObjectLeaveHolderEvent(e.Name, Objects(e.Field<LegacyObjectSpec>(0), at), holder, holders);
                }

                case LegacyTriggerType.ObjectInArea:
                    return new ObjectInAreaEvent(e.Name, Objects(e.Field<LegacyObjectSpec>(0), at), Area(e.Field<LegacyArea>(1), at));
                case LegacyTriggerType.PlayerDefeated:
                    return new PlayerDefeatedEvent(e.Name, (ushort)e.Field<int>(0));
                case LegacyTriggerType.PlayerWon:
                    return new PlayerWonEvent(e.Name, (ushort)e.Field<int>(0));
                case LegacyTriggerType.ObjectExpression:
                    return new ObjectExpressionEvent(e.Name, Objects(e.Field<LegacyObjectSpec>(0), at), Expr(e.Field<string>(1), at));
                default:
                    _report.Warn("trigger.unknown", $"record type {e.Type} is an action type in the events list; kept raw", at);
                    return new UnknownEvent(e.Name, e.Type, e.Payload);
            }
        }

        private ActionDef Action(LegacyTrigger a, int index, Dictionary<string, int> actionIndex, Dictionary<string, int> conversations)
        {
            string at = $"action '{a.Name}'";
            if (!a.IsKnown || (a.Fields.IsDefaultOrEmpty && a.Type is not ((int)LegacyTriggerType.KeepRule or (int)LegacyTriggerType.UnlockView)))
            {
                _report.Warn("trigger.unknown", $"action type {a.Type} is not understood; kept raw and does nothing", at);
                return new UnknownAction(a.Name, a.Type, a.Payload);
            }

            string Text(int field, string what)
            {
                string s = a.Field<string>(field);
                if (s.Length > 0)
                {
                    _strings.Add(new MapString($"action/{a.Name}/{what}", s));
                }

                return s;
            }

            switch ((LegacyTriggerType)a.Type)
            {
                case LegacyTriggerType.AddQuest:
                    return new AddQuestAction(a.Name, Text(0, "text"), Asset(a.Field<string>(1)));
                case LegacyTriggerType.RemoveQuest:
                    return new RemoveQuestAction(a.Name, ActionRef(a.Field<string>(0), LegacyTriggerType.AddQuest, actionIndex, at));
                case LegacyTriggerType.ChangeDiplomacy:
                {
                    uint f = unchecked((uint)a.Field<int>(2));
                    return new ChangeDiplomacyAction(a.Name, a.Field<int>(0), a.Field<int>(1), StanceOf(f), (f & 1) != 0, (f & 2) != 0, (f & 4) != 0);
                }

                case LegacyTriggerType.ChangeObjectOwner:
                    return new ChangeObjectOwnerAction(a.Name, Objects(a.Field<LegacyObjectSpec>(0), at), a.Field<int>(1));
                case LegacyTriggerType.SetObjectProperties:
                    return new SetObjectPropertiesAction(a.Name, Objects(a.Field<LegacyObjectSpec>(0), at), Expr(a.Field<string>(1), at));
                case LegacyTriggerType.Expression:
                    return new ExpressionAction(a.Name, Expr(a.Field<string>(0), at));
                case LegacyTriggerType.SetTimer:
                    return new SetTimerAction(a.Name, Expr(a.Field<string>(0), at), a.Field<int>(1) != 0);
                case LegacyTriggerType.StopTimer:
                    return new StopTimerAction(a.Name, ActionRef(a.Field<string>(0), LegacyTriggerType.SetTimer, actionIndex, at));
                case LegacyTriggerType.PlayerLose:
                {
                    string? map = NextMap(a.Field<string>(1));
                    Transition(index, CampaignEdgeKind.Lose, map);
                    return new PlayerLoseAction(a.Name, (ushort)a.Field<int>(0), map);
                }

                case LegacyTriggerType.PlayerWin:
                {
                    string? map = NextMap(a.Field<string>(1));
                    Transition(index, CampaignEdgeKind.Win, map);
                    return new PlayerWinAction(a.Name, (ushort)a.Field<int>(0), map);
                }

                case LegacyTriggerType.KeepRule:
                    return new KeepRuleAction(a.Name);
                case LegacyTriggerType.LoadMap:
                {
                    string map = MapNameOf(a.Field<string>(0));
                    Transition(index, CampaignEdgeKind.LoadMap, map);
                    return new LoadMapAction(a.Name, map);
                }

                case LegacyTriggerType.LockView:
                    return new LockViewAction(a.Name, Number(a.Field<string>(0), at, "time"), Area(a.Field<LegacyArea>(1), at));
                case LegacyTriggerType.SendChat:
                    return new SendChatAction(a.Name, (ushort)a.Field<int>(1), Text(0, "text"));
                case LegacyTriggerType.ShowStoryScreen:
                    return new ShowStoryScreenAction(a.Name, Number(a.Field<string>(4), at, "scroll time"), a.Field<int>(5) != 0, Text(0, "text"), Asset(a.Field<string>(1)), Asset(a.Field<string>(2)), Asset(a.Field<string>(3)));
                case LegacyTriggerType.ShowMessage:
                    return new ShowMessageAction(a.Name, Text(0, "title"), Text(1, "text"), Asset(a.Field<string>(2)));
                case LegacyTriggerType.SpawnObject:
                {
                    int cls = a.Field<int>(0);
                    int race = a.Field<int>(4);
                    string className = LegacyClasses.NameOf(cls);
                    string key = _types.Resolve(className, race) ?? LegacyTypeResolver.CivOf(race) + "." + className;
                    if (_types.Ruleset is not null && _types.Ruleset.EntityTypeId(key) < 0)
                    {
                        _report.Warn("object.type", $"spawned class '{className}' has no entity type; the key is kept", at);
                    }

                    int owner = a.Field<int>(2);
                    if (owner is not (>= 1 and <= 8))
                    {
                        if (owner == LegacyObject.OwnerRescue)
                        {
                            _report.Warn("trigger.owner", "spawns for the Rescue pseudo-kingdom; Nature used", at);
                        }

                        owner = 0;
                    }

                    return new SpawnObjectAction(a.Name, key, owner, Number(a.Field<string>(1), at, "count"), Area(a.Field<LegacyArea>(3), at));
                }

                case LegacyTriggerType.UnlockView:
                    return new UnlockViewAction(a.Name);
                case LegacyTriggerType.ObjectOrder:
                {
                    int code = a.Field<int>(2);
                    string order = code < OrderKeys.Count ? OrderKeys[code] : "#" + code.ToString(CultureInfo.InvariantCulture);
                    if (code >= OrderKeys.Count)
                    {
                        _report.Warn("trigger.order", $"order code {code} is unknown", at);
                    }

                    return new ObjectOrderAction(a.Name, Objects(a.Field<LegacyObjectSpec>(0), at), Area(a.Field<LegacyArea>(1), at), order);
                }

                case LegacyTriggerType.ScrollView:
                    return new ScrollViewAction(a.Name, Area(a.Field<LegacyArea>(0), at), Area(a.Field<LegacyArea>(1), at));
                case LegacyTriggerType.Conversation:
                {
                    string name = a.Field<string>(0);
                    if (!conversations.TryGetValue(name, out int ci))
                    {
                        ci = -1;
                        _report.Warn("trigger.conversation", $"conversation '{name}' does not exist", at);
                    }

                    return new ConversationAction(a.Name, ci);
                }

                case LegacyTriggerType.DestroyObject:
                    return new DestroyObjectAction(a.Name, Objects(a.Field<LegacyObjectSpec>(0), at));
                case LegacyTriggerType.ExploreArea:
                    return new ExploreAreaAction(a.Name, Number(a.Field<string>(1), at, "time"), a.Field<int>(2) != 0, Area(a.Field<LegacyArea>(0), at));
                case LegacyTriggerType.AppearObject:
                    return new AppearObjectAction(a.Name, Objects(a.Field<LegacyObjectSpec>(0), at));
                case LegacyTriggerType.ChangePlayerStatus:
                    return new ChangePlayerStatusAction(a.Name, a.Field<int>(0), a.Field<int>(1) != 0, a.Field<int>(2) != 0);
                case LegacyTriggerType.LockUnits:
                    // The byte is 0 for "lock" and 1 for "unlock" (every Lock/Unlock pair of the reference campaigns).
                    return new LockUnitsAction(a.Name, Objects(a.Field<LegacyObjectSpec>(0), at), a.Field<int>(1) == 0);
                case LegacyTriggerType.EraseObject:
                    return new EraseObjectAction(a.Name, Objects(a.Field<LegacyObjectSpec>(0), at));
                case LegacyTriggerType.Delay:
                    return new DelayAction(a.Name, Number(a.Field<string>(0), at, "time"));
                default:
                    _report.Warn("trigger.unknown", $"record type {a.Type} is an event type in the actions list; kept raw", at);
                    return new UnknownAction(a.Name, a.Type, a.Payload);
            }
        }

        private void Transition(int index, CampaignEdgeKind kind, string? map)
        {
            if (!string.IsNullOrEmpty(map))
            {
                _transitions.Add((index, kind, map));
            }
        }

        private static string? NextMap(string text)
        {
            // Player Win/Lose store "next map|?"; only the first part is known (docs/formats/wmp.md §6.4, UNK-79).
            int bar = text.IndexOf('|', StringComparison.Ordinal);
            string first = (bar >= 0 ? text[..bar] : text).Trim();
            return first.Length == 0 ? null : MapNameOf(first);
        }

        private int ActionRef(string name, LegacyTriggerType type, Dictionary<string, int> actionIndex, string at)
        {
            if (name.Length > 0 && actionIndex.TryGetValue(name, out int i) && _map.Actions[i].Type == (int)type)
            {
                return i;
            }

            _report.Warn("trigger.action", $"'{name}' is not a {type} action", at);
            return -1;
        }

        private int Number(string text, string at, string what)
        {
            string t = text.Trim();
            if (t.Length == 0)
            {
                return 0;
            }

            if (int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
            {
                return v;
            }

            _report.Warn("trigger.number", $"{what} '{text}' is not a number; 0 used", at);
            return 0;
        }

        private ObjectSpec Objects(LegacyObjectSpec s, string at)
        {
            Quantifier q = s.All ? Quantifier.All : Quantifier.Any;
            if (!s.Typed)
            {
                var indices = ImmutableArray.CreateBuilder<int>(s.ObjectIds.Length);
                foreach (ushort id in s.ObjectIds)
                {
                    if (_indexById.TryGetValue(id, out int i))
                    {
                        indices.Add(i);
                    }
                    else
                    {
                        _report.Warn("trigger.object", $"object id {id} does not exist (deleted or not converted); dropped from the set", at);
                    }
                }

                return new NamedObjects(indices.ToImmutable(), q);
            }

            CountComparison cmp = s.AtMost ? CountComparison.AtMost : CountComparison.AtLeast;
            int count = Number(s.Count, at, "count");
            ObjectCategory? category = s.ClassFilter switch
            {
                0xFFFF => ObjectCategory.AnyObject,
                0xFFFE => ObjectCategory.Units,
                0xFFFD => ObjectCategory.MilitaryUnits,
                0xFFFC => ObjectCategory.Buildings,
                0xFFFB => ObjectCategory.MajorObjects,
                _ => null,
            };
            if (category is ObjectCategory c)
            {
                return new CategoryObjects(c, s.Players, cmp, count, q);
            }

            string className = LegacyClasses.NameOf(s.ClassFilter);
            string? key = _types.Resolve(className, s.Race);
            if (key is null)
            {
                key = LegacyTypeResolver.CivOf(s.Race) + "." + className;
                _report.Warn("object.type", $"class '{className}' has no entity type; the condition can never match", at);
            }

            return new TypedObjects(key, s.Players, cmp, count, q);
        }

        private (int Index, ObjectSpec? Spec) Holder(LegacyHolder h, string at)
        {
            if (h.Typed)
            {
                LegacyObjectSpec spec = new(true, false, h.ClassFilter, h.Players, false, 0, [], "1");
                return (-1, Objects(spec, at));
            }

            if (_indexById.TryGetValue(h.ObjectId, out int i))
            {
                return (i, null);
            }

            _report.Warn("trigger.object", $"holder object id {h.ObjectId} does not exist", at);
            return (-1, null);
        }

        private AreaSpec Area(LegacyArea a, string at)
        {
            if (!a.IsObject)
            {
                // The editor lets rectangles reach one tile past the edge (right/bottom = side, ⚠ UNK-80); clamp.
                int side = _map.Side;
                int l = Math.Clamp(a.Left, 0, side - 1), t = Math.Clamp(a.Top, 0, side - 1);
                int r = Math.Clamp(a.Right, 0, side - 1), b = Math.Clamp(a.Bottom, 0, side - 1);
                if (a.Left > a.Right || a.Top > a.Bottom || a.Right < 0 || a.Bottom < 0 || a.Left >= side || a.Top >= side)
                {
                    _report.Warn("trigger.area", $"rectangle ({a.Left}, {a.Top})–({a.Right}, {a.Bottom}) is empty or outside the map", at);
                }
                else if ((l, t, r, b) != (a.Left, a.Top, a.Right, a.Bottom))
                {
                    _report.Info("trigger.area", $"rectangle ({a.Left}, {a.Top})–({a.Right}, {a.Bottom}) clamped to the map", at);
                }

                return new RectArea(l, t, r, b);
            }

            if (_indexById.TryGetValue(a.ObjectId, out int i))
            {
                return new ObjectArea(i, Number(a.Distance, at, "distance"));
            }

            _report.Warn("trigger.object", $"area object id {a.ObjectId} does not exist; an empty rectangle is used", at);
            return new RectArea(0, 0, -1, -1);
        }

        private ConversationDef Conversation(LegacyConversation c)
        {
            var byId = new Dictionary<int, int>();
            for (int i = 0; i < c.Phrases.Length; i++)
            {
                if (!byId.TryAdd(c.Phrases[i].Id, i))
                {
                    _report.Warn("conversation.phrase", $"phrase id {c.Phrases[i].Id} is used twice", $"conversation '{c.Name}'");
                }
            }

            int initial = 0;
            var phrases = ImmutableArray.CreateBuilder<PhraseDef>(c.Phrases.Length);
            foreach (LegacyPhrase p in c.Phrases)
            {
                string at = $"conversation '{c.Name}' phrase '{p.Name}'";
                initial += p.Initial ? 1 : 0;
                int? source = null;
                if (_indexById.TryGetValue(p.SourceObjectId, out int s))
                {
                    source = s;
                }
                else if (p.SourceObjectId != 0)
                {
                    _report.Warn("conversation.source", $"speaker object id {p.SourceObjectId} does not exist", at);
                }

                var follow = ImmutableArray.CreateBuilder<int>(p.FollowUps.Length);
                foreach (ushort f in p.FollowUps)
                {
                    if (byId.TryGetValue(f, out int fi))
                    {
                        follow.Add(fi);
                    }
                    else
                    {
                        _report.Warn("conversation.phrase", $"follow-up phrase id {f} does not exist", at);
                    }
                }

                if (p.Text.Length > 0)
                {
                    _strings.Add(new MapString($"conversation/{c.Name}/{p.Name}", p.Text));
                }

                phrases.Add(new PhraseDef(p.Name, p.Initial, source, Expr(p.Condition, at + " condition"), Expr(p.Result, at + " result"), p.Text, Asset(p.Icon), Asset(p.Sound), follow.ToImmutable()));
            }

            if (initial == 0)
            {
                _report.Warn("conversation.initial", "no initial phrase; the conversation cannot start", $"conversation '{c.Name}'");
            }

            if (HasCycle(phrases))
            {
                _report.Warn("conversation.cycle", "the phrase graph has a cycle (ORIG-13: the original can hang)", $"conversation '{c.Name}'");
            }

            return new ConversationDef(c.Name, phrases.ToImmutable());
        }

        // Iterative depth-first search (phrase graphs of untrusted maps may be long chains).
        private static bool HasCycle(ImmutableArray<PhraseDef>.Builder phrases)
        {
            var state = new byte[phrases.Count];
            var stack = new Stack<(int Node, int Next)>();
            for (int start = 0; start < phrases.Count; start++)
            {
                if (state[start] != 0)
                {
                    continue;
                }

                state[start] = 1;
                stack.Push((start, 0));
                while (stack.Count > 0)
                {
                    (int node, int next) = stack.Pop();
                    EquatableArray<int> follow = phrases[node].FollowUps;
                    if (next < follow.Length)
                    {
                        stack.Push((node, next + 1));
                        int f = follow[next];
                        if (state[f] == 1)
                        {
                            return true;
                        }

                        if (state[f] == 0)
                        {
                            state[f] = 1;
                            stack.Push((f, 0));
                        }
                    }
                    else
                    {
                        state[node] = 2;
                    }
                }
            }

            return false;
        }

        private ExpressionDef Expr(string text, string at)
        {
            ExpressionDef e = ExpressionParser.Parse(text, _scope);
            if (e.Root is not null || e.Error is not null)
            {
                _scope.Expressions++;
            }

            if (e.Error is not null)
            {
                _report.Warn("expression.parse", $"'{text}': {e.Error}; the expression is kept as text and evaluates to 0", at);
            }
            else if (e.Root is not null)
            {
                CollectUnknown(e.Root);
            }

            return e;
        }

        private void CollectUnknown(Expr e)
        {
            switch (e)
            {
                case UnknownRef u:
                    _unknownNames.Add(u.Name);
                    break;
                case UnaryExpr u:
                    CollectUnknown(u.Operand);
                    break;
                case BinaryExpr b:
                    CollectUnknown(b.Left);
                    CollectUnknown(b.Right);
                    break;
                case AssignExpr a:
                    CollectUnknown(a.Target);
                    CollectUnknown(a.Value);
                    break;
                case CallExpr c:
                    foreach (Expr arg in c.Arguments)
                    {
                        CollectUnknown(arg);
                    }

                    break;
                case SequenceExpr s:
                    foreach (Expr x in s.Statements)
                    {
                        CollectUnknown(x);
                    }

                    break;
            }
        }
    }

    /// <summary>Name resolution for one map: events and timers by name, user variables registered on first use.</summary>
    private sealed class Scope(List<string> events, List<string> timers) : IExpressionScope
    {
        private readonly Dictionary<string, int> _variables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _events = Index(events);
        private readonly Dictionary<string, int> _timers = Index(timers);

        public List<string> Variables { get; } = [];

        public int Expressions { get; set; }

        public int UserVariable(string name)
        {
            if (_variables.TryGetValue(name, out int i))
            {
                return i;
            }

            _variables[name] = Variables.Count;
            Variables.Add(name);
            return Variables.Count - 1;
        }

        public int Event(string name) => _events.TryGetValue(name, out int i) ? i : -1;

        public int Timer(string name) => _timers.TryGetValue(name, out int i) ? i : -1;

        private static Dictionary<string, int> Index(List<string> names)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i].Length > 0)
                {
                    map.TryAdd(names[i], i);
                }
            }

            return map;
        }
    }
}
