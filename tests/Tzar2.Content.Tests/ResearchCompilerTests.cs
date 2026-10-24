using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Xunit;

namespace Tzar2.Content.Tests;

/// <summary>
/// Technologies, upgrades, per-civ trees, research sites, modifiers and the M3 traits compiled from the synthetic
/// tables (ADRs 0040-0044; no original data, REQ-LEGAL-03).
/// </summary>
public class ResearchCompilerTests
{
    private static readonly Ruleset Classic = SyntheticLegacyContent.CompileRuleset();

    private static EntityTypeDefinition Type(string key)
        => Classic.EntityTypes[Classic.EntityTypeId(key) is >= 0 and var id ? id : throw new InvalidOperationException($"no {key}")];

    private static ushort Tech(string key) => (ushort)(Classic.TechnologyId(key) is >= 0 and var id ? id : throw new InvalidOperationException($"no {key}"));

    [Fact]
    public void Every_technology_and_upgrade_of_the_tables_compiles_in_key_order()
    {
        Assert.Equal(87, Classic.Technologies.Count);
        Assert.Equal(12, Classic.Upgrades.Count);
        Assert.Equal(Classic.Technologies.Select(t => t.Key).OrderBy(k => k, StringComparer.Ordinal), Classic.Technologies.Select(t => t.Key));
        Assert.All(Classic.Technologies.Select((t, i) => (t, i)), x => Assert.Equal(x.i, x.t.Id));
        TechnologyDefinition fields = Classic.Technologies[Tech("te_fields")];
        Assert.Equal(10_003, fields.TimeMs); // invented: 10,003 ms + 1,000 per position in the synthetic list
        Assert.Equal(104, fields.Cost[Classic.ResourceId("gold")]);
        Assert.Equal("Synthetic 0", fields.Name);
        Assert.Equal(["euro", "arab", "asia", "comm"], Classic.Civilizations); // ruleset.civilizations, then the common key (ADR 0211)
        Assert.Equal((1 << 4) - 1, Classic.HudResourceMask); // food, wood, stone, gold; iron hidden (GAME-01)
    }

    [Fact]
    public void Prerequisites_come_from_the_tables_and_the_civilization_trees()
    {
        // TSV DEPENDS ON: pikemen need Halberd (any civ); the synthetic euro tree: Halberd needs Scale Armor, knights a stable.
        EntityTraits pikeman = Type("euro.pikeman").Traits;
        Assert.Contains(pikeman.Requirements, r => r.Civilization == string.Empty && r.Technologies.SequenceEqual([Tech("te_halberd")]));
        TechnologyDefinition halberd = Classic.Technologies[Tech("te_halberd")];
        RequirementSet euro = Assert.Single(halberd.Requirements);
        Assert.Equal("euro", euro.Civilization);
        Assert.Equal(new[] { Tech("te_scalearmor") }, euro.Technologies.ToArray());
        Assert.Contains(Type("euro.knight").Traits.Requirements, r => r.Civilization == "euro" && r.Types.Contains(Type("euro.stable").Id));

        // The table names an unknown technology for cows; the tree's te_livestock replaces it.
        EntityTraits cow = Type("comm.cow").Traits;
        RequirementSet cowSet = Assert.Single(cow.Requirements);
        Assert.Equal(("euro", false), (cowSet.Civilization, cowSet.Unsatisfiable));
        Assert.Equal(new[] { Tech("te_livestock") }, cowSet.Technologies.ToArray());

        // Order gates (TECHTREE [orders]).
        Assert.Contains(Classic.OrderGates, g => g.Order == "spell_heal" && g.Civilization == "euro" && g.Technology == Tech("te_healing"));
    }

