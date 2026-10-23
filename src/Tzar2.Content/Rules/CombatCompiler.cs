using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Rules;

/// <summary>
/// Compiles the content of GAME-07…11 that is not a plain entity trait (ADRs 0090-0095): the <c>combat:</c> section
/// (constants, projectiles, unit keywords, hero names), the spell entries (a <c>Spell</c> trait; their ids are the
/// sorted keys, like technologies) and the unit filters of holders, items and conversions, which need every compiled
/// type (they are resolved after <see cref="EntityCompiler.CompileAll"/> and the types are rebuilt with them).
/// </summary>
internal sealed class CombatCompiler
{
    /// <summary>Section name.</summary>
    public const string Section = "combat";

    private const int UnitsPerTile = 2048;
    private const int UnitsPerPixel = 64;

    private static readonly string[] KnownFields =
    [
        "experiencePerDamagePercent", "experiencePerKill", "levelBaseXp", "levelStepXp", "natureManaRegenPercent", "baseLevelCap", "attackPerLevel", "defenceHalvesPerLevel",
        "heroLevel", "leadershipTechnology", "leadershipRadiusTiles", "leadershipSharePercent", "honorTechnology", "honorAttackPercent",
        "dummyMaxLevel", "exitRadiusTiles", "craftHealthPerPower", "craftManaPerPower", "craftPowerPerLevel", "craftGoldPerPower", "craftMsPerPower", "craftMaxPower", "craftBookCostPercent",
        "craftBookTimePercent", "bribeCostPercent", "crusadeAnchor",
        "crusader", "crusadeStart", "crusadeRestart", "crusadeCeilingStep", "crusadeCeilingMax", "crusadeGather", "crusaderArrivalMs", "crusadeAnchorRadiusTiles", "heroNames", "projectiles", "unitKeywords",
        "bansheeSpell", "bansheeChance", "bansheeOutOf", "tournamentTechnology", "tournamentEvery", "tournamentGrant", "tournamentPupils", "magicAttackers", "soulBerserkSpell", "dummyDamagePerPoint", "experienceSpeeds", "levelCapBuildings", "levelCapPerBuilding",
        "marketTechnology", "marketFirstMs", "marketEveryMs", "marketSize", "marketBuyPercent", "marketOffers", "marketBookSpells",
    ];

    private static readonly string[] SpellTraits = ["Legacy", "Spell"];

    private static readonly string[] SpellFields =
    [
        "effect", "target", "affects", "mana", "range", "cooldownMs", "castMs", "amount", "durationMs", "radius", "summon", "projectile",
        "pulseMs", "count", "targets", "bookValue", "durationMaxMs", "square", "scatter", "delayMs", "moveMs",
    ];

    private static readonly string[] ProjectileFields = ["pixelsPerSecond", "homing", "hitRadiusPx", "splashRadiusPx", "splashPercent", "friendlyFire"];

    private readonly RulesetCompiler.SectionSet _settings;
    private readonly List<string> _diagnostics;
    private readonly string[] _spellKeys;
    private readonly List<ResolvedEntry> _spellEntries;
    private readonly List<ProjectileDefinition> _projectiles = [];

    public CombatCompiler(RulesetCompiler.SectionSet settings, List<ResolvedEntry> sortedSpellEntries, List<string> diagnostics)
    {
        _settings = settings;
        _diagnostics = diagnostics;
        _spellEntries = sortedSpellEntries;
        _spellKeys = new string[sortedSpellEntries.Count];
        for (int i = 0; i < _spellKeys.Length; i++)
        {
            _spellKeys[i] = sortedSpellEntries[i].Key;
        }

        if (settings.Mapping(Section) is { } map)
        {
            foreach (KeyValuePair<string, YamlNode> f in map.Entries)
            {
                if (Array.IndexOf(KnownFields, f.Key) < 0)
                {
                    throw new RulesException(settings.FieldSource(Section, f.Key), f.Value.Line, $"combat.{f.Key}: unknown setting (known: {string.Join(", ", KnownFields)})");
                }
            }
        }

        ReadProjectiles();
    }

