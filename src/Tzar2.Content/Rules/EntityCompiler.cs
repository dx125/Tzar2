using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Rules;

/// <summary>
/// Compiles resolved entries into <see cref="EntityTypeDefinition"/>s (ADR 0033). Ids are the positions of the entries,
/// which the caller sorted by key. Every trait and field is checked: an unknown trait or field is an error, so typos in
/// rules fail at compile time with their file and line.
/// </summary>
internal sealed partial class EntityCompiler
{
    private const int UnitsPerTile = 2048;
    private const int UnitsPerPixel = 64;

    /// <summary>
    /// The speed of a unit whose walk sheet has the stride <paramref name="stride"/> (IMAGES.INI <c>move_len</c>; WORLD-012):
    /// the original steps one pixel every <c>(32000 / move_len) &gt;&gt; 5</c> of its milliseconds (ReTzar's reading of
    /// its movement), and its millisecond lasts 1.25 of ours (<see cref="LegacyTime.MsPerTimeUnit"/>, ADR 0169). Stride 50
    /// walks 40 px/s, 85 72.7, 56 47, 80 66.7 and 32 25.8, as measured (ADR 0172). World units per second.
    /// </summary>
    public static int StrideUnitsPerSecond(int stride)
    {
        int msPerPixel = Math.Max(1, (32000 / Math.Max(1, stride)) >> 5);
        return (int)((long)UnitsPerPixel * 1000 * 1000 / ((long)LegacyTime.MsPerTimeUnit * msPerPixel));
    }

    private static readonly Dictionary<string, string[]> Catalogue = new(StringComparer.Ordinal)
    {
        // Lookup only, never iterated (T2D004).
        ["Legacy"] = ["class", "civ", "name"],
        ["Mobile"] = ["locomotor", "speed", "pixelsPerSecond", "stride"],
        ["Collision"] = ["radius", "terrain", "clearance"],
        ["Health"] = ["max"],
        ["Armor"] = ["defence", "resistance"],
        ["RevealsShroud"] = ["sight"],
        ["Armament"] = ["attack", "range", "rate", "cooldownMs", "hitDelayMs", "hitPermille", "damage", "autoAcquire", "minRange", "maxAttack", "projectile"],
        ["Cost"] = [],
        ["BuildTime"] = ["ms"],
        ["PopulationCost"] = ["amount"],
        ["PopulationProvider"] = ["amount"],
        ["Building"] = ["width", "height", "mask", "placement", "coastReachTiles", "turnedWidth", "turnedHeight", "turnedMask"],
        ["Harvester"] = ["capacity", "gatherMs"],
        ["Builder"] = ["builds", "civilizations"],
        ["Producer"] = ["queue"],
        ["Producible"] = ["by"],
        ["Researcher"] = ["technologies", "upgrades"],
        ["ResourceNode"] = ["resource", "amount", "regenMs", "gatherMs", "carry", "keepWhenEmpty", "workers"],
        ["DropOff"] = ["resources", "access", "autoGather"],
        ["Requires"] = ["buildings", "technology", "technologies"],
        ["TechTree"] = [],
        ["CorpseOnDeath"] = [],
        ["Capturable"] = ["instant", "technology"],
        ["Capturer"] = [],
        ["InfluenceEmitter"] = ["own", "other"],
        ["Repairable"] = [],
        ["Burnable"] = [],
        ["RuinOnDeath"] = [],
        ["ManaPool"] = ["max", "regenPerMinute"],
        ["Wall"] = [],
        ["Gate"] = ["passage", "turnedPassage"],
        ["Bridge"] = [],
        ["ProtectedDelete"] = [],
        ["Seat"] = [],
        ["StartingWorker"] = [],
        ["WalkableFootprint"] = [],
        ["Decay"] = ["afterMs", "healthPerMinute"],
        ["SpawnOnDeath"] = ["type", "start", "perSecond", "max", "piece"],
        ["Lifetime"] = ["ms"],
        ["Trader"] = ["capacity", "access", "technology"],
        ["TradeDock"] = ["access"],
        ["Tavern"] = [],
        ["ClaimOnTouch"] = ["by"],
        ["Animal"] = ["wanders", "guards", "ambient"],
        ["Detector"] = ["radius"],
    };

    private readonly List<ResolvedEntry> _entries;
    private readonly List<string> _resources;
    private readonly GameplayReader _gameplay;
    private readonly string[] _keys;
    private readonly ResearchIds _research;
    private readonly List<string> _diagnostics;

