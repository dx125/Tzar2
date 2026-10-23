using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Components;
using Tzar2.Tests.Scenario;
using Xunit;

namespace Tzar2.Scripting.Tests;

/// <summary>A map's items as the original's maps describe them (ITEM-016, ADR 0186).</summary>
public class MapItemTests
{
    [Fact]
    [Trait("Rule", "ITEM-016")]
    public void Power_items_potions_and_books_take_their_effect_spell_and_look_from_the_map()
    {
        var b = new ScenarioBuilder();
        static ObjectInfo Info(string name, int value = 0, int? attack = null, int? defence = null, int? level = null, int? mana = null, int? magic = null, int? charges = null)
            => new(string.Empty, string.Empty, ObjectFlags.None, Attack: attack, Defence: defence, Level: level, Mana: mana, Item: new ItemInfo(name, value, MagicType: magic, Charges: charges));
        b.Unit("comm.item_plusplus", MapObject.Nature, 2, 2, Info("item_bow2", 1, attack: 14));
        b.Unit("comm.item_plusplus", MapObject.Nature, 4, 2, Info("item_shield1", 2, defence: 8));
        b.Unit("comm.item_health", MapObject.Nature, 6, 2, Info("item_potion3", level: 4));
        b.Unit("comm.item_health", MapObject.Nature, 8, 2, Info("item_potion4", mana: 35));
        b.Unit("comm.item_magic", MapObject.Nature, 10, 2, Info("item_book3", 1, magic: 1, charges: 2));
        using TriggerDriver d = b.Start();
        d.Step();
        World w = d.World;
        ItemState At(int x) => w.Items.Get(w.Items.Entities.ToArray().Single(e => w.Positions.Get(e).X / 2048 == x));
        string? Look(int x)
        {
            EntityId e = w.Items.Entities.ToArray().Single(i => w.Positions.Get(i).X / 2048 == x);
            int look = w.Items.Get(e).Look;
            return look == 0 ? null : w.TypeOf(e).Traits.Item!.Looks[look - 1];
        }

        Assert.Equal((ItemEffect.Attack, 14, "item_bow2"), (At(2).Effect, At(2).Power, Look(2)));
        Assert.Equal((ItemEffect.Defence, 8, "item_shield1"), (At(4).Effect, At(4).Power, Look(4)));
        Assert.Equal((ItemEffect.Levels, 4), (At(6).Effect, At(6).Power));
        Assert.Equal((ItemEffect.Mana, 35), (At(8).Effect, At(8).Power));
        EntityId book = w.Items.Entities.ToArray().Single(i => w.Positions.Get(i).X / 2048 == 10);
        Assert.Equal((w.Ruleset.SpellId("spell_lightning"), 2, "item_book3"), (w.ItemSpell(book), At(10).Charges, Look(10)));
    }

    [Fact]
    [Trait("Rule", "ITEM-018")]
    public void A_books_record_says_whether_it_runs_out_and_how_long_its_pool_takes()
    {
        // ADR 0265: +0x62 needs mana (its own pool of the time restriction), +0x63 limited charges (else endless).
        var b = new ScenarioBuilder();
        static ObjectInfo Book(bool? needsMana, int? seconds, bool? limited)
            => new(string.Empty, string.Empty, ObjectFlags.None, Item: new ItemInfo("item_book1", 0, MagicType: 1, NeedsMana: needsMana, CooldownSeconds: seconds, Charges: 4, LimitedCharges: limited));
        b.Unit("comm.item_magic", MapObject.Nature, 2, 2, Book(needsMana: true, seconds: 7, limited: true));
        b.Unit("comm.item_magic", MapObject.Nature, 4, 2, Book(needsMana: false, seconds: 7, limited: false));
        b.Unit("comm.item_magic", MapObject.Nature, 6, 2, Book(needsMana: null, seconds: null, limited: null));
        using TriggerDriver d = b.Start();
        d.Step();
        World w = d.World;
        ItemState At(int x) => w.Items.Get(w.Items.Entities.ToArray().Single(e => w.Positions.Get(e).X / 2048 == x));
        ItemDefinition type = w.Ruleset.EntityTypes[w.Ruleset.EntityTypeId("comm.item_magic")].Traits.Item!;

        Assert.Equal((ItemStateFlags.OwnCooldown, 7_000, 4), (At(2).Flags, At(2).CooldownMs, At(2).Charges));
        Assert.True(At(2).LimitedCharges(type));
        Assert.Equal((ItemStateFlags.OwnCooldown | ItemStateFlags.Endless, 0), (At(4).Flags, At(4).CooldownFor(type)));
        Assert.False(At(4).LimitedCharges(type));
        Assert.Equal(ItemStateFlags.None, At(6).Flags); // an older record keeps the type's
        Assert.Equal(type.CooldownMs, At(6).CooldownFor(type));
    }
}
