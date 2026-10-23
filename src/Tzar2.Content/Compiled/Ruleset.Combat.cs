using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Tzar2.Core.Hashing;

namespace Tzar2.Content.Compiled;

/// <summary>The compiled content of GAME-07…11 that is not per entity type: settings, spells and projectiles.</summary>
/// <param name="Settings">Ruleset-wide constants (section <c>combat:</c>).</param>
/// <param name="Spells">Spells by id.</param>
/// <param name="Projectiles">Projectiles by id.</param>
public sealed record CombatRules(CombatSettings Settings, IReadOnlyList<SpellDefinition> Spells, IReadOnlyList<ProjectileDefinition> Projectiles)
{
    /// <summary>No spells, no projectiles, default settings.</summary>
    public static CombatRules Empty { get; } = new(CombatSettings.Default, [], []);
}

/// <content>Combat, experience, holders, items and magic (GAME-07…11; ADRs 0090-0095).</content>
public sealed partial class Ruleset
{
    /// <summary>Most spells (casters store spell ids in 16 bits; items one byte of flags).</summary>
    public const int MaxSpells = 1024;

    /// <summary>Most projectile kinds.</summary>
    public const int MaxProjectiles = 64;

    private SpellDefinition[] _spells = [];
    private ProjectileDefinition[] _projectiles = [];

    /// <summary>Combat, experience, holder, item and magic constants.</summary>
    public CombatSettings Combat { get; private set; } = CombatSettings.Default;

    /// <summary>Spells by id (GAME-11).</summary>
    public IReadOnlyList<SpellDefinition> Spells => _spells;

    /// <summary>Projectiles by id (GAME-07).</summary>
    public IReadOnlyList<ProjectileDefinition> Projectiles => _projectiles;