    public EntityCompiler(List<ResolvedEntry> sortedEntries, List<string> resources, GameplayReader gameplay, ResearchIds research, List<string> diagnostics)
    {
        _entries = sortedEntries;
        _resources = resources;
        _gameplay = gameplay;
        _research = research;
        _diagnostics = diagnostics;
        _keys = new string[sortedEntries.Count];
        for (int i = 0; i < _keys.Length; i++)
        {
            _keys[i] = sortedEntries[i].Key;
        }
    }

    public List<EntityTypeDefinition> CompileAll()
    {
        var drafts = new Draft[_entries.Count];
        for (int i = 0; i < drafts.Length; i++)
        {
            drafts[i] = ReadDraft(_entries[i]);
        }

        // Reverse links: Producible.by names producers; the producers get the type in their lists.
        var produces = new List<ushort>[drafts.Length];
        for (int i = 0; i < drafts.Length; i++)
        {
            foreach ((string producer, RuleValue at) in drafts[i].ProducedBy)
            {
                int p = Resolve(producer, at, $"{_keys[i]}.Producible.by");
                if (drafts[p].FootprintWidth == 0)
                {
                    throw Error(at, $"{_keys[i]}.Producible.by: '{producer}' is not a building");
                }

                if (drafts[i].SpeedUnitsPerSecond == 0)
                {
                    // The tables give some units no SPEED (caravans): they cannot leave a building until a rule gives one.
                    _diagnostics.Add($"{_keys[i]}: has no speed, so '{producer}' cannot train it");
                    continue;
                }

                (produces[p] ??= []).Add((ushort)i);
            }
        }

        var types = new List<EntityTypeDefinition>(drafts.Length);
        for (int i = 0; i < drafts.Length; i++)
        {
            Draft d = drafts[i];
            ProducerDefinition? producer = null;
            if (produces[i] is not null || d.QueueCapacity > 0 || d.Researches.Count > 0 || d.Upgrades.Count > 0)
            {
                List<ushort> ids = produces[i] ?? [];
                ids.Sort();
                producer = new ProducerDefinition([.. ids], d.QueueCapacity > 0 ? d.QueueCapacity : _gameplay.QueueCapacity)
                {
                    Researches = [.. d.Researches],
                    Upgrades = [.. d.Upgrades],
                };
            }

            var requirements = new List<RequirementSet>();
            if (d.RequiredTechnologies.Count > 0 || d.Unsatisfiable)
            {
                requirements.Add(new RequirementSet(string.Empty, [.. d.RequiredTechnologies], [], d.Unsatisfiable));
            }

            foreach ((string civ, SortedSet<ushort> techs, List<(string Key, RuleValue At)> buildings, bool unsatisfiable) in d.CivRequirements)
            {
                requirements.Add(new RequirementSet(civ, [.. techs], ResolveList(buildings, $"{_keys[i]}.TechTree.{civ}.buildings"), unsatisfiable));
            }

            int spawnOnDeath = d.SpawnOnDeath is { } spawn ? Resolve(spawn.Key, spawn.At, $"{_keys[i]}.SpawnOnDeath.type") : -1;

            var traits = new EntityTraits
            {
                Category = d.Category,
                LegacyClass = d.LegacyClass,
                Civilization = d.Civilization,
                Cost = d.Cost,
                BuildTimeMs = d.BuildTimeMs,
                SightUnits = d.SightUnits,
                Defence = d.Defence,
                Resistance = d.Resistance,
                Armament = d.Armament,
                Harvester = d.Harvester,
                Builds = ResolveBuilds(i, drafts),
                Producer = producer,
                ResourceNode = d.ResourceNode,
                DropOffMask = d.DropOffMask,
                PopulationCost = d.PopulationCost,
                PopulationProvided = d.PopulationProvided,
                RequiredTypes = ResolveList(d.RequiredBuildings, $"{_keys[i]}.Requires.buildings"),
                RequiredTechnology = d.RequiredTechnology,
                Requirements = [.. requirements],
                ManaCost = d.ManaCost,
                ManaPool = d.ManaPool,
                DropOffAccess = d.DropOffAccess,
                AutoGather = d.AutoGather,
                Placement = d.Placement,
                CoastReachTiles = d.CoastReachTiles,
                Capturable = d.Capturable,
                IsCapturer = d.IsCapturer,
                Influence = d.Influence,
                IsRepairable = d.IsRepairable,
                Burnable = d.Burnable,
                LeavesRuin = d.LeavesRuin,
                IsWall = d.IsWall,
                Gate = d.Gate,
                IsBridge = d.IsBridge,
                ProtectedDelete = d.ProtectedDelete,
                IsSeat = d.IsSeat,
                IsStartingWorker = d.IsStartingWorker,
                WalkableFootprint = d.WalkableFootprint,
                Decay = d.Decay,
                SpawnOnDeath = spawnOnDeath,
                Meat = d.Meat,
                LifetimeMs = d.LifetimeMs,
                World = d.World with
                {
                    IsMilitary = d.Category == EntityCategory.Unit && d.Armament is not null && d.Harvester is null && !d.IsBuilder && d.World.Trader is null,
                },
            };
            traits = WithCombatTraits(i, traits, d);
            try
            {
                types.Add(new EntityTypeDefinition(
                    (ushort)i,
                    _keys[i],
                    d.Locomotor,
                    d.SpeedUnitsPerSecond,
                    d.MaxHealth,
                    d.Radius,
                    d.Clearance,
                    d.FootprintWidth,
                    d.FootprintHeight,
                    d.Mask,
                    d.LeavesCorpse,
                    traits)
                {
                    TerrainRadius = d.TerrainRadius,
                    Turned = d.Turned ?? TurnedFootprint.Transposed(d.FootprintWidth, d.FootprintHeight, d.Mask ?? []),
                });
            }
            catch (ArgumentException ex)
            {
                throw new RulesException(_entries[i].Source, _entries[i].Line, $"{_keys[i]}: {ex.Message}");
            }
        }

        return types;
    }

