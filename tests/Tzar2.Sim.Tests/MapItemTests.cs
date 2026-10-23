using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Maps;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>ADR 0264: a map object's own item properties, in map.bin version 3 and at load.</summary>
public class MapItemTests
{
    private static MapData Items() => TestMap(
        24,
        12,
        Unit("comm.item_magic", MapObject.Nature, 4, 4) with { Item = new MapItem(Power: 2, Charges: 4, Spell: "spell_lightning", Look: "item_book2") },
        Unit("comm.item_plusplus", MapObject.Nature, 6, 4) with { Item = new MapItem(ItemEffect.Defence, 12, Look: "item_shield1") },
        Unit("comm.item_health", MapObject.Nature, 8, 4) with { Item = new MapItem(ItemEffect.Mana, 30, Look: "item_potion4") },
        Unit("comm.item_magic", MapObject.Nature, 10, 4) with { Item = new MapItem(Spell: "no_such_spell", Look: "no_such_look") },
        Unit("comm.item_health", MapObject.Nature, 12, 4));

    [Fact]
    public void Map_items_round_trip_through_map_bin_version_3()
    {
        MapData map = Items();
        byte[] bytes = MapBinary.Write(map);
        Assert.Equal(MapBinary.ItemsVersion, BitConverter.ToUInt16(bytes, 4));
        MapData back = MapBinary.Read(bytes);
        Assert.Equal(map.ContentHash, back.ContentHash);
        Assert.True(map.Objects.AsSpan().SequenceEqual(back.Objects.AsSpan()));
        Assert.Equal(bytes, MapBinary.Write(back));
    }

    [Fact]
    public void Only_maps_with_items_change_their_hash()
    {
        MapData map = Items();
        MapObject[] plain = [.. map.Objects.ToArray().Select(o => o with { Item = null })];
        MapData without = TestMap(24, 12, plain);
        Assert.NotEqual(map.ContentHash, without.ContentHash);
        Assert.Equal(TestMap(24, 12, plain).ContentHash, without.ContentHash);
        Assert.Equal(MapBinary.Version, BitConverter.ToUInt16(MapBinary.Write(without), 4)); // as before ADR 0264
        Assert.NotEqual(TestMap(24, 12, [plain[0] with { Item = new MapItem(Power: 3) }]).ContentHash, TestMap(24, 12, [plain[0] with { Item = new MapItem(Power: 4) }]).ContentHash);
    }

    [Fact]
    public void Map_items_load_with_their_own_properties()
    {
        using var d = new SimDriver(Items(), ruleset: Classic);
        World w = d.World;
        ItemState At(int i) => w.Items.Get(w.MapObjectEntities[i]);
        Ruleset r = w.Ruleset;

        ItemState book = At(0);
        Assert.Equal((2, 4, r.SpellId("spell_lightning") + 1, 2), (book.Power, book.Charges, (int)book.Spell, (int)book.Look));
        Assert.Equal(r.SpellId("spell_lightning"), w.ItemSpell(w.MapObjectEntities[0]));

        ItemState shield = At(1);
        Assert.Equal((ItemEffect.Defence, 12, 8), (shield.Effect, shield.Power, (int)shield.Look)); // item_shield1 is the 8th look

        ItemState potion = At(2);
        Assert.Equal((ItemEffect.Mana, 30, 4), (potion.Effect, potion.Power, (int)potion.Look));

        ItemState unknown = At(3); // unknown keys: the type's own spell and image
        Assert.Equal((0, 0), ((int)unknown.Spell, (int)unknown.Look));
        Assert.Equal(r.EntityTypes[r.EntityTypeId("comm.item_magic")].Traits.Item!.Power, unknown.Power);

        ItemState plain = At(4);
        Assert.Equal(r.EntityTypes[r.EntityTypeId("comm.item_health")].Traits.Item!.Power, plain.Power);
        Assert.Equal(ItemEffect.Default, plain.Effect);
    }

    [Fact]
    public void Invalid_items_are_refused()
    {
        MapData bad = TestMap(8, 8, Unit("comm.item_health", MapObject.Nature, 2, 2) with { Item = new MapItem(Power: -1) });
        Assert.Throws<ArgumentException>(() => MapBinary.Write(bad));
        byte[] bytes = MapBinary.Write(TestMap(8, 8, Unit("comm.item_health", MapObject.Nature, 2, 2) with { Item = new MapItem(ItemEffect.Health, 5) }));
        int effect = Array.LastIndexOf(bytes, (byte)ItemEffect.Health);
        bytes[effect] = 99;
        Assert.Throws<FormatException>(() => MapBinary.Read(bytes));
    }
}