    /// <summary>The spell id of <paramref name="key"/>, or null.</summary>
    public ushort? SpellId(string key)
    {
        int i = Array.BinarySearch(_spellKeys, key, StringComparer.Ordinal);
        return i >= 0 ? (ushort)i : null;
    }

    /// <summary>The projectile id of <paramref name="key"/>, or null.</summary>
    public int? ProjectileId(string key)
    {
        foreach (ProjectileDefinition p in _projectiles)
        {
            if (p.Key == key)
            {
                return p.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the pending unit filters into the types (rebuilt in place) and compiles the settings and spells.
    /// </summary>
    public CombatRules Compile(List<EntityTypeDefinition> types, List<(int Type, CombatSpecs Specs)> pending, ResearchCompiler research, ResearchIds researchIds, LegacyTables? legacy, LegacyGroups? groups = null)
    {
        Dictionary<string, LocomotorMask> keywords = UnitKeywords();
        foreach ((int index, CombatSpecs specs) in pending)
        {
            EntityTypeDefinition t = types[index];
            EntityTraits traits = t.Traits;
            if (specs.HolderAccepts is { } accepts && traits.Holder is { } holder)
            {
                traits = traits with { Holder = holder with { Accepts = Resolve(types, accepts.Specs, accepts.At, keywords, t.Traits.Civilization, $"{t.Key}.Holder.accepts") } };
            }

            if (specs.ItemHolders is { } holders && traits.Item is { } item)
            {
                traits = traits with { Item = item with { Holders = Resolve(types, holders.Specs, holders.At, keywords, null, $"{t.Key}.Item.holders") } };
            }

            if (specs.ConvertClasses is { } classes && traits.ConvertsFrom is { } convert)
            {
                traits = traits with { ConvertsFrom = convert with { From = Resolve(types, classes.Specs, classes.At, keywords, t.Traits.Civilization, $"{t.Key}.ConvertsFrom.classes") } };
            }

            if (specs.ImpersonateNever is { } never)
            {
                // Groups of BONUS.INI (machines, ships, magic_creatures) or classes; 'except' gives some back (the mage is a man).
                var off = new List<ushort>();
                foreach (string spec in never.Specs)
                {
                    (groups ?? LegacyGroups.Empty).Match(spec, types, off);
                }

                var back = new List<ushort>();
                foreach (string spec in specs.ImpersonateExcept?.Specs ?? [])
                {
                    (groups ?? LegacyGroups.Empty).Match(spec, types, back);
                }

                var ids = new SortedSet<ushort>(off);
                ids.ExceptWith(back);
                traits = traits with { NotImpersonated = [.. ids] };
            }

            types[index] = WithTraits(t, traits);
        }

        var spells = new List<SpellDefinition>(_spellEntries.Count);
        for (int i = 0; i < _spellEntries.Count; i++)
        {
            spells.Add(CompileSpell((ushort)i, _spellEntries[i], types, research));
        }

        return new CombatRules(Settings(types, researchIds, legacy, spells) with { TournamentPupils = Filter(research, "tournamentPupils"), MagicAttackers = Filter(research, "magicAttackers"), LevelCapBuildings = Filter(research, "levelCapBuildings"), SoulBerserkSpell = SpellOf("soulBerserkSpell", spells), MarketOffers = Offers(types), MarketBookSpells = BookSpells(spells) }, spells, _projectiles);
    }

    private static EntityTypeDefinition WithTraits(EntityTypeDefinition t, EntityTraits traits)
        => new(t.Id, t.Key, t.Locomotor, t.SpeedUnitsPerSecond, t.MaxHealth, t.Radius, t.ClearanceTiles, t.FootprintWidth, t.FootprintHeight, t.Footprint.ToArray(), t.LeavesCorpse, traits);

    // Keywords of [accept]/[enter] lists (allunits, earthunits, infantryunits, mountedunits) → the locomotors of the units
    // they mean; anything else is a class (of the holder's civilization or the common one) or an entity key.
    private Dictionary<string, LocomotorMask> UnitKeywords()
    {
        var map = new Dictionary<string, LocomotorMask>(StringComparer.Ordinal); // lookup only (T2D004)
        if (_settings.Field(Section, "unitKeywords") is { } node)
        {
            string source = _settings.FieldSource(Section, "unitKeywords");
            foreach (KeyValuePair<string, YamlNode> k in RulesetCompiler.Map(node, source, "combat.unitKeywords").Entries)
            {
                LocomotorMask mask = LocomotorMask.None;
                foreach (string loco in RulesDocument.StringList(k.Value, source, $"combat.unitKeywords.{k.Key}"))
                {
                    mask |= RulesetCompiler.ParseLocomotor(loco, source, k.Value.Line).Family();
                }

                map[k.Key] = mask;
            }
        }

        return map;
    }

    private ImmutableArray<ushort> Resolve(List<EntityTypeDefinition> types, List<string> specs, RuleValue at, Dictionary<string, LocomotorMask> keywords, string? civilization, string what)
    {
        var ids = new SortedSet<ushort>();
        foreach (string spec in specs)
        {
            if (keywords.TryGetValue(spec, out LocomotorMask mask))
            {
                foreach (EntityTypeDefinition t in types)
                {
                    if (t.IsMobile && t.Traits.Category == EntityCategory.Unit && mask.Allows(t.Locomotor))
                    {
                        ids.Add(t.Id);
                    }
                }

                continue;
            }

            int before = ids.Count;
            foreach (EntityTypeDefinition t in types)
            {
                bool cls = t.Traits.LegacyClass == spec && (civilization is null || civilization.Length == 0 || civilization == "comm"
                    || t.Traits.Civilization == civilization || t.Traits.Civilization == "comm");
                if (cls || t.Key == spec)
                {
                    ids.Add(t.Id);
                }
            }

            if (ids.Count == before)
            {
                _diagnostics.Add($"{what}: '{spec}' matches no type ({at.Source})");
            }
        }

        return [.. ids];
    }

    private void ReadProjectiles()
    {
        if (_settings.Field(Section, "projectiles") is not { } node)
        {
            return;
        }

        string source = _settings.FieldSource(Section, "projectiles");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        YamlMapping map = RulesetCompiler.Map(node, source, "combat.projectiles");
        foreach (KeyValuePair<string, YamlNode> p in map.Entries)
        {
            keys.Add(p.Key);
        }

        foreach (string key in keys)
        {
            YamlMapping p = RulesetCompiler.Map(map.Get(key), source, $"combat.projectiles.{key}");
            foreach (KeyValuePair<string, YamlNode> f in p.Entries)
            {
                if (Array.IndexOf(ProjectileFields, f.Key) < 0)
                {
                    throw new RulesException(source, f.Value.Line, $"combat.projectiles.{key}: unknown field '{f.Key}' (known: {string.Join(", ", ProjectileFields)})");
                }
            }

            int Px(string field, int fallback, int max)
            {
                int v = p.Get(field) is { } n ? RulesetCompiler.Int(n, source, $"combat.projectiles.{key}.{field}") : fallback;
                return v >= 0 && v <= max ? v * UnitsPerPixel : throw new RulesException(source, p.Line, $"combat.projectiles.{key}.{field} must be 0..{max}");
            }

            int speed = Px("pixelsPerSecond", 0, 15_000);
            if (speed <= 0)
            {
                throw new RulesException(source, p.Line, $"combat.projectiles.{key}.pixelsPerSecond must be positive");
            }

            int splash = p.Get("splashPercent") is { } sp ? RulesetCompiler.Int(sp, source, $"combat.projectiles.{key}.splashPercent") : 100;
            _projectiles.Add(new ProjectileDefinition(
                (ushort)_projectiles.Count,
                key,
                speed,
                p.Get("homing") is not { } h || RulesetCompiler.Bool(h, source, $"combat.projectiles.{key}.homing"),
                Px("hitRadiusPx", 16, 512),
                Px("splashRadiusPx", 0, 512),
                splash is >= 0 and <= 1000 ? splash : throw new RulesException(source, p.Line, $"combat.projectiles.{key}.splashPercent must be 0..1000"),
                p.Get("friendlyFire") is { } ff && RulesetCompiler.Bool(ff, source, $"combat.projectiles.{key}.friendlyFire")));
        }
    }

    private CombatSettings Settings(List<EntityTypeDefinition> types, ResearchIds research, LegacyTables? legacy, List<SpellDefinition> spells)
    {
        CombatSettings d = CombatSettings.Default;
        int bansheeSpell = -1;
        if (_settings.Scalar(Section, "bansheeSpell") is { } bansheeKey)
        {
            bansheeSpell = spells.FindIndex(s => s.Key == bansheeKey);
            if (bansheeSpell < 0)
            {
                _diagnostics.Add($"combat.bansheeSpell: unknown spell '{bansheeKey}'");
            }
        }

        int bansheeOutOf = Range("bansheeOutOf", d.BansheeOutOf, 1, 1_000_000);
        return d with
        {
            ExperiencePerDamagePercent = Range("experiencePerDamagePercent", d.ExperiencePerDamagePercent, 0, 100_000),
            ExperiencePerKill = Range("experiencePerKill", d.ExperiencePerKill, 0, 1_000_000),
            LevelBaseXp = Range("levelBaseXp", d.LevelBaseXp, 1, 1_000_000),
            LevelStepXp = Range("levelStepXp", d.LevelStepXp, -1, 1_000_000),
            NatureManaRegenPercent = Range("natureManaRegenPercent", d.NatureManaRegenPercent, 0, 1_000),
            BaseLevelCap = Range("baseLevelCap", d.BaseLevelCap, 0, 10_000),
            AttackPerLevel = Range("attackPerLevel", d.AttackPerLevel, 0, 10_000),
            DefenceHalvesPerLevel = Range("defenceHalvesPerLevel", d.DefenceHalvesPerLevel, 0, 10_000),
            HeroLevel = Range("heroLevel", d.HeroLevel, 1, 10_000),
            LeadershipTechnology = Technology("leadershipTechnology", research),
            LeadershipRadiusUnits = Range("leadershipRadiusTiles", d.LeadershipRadiusUnits / UnitsPerTile, 0, 64) * UnitsPerTile,
            LeadershipSharePercent = Range("leadershipSharePercent", d.LeadershipSharePercent, 0, 1000),
            HonorTechnology = Technology("honorTechnology", research),
            HonorAttackPercent = Range("honorAttackPercent", d.HonorAttackPercent, 0, 10_000),
            DummyMaxLevel = Range("dummyMaxLevel", d.DummyMaxLevel, 0, 10_000),
            ExitRadiusTiles = Range("exitRadiusTiles", d.ExitRadiusTiles, 1, 16),
            CraftHealthPerPower = Range("craftHealthPerPower", d.CraftHealthPerPower, 0, 100_000),
            CraftManaPerPower = Range("craftManaPerPower", d.CraftManaPerPower, 0, 100_000),
            CraftPowerPerLevel = Range("craftPowerPerLevel", d.CraftPowerPerLevel, 1, 10_000),
            CraftGoldPerPower = Range("craftGoldPerPower", d.CraftGoldPerPower, 1, 1_000_000),
            CraftMsPerPower = Range("craftMsPerPower", d.CraftMsPerPower, 0, 600_000),
            CraftMaxPower = Range("craftMaxPower", d.CraftMaxPower, 1, 10_000),
            CraftBookCostPercent = Range("craftBookCostPercent", d.CraftBookCostPercent, 1, 10_000),
            CraftBookTimePercent = Range("craftBookTimePercent", d.CraftBookTimePercent, 1, 10_000),
            BribeCostPercent = Range("bribeCostPercent", d.BribeCostPercent, 0, 100_000),
            CrusadeAnchor = Type("crusadeAnchor", types),
            Crusader = Type("crusader", types),
            CrusadeStart = Range("crusadeStart", d.CrusadeStart, 0, 1000),
            CrusadeRestart = Range("crusadeRestart", d.CrusadeRestart, 0, 1000),
            CrusadeCeilingStep = Range("crusadeCeilingStep", d.CrusadeCeilingStep, 0, 1000),
            CrusadeCeilingMax = Range("crusadeCeilingMax", d.CrusadeCeilingMax, 0, 1000),
            CrusadeGather = Gather(d.CrusadeGather),
            CrusaderArrivalMs = Range("crusaderArrivalMs", d.CrusaderArrivalMs, 1, 60_000),
            CrusadeAnchorRadiusUnits = Range("crusadeAnchorRadiusTiles", d.CrusadeAnchorRadiusUnits / UnitsPerTile, 0, 64) * UnitsPerTile,
            HeroNames = HeroNameLists(legacy),
            BansheeSpell = bansheeSpell,
            BansheeChance = Range("bansheeChance", d.BansheeChance, 0, bansheeOutOf),
            BansheeOutOf = bansheeOutOf,
            TournamentTechnology = Technology("tournamentTechnology", research),
            DummyDamagePerPoint = Range("dummyDamagePerPoint", d.DummyDamagePerPoint, 1, 1_000_000),
            MarketTechnology = Technology("marketTechnology", research),
            MarketFirstMs = Range("marketFirstMs", d.MarketFirstMs, 0, 3_600_000),
            MarketEveryMs = Range("marketEveryMs", d.MarketEveryMs, 1, 3_600_000),
            MarketSize = Range("marketSize", d.MarketSize, 1, 8),
            MarketBuyPercent = Range("marketBuyPercent", d.MarketBuyPercent, 0, 100_000),
            ExperienceSpeeds = Speeds(research),
            LevelCapPerBuilding = Range("levelCapPerBuilding", d.LevelCapPerBuilding, 0, 10_000),
            TournamentEvery = Range("tournamentEvery", d.TournamentEvery, 1, 255),
            TournamentGrant = Range("tournamentGrant", d.TournamentGrant, 0, 1_000_000),
        };
    }

    // combat.heroNames: "legacy" = DATA/HEROES.INI (a section per civilization, a name per line), or { civ: [names] }.
    private ImmutableArray<HeroNames> HeroNameLists(LegacyTables? legacy)
    {
        if (_settings.Field(Section, "heroNames") is not { } node)
        {
            return [];
        }

        string source = _settings.FieldSource(Section, "heroNames");
        var list = new List<HeroNames>();
        if (node is YamlScalar { Value: "legacy" })
        {
            if (legacy is null || !legacy.Has("data/heroes.ini"))
            {
                _diagnostics.Add("combat.heroNames: HEROES.INI is not in the imported package; heroes get numbered names");
                return [];
            }

            foreach (LegacyIniSection section in legacy.Ini("data/heroes.ini").Sections)
            {
                if (string.IsNullOrWhiteSpace(section.Name))
                {
                    continue;
                }

                var names = new List<string>();
                foreach (KeyValuePair<string?, string> e in section.Entries)
                {
                    string name = (e.Key is null ? e.Value : e.Key + (e.Value.Length > 0 ? " = " + e.Value : string.Empty)).Trim();
                    if (name.Length is > 0 and <= 64)
                    {
                        names.Add(name);
                    }
                }

                list.Add(new HeroNames(section.Name.Trim().ToLowerInvariant(), [.. names]));
            }
        }
        else
        {
            foreach (KeyValuePair<string, YamlNode> civ in RulesetCompiler.Map(node, source, "combat.heroNames").Entries)
            {
                list.Add(new HeroNames(civ.Key, [.. RulesDocument.StringList(civ.Value, source, $"combat.heroNames.{civ.Key}")]));
            }
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Civilization, b.Civilization));
        return [.. list];
    }

    private SpellDefinition CompileSpell(ushort id, ResolvedEntry e, List<EntityTypeDefinition> types, ResearchCompiler research)
    {
        foreach (ResolvedTrait trait in e.Traits)
        {
            if (Array.IndexOf(SpellTraits, trait.Name) < 0)
            {
                throw new RulesException(trait.Source, trait.Line, $"{e.Key}: trait '{trait.Name}' is not allowed on a spell (allowed: {string.Join(", ", SpellTraits)})");
            }
        }

        ResolvedTrait spell = e.Find("Spell")!;
        foreach (KeyValuePair<string, RuleValue> f in spell.Fields)
        {
            if (Array.IndexOf(SpellFields, f.Key) < 0)
            {
                throw new RulesException(f.Value.Source, f.Value.Node.Line, $"{e.Key}.Spell: unknown field '{f.Key}' (known: {string.Join(", ", SpellFields)})");
            }
        }

        string Where(string field) => $"{e.Key}.Spell.{field}";
        string? Str(string field) => spell.Get(field) is { } v ? RulesetCompiler.Str(v.Node, v.Source, Where(field)) : null;
        int Int(string field, int fallback, int max)
        {
            if (spell.Get(field) is not { } v)
            {
                return fallback;
            }

            int value = RulesetCompiler.Int(v.Node, v.Source, Where(field));
            return value >= 0 && value <= max ? value : throw new RulesException(v.Source, v.Node.Line, $"{Where(field)} must be 0..{max}");
        }

        string effectText = Str("effect") ?? "damage";
        SpellEffect effect = effectText switch
        {
            "damage" => SpellEffect.Damage,
            "heal" => SpellEffect.Heal,
            "bless" => SpellEffect.Bless,
            "freeze" => SpellEffect.Freeze,
            "stone" => SpellEffect.Stone,
            "cloak" => SpellEffect.Cloak,
            "confuse" => SpellEffect.Confuse,
            "summon" => SpellEffect.Summon,
            "raiseDead" => SpellEffect.RaiseDead,
            "reveal" => SpellEffect.Reveal,
            "experience" => SpellEffect.Experience,
            "areaDamage" => SpellEffect.AreaDamage,
            "curse" => SpellEffect.Curse,
            "kill" => SpellEffect.Kill,
            "hypnotize" => SpellEffect.Hypnotize,
            "soulBind" => SpellEffect.SoulBind,
            _ => throw new RulesException(spell.Source, spell.Line, $"{Where("effect")}: unknown effect '{effectText}'"),
        };
        SpellTarget target = (Str("target") ?? DefaultTarget(effect)) switch
        {
            "unit" => SpellTarget.Unit,
            "ground" => SpellTarget.Ground,
            "self" => SpellTarget.Self,
            { } other => throw new RulesException(spell.Source, spell.Line, $"{Where("target")}: '{other}' is not unit/ground/self"),
        };
        SpellAffects affects = (Str("affects") ?? DefaultAffects(effect)) switch
        {
            "own" => SpellAffects.Own,
            "enemy" => SpellAffects.Enemy,
            "all" => SpellAffects.All,
            { } other => throw new RulesException(spell.Source, spell.Line, $"{Where("affects")}: '{other}' is not own/enemy/all"),
        };
        int summon = -1;
        if (Str("summon") is { } summonKey)
        {
            summon = types.FindIndex(t => t.Key == summonKey);
            if (summon < 0)
            {
                throw new RulesException(spell.Source, spell.Line, $"{Where("summon")}: unknown entity '{summonKey}'");
            }
        }

        int projectile = -1;
        if (Str("projectile") is { } projectileKey)
        {
            projectile = ProjectileId(projectileKey) ?? throw new RulesException(spell.Source, spell.Line, $"{Where("projectile")}: unknown projectile '{projectileKey}'");
        }

        ImmutableArray<ushort> targets = [];
        if (spell.Get("targets") is { } filter)
        {
            YamlMapping m = RulesetCompiler.Map(filter.Node, filter.Source, Where("targets"));
            targets = research.Filter(e.Key, m, filter.Source);
            if (targets.IsEmpty)
            {
                _diagnostics.Add($"{e.Key}: its targets match no type; it affects nothing");
            }
        }

        if (effect is SpellEffect.Summon or SpellEffect.RaiseDead or SpellEffect.Reveal or SpellEffect.SoulBind && summon < 0)
        {
            throw new RulesException(spell.Source, spell.Line, $"{e.Key}: a {effectText} spell needs Spell.summon");
        }

        return new SpellDefinition(
            id,
            e.Key,
            effect,
            target,
            affects,
            Int("mana", 0, 1_000_000),
            Int("range", 0, 1024) * UnitsPerTile, // magic vision reaches anywhere (1024 tiles)
            Int("cooldownMs", 0, 3_600_000),
            Int("castMs", 0, 60_000),
            Int("amount", 0, 1_000_000),
            Int("durationMs", 0, 3_600_000),
            Int("radius", 0, 32) * UnitsPerTile,
            summon,
            projectile,
            targets)
        {
            PulseMs = Math.Max(1, Int("pulseMs", 1000, 60_000)),
            Count = Math.Clamp(Int("count", 1, 64), 1, 64),
            BookValue = Int("bookValue", 0, 1_000_000),
            DurationMaxMs = Int("durationMaxMs", 0, 3_600_000),
            Square = Int("square", 0, 1) == 1,
            ScatterUnits = Int("scatter", 0, 32) * UnitsPerTile,
            DelayMs = Int("delayMs", 0, 60_000),
            MoveMs = Int("moveMs", 0, 60_000),
        };
    }

    private static string DefaultTarget(SpellEffect effect) => effect switch
    {
        SpellEffect.Summon or SpellEffect.RaiseDead or SpellEffect.Reveal or SpellEffect.AreaDamage => "ground",
        _ => "unit",
    };

    private static string DefaultAffects(SpellEffect effect) => effect switch
    {
        SpellEffect.Heal or SpellEffect.Bless or SpellEffect.Cloak or SpellEffect.Experience => "own",
        SpellEffect.AreaDamage => "all",
        _ => "enemy",
    };

    // A type filter (classes, civ...) of the combat settings: combat.tournamentPupils, combat.magicAttackers.
    private ImmutableArray<ushort> Filter(ResearchCompiler research, string field)
    {
        if (_settings.Field(Section, field) is not { } node)
        {
            return [];
        }

        string source = _settings.FieldSource(Section, field);
        return research.Filter("combat." + field, RulesetCompiler.Map(node, source, "combat." + field), source);
    }

    // combat.crusadeGather: { from minute: every seconds }, ascending from 0; absent: the original's steps.
    private ImmutableArray<CrusadeGatherStep> Gather(ImmutableArray<CrusadeGatherStep> fallback)
    {
        if (_settings.Field(Section, "crusadeGather") is not { } node)
        {
            return fallback;
        }

        string source = _settings.FieldSource(Section, "crusadeGather");
        var list = new List<CrusadeGatherStep>();
        foreach (KeyValuePair<string, YamlNode> e in RulesetCompiler.Map(node, source, "combat.crusadeGather").Entries)
        {
            int every = RulesetCompiler.Int(e.Value, source, "combat.crusadeGather." + e.Key);
            int first = list.Count == 0 ? 0 : list[^1].FromMinute + 1;
            if (!int.TryParse(e.Key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int minute)
                || (list.Count == 0 ? minute != 0 : minute < first) || every is < 1 or > 3600)
            {
                throw new RulesException(source, e.Value.Line, "combat.crusadeGather: ascending minutes from 0, each with 1..3600 seconds");
            }

            list.Add(new CrusadeGatherStep(minute, every));
        }

        return [.. list];
    }

    // combat.experienceSpeeds: { technology: percent }, in file order.
    private ImmutableArray<ExperienceSpeed> Speeds(ResearchIds research)
    {
        if (_settings.Field(Section, "experienceSpeeds") is not { } node)
        {
            return [];
        }

        string source = _settings.FieldSource(Section, "experienceSpeeds");
        var list = new List<ExperienceSpeed>();
        foreach (KeyValuePair<string, YamlNode> e in RulesetCompiler.Map(node, source, "combat.experienceSpeeds").Entries)
        {
            int percent = RulesetCompiler.Int(e.Value, source, "combat.experienceSpeeds." + e.Key);
            if (research.Technology(e.Key) is { } id)
            {
                list.Add(new ExperienceSpeed(id, percent is >= 1 and <= 1000 ? percent : throw new RulesException(source, e.Value.Line, $"combat.experienceSpeeds.{e.Key} must be 1..1000")));
            }
            else
            {
                _diagnostics.Add($"combat.experienceSpeeds: unknown technology '{e.Key}'");
            }
        }

        return [.. list];
    }

    // combat.marketOffers: a list of kinds, each a list of "item effect look min-max" (a book: "item spells look").
    private ImmutableArray<ImmutableArray<MarketVariant>> Offers(List<EntityTypeDefinition> types)
    {
        if (_settings.Field(Section, "marketOffers") is not { } node)
        {
            return [];
        }

        string source = _settings.FieldSource(Section, "marketOffers");
        var kinds = ImmutableArray.CreateBuilder<ImmutableArray<MarketVariant>>();
        foreach (YamlNode kindNode in RulesetCompiler.Seq(node, source, "combat.marketOffers").Items)
        {
            var variants = ImmutableArray.CreateBuilder<MarketVariant>();
            foreach (string text in RulesDocument.StringList(kindNode, source, "combat.marketOffers[]"))
            {
                string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int item = parts.Length > 0 ? types.FindIndex(t => t.Key == parts[0]) : -1;
                if (parts.Length is < 3 or > 4 || item < 0 || types[item].Traits.Item is not { } def)
                {
                    throw new RulesException(source, kindNode.Line, $"combat.marketOffers: '{text}' is not 'item effect look min-max' of an item");
                }

                int look = def.Looks.IndexOf(parts[2]) + 1;
                if (look == 0)
                {
                    throw new RulesException(source, kindNode.Line, $"combat.marketOffers: '{parts[2]}' is not a look of {parts[0]}");
                }

                if (parts[1] == "spells")
                {
                    variants.Add(new MarketVariant((ushort)item, ItemEffect.Default, look, 0, 0, true));
                    continue;
                }

                ItemEffect effect = parts[1] switch
                {
                    "attack" => ItemEffect.Attack,
                    "defence" => ItemEffect.Defence,
                    "health" => ItemEffect.Health,
                    "levels" => ItemEffect.Levels,
                    "mana" => ItemEffect.Mana,
                    _ => throw new RulesException(source, kindNode.Line, $"combat.marketOffers: unknown effect '{parts[1]}'"),
                };
                string[] range = parts.Length == 4 ? parts[3].Split('-') : ["1", "1"];
                if (range.Length != 2 || !int.TryParse(range[0], System.Globalization.CultureInfo.InvariantCulture, out int min)
                    || !int.TryParse(range[1], System.Globalization.CultureInfo.InvariantCulture, out int max) || min < 0 || max < min)
                {
                    throw new RulesException(source, kindNode.Line, $"combat.marketOffers: '{text}' has no 'min-max'");
                }

                variants.Add(new MarketVariant((ushort)item, effect, look, min, max, false));
            }

            kinds.Add(variants.ToImmutable());
        }

        return kinds.ToImmutable();
    }

    private ImmutableArray<ushort> BookSpells(List<SpellDefinition> spells)
    {
        if (_settings.Field(Section, "marketBookSpells") is not { } node)
        {
            return [];
        }

        string source = _settings.FieldSource(Section, "marketBookSpells");
        var ids = ImmutableArray.CreateBuilder<ushort>();
        foreach (string key in RulesDocument.StringList(node, source, "combat.marketBookSpells"))
        {
            int id = spells.FindIndex(s => s.Key == key);
            if (id < 0)
            {
                throw new RulesException(source, node.Line, $"combat.marketBookSpells: unknown spell '{key}'");
            }

            ids.Add((ushort)id);
        }

        return ids.ToImmutable();
    }

    private int SpellOf(string field, List<SpellDefinition> spells)
    {
        if (_settings.Scalar(Section, field) is not { } key)
        {
            return -1;
        }

        int id = spells.FindIndex(s => s.Key == key);
        if (id < 0)
        {
            _diagnostics.Add($"combat.{field}: unknown spell '{key}'");
        }

        return id;
    }

    private int Range(string field, int fallback, int min, int max)
    {
        int value = _settings.Int(Section, field) ?? fallback;
        return value >= min && value <= max
            ? value
            : throw new RulesException(_settings.FieldSource(Section, field), _settings.Field(Section, field)?.Line ?? 0, $"combat.{field} must be {min}..{max}");
    }

    private int Technology(string field, ResearchIds research)
    {
        if (_settings.Scalar(Section, field) is not { } key)
        {
            return -1;
        }

        if (research.Technology(key) is { } id)
        {
            return id;
        }

        _diagnostics.Add($"combat.{field}: unknown technology '{key}'; the effect never applies");
        return -1;
    }

    private int Type(string field, List<EntityTypeDefinition> types)
    {
        if (_settings.Scalar(Section, field) is not { } key)
        {
            return -1;
        }

        int i = types.FindIndex(t => t.Key == key);
        if (i < 0)
        {
            _diagnostics.Add($"combat.{field}: unknown entity '{key}'");
        }

        return i;
    }
}