    private Draft ReadDraft(ResolvedEntry entry)
    {
        var d = new Draft();
        string key = entry.Key;
        foreach (ResolvedTrait trait in entry.Traits)
        {
            if (!Catalogue.TryGetValue(trait.Name, out string[]? fields) && !CombatCatalogue.TryGetValue(trait.Name, out fields))
            {
                throw new RulesException(trait.Source, trait.Line, $"{key}: unknown trait '{trait.Name}'");
            }

            if (trait.Name is not ("Cost" or "TechTree"))
            {
                foreach (KeyValuePair<string, RuleValue> f in trait.Fields)
                {
                    if (Array.IndexOf(fields, f.Key) < 0)
                    {
                        throw Error(f.Value, $"{key}.{trait.Name}: unknown field '{f.Key}' (known: {string.Join(", ", fields)})");
                    }
                }
            }
        }

        string Where(string trait, string field) => $"{key}.{trait}.{field}";
        int? IntOf(string trait, string field) => entry.Find(trait)?.Get(field) is { } v ? RulesetCompiler.Int(v.Node, v.Source, Where(trait, field)) : null;
        string? StrOf(string trait, string field) => entry.Find(trait)?.Get(field) is { } v ? RulesetCompiler.Str(v.Node, v.Source, Where(trait, field)) : null;
        int NonNegative(string trait, string field, int fallback)
        {
            int value = IntOf(trait, field) ?? fallback;
            return value >= 0 ? value : throw Error(entry.Find(trait)!.Get(field)!.Value, $"{Where(trait, field)} must not be negative");
        }

        d.LegacyClass = StrOf("Legacy", "class") ?? string.Empty;
        d.Civilization = StrOf("Legacy", "civ") ?? string.Empty;
        d.MaxHealth = Math.Max(1, NonNegative("Health", "max", 1));
        d.Defence = IntOf("Armor", "defence") ?? 0;
        d.Resistance = NonNegative("Armor", "resistance", 0);
        int Scaled(string trait, string field, long value, int factor)
            => value * factor is var v && v <= int.MaxValue
                ? (int)v
                : throw new RulesException(entry.Find(trait)?.Source ?? entry.Source, entry.Find(trait)?.Line ?? entry.Line, $"{Where(trait, field)}: {value} is too large");

        d.SightUnits = Scaled("RevealsShroud", "sight", NonNegative("RevealsShroud", "sight", 0), UnitsPerTile);
        d.BuildTimeMs = NonNegative("BuildTime", "ms", 0);
        d.PopulationCost = NonNegative("PopulationCost", "amount", 0);
        d.PopulationProvided = NonNegative("PopulationProvider", "amount", 0);
        d.LeavesCorpse = entry.Find("CorpseOnDeath") is not null;

        if (entry.Find("Mobile") is { } mobile)
        {
            d.Locomotor = mobile.Get("locomotor") is { } loco ? RulesetCompiler.ParseLocomotor(RulesetCompiler.Str(loco.Node, loco.Source, Where("Mobile", "locomotor")), loco.Source, loco.Node.Line) : Locomotor.Foot;
            // A measured speed first (ADR 0172), then the original's rule from the walk sheet's stride (WORLD-012), then
            // SPEED x speedPixelsPerSecondPerPoint (UNVERIFIED) for what has neither.
            if (IntOf("Mobile", "pixelsPerSecond") is { } measured)
            {
                d.SpeedUnitsPerSecond = Scaled("Mobile", "pixelsPerSecond", Math.Max(0, measured), UnitsPerPixel);
            }
            else if (IntOf("Mobile", "stride") is > 0 and var stride)
            {
                d.SpeedUnitsPerSecond = StrideUnitsPerSecond(stride);
            }
            else
            {
                int pixels = Scaled("Mobile", "speed", NonNegative("Mobile", "speed", 0), _gameplay.SpeedPixelsPerSecondPerPoint);
                d.SpeedUnitsPerSecond = Scaled("Mobile", "pixelsPerSecond", Math.Max(0, pixels), UnitsPerPixel);
            }
        }

        d.Radius = Scaled("Collision", "radius", Math.Max(1, NonNegative("Collision", "radius", EntityTypeDefinition.DefaultRadius / UnitsPerPixel)), UnitsPerPixel);
        d.Clearance = Math.Max(1, NonNegative("Collision", "clearance", 1));
        d.TerrainRadius = IntOf("Collision", "terrain") is { } terrain ? Scaled("Collision", "terrain", Math.Max(1, NonNegative("Collision", "terrain", 1)), UnitsPerPixel) : 0;

        if (entry.Find("Cost") is { } cost)
        {
            int[] amounts = new int[_resources.Count];
            foreach (KeyValuePair<string, RuleValue> f in cost.Fields)
            {
                int r = _resources.IndexOf(f.Key);
                if (r < 0 && f.Key == "mana")
                {
                    continue; // GAME-01: mana is per caster, not a stockpile
                }

                if (r < 0)
                {
                    throw Error(f.Value, $"{key}.Cost: unknown resource '{f.Key}' (known: {string.Join(", ", _resources)}, mana)");
                }

                amounts[r] = RulesetCompiler.Int(f.Value.Node, f.Value.Source, Where("Cost", f.Key));
            }

            d.Cost = [.. amounts];
            d.ManaCost = NonNegative("Cost", "mana", 0);
        }

        if (entry.Find("Armament") is { } unrated && (IntOf("Armament", "attack") ?? 0) > 0 && (IntOf("Armament", "rate") ?? 0) <= 0 && IntOf("Armament", "cooldownMs") is null)
        {
            // A table row with an ATTACK but no RATE (priests, Jihad warriors) cannot attack until a rule gives a rate.
            _diagnostics.Add($"{key}: Armament has no rate or cooldownMs; the type cannot attack ({unrated.Source})");
        }
        else if (entry.Find("Armament") is { } arm && (IntOf("Armament", "attack") ?? 0) > 0)
        {
            int rate = IntOf("Armament", "rate") ?? 0;
            int? explicitCooldown = IntOf("Armament", "cooldownMs");
            int cooldown = explicitCooldown ?? Math.Max(1, _gameplay.AttackCycleMs / rate);
            // The hit: an explicit time, else the share of the swing the attack animation gives (WAR-014), else the placeholder.
            int? permille = IntOf("Armament", "hitPermille");
            if (permille is < 0 or > 1000)
            {
                throw Error(arm.Get("hitPermille")!.Value, $"{Where("Armament", "hitPermille")} must be 0..1000");
            }

            int hit = IntOf("Armament", "hitDelayMs") ?? (int)(permille is { } p ? (long)cooldown * p / 1000 : (long)cooldown * _gameplay.HitDelayPercent / 100);
            DamageClass damage = (StrOf("Armament", "damage") ?? (IntOf("Armament", "range") > 0 ? "arrow" : "sword")) switch
            {
                "sword" => DamageClass.Sword,
                "arrow" => DamageClass.Arrow,
                "cannon" => DamageClass.Cannon,
                { } other => throw new RulesException(arm.Source, arm.Line, $"{key}.Armament.damage: '{other}' is not sword/arrow/cannon"),
            };
            bool auto = arm.Get("autoAcquire") is not { } a || RulesetCompiler.Bool(a.Node, a.Source, Where("Armament", "autoAcquire"));
            d.Armament = new ArmamentDefinition(
                NonNegative("Armament", "attack", 0),
                Scaled("Armament", "range", NonNegative("Armament", "range", 0), UnitsPerTile),
                cooldown,
                Math.Min(hit, cooldown),
                damage,
                auto);
        }

        if (entry.Find("Building") is { } building)
        {
            d.FootprintWidth = NonNegative("Building", "width", 1);
            d.FootprintHeight = NonNegative("Building", "height", 1);
            if (building.Get("mask") is { } mask)
            {
                d.Mask = ReadMask(mask, d.FootprintWidth, d.FootprintHeight, Where("Building", "mask"));
            }

            // A footprint of its own when turned (the original's gates, 7 x 1 and 1 x 6; its bridges, 11 x 7 and 6 x 11).
            if (IntOf("Building", "turnedWidth") is > 0 and var tw && IntOf("Building", "turnedHeight") is > 0 and var th)
            {
                byte[] cells = building.Get("turnedMask") is { } turnedMask ? ReadMask(turnedMask, tw, th, Where("Building", "turnedMask")) : [.. System.Linq.Enumerable.Repeat((byte)1, tw * th)];
                d.Turned = new TurnedFootprint(tw, th, [.. cells]);
            }

            d.Placement = (StrOf("Building", "placement") ?? "land") switch
            {
                "land" => PlacementKind.Land,
                "coast" => PlacementKind.Coast,
                "bridge" => PlacementKind.Bridge,
                { } other => throw Error(building.Get("placement")!.Value, $"{Where("Building", "placement")}: '{other}' is not land/coast/bridge"),
            };
            d.CoastReachTiles = NonNegative("Building", "coastReachTiles", 2);
            if (d.CoastReachTiles > 16)
            {
                throw Error(building.Get("coastReachTiles")!.Value, $"{Where("Building", "coastReachTiles")} must be 0..16");
            }
        }

        if (entry.Find("Harvester") is { } harvester)
        {
            int[] gather = new int[_resources.Count];
            if (harvester.Get("gatherMs") is { } g)
            {
                foreach (KeyValuePair<string, YamlNode> f in RulesetCompiler.Map(g.Node, g.Source, Where("Harvester", "gatherMs")).Entries)
                {
                    int r = _resources.IndexOf(f.Key);
                    if (r < 0)
                    {
                        throw new RulesException(g.Source, f.Value.Line, $"{key}.Harvester.gatherMs: unknown resource '{f.Key}'");
                    }

                    gather[r] = RulesetCompiler.Int(f.Value, g.Source, $"{key}.Harvester.gatherMs.{f.Key}");
                }
            }

            d.Harvester = new HarvesterDefinition(Math.Max(1, NonNegative("Harvester", "capacity", 1)), [.. gather]);
        }

        if (entry.Find("Builder") is { } builder)
        {
            d.IsBuilder = true;
            if (builder.Get("builds") is { } builds)
            {
                foreach (string b in RulesDocument.StringList(builds.Node, builds.Source, Where("Builder", "builds")))
                {
                    d.Builds.Add((b, builds));
                }
            }

            if (builder.Get("civilizations") is { } civs)
            {
                d.BuildsCivilizations.AddRange(RulesDocument.StringList(civs.Node, civs.Source, Where("Builder", "civilizations")));
            }
        }

        if (entry.Find("Producer") is not null)
        {
            d.QueueCapacity = IntOf("Producer", "queue") ?? _gameplay.QueueCapacity;
        }

        if (entry.Find("Producible")?.Get("by") is { } by)
        {
            foreach (string p in RulesDocument.StringList(by.Node, by.Source, Where("Producible", "by")))
            {
                d.ProducedBy.Add((p, by));
            }
        }

        if (entry.Find("ResourceNode") is { } node)
        {
            string resource = StrOf("ResourceNode", "resource") ?? throw new RulesException(node.Source, node.Line, $"{key}.ResourceNode needs a resource");
            int r = _resources.IndexOf(resource);
            if (r < 0)
            {
                throw new RulesException(node.Source, node.Line, $"{key}.ResourceNode: unknown resource '{resource}'");
            }

            string amountText = StrOf("ResourceNode", "amount") ?? throw new RulesException(node.Source, node.Line, $"{key}.ResourceNode needs an amount");
            int amount = amountText == "unlimited" ? ResourceNodeDefinition.Unlimited : Math.Max(1, NonNegative("ResourceNode", "amount", 1));
            d.ResourceNode = new ResourceNodeDefinition(r, amount)
            {
                RegenMs = NonNegative("ResourceNode", "regenMs", 0),
                GatherMs = NonNegative("ResourceNode", "gatherMs", 0),
                CarryLoad = NonNegative("ResourceNode", "carry", 0),
                KeepWhenEmpty = node.Get("keepWhenEmpty") is { } keep && RulesetCompiler.Bool(keep.Node, keep.Source, Where("ResourceNode", "keepWhenEmpty")),
                MaxWorkers = NonNegative("ResourceNode", "workers", 0),
            };
        }

        if (entry.Find("DropOff") is { } dropOff)
        {
            if (dropOff.Get("resources") is { } drop)
            {
                foreach (string resource in RulesDocument.StringList(drop.Node, drop.Source, Where("DropOff", "resources")))
                {
                    int r = _resources.IndexOf(resource);
                    d.DropOffMask |= r >= 0 ? 1 << r : throw Error(drop, $"{key}.DropOff: unknown resource '{resource}'");
                }
            }

            if (dropOff.Get("access") is { } access)
            {
                d.DropOffAccess = DropOffAccess.None;
                foreach (string a in RulesDocument.StringList(access.Node, access.Source, Where("DropOff", "access")))
                {
                    d.DropOffAccess |= a switch
                    {
                        "land" => DropOffAccess.Land,
                        "water" => DropOffAccess.Water,
                        _ => throw Error(access, $"{Where("DropOff", "access")}: '{a}' is not land/water"),
                    };
                }
            }

            d.AutoGather = dropOff.Get("autoGather") is { } auto && RulesetCompiler.Bool(auto.Node, auto.Source, Where("DropOff", "autoGather"));
        }

        if (entry.Find("Requires") is { } requires)
        {
            if (requires.Get("buildings") is { } req)
            {
                foreach (string b in RulesDocument.StringList(req.Node, req.Source, Where("Requires", "buildings")))
                {
                    d.RequiredBuildings.Add((b, req));
                }
            }

            d.RequiredTechnology = StrOf("Requires", "technology") ?? string.Empty;
            var techs = new List<(string, RuleValue)>();
            if (requires.Get("technology") is { } single)
            {
                techs.Add((d.RequiredTechnology, single));
            }

            if (requires.Get("technologies") is { } many)
            {
                foreach (string t in RulesDocument.StringList(many.Node, many.Source, Where("Requires", "technologies")))
                {
                    techs.Add((t, many));
                }
            }

            foreach ((string t, RuleValue at) in techs)
            {
                if (_research.Technology(t) is { } id)
                {
                    d.RequiredTechnologies.Add(id);
                }
                else
                {
                    d.Unsatisfiable = true;
                    _diagnostics.Add($"{key}: requires '{t}', which does not exist; it can never be built or trained ({at.Source})");
                }
            }
        }

        if (entry.Find("TechTree") is { } tree)
        {
            foreach (KeyValuePair<string, RuleValue> civ in tree.Fields)
            {
                YamlMapping spec = RulesetCompiler.Map(civ.Value.Node, civ.Value.Source, Where("TechTree", civ.Key));
                var techs = new SortedSet<ushort>();
                var buildings = new List<(string Key, RuleValue At)>();
                bool unsatisfiable = false;
                foreach (KeyValuePair<string, YamlNode> f in spec.Entries)
                {
                    List<string> names = RulesDocument.StringList(f.Value, civ.Value.Source, $"{Where("TechTree", civ.Key)}.{f.Key}");
                    switch (f.Key)
                    {
                        case "technologies":
                            foreach (string t in names)
                            {
                                if (_research.Technology(t) is { } id)
                                {
                                    techs.Add(id);
                                }
                                else
                                {
                                    unsatisfiable = true;
                                    _diagnostics.Add($"{key}: the {civ.Key} technology tree requires '{t}', which does not exist");
                                }
                            }

                            break;
                        case "buildings":
                            foreach (string b in names)
                            {
                                buildings.Add((b, new RuleValue(f.Value, civ.Value.Source)));
                            }

                            break;
                        default:
                            throw new RulesException(civ.Value.Source, f.Value.Line, $"{Where("TechTree", civ.Key)}: unknown field '{f.Key}' (technologies, buildings)");
                    }
                }

                d.CivRequirements.Add((civ.Key, techs, buildings, unsatisfiable));
            }
        }

        if (entry.Find("Researcher") is { } researcher)
        {
            foreach ((string field, List<ushort> into, Func<string, ushort?> lookup) in new (string, List<ushort>, Func<string, ushort?>)[]
            {
                ("technologies", d.Researches, _research.Technology),
                ("upgrades", d.Upgrades, _research.Upgrade),
            })
            {
                if (researcher.Get(field) is not { } list)
                {
                    continue;
                }

                foreach (string name in RulesDocument.StringList(list.Node, list.Source, Where("Researcher", field)))
                {
                    ushort id = lookup(name) ?? throw Error(list, $"{Where("Researcher", field)}: unknown '{name}'");
                    if (!into.Contains(id))
                    {
                        into.Add(id);
                    }
                }
            }

            d.Researches.Sort();
            d.Upgrades.Sort();
        }

        if (entry.Find("Capturable") is { } capturable)
        {
            string? tech = StrOf("Capturable", "technology");
            int techId = tech is null ? -1 : _research.Technology(tech) ?? throw Error(capturable.Get("technology")!.Value, $"{Where("Capturable", "technology")}: unknown '{tech}'");
            d.Capturable = new CapturableDefinition(capturable.Get("instant") is { } instant && RulesetCompiler.Bool(instant.Node, instant.Source, Where("Capturable", "instant")), techId);
        }

        if (entry.Find("InfluenceEmitter") is not null)
        {
            d.Influence = new InfluenceDefinition(IntOf("InfluenceEmitter", "own") ?? 0, IntOf("InfluenceEmitter", "other") ?? 0);
        }

        if (entry.Find("ManaPool") is not null)
        {
            d.ManaPool = new ManaPoolDefinition(NonNegative("ManaPool", "max", 0), NonNegative("ManaPool", "regenPerMinute", _gameplay.ManaRegenPerMinute));
        }

        if (entry.Find("Gate") is { } gate)
        {
            d.GateRows = gate.Get("passage");
            d.GateTurnedRows = gate.Get("turnedPassage");
        }

        if (entry.Find("Decay") is not null)
        {
            d.Decay = new DecayDefinition(NonNegative("Decay", "afterMs", 0), NonNegative("Decay", "healthPerMinute", 0));
        }

        if (entry.Find("SpawnOnDeath")?.Get("type") is { } spawnType)
        {
            d.SpawnOnDeath = (RulesetCompiler.Str(spawnType.Node, spawnType.Source, Where("SpawnOnDeath", "type")), spawnType);
            if (entry.Find("SpawnOnDeath")!.Get("piece") is not null)
            {
                d.Meat = new MeatDefinition(NonNegative("SpawnOnDeath", "start", 0), NonNegative("SpawnOnDeath", "perSecond", 0),
                    NonNegative("SpawnOnDeath", "max", 0), Math.Max(1, NonNegative("SpawnOnDeath", "piece", 1)));
            }
        }

        d.IsCapturer = entry.Find("Capturer") is not null;
        d.IsRepairable = entry.Find("Repairable") is not null;
        d.Burnable = entry.Find("Burnable") is not null;
        d.LeavesRuin = entry.Find("RuinOnDeath") is not null;
        d.IsWall = entry.Find("Wall") is not null;
        d.IsBridge = entry.Find("Bridge") is not null;
        d.ProtectedDelete = entry.Find("ProtectedDelete") is not null;
        d.IsSeat = entry.Find("Seat") is not null;
        d.IsStartingWorker = entry.Find("StartingWorker") is not null;
        d.WalkableFootprint = entry.Find("WalkableFootprint") is not null;
        d.LifetimeMs = NonNegative("Lifetime", "ms", 0);
        if (d.GateRows is { } passage)
        {
            byte[] cells = ReadMask(passage, d.FootprintWidth, d.FootprintHeight, Where("Gate", "passage"));
            TurnedFootprint turned = d.Turned ?? TurnedFootprint.Transposed(d.FootprintWidth, d.FootprintHeight, cells);
            byte[] turnedCells = d.GateTurnedRows is { } turnedRows
                ? ReadMask(turnedRows, turned.Width, turned.Height, Where("Gate", "turnedPassage"))
                : [.. TurnedFootprint.Transposed(d.FootprintWidth, d.FootprintHeight, cells).Mask];
            if (turnedCells.Length != turned.Width * turned.Height)
            {
                throw Error(passage, $"{Where("Gate", "turnedPassage")} needs the turned footprint's {turned.Width} x {turned.Height} cells");
            }

            d.Gate = new GateDefinition([.. cells]) { TurnedPassage = [.. turnedCells] };
        }

        d.World = ReadWorldTraits(entry, key);
        d.Category = d.ResourceNode is not null && d.SpeedUnitsPerSecond == 0 ? EntityCategory.Resource
            : d.FootprintWidth > 0 ? EntityCategory.Building
            : d.SpeedUnitsPerSecond > 0 ? EntityCategory.Unit
            : EntityCategory.Other;
        ReadCombatTraits(entry, d);
        return d;
    }