    /// <summary>The id of spell <paramref name="key"/>, or -1.</summary>
    public int SpellId(string key)
    {
        for (int i = 0; i < _spells.Length; i++)
        {
            if (string.Equals(_spells[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    // Called by the constructor once the entity types, resources and technologies are set, before ValidateTraits.
    private void InitCombat(CombatRules? combat)
    {
        combat ??= CombatRules.Empty;
        Combat = combat.Settings ?? throw new ArgumentException("Combat settings must not be null.", nameof(combat));
        Combat.Validate(_entityTypes.Length, _technologies.Length);
        _spells = [.. combat.Spells ?? []];
        _projectiles = [.. combat.Projectiles ?? []];
        if (Combat.BansheeSpell >= _spells.Length || Combat.SoulBerserkSpell >= _spells.Length)
        {
            throw new ArgumentException("combat: the banshee spell must be a spell.", nameof(combat));
        }

        if (_spells.Length > MaxSpells || _projectiles.Length > MaxProjectiles)
        {
            throw new ArgumentException($"At most {MaxSpells} spells and {MaxProjectiles} projectiles.", nameof(combat));
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _projectiles.Length; i++)
        {
            ProjectileDefinition p = _projectiles[i] ?? throw new ArgumentException("Null projectile.", nameof(combat));
            if (p.Id != i || string.IsNullOrEmpty(p.Key) || !keys.Add(p.Key) || p.SpeedUnitsPerSecond is < 1 or > 1_000_000
                || p.HitRadiusUnits is < 0 or > 16 * 2048 || p.SplashRadiusUnits is < 0 or > 16 * 2048 || p.SplashPercent is < 0 or > 1000)
            {
                throw new ArgumentException($"Projectile '{p.Key}' must have id {i}, a unique key and values in range.", nameof(combat));
            }
        }

        keys.Clear();
        for (int i = 0; i < _spells.Length; i++)
        {
            SpellDefinition s = _spells[i] ?? throw new ArgumentException("Null spell.", nameof(combat));
            if (s.Id != i || string.IsNullOrEmpty(s.Key) || !keys.Add(s.Key) || s.Effect > SpellEffect.SoulBind || s.Target > SpellTarget.Self
                || s.Affects > SpellAffects.All || s.ManaCost is < 0 or > 1_000_000 || s.RangeUnits is < 0 or > 1024 * 2048
                || s.CooldownMs is < 0 or > 3_600_000 || s.CastMs is < 0 or > 60_000 || s.Amount is < 0 or > 1_000_000
                || s.DurationMs is < 0 or > 3_600_000 || s.RadiusUnits is < 0 or > 32 * 2048 || s.Summon < -1 || s.Summon >= _entityTypes.Length
                || s.Projectile < -1 || s.Projectile >= _projectiles.Length || s.PulseMs is < 1 or > 60_000 || s.Count is < 1 or > 64
                || (s.Effect is SpellEffect.Summon or SpellEffect.RaiseDead or SpellEffect.Reveal && s.Summon < 0))
            {
                throw new ArgumentException($"Spell '{s.Key}' must have id {i}, a unique key and values in range.", nameof(combat));
            }

            if (s.Summon >= 0 && (_entityTypes[s.Summon].HasFootprint || (s.Effect is not (SpellEffect.Reveal or SpellEffect.SoulBind) && !_entityTypes[s.Summon].IsMobile) || (s.Effect == SpellEffect.SoulBind && _entityTypes[s.Summon].Traits.Item is not { Kind: ItemKind.Soul })))
            {
                throw new ArgumentException($"Spell '{s.Key}' summons '{_entityTypes[s.Summon].Key}', which is not a unit.", nameof(combat));
            }

            CheckAscendingIds(s.TargetTypes, _entityTypes.Length, $"Spell '{s.Key}': target types");
        }

        foreach (EntityTypeDefinition e in _entityTypes)
        {
            ValidateCombatTraits(e);
        }
    }

    private void ValidateCombatTraits(EntityTypeDefinition e)
    {
        EntityTraits t = e.Traits;
        string where = $"Entity type '{e.Key}'";
        if (t.Armament is { } arm && arm.Projectile >= _projectiles.Length)
        {
            throw new ArgumentException($"{where}: unknown projectile.");
        }

        t.Holder?.Validate(_entityTypes.Length, where);
        t.Item?.Validate(_entityTypes.Length, _resourceTypes.Length, _spells.Length, where);
        ArgumentOutOfRangeException.ThrowIfNegative(t.InventorySlots, where);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(t.InventorySlots, InventoryLimit, where);
        CheckAscendingIds(t.Spells, _spells.Length, $"{where}: spells");
        if ((t.Holder is not null || t.InventorySlots > 0 || !t.Spells.IsEmpty || t.GainsExperience) && !e.IsMobile && !e.HasFootprint)
        {
            throw new ArgumentException($"{where}: holders, inventories, casters and experience need a unit or a building.");
        }

        if ((t.InventorySlots > 0 || t.GainsExperience) && !e.IsMobile)
        {
            throw new ArgumentException($"{where}: inventories and experience are for units.");
        }

        if (t.Item is not null && (e.IsMobile || e.HasFootprint))
        {
            throw new ArgumentException($"{where}: an item has neither speed nor a footprint.");
        }

        CheckAscendingIds(t.NotImpersonated, _entityTypes.Length, $"{where}: not impersonated");
        if (t.ConvertsFrom is { } c)
        {
            CheckAscendingIds(c.From, _entityTypes.Length, $"{where}: converts from");
            if (!e.IsMobile || string.IsNullOrEmpty(c.Order))
            {
                throw new ArgumentException($"{where}: only units convert from units, by an order.");
            }
        }

        if (t.Crafter is { } crafter)
        {
            CheckAscendingIds(crafter.Items, _entityTypes.Length, $"{where}: crafts");
            foreach (ushort id in crafter.Items)
            {
                if (_entityTypes[id].Traits.Item is null)
                {
                    throw new ArgumentException($"{where}: crafts '{_entityTypes[id].Key}', which is not an item.");
                }
            }
        }
    }

    /// <summary>Most inventory slots of one unit.</summary>
    public const int InventoryLimit = 8;

    private void AddCombat(StateHasher hasher)
    {
        CombatSettings c = Combat;
        hasher.Add(c.ExperiencePerDamagePercent).Add(c.ExperiencePerKill).Add(c.LevelBaseXp).Add(c.BaseLevelCap).Add(c.AttackPerLevel)
            .Add(c.DefenceHalvesPerLevel).Add(c.HeroLevel).Add(c.LeadershipTechnology).Add(c.LeadershipRadiusUnits).Add(c.LeadershipSharePercent)
            .Add(c.HonorTechnology).Add(c.HonorAttackPercent).Add(c.DummyMaxLevel).Add(c.ExitRadiusTiles).Add(c.CraftGoldPerPower)
            .Add(c.CraftMsPerPower).Add(c.CraftMaxPower).Add(c.CraftHealthPerPower).Add(c.CraftManaPerPower).Add(c.CraftPowerPerLevel).Add(c.CraftBookCostPercent).Add(c.CraftBookTimePercent).Add(c.BribeCostPercent).Add(c.CrusadeAnchor).Add(c.Crusader).Add(c.CrusadeStart).Add(c.CrusadeRestart).Add(c.CrusadeCeilingStep).Add(c.CrusadeCeilingMax)
            .Add(c.CrusadeAnchorRadiusUnits).Add(c.BansheeSpell).Add(c.BansheeChance).Add(c.BansheeOutOf)
            .Add(c.TournamentTechnology).Add(c.TournamentEvery).Add(c.TournamentGrant).AddSpan<ushort>(c.TournamentPupils.AsSpan())
            .AddSpan<ushort>(c.MagicAttackers.AsSpan()).Add(c.SoulBerserkSpell)
            .Add(c.ExperienceSpeeds.Length).Add(c.DummyDamagePerPoint)
            .Add(c.MarketTechnology).Add(c.MarketFirstMs).Add(c.MarketEveryMs).Add(c.MarketSize).Add(c.MarketBuyPercent)
            .AddSpan<ushort>(c.MarketBookSpells.AsSpan()).Add(c.MarketOffers.Length).AddSpan<ushort>(c.LevelCapBuildings.AsSpan()).Add(c.LevelCapPerBuilding).Add(c.HeroNames.Length);
        foreach (ImmutableArray<MarketVariant> kind in c.MarketOffers)
        {
            hasher.Add(kind.Length);
            foreach (MarketVariant v in kind)
            {
                hasher.Add(v.Item).Add((byte)v.Effect).Add(v.Look).Add(v.Min).Add(v.Max).Add(v.Book);
            }
        }

        foreach (ExperienceSpeed speed in c.ExperienceSpeeds)
        {
            hasher.Add(speed.Technology).Add(speed.Percent);
        }

        foreach (HeroNames h in c.HeroNames)
        {
            hasher.Add(Encoding.UTF8.GetBytes(h.Civilization)).Add(h.Names.Length);
            foreach (string name in h.Names)
            {
                hasher.Add(Encoding.UTF8.GetBytes(name));
            }
        }

        hasher.Add(_projectiles.Length);
        foreach (ProjectileDefinition p in _projectiles)
        {
            hasher.Add(Encoding.UTF8.GetBytes(p.Key)).Add(p.SpeedUnitsPerSecond).Add(p.Homing).Add(p.HitRadiusUnits).Add(p.SplashRadiusUnits)
                .Add(p.SplashPercent).Add(p.FriendlyFire);
        }

        hasher.Add(_spells.Length);
        foreach (SpellDefinition s in _spells)
        {
            hasher.Add(Encoding.UTF8.GetBytes(s.Key)).Add((byte)s.Effect).Add((byte)s.Target).Add((byte)s.Affects).Add(s.ManaCost).Add(s.RangeUnits)
                .Add(s.CooldownMs).Add(s.CastMs).Add(s.Amount).Add(s.DurationMs).Add(s.RadiusUnits).Add(s.Summon).Add(s.Projectile)
                .Add(s.PulseMs).Add(s.Count).AddSpan<ushort>(s.TargetTypes.AsSpan()).Add(s.BookValue).Add(s.DurationMaxMs);
        }
    }

    private static void AddCombatTraits(StateHasher hasher, EntityTraits t)
    {
        hasher.Add(t.Armament?.MinRangeUnits ?? 0).Add(t.Armament?.MaxAttack ?? 0).Add(t.Armament?.Projectile ?? -1)
            .Add(t.InventorySlots).AddSpan<ushort>(t.Spells.AsSpan()).Add(t.GainsExperience).Add((byte)t.Abilities);
        hasher.Add(t.Holder is not null);
        if (t.Holder is { } h)
        {
            hasher.Add(h.Capacity).AddSpan<ushort>(h.Accepts.AsSpan()).Add(h.HealPerMinute).Add(h.Damage.Length);
            foreach (DamageShare s in h.Damage)
            {
                hasher.Add(s.TakePercent).Add(s.DistributePercent);
            }
        }

        hasher.Add(t.Item is not null);
        if (t.Item is { } i)
        {
            hasher.Add((byte)i.Kind).AddSpan<ushort>(i.Holders.AsSpan()).Add(i.Power).Add(i.Resource).Add(i.Spell).Add(i.Charges).Add(i.CooldownMs)
                .Add(i.NeedsMana).Add(i.PotionLevel).Add(i.PotionAttack).Add(i.PotionDefence).Add(i.PotionHealth).Add(i.PotionMana).Add(i.Looks.Length);
            foreach (string look in i.Looks)
            {
                hasher.Add(Encoding.UTF8.GetBytes(look));
            }

            hasher.Add(i.Crafts.Length);
            foreach (CraftVariant v in i.Crafts)
            {
                hasher.Add((byte)v.Effect).Add(v.Spell).Add(v.Look);
            }
        }

        hasher.AddSpan<ushort>(t.NotImpersonated.AsSpan());
        hasher.Add(t.ConvertsFrom is not null);
        if (t.ConvertsFrom is { } c)
        {
            hasher.AddSpan<ushort>(c.From.AsSpan()).Add(Encoding.UTF8.GetBytes(c.Order));
        }

        hasher.Add(t.Crafter is not null);
        if (t.Crafter is { } crafter)
        {
            hasher.AddSpan<ushort>(crafter.Items.AsSpan());
        }
    }
}
