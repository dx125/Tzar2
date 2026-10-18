using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Determinism and snapshots of the GAME-07…11 state (ADR 0096): projectiles in flight, units in holders, items carried,
/// spells being cast, area spells, lasting effects, stances, crafting, conversions and crusades round-trip, continue
/// identically on any worker count; corrupt values are rejected (ADR 0019).
/// </summary>
public class M3eSnapshotTests
{
    private static readonly string[] Coast =
    [
        "..........................~~~~~~",
        "..........................~~~~~~",
        "..........................~~~~~~",
        "..........................~~~~~~",
        "..........................~~~~~~",
        "..........................~~~~~~",
        "..........................~~~~~~",
        "..........................~~~~~~",
        "..........................~~~~~~",
        "..........................~~~~~~",
        "..........................~~~~~~",
        "..........................~~~~~~",
    ];

    private static MapData Busy() => AsciiMap(
        Coast,
        Structure("euro.castle", 0, 0, 0),
        Structure("euro.cathedral", 0, 0, 6),
        Structure("arab.magicworkshop", 0, 6, 8),
        Unit("comm.galleon", 0, 28, 4),
        Unit("euro.mage", 0, 8, 3),
        Unit("euro.priest", 0, 8, 5),
        Unit("euro.archer", 0, 9, 4),
        Unit("comm.catapult", 0, 7, 6),
        Unit("euro.footman", 0, 6, 2),
        Unit("euro.footman", 0, 6, 3),
        Unit("euro.footman", 0, 24, 6),
        Unit("arab.peon", 0, 10, 10),
        Unit("comm.item", MapObject.Nature, 9, 2),
        Unit("comm.item_magic", MapObject.Nature, 9, 6),
        Unit("euro.footman", 1, 20, 3),
        Unit("euro.footman", 1, 20, 5),
        Unit("euro.maceman", 1, 21, 4),
        Unit("euro.footman", 1, 20, 9));

    // Sets everything going; returns the driver after `ticks`.
    private static SimDriver Run(int workers, int ticks)
    {
        var d = new SimDriver(Busy(), workers: workers, ruleset: Classic);
        World w = d.World;
        w.Players[0].Resources[Resource("gold")] = 9000;
        w.Players[0].Resources[Resource("food")] = 9000;
        foreach (string t in new[] { "te_learning", "te_healing", "te_bless", "te_freeze", "te_jihad" })
        {
            Grant(w, 0, t);
        }

        EntityId mage = OfType(w, "euro.mage").Single();
        EntityId priest = OfType(w, "euro.priest").Single();
        EntityId archer = OfType(w, "euro.archer").Single();
        EntityId catapult = OfType(w, "comm.catapult").Single();
        EntityId[] footmen = [.. OfType(w, "euro.footman", 0)];
        EntityId[] enemies = [.. OfType(w, "euro.footman", 1)];
        d.Order(0, CommandKind.Board, [footmen[0]], target: OfType(w, "euro.castle").Single());
        d.Order(0, CommandKind.Board, [footmen[2]], target: OfType(w, "comm.galleon").Single());
        d.Order(0, CommandKind.PickItem, [footmen[1]], target: OfType(w, "comm.item").Single());
        d.Order(0, CommandKind.PickItem, [archer], target: OfType(w, "comm.item_magic").Single());
        d.Order(0, CommandKind.CastSpell, [mage], target: enemies[0], arg0: Spell(Classic, "spell_freeze"));
        d.Order(0, CommandKind.CastSpell, [priest], target: catapult, arg0: Spell(Classic, "spell_bless"));
        d.Order(0, CommandKind.AttackGround, [catapult], 20 * Tile, 4 * Tile);
        d.Order(0, CommandKind.ConvertUnit, [.. OfType(w, "arab.peon")], arg0: Id("arab.jihad_warrior"));
        d.Order(0, CommandKind.CraftItem, [.. OfType(w, "arab.magicworkshop")], arg0: Id("comm.item_armor"), arg1: 400);
        d.Order(0, CommandKind.PreachCrusade, [.. OfType(w, "euro.cathedral")], 6 * Tile, 7 * Tile);
        d.Order(1, CommandKind.SetStance, [enemies[2]], arg0: (int)CombatStance.StandGround);
        for (int t = 0; t < ticks; t++)
        {
            if (t == 60)
            {
                d.Order(0, CommandKind.UseItem, [archer], 20 * Tile, 6 * Tile, arg0: 0); // a rain of fire from the book
                d.Order(0, CommandKind.Attack, [footmen[1]], target: enemies[1]);
                d.Order(0, CommandKind.CastSpell, [mage], target: enemies[1], arg0: Spell(Classic, "spell_lightning"));
            }

            d.Step();
        }

        return d;
    }