    // Rows of 0/1 cells, one row per footprint row (masks and gate passages).
    private static byte[] ReadMask(RuleValue mask, int width, int height, string what)
    {
        List<string> rows = RulesDocument.StringList(mask.Node, mask.Source, what);
        if (rows.Count != height)
        {
            throw Error(mask, $"{what} needs {height} rows");
        }

        var cells = new byte[width * height];
        for (int y = 0; y < rows.Count; y++)
        {
            if (rows[y].Length != width)
            {
                throw Error(mask, $"{what} row {y} needs {width} cells of 0/1");
            }

            for (int x = 0; x < width; x++)
            {
                cells[(y * width) + x] = rows[y][x] switch
                {
                    '1' => 1,
                    '0' => 0,
                    _ => throw Error(mask, $"{what}: use 0 and 1"),
                };
            }
        }

        return cells;
    }

    private ImmutableArray<ushort> ResolveBuilds(int index, Draft[] drafts)
    {
        Draft d = drafts[index];
        if (!d.IsBuilder)
        {
            return [];
        }

        var ids = new SortedSet<ushort>();
        foreach ((string b, RuleValue at) in d.Builds)
        {
            ids.Add((ushort)Resolve(b, at, $"{_keys[index]}.Builder.builds"));
        }

        if (d.BuildsCivilizations.Count > 0)
        {
            for (int i = 0; i < drafts.Length; i++)
            {
                Draft other = drafts[i];
                // Constructible: a footprint and a build time (buildings, fields; never trees or rocks).
                if (other.FootprintWidth == 0 || other.BuildTimeMs <= 0)
                {
                    continue;
                }

                foreach (string civ in d.BuildsCivilizations)
                {
                    string wanted = civ == "own" ? d.Civilization : civ;
                    if (wanted.Length > 0 && string.Equals(other.Civilization, wanted, StringComparison.Ordinal))
                    {
                        ids.Add((ushort)i);
                    }
                }
            }
        }

        return [.. ids];
    }

