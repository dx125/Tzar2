using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// The owner's check of 2026-10-21: every bonus a technology gives reaches the units and buildings it is for, only for
/// the kingdom that researched it, and the mechanics read it (docs/rules/technology.yaml). The classic ruleset's own
/// effects are checked, not test copies of them.
/// </summary>
public class TechnologyEffectTests
{
    private const int Tile = Map.MapState.UnitsPerTile;

    // Effects whose targets the data does not have: iron rocks (UNK-14). The synthetic tables have no monks either; the
    // local test on the original's data (OriginTechnologyTests) checks those.
    private static readonly HashSet<(string Technology, ModifierStat Stat)> KnownEmpty =
        [("te_ironplus", ModifierStat.CarryCapacity), ("te_meditation", ModifierStat.Cost), ("te_philosophy", ModifierStat.StartingLevel)];

    public static TheoryData<string> EffectTechnologies()
    {
        var data = new TheoryData<string>();
        foreach (TechnologyDefinition t in Classic.Technologies.Where(t => !t.Effects.IsEmpty))
        {
            data.Add(t.Key);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EffectTechnologies))]
    [Trait("Rule", "TECH-003")]
    [Trait("Rule", "TECH-004")]
    [Trait("Rule", "TECH-007")]
    [Trait("Rule", "TECH-008")]
    [Trait("Rule", "TECH-009")]
    [Trait("Rule", "TECH-011")]
    [Trait("Rule", "TECH-012")]
    [Trait("Rule", "TECH-013")]
    [Trait("Rule", "TECH-016")]
    [Trait("Rule", "TECH-020")]
    [Trait("Rule", "TECH-021")]
    [Trait("Rule", "TECH-025")]
    [Trait("Rule", "TECH-026")]
    [Trait("Rule", "TECH-032")]
    public void Every_effect_of_a_technology_changes_its_targets_for_the_kingdom_that_researched_it(string key)
    {
        TechnologyDefinition tech = Classic.Technologies[Classic.TechnologyId(key)];
        using var d = new SimDriver(TestMap(32, 12, Structure("euro.castle", 0, 2, 2), Structure("euro.castle", 1, 20, 2)), ruleset: Classic);
        var probes = new List<(ModifierDefinition Effect, ushort Type, int Before)>();
        foreach (ModifierDefinition effect in tech.Effects)
        {
            if (effect.Types.IsEmpty)
            {
                Assert.Contains((key, effect.Stat), KnownEmpty);
                continue;
            }

            foreach (ushort type in effect.Types)
            {
                probes.Add((effect, type, d.World.Stats.Apply(0, effect.Stat, type, 1000)));
            }
        }

        ResearchTests.Grant(d, 0, Classic, key);
        d.Step();
        foreach ((ModifierDefinition effect, ushort type, int before) in probes)
        {
            int after = d.World.Stats.Apply(0, effect.Stat, type, 1000);
            int sign = Math.Sign(effect.Add != 0 ? effect.Add : effect.BasisPoints);
            Assert.True(Math.Sign(after - before) == sign, $"{key} {effect.Stat} on {Classic.EntityTypes[type].Key}: {before} -> {after}");
            Assert.Equal(before, d.World.Stats.Apply(1, effect.Stat, type, 1000)); // the other kingdom did not research it
        }
    }

    [Theory]
    [InlineData("te_temperiron", ModifierStat.Attack, "euro.footman", "euro.archer")]
    [InlineData("te_crossbow", ModifierStat.Attack, "euro.archer", "euro.footman")]
    [InlineData("te_scalearmor", ModifierStat.Defence, "euro.footman", "euro.knight")]
    [InlineData("te_engineering", ModifierStat.Attack, "comm.catapult", "euro.footman")]
    [InlineData("te_feudalism", ModifierStat.Cost, "euro.peon", "euro.footman")]
    [InlineData("te_woodplus", ModifierStat.CarryCapacity, "comm.tree", "comm.res_gold")]
    [InlineData("te_goldplus", ModifierStat.CarryCapacity, "comm.res_gold", "comm.tree")]
    [InlineData("te_architecture", ModifierStat.BuildRate, "euro.house", "euro.footman")]
    [InlineData("te_fanaticism", ModifierStat.Resistance, "euro.castle", "euro.footman")]
    [InlineData("te_supplystorage", ModifierStat.PopulationProvided, "euro.house", "euro.castle")]
    [InlineData("te_gunpowder", ModifierStat.Attack, "comm.tower_stone2", "comm.tower_stone")]
    [InlineData("te_tradetaxes", ModifierStat.Income, "euro.market", "euro.castle")]
    [InlineData("te_banking", ModifierStat.Income, "euro.market", "euro.castle")]
    [InlineData("te_medicine", ModifierStat.AbilityRange, "euro.priest", "euro.mage")]
    [InlineData("te_landingtactics", ModifierStat.HolderCapacity, "comm.galleon", "euro.castle")]
    public void Technologies_reach_the_units_and_buildings_they_are_for(string technology, ModifierStat stat, string hit, string missed)
    {
        TechnologyDefinition tech = Classic.Technologies[Classic.TechnologyId(technology)];
        ModifierDefinition effect = tech.Effects.First(e => e.Stat == stat);
        Assert.True(effect.AppliesTo(Id(hit)), $"{technology} should reach {hit}");
        Assert.False(effect.AppliesTo(Id(missed)), $"{technology} should not reach {missed}");
    }

    [Fact]
    [Trait("Rule", "TECH-021")]
    public void Medicine_lets_priests_heal_from_farther()
    {
        using var d = new SimDriver(TestMap(32, 12, Structure("euro.castle", 0, 2, 2), Unit("euro.priest", 0, 10, 6)), ruleset: Classic);
        EntityId priest = OfType(d.World, "euro.priest").Single();
        SpellDefinition heal = Classic.Spells[Classic.SpellId("spell_heal")];
        int before = MagicSystem.RangeOf(d.World, priest, heal);
        ResearchTests.Grant(d, 0, Classic, "te_medicine");
        d.Step();
        Assert.True(MagicSystem.RangeOf(d.World, priest, heal) > before);
    }

    [Fact]
    public void Trade_taxes_and_banking_raise_the_profit_of_a_delivery_at_a_market()
    {
        using var d = new SimDriver(TestMap(48, 12, Structure("euro.castle", 0, 2, 2), Structure("euro.market", 0, 40, 2)), ruleset: Classic);
        EntityId market = OfType(d.World, "euro.market").Single();
        int Profit() => TradeSystem.ProfitOf(d.World, 0, 100, 2 * Tile, 2 * Tile, market);
        int plain = Profit();
        Assert.True(plain > 0);
        ResearchTests.Grant(d, 0, Classic, "te_tradetaxes");
        d.Step();
        int taxed = Profit();
        ResearchTests.Grant(d, 0, Classic, "te_banking");
        d.Step();
        Assert.True(taxed > plain, $"{plain} -> {taxed}");
        Assert.True(Profit() > taxed, $"{taxed} -> {Profit()}");
    }

    [Fact]
    [Trait("Rule", "TECH-032")]
    public void Concentration_brings_mana_back_faster()
    {
        int ManaAfter(bool concentration)
        {
            using var d = new SimDriver(TestMap(32, 12, Structure("euro.castle", 0, 2, 2), Unit("euro.mage", 0, 10, 6)), ruleset: Classic);
            if (concentration)
            {
                ResearchTests.Grant(d, 0, Classic, "te_concentration");
            }

            EntityId mage = OfType(d.World, "euro.mage").Single();
            d.World.IsLocked = false;
            d.World.Manas.Get(mage).MilliMana = 0;
            d.World.IsLocked = true;
            d.RunUntil(() => false, 400);
            return d.World.Manas.Get(mage).MilliMana;
        }

        Assert.True(ManaAfter(true) > ManaAfter(false));
    }

    [Fact]
    [Trait("Rule", "TECH-020")]
    public void Fanaticism_makes_the_kingdoms_buildings_harder_to_capture()
    {
        // Resistance mainly makes capture harder (manual "Buildings"). Against blows it changes little: RESISTANCE 40 with
        // the placeholder scale of 1000 lowers a footman's 9 by under 1 % (UNK-08, UNK-19).
        int CaptureTicks(bool fanaticism)
        {
            using var d = new SimDriver(
                TestMap(48, 16, Structure("euro.castle", 1, 40, 2), Structure("euro.house", 1, 14, 4), Unit("euro.peon", 0, 12, 4), Unit("euro.peon", 0, 12, 5)),
                ruleset: Classic);
            if (fanaticism)
            {
                ResearchTests.Grant(d, 1, Classic, "te_fanaticism");
            }

            EntityId house = OfType(d.World, "euro.house").Single();
            d.Order(0, CommandKind.Capture, [.. OfType(d.World, "euro.peon")], target: house);
            return d.RunUntil(() => d.Count(Events.SimEventKind.Captured) == 1, 3000);
        }

        int plain = CaptureTicks(false);
        int fanatic = CaptureTicks(true);
        Assert.True(fanatic > plain, $"captured in {plain} ticks without Fanaticism, {fanatic} with it");
    }
}
