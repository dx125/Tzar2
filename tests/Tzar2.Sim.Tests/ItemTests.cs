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
    [Trait("Rule", "ECO-023")]
    public void A_peasant_delivers_its_load_at_the_store_it_is_sent_to()
    {
        using var d = new SimDriver(
            TestMap(40, 12, Structure("euro.castle", 0, 2, 2), Structure("euro.storage", 0, 30, 2), Unit("euro.peon", 0, 8, 8), Item("comm.resitem", 9, 8)),
            ruleset: Classic);
        int goldId = Resource("gold");
        int gold = d.World.Players[0].Resources[goldId];
        EntityId peon = OfType(d.World, "euro.peon").Single();
        Pick(d, peon, OfType(d.World, "comm.resitem").Single());
        Assert.Equal(100, d.World.Workers.Get(peon).CarryAmount); // on its way to the nearest store, the castle

        // Sent to the storage instead.
        EntityId storage = OfType(d.World, "euro.storage").Single();
        d.Order(0, CommandKind.Harvest, [peon], target: storage);
        d.RunUntil(() => d.World.Workers.Get(peon).CarryAmount == 0, 3000);
        Assert.Equal(gold + 100, d.World.Players[0].Resources[goldId]);
        Assert.Equal(storage.Index, d.Events.Last(e => e.Event.Kind == SimEventKind.ResourceDelivered).Event.Y);
        for (int t = 0; t < 100; t++)
        {
            d.Step();
        }

        Assert.Equal(WorkerTask.None, d.World.Workers.Get(peon).Task); // the order ends with the delivery

        // A peasant with nothing to deliver is not sent.
        d.Order(0, CommandKind.Harvest, [peon], target: storage);
        d.Step();
        Assert.Contains(CommandRejection.NoValidEntities, d.Rejections());
    }

    [Fact]
    [Trait("Rule", "ITEM-006")]
    [Trait("Rule", "NAT-004")]
    public void Resource_items_go_to_the_stockpile_and_a_peasant_carries_them_home()
    {
        using var d = new SimDriver(
            TestMap(24, 12, Structure("euro.castle", 0, 14, 2), Unit("euro.footman", 0, 4, 4), Unit("euro.peon", 0, 4, 8), Item("comm.resitem", 6, 4), Item("comm.resitem", 6, 8)),
            ruleset: Classic);
        int goldId = Resource("gold");
        int gold = d.World.Players[0].Resources[goldId];
        EntityId[] items = [.. OfType(d.World, "comm.resitem").OrderBy(i => d.World.Positions.Get(i).Y)];
        Pick(d, OfType(d.World, "euro.footman").Single(), items[0]);
        Assert.False(d.World.Entities.IsAlive(items[0]));
        Assert.Equal(gold + 100, d.World.Players[0].Resources[goldId]); // at once (UNVERIFIED default amount)

        // A peasant takes it as its load (ReTzar 0x464f60) and delivers it.
        EntityId peon = OfType(d.World, "euro.peon").Single();
        Pick(d, peon, items[1]);
        Assert.Equal((100, goldId), (d.World.Workers.Get(peon).CarryAmount, (int)d.World.Workers.Get(peon).CarryResource));
        Assert.Equal(gold + 100, d.World.Players[0].Resources[goldId]);
        d.RunUntil(() => d.World.Workers.Get(peon).CarryAmount == 0, 2000);
        Assert.Equal(gold + 200, d.World.Players[0].Resources[goldId]);
    }

    [Fact]
    [Trait("Rule", "ITEM-005")]
    public void Potions_heal_and_level_and_are_used_up()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Item("comm.item_health", 5, 4), Item("comm.item_health", 5, 6)), ruleset: Classic);
        Grant(d.World, 0, "te_learning");
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId potion = OfType(d.World, "comm.item_health").First();
        EntityId plus = OfType(d.World, "comm.item_health").Last();
        d.World.Items.Get(plus).Effect = ItemEffect.Levels; // a potion of one level (ITEM-016)
        d.World.Items.Get(plus).Power = 1;
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
    [Trait("Rule", "ITEM-018")]
    public void A_map_book_may_never_run_out_and_waits_its_own_pool()
    {
        // ADR 0265: a book without limited charges is endless; one that needs mana waits its own pool (6 s here) instead of
        // the type's 10 s. The caster still waits the spell's own recast (fire rain 5 s, Tzar2's UNVERIFIED value).
        using var d = new SimDriver(TestMap(32, 16, Unit("euro.footman", 0, 4, 4), Item("comm.item_magic", 5, 4)), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId book = OfType(d.World, "comm.item_magic").Single();
        ref ItemState state = ref d.World.Items.Get(book);
        state.Flags = ItemStateFlags.Endless | ItemStateFlags.OwnCooldown;
        state.CooldownMs = 6_000;
        int charges = state.Charges;
        Pick(d, footman, book);
        int tickMs = d.World.Config.TickLengthMilliseconds;
        int NotReady() => d.Rejections().Count(r => r == CommandRejection.SpellNotReady);
        for (int use = 0; use < 5; use++) // more casts than the type's three charges
        {
            d.Order(0, CommandKind.UseItem, [footman], (12 * Tile) + use, 8 * Tile, arg0: 0);
            d.RunUntil(() => d.Count(SimEventKind.SpellCast) == use + 1, 400);
            Assert.Equal(use + 1, d.Count(SimEventKind.SpellCast));
            d.RunUntil(() => false, 5_500 / tickMs); // past the spell's recast, not yet the pool
            int rejected = NotReady();
            d.Order(0, CommandKind.UseItem, [footman], 12 * Tile, 8 * Tile, arg0: 0);
            d.Step();
            Assert.Equal(rejected + 1, NotReady()); // its own pool refills first
            d.RunUntil(() => false, 1_000 / tickMs); // 6.5 s: the pool is full, the type's 10 s are not over
        }

        Assert.True(d.World.Entities.IsAlive(book)); // never used up
        Assert.Equal(charges, d.World.Items.Get(book).Charges);

        // An own pool of 0 (the record does not need mana): only the spell's recast, never the type's 10 s.
        d.World.Items.Get(book).CooldownMs = 0;
        d.RunUntil(() => false, 6_000 / tickMs);
        int casts = d.Count(SimEventKind.SpellCast);
        d.Order(0, CommandKind.UseItem, [footman], 12 * Tile, 8 * Tile, arg0: 0);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == casts + 1, 400);
        d.RunUntil(() => false, 5_500 / tickMs);
        int before = NotReady();
        d.Order(0, CommandKind.UseItem, [footman], 13 * Tile, 8 * Tile, arg0: 0);
        d.Step();
        Assert.Equal(before, NotReady());
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == casts + 2, 400);
        Assert.Equal(casts + 2, d.Count(SimEventKind.SpellCast));
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
        d.Order(0, CommandKind.CraftItem, [workshop], arg0: Id("comm.item_plusplus"), arg1: 320);
        d.Step();
        Assert.Equal(gold - 300, d.World.Players[0].Resources[Resource("gold")]); // 6 power x 50 gold (UNVERIFIED)
        d.Order(0, CommandKind.CraftItem, [workshop], arg0: Id("comm.item_plusplus"), arg1: 100);
        d.Step();
        Assert.Contains(CommandRejection.NoValidEntities, d.Rejections()); // busy
        d.RunUntil(() => d.Count(SimEventKind.ItemCrafted) > 0, 400);
        EntityId item = OfType(d.World, "comm.item_plusplus").Single();
        Assert.Equal((6, ItemEffect.Attack), (d.World.Items.Get(item).Power, d.World.Items.Get(item).Effect)); // a sword
    }

    [Fact]
    [Trait("Rule", "ITEM-016")]
    public void The_magic_workshop_makes_books_of_other_spells_potions_of_each_effect_and_shields()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("arab.magicworkshop", 0, 8, 4), Unit("euro.footman", 0, 20, 4), Unit("euro.footman", 1, 26, 4)), ruleset: Classic);
        Grant(d.World, 0, "te_learning");
        EntityId workshop = OfType(d.World, "arab.magicworkshop").Single();
        EntityId footman = OfType(d.World, "euro.footman", 0).Single();
        d.World.Players[0].Resources[Resource("gold")] = 5000;
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        d.Order(0, CommandKind.SetStance, [footman], arg0: (int)CombatStance.HoldFire); // no fight while the workshop works
        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.HoldFire);
        EntityId Make(string type, int variant, int gold)
        {
            int before = d.Count(SimEventKind.ItemCrafted);
            d.Order(0, CommandKind.CraftItem, [workshop], variant, 0, arg0: Id(type), arg1: gold);
            d.RunUntil(() => d.Count(SimEventKind.ItemCrafted) > before, 2000);
            Assert.Equal(before + 1, d.Count(SimEventKind.ItemCrafted));
            return Of(d, SimEventKind.ItemCrafted).Last().Entity;
        }

        // A shield (power items: sword, bow, shield): defence while carried, in its own look.
        ItemDefinition power = Classic.EntityTypes[Id("comm.item_plusplus")].Traits.Item!;
        EntityId shield = Make("comm.item_plusplus", 2, 250);
        ItemState s = d.World.Items.Get(shield);
        Assert.Equal((ItemEffect.Defence, 5, "item_shield1"), (s.Effect, s.Power, power.Looks[s.Look - 1]));
        int defence = Sim.Systems.CombatStats.Defence(d.World, footman, d.World.TypeOf(footman));
        int attack = Sim.Systems.CombatStats.Attack(d.World, footman, d.World.TypeOf(footman), d.World.TypeOf(footman).Traits.Armament!);
        Pick(d, footman, shield);
        Assert.Equal(defence + 5, Sim.Systems.CombatStats.Defence(d.World, footman, d.World.TypeOf(footman)));
        Assert.Equal(attack, Sim.Systems.CombatStats.Attack(d.World, footman, d.World.TypeOf(footman), d.World.TypeOf(footman).Traits.Armament!));

        // A potion of levels: 10 points of power, 5 per level (UNVERIFIED), so two levels.
        EntityId potion = Make("comm.item_health", 1, 500);
        Assert.Equal((ItemEffect.Levels, 2), (d.World.Items.Get(potion).Effect, d.World.Items.Get(potion).Power));
        int hp = Hp(d, footman);
        Pick(d, footman, potion);
        d.Order(0, CommandKind.UseItem, [footman], arg0: 1);
        d.Step();
        Assert.False(d.World.Entities.IsAlive(potion));
        Assert.Equal(2, d.World.Experiences.Get(footman).Level);
        Assert.True(Hp(d, footman) >= hp); // no hit points from a potion of levels

        // A book of Lightning, not Fire Rain: two uses for 400 gold.
        EntityId book = Make("comm.item_magic", 1, 400);
        Assert.Equal(Classic.SpellId("spell_lightning"), d.World.ItemSpell(book));
        Assert.Equal(2, d.World.Items.Get(book).Charges);
        Pick(d, footman, book);
        d.Order(0, CommandKind.UseItem, [footman], target: enemy, arg0: 1);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) > 0, 400);
        Assert.Equal(1, d.World.Items.Get(book).Charges);

        // Out of range: a variant the type does not have.
        d.Order(0, CommandKind.CraftItem, [workshop], 3, 0, arg0: Id("comm.item_plusplus"), arg1: 100);
        d.Step();
        Assert.Contains(CommandRejection.InvalidArgument, d.Rejections());
    }

    [Fact]
    [Trait("Rule", "ITEM-009")]
    public void A_spell_book_costs_four_times_more_gold_per_point_and_takes_twice_as_long()
    {
        // 1.05: magic books made in the magic workshop are four times more expensive and take twice as long.
        (int Power, int LeftMs) Craft(string item)
        {
            using var d = new SimDriver(TestMap(24, 16, Structure("arab.magicworkshop", 0, 8, 4)), ruleset: Classic);
            EntityId workshop = OfType(d.World, "arab.magicworkshop").Single();
            d.Order(0, CommandKind.CraftItem, [workshop], arg0: Id(item), arg1: 400);
            d.Step();
            CraftJob job = d.World.CraftJobs.Get(workshop);
            return (job.Power, job.LeftMs);
        }

        (int weaponPower, int weaponMs) = Craft("comm.item_plusplus");
        (int bookPower, int bookMs) = Craft("comm.item_magic");
        Assert.Equal(8, weaponPower); // 400 gold at 50 per point
        Assert.Equal(2, bookPower); // 400 gold at 200 per point
        Assert.Equal(2 * 2 * 2000, bookMs + 50); // twice 2 s per point; one tick (50 ms) has run
        Assert.Equal(8 * 2000, weaponMs + 50);
    }
}