    private ImmutableArray<ushort> ResolveList(List<(string Key, RuleValue At)> keys, string what)
    {
        var ids = new SortedSet<ushort>();
        foreach ((string k, RuleValue at) in keys)
        {
            ids.Add((ushort)Resolve(k, at, what));
        }

        return [.. ids];
    }

    private int Resolve(string key, RuleValue at, string what)
    {
        int i = Array.BinarySearch(_keys, key, StringComparer.Ordinal);
        return i >= 0 ? i : throw Error(at, $"{what}: unknown entity '{key}'");
    }

    private static RulesException Error(RuleValue at, string message) => new(at.Source, at.Node.Line, message);

    private sealed partial class Draft
    {
        public EntityCategory Category;
        public string LegacyClass = string.Empty;
        public string Civilization = string.Empty;
        public Locomotor Locomotor;
        public int SpeedUnitsPerSecond;
        public int MaxHealth = 1;
        public int Radius = EntityTypeDefinition.DefaultRadius;
        public int Clearance = 1;
        public int TerrainRadius;
        public int FootprintWidth;
        public int FootprintHeight;
        public byte[]? Mask;
        public TurnedFootprint? Turned;
        public RuleValue? GateTurnedRows;
        public bool LeavesCorpse;
        public ImmutableArray<int> Cost = [];
        public int BuildTimeMs;
        public int SightUnits;
        public int Defence;
        public int Resistance;
        public ArmamentDefinition? Armament;
        public HarvesterDefinition? Harvester;
        public bool IsBuilder;
        public List<(string, RuleValue)> Builds = [];
        public List<string> BuildsCivilizations = [];
        public int QueueCapacity;
        public List<(string, RuleValue)> ProducedBy = [];
        public ResourceNodeDefinition? ResourceNode;
        public int DropOffMask;
        public int PopulationCost;
        public int PopulationProvided;
        public List<(string Key, RuleValue At)> RequiredBuildings = [];
        public string RequiredTechnology = string.Empty;
        public List<ushort> RequiredTechnologies = [];
        public bool Unsatisfiable;
        public List<(string Civ, SortedSet<ushort> Techs, List<(string Key, RuleValue At)> Buildings, bool Unsatisfiable)> CivRequirements = [];
        public List<ushort> Researches = [];
        public List<ushort> Upgrades = [];
        public int ManaCost;
        public ManaPoolDefinition? ManaPool;
        public DropOffAccess DropOffAccess = DropOffAccess.Land;
        public bool AutoGather;
        public PlacementKind Placement;
        public int CoastReachTiles = 2;
        public CapturableDefinition? Capturable;
        public bool IsCapturer;
        public InfluenceDefinition? Influence;
        public bool IsRepairable;
        public bool Burnable;
        public bool LeavesRuin;
        public bool IsWall;
        public RuleValue? GateRows;
        public GateDefinition? Gate;
        public bool IsBridge;
        public bool ProtectedDelete;
        public bool IsSeat;
        public bool IsStartingWorker;
        public bool WalkableFootprint;
        public DecayDefinition? Decay;
        public (string Key, RuleValue At)? SpawnOnDeath;
        public MeatDefinition? Meat;
        public int LifetimeMs;
        public WorldTraits World = WorldTraits.None;
    }
}
