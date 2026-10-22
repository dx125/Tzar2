using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Rules;

/// <content>
/// The traits of GAME-07…11 (ADRs 0090-0095): armament extras (minimum range, maximum damage, projectile), <c>Holder</c>,
/// <c>Inventory</c>, <c>Item</c>, <c>Spellcaster</c>, <c>Experience</c>, abilities, <c>ConvertsFrom</c>,
/// <c>ItemCrafter</c>. Filters that name groups of units (holder accepts, item holders, conversion sources) need the
/// compiled types; they are resolved afterwards by <see cref="CombatCompiler"/> from <see cref="PendingSpecs"/>.
/// </content>
internal sealed partial class EntityCompiler
{
    private const int UnitsPerTileCombat = 2048;

    private static readonly Dictionary<string, string[]> CombatCatalogue = new(StringComparer.Ordinal)
    {
        // Lookup only, never iterated (T2D004).
        ["Holder"] = ["capacity", "accepts", "damage", "healPerMinute"],
        ["Inventory"] = ["slots"],
        ["Item"] = ["kind", "holders", "power", "resource", "spell", "charges", "cooldownMs", "needsMana", "level", "attack", "defence", "health", "mana", "looks", "crafts"],
        ["Spellcaster"] = ["spells"],
        ["Experience"] = [],
        ["Briber"] = [],
        ["Impersonator"] = ["never", "except"],
        ["BribeImmune"] = [],
        ["CrusadePreacher"] = [],
        ["TrainingDummy"] = [],
        ["KillImmune"] = [],
        ["ConvertsFrom"] = ["classes", "order"],
        ["ItemCrafter"] = ["items"],
    };

    private static readonly string[] DamageClassKeys = ["sword", "arrow", "cannon"];

    /// <summary>Spell ids by key (entries with a <c>Spell</c> trait, sorted by key); set before <see cref="CompileAll"/>.</summary>
    public Func<string, ushort?> SpellIds { get; set; } = static _ => null;

    /// <summary>Projectile ids by key (section <c>combat.projectiles</c>); set before <see cref="CompileAll"/>.</summary>
    public Func<string, int?> ProjectileIds { get; set; } = static _ => null;

    /// <summary>Unit filters of each compiled type that the combat compiler resolves once every type is known.</summary>
    public List<(int Type, CombatSpecs Specs)> PendingSpecs { get; } = [];

