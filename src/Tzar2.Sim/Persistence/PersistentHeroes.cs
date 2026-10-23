using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Components;

namespace Tzar2.Sim.Persistence;

/// <summary>An item a persistent unit carries between campaign maps.</summary>
/// <param name="TypeKey">The item type's stable key (<c>comm.item_magic</c>).</param>
/// <param name="Power">Its power (weapon attack, armor defence, resource amount).</param>
/// <param name="Charges">Spell book charges left.</param>
/// <param name="SpellKey">A book's own spell (ITEM-016), or null for its type's.</param>
/// <param name="Effect">What it adds (ITEM-016).</param>
/// <param name="Look">1 + its image in the type's looks, or 0.</param>
/// <param name="CooldownMs">A spell book's own cooldown (ADR 0265).</param>
/// <param name="Flags">Its own book properties (ADR 0265).</param>
public sealed record PersistentItem(string TypeKey, int Power, int Charges, string? SpellKey = null, ItemEffect Effect = ItemEffect.Default, int Look = 0, int CooldownMs = 0, ItemStateFlags Flags = ItemStateFlags.None);

/// <summary>
/// What a persistent unit carries from one campaign map to the next (editor "Persistent" flag: "the unit properties
/// are saved at the end of each campaign map ... a unit with the same name in one of the following campaign maps" gets
/// them; GAME-08/20; ADR 0092). The campaign layer (M4) stores the records; this is the data model and its format.
/// </summary>
/// <param name="Name">The unit's name (the key that matches it on the next map).</param>
/// <param name="TypeKey">Its type's stable key.</param>
/// <param name="Level">Experience level.</param>
/// <param name="Experience">Experience points.</param>
/// <param name="Hero">Whether it is a named hero.</param>
/// <param name="BonusAttack">Attack added by potions.</param>
/// <param name="BonusDefence">Defence added by potions.</param>
/// <param name="Items">The items it carries, in slot order.</param>
public sealed record PersistentHero(string Name, string TypeKey, int Level, int Experience, bool Hero, int BonusAttack, int BonusDefence, ImmutableArray<PersistentItem> Items);

/// <summary>Captures, applies and serializes <see cref="PersistentHero"/> records (versioned binary, little-endian).</summary>
public static class PersistentHeroes
{
    /// <summary>Format version of <see cref="Write"/>.</summary>
    public const int FormatVersion = 3; // 2: items keep their own spell, effect and look (ITEM-016); 3: a book's own cooldown and endless charges (ADR 0265); 1 and 2 are still read

    /// <summary>Most records in one list (bounds untrusted input).</summary>
    public const int MaxRecords = 4096;

    private const int MaxNameBytes = 256;

    /// <summary>The named units of <paramref name="player"/> (heroes and every other named unit), ascending by entity.</summary>
    public static List<PersistentHero> Capture(World world, int player)
    {
        ArgumentNullException.ThrowIfNull(world);
        var list = new List<PersistentHero>();
        ReadOnlySpan<EntityId> named = world.Names.Entities;
        for (int i = 0; i < named.Length; i++)
        {
            EntityId e = named[i];
            if (world.Identities.IndexOf(e) is not (>= 0 and var p) || world.Identities.At(p).Owner != player || !world.TypeOf(e).IsMobile)
            {
                continue;
            }

            Experience x = world.Experiences.IndexOf(e) is >= 0 and var xi ? world.Experiences.At(xi) : default;
            var items = ImmutableArray.CreateBuilder<PersistentItem>();
            if (world.Inventories.IndexOf(e) is >= 0 and var ii)
            {
                Inventory inventory = world.Inventories.At(ii);
                for (int k = 0; k < inventory.Count; k++)
                {
                    EntityId item = EntityId.FromValue(inventory.Items[k]);
                    ItemState s = world.Items.Get(item);
                    items.Add(new PersistentItem(world.TypeOf(item).Key, s.Power, s.Charges, s.Spell > 0 ? world.Ruleset.Spells[s.Spell - 1].Key : null, s.Effect, s.Look, s.CooldownMs, s.Flags));
                }
            }

            list.Add(new PersistentHero(world.Names.Get(e).Value, world.TypeOf(e).Key, x.Level, x.Xp, (x.Flags & ExperienceFlags.Hero) != 0, x.BonusAttack, x.BonusDefence, items.ToImmutable()));
        }

        return list;
    }

    /// <summary>
    /// Gives a unit placed at match setup the properties of a record (level, experience, hero, potions, name, items).
    /// Items of unknown types are skipped.
    /// </summary>
    /// <exception cref="InvalidOperationException">The world is locked (only the match setup may do this).</exception>
    public static void Apply(World world, EntityId unit, PersistentHero hero)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(hero);
        if (world.IsLocked)
        {
            throw new InvalidOperationException("Persistent units are applied at match setup.");
        }

        world.SetCold(unit, new EntityName(hero.Name));
        if (world.Experiences.IndexOf(unit) is >= 0 and var x)
        {
            ref Experience e = ref world.Experiences.At(x);
            e.Level = (ushort)Math.Clamp(hero.Level, 0, 10_000);
            e.Xp = Math.Max(0, hero.Experience);
            e.Flags = hero.Hero && e.Level >= world.Ruleset.Combat.HeroLevel ? ExperienceFlags.Hero : ExperienceFlags.None;
            e.BonusAttack = Math.Clamp(hero.BonusAttack, 0, 100_000);
            e.BonusDefence = Math.Clamp(hero.BonusDefence, 0, 100_000);
            world.RefreshMaxHealth(unit);
            ref Health h = ref world.Healths.Get(unit);
            h.Current = h.Max;
        }

