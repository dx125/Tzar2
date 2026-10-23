using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>The spells' magnitudes as ReTzar reads them from the original (MAG-024, ADR 0261).</summary>
public class SpellMagnitudeTests
{
    private const int Tile = SimDriver.Tile;

    private static void Apply(SimDriver d, string spell, EntityId caster, EntityId target)
    {
        Position at = d.PositionOf(target);
        MagicSystem.ApplyEffect(d.World, Spell(d.World.Ruleset, spell), caster, d.World.Identities.Get(caster).Owner, target, at.X, at.Y, []);
    }

    [Fact]
    [Trait("Rule", "MAG-024")]
    public void Freeze_holds_every_unit_within_three_tiles_for_twenty_seconds_without_defence_and_hurts_but_never_kills()
    {
        using var d = new SimDriver(
            TestMap(32, 12, Unit("euro.mage", 0, 2, 2), Unit("euro.footman", 1, 10, 4), Unit("euro.footman", 0, 12, 4), Unit("euro.footman", 1, 14, 4), Unit("euro.footman", 1, 20, 4)),
            ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId[] f = [.. OfType(d.World, "euro.footman").OrderBy(e => d.PositionOf(e).X)];
        d.World.Healths.Get(f[2]).Current = 10;
        Apply(d, "spell_freeze", mage, f[1]);
        Assert.All(f[..3], e => Assert.False(d.World.CanAct(e))); // own units too
        Assert.True(d.World.CanAct(f[3])); // 8 tiles away
        Assert.Equal(0, CombatStats.Defence(d.World, f[0], d.World.TypeOf(f[0])));
        Assert.Equal(70 - 40, Hp(d, f[0]));
        Assert.Equal(1, Hp(d, f[2])); // never killed
        d.RunUntil(() => d.World.CanAct(f[0]), 1000);
        Assert.InRange(d.World.Tick.Value, (20_000 / 50) - 2, (20_000 / 50) + 2);
    }

    [Fact]
    [Trait("Rule", "MAG-024")]
    public void Bless_adds_five_attack_to_the_friendly_units_within_four_tiles()
    {
        using var d = new SimDriver(
            TestMap(32, 12, Unit("euro.priest", 0, 2, 2), Unit("euro.footman", 0, 10, 4), Unit("euro.footman", 0, 13, 4), Unit("euro.footman", 1, 11, 5), Unit("euro.footman", 0, 20, 4)),
            ruleset: Classic);
        EntityId priest = OfType(d.World, "euro.priest").Single();
        EntityId[] own = [.. OfType(d.World, "euro.footman", 0).OrderBy(e => d.PositionOf(e).X)];
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        int Attack(EntityId e) => CombatStats.Attack(d.World, e, d.World.TypeOf(e), d.World.TypeOf(e).Traits.Armament!);
        int before = Attack(own[0]);
        Apply(d, "spell_bless", priest, own[0]);
        Assert.Equal(before + 5, Attack(own[0]));
        Assert.Equal(before + 5, Attack(own[1]));
        Assert.Equal(before, Attack(enemy));
        Assert.Equal(before, Attack(own[2])); // 10 tiles away
    }

    [Fact]
    [Trait("Rule", "MAG-024")]
    public void Confusion_takes_every_unit_within_two_tiles_and_stone_lasts_two_to_five_minutes()
    {
        using var d = new SimDriver(
            TestMap(32, 12, Unit("euro.mage", 0, 2, 2), Unit("euro.footman", 1, 10, 4), Unit("euro.footman", 1, 11, 5), Unit("euro.footman", 1, 16, 4)),
            ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId[] f = [.. OfType(d.World, "euro.footman").OrderBy(e => d.PositionOf(e).X)];
        Apply(d, "spell_berserk", mage, f[0]);
        Assert.True(d.World.IsConfused(f[0]));
        Assert.True(d.World.IsConfused(f[1]));
        Assert.False(d.World.IsConfused(f[2]));

        Apply(d, "spell_turntostone", mage, f[2]);
        int ticks = d.World.Statuses.Get(f[2]).StoneUntil - d.World.Tick.Value;
        Assert.InRange(ticks, 120_000 / 50, 300_000 / 50);
    }

    [Fact]
    [Trait("Rule", "MAG-024")]
    public void Raise_dead_raises_every_body_within_eight_tiles_of_the_caster()
    {
        using var d = new SimDriver(
            TestMap(40, 12, [Unit("euro.mage", 0, 4, 4), .. Enumerable.Range(0, 7).Select(i => Unit("euro.peon", 1, 6 + i, 3)), Unit("euro.peon", 1, 30, 3)]),
            ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        d.Order(1, CommandKind.DebugKill, [.. OfType(d.World, "euro.peon")]);
        d.Step();
        Grant(d.World, 0, "te_necromancy");
        d.Order(0, CommandKind.CastSpell, [mage], arg0: Spell(d.World.Ruleset, "raisedead"));
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 200);
        Assert.Equal(7, OfType(d.World, "comm.skeleton", 0).Count()); // not the far one
    }
}
