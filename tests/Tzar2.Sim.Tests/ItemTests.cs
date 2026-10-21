using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>GAME-10 (ADR 0094): picking up, dropping, using and giving items; their effects; crafting.</summary>
public class ItemTests
{
    private static MapObject Item(string key, int tileX, int tileY) => Unit(key, MapObject.Nature, tileX, tileY);

    private static void Pick(SimDriver d, EntityId unit, EntityId item)
    {
        d.Order(d.World.Identities.Get(unit).Owner, CommandKind.PickItem, [unit], target: item);
        d.RunUntil(() => d.World.IsHeld(item) || !d.World.Entities.IsAlive(item), 400);
    }

    [Fact]
    [Trait("Rule", "ITEM-002")]
    public void A_weapon_raises_attack_while_carried()
    {
        // footman 9 - defence 2 = 7; with the weapon (+3, UNVERIFIED default) 12 - 2 = 10... minus nothing: 10.
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Item("comm.item", 6, 4), Unit("euro.footman", 1, 10, 4)), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman", 0).Single();
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        EntityId sword = OfType(d.World, "comm.item").Single();
        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.HoldFire);
        Pick(d, footman, sword);
        Assert.True(d.World.IsHeld(sword));
        Assert.Equal(1, d.World.Inventories.Get(footman).Count);
        d.Order(0, CommandKind.Attack, [footman], target: enemy);
        d.RunUntil(() => d.Count(SimEventKind.Damaged) > 0, 400);
        Assert.Equal(10, Of(d, SimEventKind.Damaged).First().Arg);

        d.Order(0, CommandKind.DropItem, [footman], arg0: 0);
        d.Step();
        Assert.False(d.World.IsHeld(sword));
        Assert.Equal(0, d.World.Inventories.Get(footman).Count);
        d.Order(0, CommandKind.Attack, [footman], target: enemy);
        int before = d.Count(SimEventKind.Damaged);
        d.RunUntil(() => d.Count(SimEventKind.Damaged) > before, 400);
        Assert.Equal(7, Of(d, SimEventKind.Damaged).Last().Arg);
    }

    [Fact]
    [Trait("Rule", "ITEM-014")]
    public void Two_units_racing_for_one_item_never_duplicate_it()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.footman", 0, 8, 4), Item("comm.item", 6, 4)), ruleset: Classic);
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        EntityId sword = OfType(d.World, "comm.item").Single();
        d.Order(0, CommandKind.PickItem, footmen, target: sword);
        d.RunUntil(() => d.World.Errands.Count == 0, 400);
        Assert.Equal(1, d.World.Items.Count);
        Assert.Equal(1, footmen.Sum(f => d.World.Inventories.Get(f).Count));
        Assert.Equal(1, d.Count(SimEventKind.ItemPicked));
    }

    [Fact]
    [Trait("Rule", "ITEM-001")]
    public void A_full_inventory_takes_nothing_more()
    {
        Ruleset rules = ClassicWith("euro.footman: { Inventory: { slots: 1 } }");
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Item("comm.item", 5, 4), Item("comm.item_armor", 5, 6)), ruleset: rules);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        Pick(d, footman, OfType(d.World, "comm.item").Single());
        d.Order(0, CommandKind.PickItem, [footman], target: OfType(d.World, "comm.item_armor").Single());
        d.Step();
        Assert.Contains(CommandRejection.InventoryFull, d.Rejections());
    }

    [Fact]
    [Trait("Rule", "ITEM-006")]
    public void Resource_items_go_to_the_stockpile()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.peon", 0, 4, 4), Item("comm.resitem", 6, 4)), ruleset: Classic);
        int gold = d.World.Players[0].Resources[Resource("gold")];
        EntityId item = OfType(d.World, "comm.resitem").Single();
        Pick(d, OfType(d.World, "euro.peon").Single(), item);
        Assert.False(d.World.Entities.IsAlive(item));
        Assert.Equal(gold + 100, d.World.Players[0].Resources[Resource("gold")]); // UNVERIFIED default amount
    }

    [Fact]
    [Trait("Rule", "ITEM-005")]
    public void Potions_heal_and_level_and_are_used_up()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Item("comm.item_health", 5, 4), Item("comm.item_plusplus", 5, 6)), ruleset: Classic);
        Grant(d.World, 0, "te_learning");
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId potion = OfType(d.World, "comm.item_health").Single();
        EntityId plus = OfType(d.World, "comm.item_plusplus").Single();
        Pick(d, footman, potion);
        Pick(d, footman, plus);
        d.World.Healths.Get(footman).Current = 10;
        d.Order(0, CommandKind.UseItem, [footman], arg0: 0);
        d.Step();
        Assert.Equal(70, Hp(d, footman));
        Assert.False(d.World.Entities.IsAlive(potion));
        d.Order(0, CommandKind.UseItem, [footman], arg0: 0);
        d.Step();
        Assert.Equal(1, d.World.Experiences.Get(footman).Level);
        Assert.Equal(0, d.World.Inventories.Get(footman).Count);
    }

    [Fact]
    [Trait("Rule", "ITEM-003")]
    public void A_spell_book_has_charges_and_a_cooldown()
    {
        // The Fire Rain book (manual): 3 charges, 10 s between uses (UNVERIFIED defaults), no mana needed.
        using var d = new SimDriver(TestMap(32, 16, Unit("euro.footman", 0, 4, 4), Item("comm.item_magic", 5, 4)), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId book = OfType(d.World, "comm.item_magic").Single();
        Pick(d, footman, book);
        for (int use = 0; use < 3; use++)
        {
            d.Order(0, CommandKind.UseItem, [footman], (12 * Tile) + use, 8 * Tile, arg0: 0);
            d.RunUntil(() => d.Count(SimEventKind.SpellCast) == use + 1, 400);
            Assert.Equal(use + 1, d.Count(SimEventKind.SpellCast));
            if (use < 2)
            {
                d.Order(0, CommandKind.UseItem, [footman], 12 * Tile, 8 * Tile, arg0: 0);
                d.Step();
                Assert.Contains(CommandRejection.SpellNotReady, d.Rejections());
                d.RunUntil(() => false, 200); // the 10 s cooldown
            }
        }

        Assert.False(d.World.Entities.IsAlive(book)); // spent
        Assert.Equal(0, d.World.Inventories.Get(footman).Count);
    }

    [Fact]
    [Trait("Rule", "ITEM-011")]
    public void A_dead_units_items_fall_to_the_ground_and_friends_can_be_given_items()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.archer", 0, 4, 7), Item("comm.item", 5, 4), Item("comm.item_armor", 5, 5)), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId archer = OfType(d.World, "euro.archer").Single();
        EntityId sword = OfType(d.World, "comm.item").Single();
        EntityId armor = OfType(d.World, "comm.item_armor").Single();
        Pick(d, footman, sword);
        Pick(d, footman, armor);
        d.Order(0, CommandKind.GiveItem, [footman], target: archer, arg0: 1);
        d.RunUntil(() => d.World.Inventories.Get(archer).Count == 1, 400);
        Assert.True(d.World.Helds.Get(armor).Holder == archer.Value);

        Position at = d.PositionOf(footman);
        d.Order(0, CommandKind.DebugKill, [footman]);
        d.Step();
        Assert.False(d.World.IsHeld(sword));
        Assert.Equal(at, d.PositionOf(sword));
        Assert.Contains(Of(d, SimEventKind.ItemDropped), e => e.Entity == sword);
    }

    [Fact]
    public void The_magic_workshop_crafts_better_items_for_more_gold()
    {
        using var d = new SimDriver(TestMap(24, 16, Structure("arab.magicworkshop", 0, 8, 4)), ruleset: Classic);
        EntityId workshop = OfType(d.World, "arab.magicworkshop").Single();
        int gold = d.World.Players[0].Resources[Resource("gold")];
        d.Order(0, CommandKind.CraftItem, [workshop], arg0: Id("comm.item"), arg1: 320);
        d.Step();
        Assert.Equal(gold - 300, d.World.Players[0].Resources[Resource("gold")]); // 6 power x 50 gold (UNVERIFIED)
        d.Order(0, CommandKind.CraftItem, [workshop], arg0: Id("comm.item"), arg1: 100);
        d.Step();
        Assert.Contains(CommandRejection.NoValidEntities, d.Rejections()); // busy
        d.RunUntil(() => d.Count(SimEventKind.ItemCrafted) > 0, 400);
        EntityId item = OfType(d.World, "comm.item").Single();
        Assert.Equal(6, d.World.Items.Get(item).Power);
    }
}