    [Fact]
    public void Order_gates_are_found_without_allocating_and_near_misses_are_not()
    {
        Assert.NotEmpty(Classic.OrderGates);
        foreach (OrderGate g in Classic.OrderGates)
        {
            Assert.Equal(g.Technology, Classic.OrderTechnology(g.Order, g.Civilization));
            Assert.Equal(-1, Classic.OrderTechnology(g.Order + "x", g.Civilization));
            Assert.Equal(-1, Classic.OrderTechnology(g.Order, g.Civilization + "x"));
            Assert.Equal(-1, Classic.OrderTechnology(g.Order[..^1], g.Civilization));
            Assert.Equal(-1, Classic.OrderTechnology(g.Order + "\n" + g.Civilization, string.Empty));
        }

        Assert.Equal(-1, Classic.OrderTechnology(string.Empty, string.Empty));
        OrderGate first = Classic.OrderGates[0];
        Classic.OrderTechnology(first.Order, first.Civilization);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Classic.OrderTechnology(first.Order, first.Civilization);
            Classic.OrderTechnology("spell_nothing", "euro");
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Research_sites_come_from_the_command_cards()
    {
        ProducerDefinition blacksmith = Type("euro.blacksmith").Traits.Producer!;
        Assert.True(blacksmith.Produces.IsEmpty);
        Assert.True(blacksmith.CanResearch(Tech("te_halberd")));
        Assert.True(blacksmith.CanResearch(Tech("te_temperiron")));
        Assert.False(blacksmith.CanResearch(Tech("te_fields")));
        Assert.True(Type("euro.castle").Traits.Producer!.CanResearch(Tech("te_feudalism")));
        Assert.True(Type("euro.castle").Traits.Producer!.CanProduce(Type("euro.peon").Id));

        // Upgrades: towers, walls and gates convert in place; galleons buy cannon levels in order.
        UpgradeDefinition guard = Classic.Upgrades[Classic.UpgradeId("to_wtower2")];
        Assert.True(guard.IsConversion);
        Assert.Equal(Type("comm.tower_wood2").Id, guard.ConvertedType(Type("comm.tower_wood").Id));
        Assert.True(Type("comm.tower_wood").Traits.Producer!.CanUpgrade(guard.Id));
        UpgradeDefinition cannon = Classic.Upgrades[Classic.UpgradeId("to_stower2_1")];
        Assert.Contains(cannon.Requirements, r => r.Civilization == "euro" && r.Technologies.Contains(Tech("te_cannontower")));
        UpgradeDefinition level3 = Classic.Upgrades[Classic.UpgradeId("to_ship_level3")];
        Assert.False(level3.IsConversion);
        Assert.Equal(new[] { (ushort)Classic.UpgradeId("to_ship_level2") }, level3.After.ToArray());
        Assert.True(Type("comm.galleon").Traits.Producer!.CanUpgrade(level3.Id));
        Assert.True(Type("comm.galleon").Traits.Producer!.Produces.IsEmpty);
        ModifierDefinition shot = Assert.Single(level3.Effects);
        Assert.Equal((ModifierStat.FireRate, 33), (shot.Stat, shot.Add)); // "fire more frequently" (TECH-036): period 2400 -> 1900
    }

    [Fact]
    public void Effects_resolve_their_filters_to_types()
    {
        ModifierDefinition fields = Assert.Single(Classic.Technologies[Tech("te_fields")].Effects);
        Assert.Equal((ModifierStat.CarryCapacity, 2), (fields.Stat, fields.Add)); // a field's peasant carries 2 more (TECH-006)
        Assert.Equal(new[] { Type("comm.field").Id }, fields.Types.ToArray());

        ModifierDefinition temper = Assert.Single(Classic.Technologies[Tech("te_temperiron")].Effects);
        Assert.True(temper.AppliesTo(Type("euro.footman").Id));
        Assert.False(temper.AppliesTo(Type("euro.archer").Id)); // arrows are not swords
        Assert.False(temper.AppliesTo(Type("comm.tower_wood").Id)); // category unit

        ModifierDefinition armor = Assert.Single(Classic.Technologies[Tech("te_scalearmor")].Effects);
        // The units on foot (ReTzar, 0x4de880): not the knights, not the siege weapons (ADR 0191).
        Assert.Equal(1, armor.Add);
        Assert.Contains(Type("euro.footman").Id, armor.Types.ToArray());
        Assert.Contains(Type("euro.mage").Id, armor.Types.ToArray());
        Assert.DoesNotContain(Type("euro.knight").Id, armor.Types.ToArray());
        Assert.DoesNotContain(Type("comm.catapult").Id, armor.Types.ToArray());

        ModifierDefinition supply = Assert.Single(Classic.Technologies[Tech("te_supplystorage")].Effects);
        Assert.Equal((ModifierStat.PopulationLimit, 2500), (supply.Stat, supply.BasisPoints)); // a quarter more (TECH-022)
        Assert.Empty(Classic.Technologies[Tech("te_halberd")].Effects); // unlocks only
    }

    [Fact]
    public void M3_traits_compile_from_the_tables_and_the_yaml()
    {
        // GAME-04: capture flags (OBJECTS type), EMIT influence; protected deletes.
        EntityTraits castle = Type("euro.castle").Traits;
        Assert.NotNull(castle.Capturable);
        Assert.False(castle.Capturable!.Instant);
        Assert.Equal((300, -150), (castle.Influence!.Own, castle.Influence.Other));
        Assert.True(castle.ProtectedDelete);
        Assert.Null(Type("comm.field").Traits.Capturable);
        Assert.True(Type("euro.peon").Traits.IsCapturer);

        // GAME-01: mana (max_manna) and mana costs.
        Assert.Equal(90, Type("euro.mage").Traits.ManaPool!.Max);
        Assert.Equal(120, Type("euro.mage").Traits.ManaPool!.RegenPerMinute); // 2 a second (ReTzar 0x502ea2)

        // GAME-02: cows hold milk, regrow it and leave meat; fishing boats sail; fish patches have no footprint.
        EntityTypeDefinition cow = Type("comm.cow");
        Assert.Equal(EntityCategory.Unit, cow.Traits.Category);
        Assert.Equal((40, 4000, true), (cow.Traits.ResourceNode!.Amount, cow.Traits.ResourceNode.RegenMs, cow.Traits.ResourceNode.KeepWhenEmpty));
        Assert.Equal(Type("comm.meat").Id, cow.Traits.SpawnOnDeath);
        Assert.Equal(Tech("te_cowpoaching"), cow.Traits.Capturable!.RequiredTechnology);
        Assert.True(cow.Traits.Capturable.Instant);
        Assert.Equal(Locomotor.Ship, Type("comm.fishing_boat").Locomotor);
        Assert.NotNull(Type("comm.fishing_boat").Traits.Harvester);
        EntityTypeDefinition fish = Type("comm.fishpatch");
        Assert.False(fish.HasFootprint);
        Assert.Equal((EntityCategory.Resource, 333), (fish.Traits.Category, fish.Traits.ResourceNode!.Amount));
        Assert.Equal(DropOffAccess.Water, Type("euro.harbor").Traits.DropOffAccess);
        Assert.Equal(PlacementKind.Coast, Type("euro.harbor").Traits.Placement);
        Assert.True(Type("euro.lumber").Traits.AutoGather);
        Assert.Equal(120_000, Type("comm.meat").Traits.LifetimeMs);
        Assert.NotNull(Type("comm.field").Traits.Decay);

        // GAME-03: walls, gates, bridges; template removals pass on (fields and walls do not burn).
        Assert.True(Type("comm.wall_wood").Traits.IsWall);
        Assert.False(Type("comm.wall_wood").Traits.Burnable);
        Assert.False(Type("comm.field").Traits.Burnable);
        Assert.True(Type("euro.house").Traits.Burnable);
        Assert.True(Type("euro.house").Traits.LeavesRuin);
        Assert.True(Type("euro.house").Traits.IsRepairable);
        Assert.True(Type("comm.galleon").Traits.IsRepairable);
        Assert.False(Type("comm.galleon").LeavesCorpse);
        EntityTypeDefinition gate = Type("comm.gate_wood");
        Assert.Equal((7, 1), (gate.FootprintWidth, gate.FootprintHeight)); // gw0_anim's cells (CTRL-024)
        Assert.Equal((1, 6), (gate.Turned.Width, gate.Turned.Height)); // gw2_anim's
        Assert.Equal(new byte[] { 0, 0, 1, 1, 1, 0, 0 }, gate.Traits.Gate!.Passage.ToArray()); // the door, the middle three
        Assert.Equal(new byte[] { 0, 1, 1, 1, 0, 0 }, gate.Traits.Gate!.TurnedPassage.ToArray());
        Assert.True(Type("comm.bridge").Traits.IsBridge);
        Assert.Equal(PlacementKind.Bridge, Type("comm.bridge").Traits.Placement);

        // Towers: buildings armed from the unit table.
        EntityTypeDefinition tower = Type("comm.tower_wood");
        Assert.True(tower.HasFootprint);
        Assert.Equal(390, tower.MaxHealth);
        Assert.Equal((6, 7 * 2048), (tower.Traits.Armament!.Attack, tower.Traits.Armament.RangeUnits));
    }

    [Theory]
    [InlineData("te_fields: { Technology: { effects: [{ stat: speed, percent: 5 }] } }", "unknown stat 'speed'")]
    [InlineData("te_fields: { Technology: { effects: [{ stat: attack, groups: [nope] }] } }", "unknown group 'nope'")]
    [InlineData("te_fields: { Technology: { effects: [{ stat: attack, percent: 5, wizards: 1 }] } }", "unknown field 'wizards'")]
    [InlineData("te_fields: { Armament: { attack: 1 } }", "is not allowed on a technology")]
    [InlineData("euro.castle: { Researcher: { technologies: [te_nothing] } }", "unknown 'te_nothing'")]
    [InlineData("to_ship_level3: { Upgrade: { after: [to_nothing] } }", "unknown upgrade 'to_nothing'")]
    public void Invalid_research_rules_fail(string yaml, string expected)
    {
        RulesException ex = Assert.Throws<RulesException>(() => SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions { OverrideTexts = [new KeyValuePair<string, string>("test.yaml", yaml)] }));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Research_data_is_in_the_ruleset_hash()
    {
        Ruleset patched = SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions
        {
            OverrideTexts = [new KeyValuePair<string, string>("test.yaml", "te_fields: { Technology: { effects: [{ stat: gatherRate, percent: 30, classes: [field] }] } }")],
        });
        Assert.NotEqual(Classic.ContentHash, patched.ContentHash);
        Assert.Equal(Classic.ContentHash, SyntheticLegacyContent.CompileRuleset().ContentHash);
    }
}