        Position at = world.Positions.Get(unit);
        foreach (PersistentItem item in hero.Items)
        {
            int type = world.Ruleset.EntityTypeId(item.TypeKey);
            if (type < 0 || world.Ruleset.EntityTypes[type].Traits.Item is null || world.Entities.RemainingCapacity < 1)
            {
                continue;
            }

            EntityId e = world.Spawn((ushort)type, MapObject.Nature, at.X, at.Y);
            ref ItemState s = ref world.Items.Get(e);
            s.Power = Math.Clamp(item.Power, 0, 1_000_000);
            s.Charges = Math.Clamp(item.Charges, 0, 1_000_000);
            s.Spell = item.SpellKey is { } spellKey && world.Ruleset.SpellId(spellKey) is >= 0 and var spell ? (ushort)(spell + 1) : (ushort)0;
            s.Effect = item.Effect <= ItemEffect.Mana ? item.Effect : ItemEffect.Default;
            s.Look = (byte)(item.Look >= 0 && item.Look <= world.Ruleset.EntityTypes[type].Traits.Item!.Looks.Length ? item.Look : 0);
            s.CooldownMs = Math.Clamp(item.CooldownMs, 0, 3_600_000);
            s.Flags = item.Flags & (ItemStateFlags.OwnCooldown | ItemStateFlags.Endless);
            if (Systems.CombatMagicCommands.CanCarry(world, unit, e))
            {
                world.PutInInventory(unit, e);
            }
        }
    }

    /// <summary>Writes records.</summary>
    public static void Write(BinaryBufferWriter writer, IReadOnlyList<PersistentHero> heroes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(heroes);
        writer.WriteVarUInt64(FormatVersion);
        writer.WriteVarUInt64((ulong)heroes.Count);
        foreach (PersistentHero h in heroes)
        {
            writer.WriteString(h.Name);
            writer.WriteString(h.TypeKey);
            writer.WriteVarInt64(h.Level);
            writer.WriteVarInt64(h.Experience);
            writer.WriteBoolean(h.Hero);
            writer.WriteVarInt64(h.BonusAttack);
            writer.WriteVarInt64(h.BonusDefence);
            writer.WriteVarUInt64((ulong)h.Items.Length);
            foreach (PersistentItem item in h.Items)
            {
                writer.WriteString(item.TypeKey);
                writer.WriteVarInt64(item.Power);
                writer.WriteVarInt64(item.Charges);
                writer.WriteString(item.SpellKey ?? string.Empty);
                writer.WriteVarInt64((long)item.Effect);
                writer.WriteVarInt64(item.Look);
                writer.WriteVarInt64(item.CooldownMs);
                writer.WriteVarInt64((long)item.Flags);
            }
        }
    }

    /// <summary>Reads records written by <see cref="Write"/> (untrusted: bounded and range-checked).</summary>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    public static List<PersistentHero> Read(ReadOnlySpan<byte> data)
    {
        var reader = new BinaryBufferReader(data);
        ulong version = reader.ReadVarUInt64();
        if (version is not (1 or 2 or FormatVersion))
        {
            throw new InvalidDataException("Unsupported persistent hero format.");
        }

        ulong count = reader.ReadVarUInt64();
        if (count > MaxRecords)
        {
            throw new InvalidDataException("Too many persistent heroes.");
        }

        var list = new List<PersistentHero>((int)count);
        for (ulong i = 0; i < count; i++)
        {
            string name = reader.ReadString(MaxNameBytes);
            string type = reader.ReadString(MaxNameBytes);
            int level = Bounded(reader.ReadVarInt64(), 10_000);
            int xp = Bounded(reader.ReadVarInt64(), int.MaxValue);
            bool hero = reader.ReadBoolean();
            int attack = Bounded(reader.ReadVarInt64(), 100_000);
            int defence = Bounded(reader.ReadVarInt64(), 100_000);
            ulong items = reader.ReadVarUInt64();
            if (items > (ulong)Ruleset.InventoryLimit)
            {
                throw new InvalidDataException("Too many persistent items.");
            }

            var carried = ImmutableArray.CreateBuilder<PersistentItem>((int)items);
            for (ulong k = 0; k < items; k++)
            {
                var item = new PersistentItem(reader.ReadString(MaxNameBytes), Bounded(reader.ReadVarInt64(), 1_000_000), Bounded(reader.ReadVarInt64(), 1_000_000));
                if (version >= 2)
                {
                    string spell = reader.ReadString(MaxNameBytes);
                    item = item with { SpellKey = spell.Length > 0 ? spell : null, Effect = (ItemEffect)Bounded(reader.ReadVarInt64(), (int)ItemEffect.Mana), Look = Bounded(reader.ReadVarInt64(), 255) };
                }

                if (version >= 3)
                {
                    item = item with { CooldownMs = Bounded(reader.ReadVarInt64(), 3_600_000), Flags = (ItemStateFlags)Bounded(reader.ReadVarInt64(), (int)(ItemStateFlags.OwnCooldown | ItemStateFlags.Endless)) };
                }

                carried.Add(item);
            }

            list.Add(new PersistentHero(name, type, level, xp, hero, attack, defence, carried.MoveToImmutable()));
        }

        if (!reader.IsAtEnd)
        {
            throw new InvalidDataException("Trailing bytes after the persistent heroes.");
        }

        return list;

        static int Bounded(long value, int max) => value >= 0 && value <= max ? (int)value : throw new InvalidDataException($"Persistent value {value} is out of range.");
    }
}