    private void ReadCombatTraits(ResolvedEntry entry, Draft d)
    {
        string key = entry.Key;
        string Where(string trait, string field) => $"{key}.{trait}.{field}";
        int? IntOf(string trait, string field) => entry.Find(trait)?.Get(field) is { } v ? RulesetCompiler.Int(v.Node, v.Source, Where(trait, field)) : null;
        string? StrOf(string trait, string field) => entry.Find(trait)?.Get(field) is { } v ? RulesetCompiler.Str(v.Node, v.Source, Where(trait, field)) : null;
        int NonNegative(string trait, string field, int fallback, int max)
        {
            int value = IntOf(trait, field) ?? fallback;
            return value >= 0 && value <= max ? value : throw Error(entry.Find(trait)!.Get(field)!.Value, $"{Where(trait, field)} must be 0..{max}");
        }

        var specs = new CombatSpecs();
        if (d.Armament is not null && entry.Find("Armament") is { } arm)
        {
            d.MinRangeUnits = NonNegative("Armament", "minRange", 0, 64) * UnitsPerTileCombat;
            d.MaxAttack = NonNegative("Armament", "maxAttack", 0, 1_000_000);
            if (arm.Get("projectile") is { } p)
            {
                string name = RulesetCompiler.Str(p.Node, p.Source, Where("Armament", "projectile"));
                d.Projectile = name == "none" ? -1 : ProjectileIds(name) ?? throw Error(p, $"{Where("Armament", "projectile")}: unknown projectile '{name}' (combat.projectiles)");
            }
        }

        if (entry.Find("Holder") is { } holder)
        {
            var shares = new DamageShare[3] { DamageShare.Default, DamageShare.Default, DamageShare.Default };
            if (holder.Get("damage") is { } dmg)
            {
                foreach (KeyValuePair<string, YamlNode> f in RulesetCompiler.Map(dmg.Node, dmg.Source, Where("Holder", "damage")).Entries)
                {
                    int cls = Array.IndexOf(DamageClassKeys, f.Key);
                    List<string> pair = RulesDocument.StringList(f.Value, dmg.Source, $"{Where("Holder", "damage")}.{f.Key}");
                    if (cls < 0 || pair.Count != 2)
                    {
                        throw new RulesException(dmg.Source, f.Value.Line, $"{Where("Holder", "damage")}: expected sword/arrow/cannon: [take%, distribute%]");
                    }

                    shares[cls] = new DamageShare(Percent(pair[0], dmg), Percent(pair[1], dmg));
                }
            }

            d.Holder = new HolderDefinition(Math.Max(1, NonNegative("Holder", "capacity", 1, HolderDefinition.MaxCapacity)), [], [.. shares], NonNegative("Holder", "healPerMinute", 0, 1_000_000));
            if (holder.Get("accepts") is { } accepts)
            {
                specs.HolderAccepts = (RulesDocument.StringList(accepts.Node, accepts.Source, Where("Holder", "accepts")), accepts);
            }
        }

        if (entry.Find("Inventory") is not null)
        {
            d.InventorySlots = NonNegative("Inventory", "slots", 0, Ruleset.InventoryLimit);
        }

        if (entry.Find("Item") is { } item)
        {
            string kindText = StrOf("Item", "kind") ?? throw new RulesException(item.Source, item.Line, $"{key}.Item needs a kind");
            ItemKind kind = kindText switch
            {
                "weapon" => ItemKind.Weapon,
                "armor" => ItemKind.Armor,
                "spellbook" => ItemKind.SpellBook,
                "scroll" => ItemKind.Scroll,
                "potion" => ItemKind.Potion,
                "resource" => ItemKind.Resource,
                "flag" => ItemKind.Flag,
                "soul" => ItemKind.Soul,
                "plusplus" => ItemKind.PlusPlus,
                _ => throw Error(item.Get("kind")!.Value, $"{Where("Item", "kind")}: '{kindText}' is not weapon/armor/spellbook/scroll/potion/resource/flag/soul/plusplus"),
            };
            int resource = -1;
            if (StrOf("Item", "resource") is { } r)
            {
                resource = _resources.IndexOf(r);
                if (resource < 0)
                {
                    throw Error(item.Get("resource")!.Value, $"{Where("Item", "resource")}: unknown resource '{r}'");
                }
            }

            int spell = -1;
            if (StrOf("Item", "spell") is { } s)
            {
                spell = SpellIds(s) ?? throw Error(item.Get("spell")!.Value, $"{Where("Item", "spell")}: unknown spell '{s}'");
            }

            d.Item = new ItemDefinition(
                kind,
                [],
                NonNegative("Item", "power", 0, 1_000_000),
                resource,
                spell,
                NonNegative("Item", "charges", 0, 1_000_000),
                NonNegative("Item", "cooldownMs", 0, 3_600_000),
                item.Get("needsMana") is { } nm && RulesetCompiler.Bool(nm.Node, nm.Source, Where("Item", "needsMana")))
            {
                PotionLevel = NonNegative("Item", "level", 0, 1000),
                PotionAttack = NonNegative("Item", "attack", 0, 100_000),
                PotionDefence = NonNegative("Item", "defence", 0, 100_000),
                PotionHealth = NonNegative("Item", "health", 0, 1_000_000),
                PotionMana = NonNegative("Item", "mana", 0, 1_000_000),
            };
            if (item.Get("looks") is { } looks)
            {
                d.Item = d.Item with { Looks = [.. RulesDocument.StringList(looks.Node, looks.Source, Where("Item", "looks"))] };
            }

            // crafts: "effect [look]" or "spell [look]" (ITEM-016): what a crafter makes of the type.
            if (item.Get("crafts") is { } craftList)
            {
                var variants = ImmutableArray.CreateBuilder<CraftVariant>();
                foreach (string text in RulesDocument.StringList(craftList.Node, craftList.Source, Where("Item", "crafts")))
                {
                    string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length is < 1 or > 2)
                    {
                        throw Error(craftList, $"{Where("Item", "crafts")}: '{text}' is not 'effect [look]' or 'spell [look]'");
                    }

                    ItemEffect effect = parts[0] switch
                    {
                        "attack" => ItemEffect.Attack,
                        "defence" => ItemEffect.Defence,
                        "health" => ItemEffect.Health,
                        "levels" => ItemEffect.Levels,
                        "mana" => ItemEffect.Mana,
                        _ => ItemEffect.Default,
                    };
                    int craftSpell = -1;
                    if (effect == ItemEffect.Default)
                    {
                        craftSpell = SpellIds(parts[0]) ?? throw Error(craftList, $"{Where("Item", "crafts")}: '{parts[0]}' is neither an effect (attack, defence, health, levels, mana) nor a spell");
                    }

                    int look = parts.Length < 2 ? 0 : d.Item.Looks.IndexOf(parts[1]) + 1;
                    if (look == 0 && parts.Length == 2)
                    {
                        throw Error(craftList, $"{Where("Item", "crafts")}: look '{parts[1]}' is not in looks");
                    }

                    variants.Add(new CraftVariant(effect, craftSpell, look));
                }

                d.Item = d.Item with { Crafts = variants.ToImmutable() };
            }
            if (item.Get("holders") is { } holders)
            {
                specs.ItemHolders = (RulesDocument.StringList(holders.Node, holders.Source, Where("Item", "holders")), holders);
            }
        }