    [Fact]
    public void A_busy_combat_and_magic_world_round_trips_and_continues_identically()
    {
        using SimDriver d = Run(workers: 1, ticks: 75);
        World w = d.World;
        Assert.True(w.Helds.Count >= 3, "units inside holders and items carried");
        Assert.True(w.Projectiles.Count + w.AreaEffects.Count + w.Statuses.Count + w.Errands.Count + w.CraftJobs.Count + w.Crusades.Count > 3);

        byte[] snapshot = d.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, Classic, w.Map.Data, workerCount: 3);
        Assert.Equal(snapshot, restored.CaptureSnapshot());
        var empty = new CommandList();
        for (int t = 0; t < 600; t++)
        {
            Assert.Equal(d.Step().StateHash, restored.Tick(empty).StateHash);
        }

        Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
    }

    [Fact]
    public void The_same_battle_gives_the_same_hashes_on_one_and_four_workers()
    {
        ulong Stream(int workers)
        {
            using SimDriver d = Run(workers, 0);
            var h = new StateHasher();
            for (int t = 0; t < 900; t++)
            {
                TickResult r = d.Step();
                h.Add(r.StateHash).Add(r.FullHash ?? 0UL);
            }

            Assert.True(d.Count(SimEventKind.SpellCast) >= 2 && d.Count(SimEventKind.ProjectileLaunched) > 0 && d.Count(SimEventKind.UnitConverted) == 1);
            return h.GetCurrentHash();
        }

        Assert.Equal(Stream(1), Stream(4));
    }

    [Fact]
    public void The_uncorrupted_state_of_the_corruption_tests_restores()
    {
        using SimDriver d = Run(workers: 1, ticks: 40);
        Assert.True(d.World.Helds.Count >= 2 && d.World.Casters.Entities.ToArray().Any(c => d.World.Casters.Get(c).Phase != CastPhase.None) || d.World.Statuses.Count > 0);
        byte[] snapshot = Snapshots.WorldSnapshot.Capture(d.World);
        using Simulation restored = Simulation.Restore(snapshot, Classic, d.World.Map.Data);
        Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
    }

    public static TheoryData<string> Corruptions() =>
    [
        "pending experience", "hero below the hero level", "held by a dead holder", "held unit still moving", "holder count",
        "item in two slots", "inventory slot beyond the count", "projectile with an identity", "projectile off the map",
        "area effect of a damage spell", "status percent", "caster without the spell", "stance 9", "craft job of a non-item",
        "convert into a non-conversion", "errand padding", "combat ground mode on a target",
    ];

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void Invalid_combat_and_magic_state_is_rejected(string corruption)
    {
        using SimDriver d = Run(workers: 1, ticks: 40);
        World w = d.World;
        w.IsLocked = false;
        EntityId footman = OfType(w, "euro.footman", 0).First(f => !w.IsHeld(f));
        EntityId held = w.Helds.Entities.ToArray().First(e => !w.Items.Has(e));
        EntityId mage = OfType(w, "euro.mage").Single();
        switch (corruption)
        {
            case "pending experience": w.Experiences.Get(footman).PendingXp = 5; break;
            case "hero below the hero level": w.Experiences.Get(footman).Flags = ExperienceFlags.Hero; break;
            case "held by a dead holder": w.Helds.Get(held).Holder = EntityId.Create(4000, 1).Value; break;
            case "held unit still moving": w.Motions.Set(held, default); break;
            case "holder count": w.Holders.Get(OfType(w, "euro.castle").Single()).Count = 7; break;
            case "item in two slots":
            {
                EntityId carrier = w.Inventories.Entities.ToArray().First(e => w.Inventories.Get(e).Count == 1);
                ref Inventory inv = ref w.Inventories.Get(carrier);
                inv.Items[1] = inv.Items[0];
                inv.Count = 2;
                break;
            }

            case "inventory slot beyond the count": w.Inventories.Get(footman).Items[5] = footman.Value; break;
            case "projectile with an identity":
            {
                EntityId p = w.Entities.Create();
                w.Projectiles.Set(p, new Projectile { Spell = Projectile.NoSpell });
                w.Identities.Set(p, default);
                break;
            }

            case "projectile off the map":
            {
                EntityId p = w.Entities.Create();
                w.Projectiles.Set(p, new Projectile { X = -5, Spell = Projectile.NoSpell });
                break;
            }

            case "area effect of a damage spell":
            {
                EntityId a = w.Entities.Create();
                w.AreaEffects.Set(a, new AreaEffect { Spell = Spell(Classic, "spell_lightning") });
                break;
            }

            case "status percent": w.Statuses.Set(footman, new StatusEffects { CursePercent = 500, CurseUntil = 50 }); break;
            case "caster without the spell":
            {
                ref Caster c = ref w.Casters.Get(mage);
                c.Phase = CastPhase.Casting;
                c.Spell = Spell(Classic, "spell_heal");
                break;
            }

            case "stance 9": w.Stances.Set(footman, new StanceState { Value = (CombatStance)9 }); break;
            case "craft job of a non-item": w.CraftJobs.Set(OfType(w, "arab.magicworkshop").Single(), new CraftJob { ItemType = Id("euro.footman"), Power = 1 }); break;
            case "convert into a non-conversion": w.Errands.Set(footman, new Errand { Kind = ErrandKind.Convert, Arg = Id("euro.knight"), TimerMs = 5, MoveTick = -1 }); break;
            case "errand padding": w.Errands.Set(footman, new Errand { Kind = ErrandKind.Board, Target = 1, MoveTick = -1, Reserved1 = 3 }); break;
            case "combat ground mode on a target":
                w.Combats.Get(footman).Mode = CombatMode.Ground;
                w.Combats.Get(footman).Target = OfType(w, "euro.footman", 1).First().Value;
                break;
        }

        byte[] snapshot = Snapshots.WorldSnapshot.Capture(w);
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, Classic, w.Map.Data));
    }
}