        if (entry.Find("Spellcaster")?.Get("spells") is { } spells)
        {
            var ids = new SortedSet<ushort>();
            foreach (string s in RulesDocument.StringList(spells.Node, spells.Source, Where("Spellcaster", "spells")))
            {
                if (SpellIds(s) is { } id)
                {
                    ids.Add(id);
                }
                else
                {
                    // A command card names a spell order the rules do not define: the caster cannot cast it.
                    _diagnostics.Add($"{Where("Spellcaster", "spells")}: no spell '{s}' in the rules; it is left out ({spells.Source})");
                }
            }

            d.Spells = [.. ids];
        }

        d.GainsExperience = entry.Find("Experience") is not null;
        d.Abilities = (entry.Find("Briber") is not null ? AbilityFlags.Bribe : 0)
            | (entry.Find("BribeImmune") is not null ? AbilityFlags.BribeImmune : 0)
            | (entry.Find("CrusadePreacher") is not null ? AbilityFlags.PreachCrusade : 0)
            | (entry.Find("TrainingDummy") is not null ? AbilityFlags.TrainingDummy : 0)
            | (entry.Find("KillImmune") is not null ? AbilityFlags.KillImmune : 0)
            | (entry.Find("Impersonator") is not null ? AbilityFlags.Impersonate : 0);
        if (entry.Find("Impersonator") is { } impersonator)
        {
            if (impersonator.Get("never") is { } never)
            {
                specs.ImpersonateNever = (RulesDocument.StringList(never.Node, never.Source, Where("Impersonator", "never")), never);
            }

            if (impersonator.Get("except") is { } except)
            {
                specs.ImpersonateExcept = (RulesDocument.StringList(except.Node, except.Source, Where("Impersonator", "except")), except);
            }
        }

        if (entry.Find("ConvertsFrom") is { } convert)
        {
            string order = StrOf("ConvertsFrom", "order") ?? throw new RulesException(convert.Source, convert.Line, $"{key}.ConvertsFrom needs an order");
            d.ConvertOrder = order;
            if (convert.Get("classes") is { } classes)
            {
                specs.ConvertClasses = (RulesDocument.StringList(classes.Node, classes.Source, Where("ConvertsFrom", "classes")), classes);
            }
        }

        if (entry.Find("ItemCrafter")?.Get("items") is { } crafts)
        {
            foreach (string item2 in RulesDocument.StringList(crafts.Node, crafts.Source, Where("ItemCrafter", "items")))
            {
                d.CraftItems.Add((item2, crafts));
            }

            d.IsCrafter = true;
        }

        d.CombatSpecs = specs;
    }

    private EntityTraits WithCombatTraits(int index, EntityTraits traits, Draft d)
    {
        if (!d.CombatSpecs.IsEmpty)
        {
            PendingSpecs.Add((index, d.CombatSpecs));
        }

        return traits with
        {
            Armament = traits.Armament is { } a ? a with { MinRangeUnits = Math.Min(d.MinRangeUnits, a.RangeUnits), MaxAttack = d.MaxAttack, Projectile = d.Projectile } : null,
            Holder = d.Holder,
            InventorySlots = d.InventorySlots,
            Item = d.Item,
            Spells = d.Spells,
            GainsExperience = d.GainsExperience,
            Abilities = d.Abilities,
            ConvertsFrom = d.ConvertOrder is { } order ? new ConversionDefinition([], order) : null,
            Crafter = d.IsCrafter ? new CrafterDefinition(ResolveList(d.CraftItems, $"{_keys[index]}.ItemCrafter.items")) : null,
        };
    }

    private static int Percent(string text, RuleValue at)
        => int.TryParse(text.TrimEnd('%'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v) && v <= 1000
            ? v
            : throw Error(at, $"'{text}' is not a percentage 0..1000");

    private sealed partial class Draft
    {
        public int MinRangeUnits;
        public int MaxAttack;
        public int Projectile = -1;
        public HolderDefinition? Holder;
        public int InventorySlots;
        public ItemDefinition? Item;
        public ImmutableArray<ushort> Spells = [];
        public bool GainsExperience;
        public AbilityFlags Abilities;
        public string? ConvertOrder;
        public bool IsCrafter;
        public List<(string Key, RuleValue At)> CraftItems = [];
        public CombatSpecs CombatSpecs = new();
    }
}

/// <summary>Unit filters of one type to resolve once all types are compiled (lists of keywords, classes or keys).</summary>
internal sealed class CombatSpecs
{
    public (List<string> Specs, RuleValue At)? HolderAccepts { get; set; }

    public (List<string> Specs, RuleValue At)? ItemHolders { get; set; }

    public (List<string> Specs, RuleValue At)? ConvertClasses { get; set; }

    public (List<string> Specs, RuleValue At)? ImpersonateNever { get; set; }

    public (List<string> Specs, RuleValue At)? ImpersonateExcept { get; set; }

    public bool IsEmpty => HolderAccepts is null && ItemHolders is null && ConvertClasses is null && ImpersonateNever is null;
}
