using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Xunit;

namespace Tzar2.Content.Tests;

/// <summary>The ruleset compiler on synthetic legacy tables (ADRs 0031-0033; REQ-LEGAL-03: no original data in CI).</summary>
public class RulesetCompilerTests
{
    private static readonly Ruleset Classic = SyntheticLegacyContent.CompileRuleset();

    private static EntityTypeDefinition Type(Ruleset ruleset, string key)
        => ruleset.EntityTypes[ruleset.EntityTypeId(key) is >= 0 and var id ? id : throw new InvalidOperationException($"no {key}")];

    private static int Resource(string key) => Classic.ResourceId(key);

    [Fact]
    public void Classic_compiles_the_listed_classes_with_dense_ids_in_key_order()
    {
        string[] keys = Classic.EntityTypes.Select(t => t.Key).ToArray();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), keys);
        // M3 imports every unit and building of the tables (ADR 0040), plus the YAML-only tree, meat and birds (ADR 0104);
        // stream E adds the item classes, the crusade anchor and the fog revealer (ADRs 0094, 0095) and the armor item.
        Assert.Equal(
            [
                "arab.jihad_warrior", "arab.magicworkshop", "arab.peon", "comm.bat", "comm.bear", "comm.bird",
                "comm.bridge", "comm.carpet", "comm.catapult", "comm.cow", "comm.dragon", "comm.dummy", "comm.dwarf",
                "comm.eagle", "comm.field", "comm.fishing_boat", "comm.fishpatch", "comm.fog_revealer", "comm.galleon",
                "comm.gate_stone", "comm.gate_wood", "comm.ghost", "comm.ground_object", "comm.hulk", "comm.item", "comm.item_armor", "comm.item_flag",
                "comm.item_health", "comm.item_magic", "comm.item_plusplus", "comm.item_scroll", "comm.item_soul",
                "comm.jinn", "comm.magicgateway", "comm.meat", "comm.ork", "comm.res_gold", "comm.res_stone",
                "comm.resitem", "comm.skeleton", "comm.tower_stone", "comm.tower_stone2", "comm.tower_wood",
                "comm.tower_wood2", "comm.tree", "comm.troll", "comm.wall_stone", "comm.wall_wood", "comm.xebec", "euro.academy",
                "euro.archer", "euro.barrack", "euro.blacksmith", "euro.castle", "euro.cathedral", "euro.crusadeanchor",
                "euro.crusader", "euro.farm", "euro.footman", "euro.harbor", "euro.house", "euro.knight", "euro.lumber",
                "euro.maceman", "euro.mage", "euro.magetower", "euro.market", "euro.outpost", "euro.peon", "euro.pikeman",
                "euro.priest", "euro.stable", "euro.storage", "euro.tavern", "euro.tcguild", "euro.trader", "euro.workshop",
            ],
            keys);
        Assert.Equal(["food", "wood", "stone", "gold", "iron"], Classic.ResourceTypes);
        Assert.Equal(["grass", "water"], Classic.TerrainTypes.Select(t => t.Key));
        Assert.Equal(0, Classic.TerrainTypes[0].Id); // test maps (ScenarioMaps) use grass = 0, water = 1
        Assert.True(Classic.TerrainTypes[1].IsWater);
    }

    [Fact]
    public void Unit_columns_become_traits()
    {
        EntityTypeDefinition peon = Type(Classic, "euro.peon");
        EntityTraits t = peon.Traits;
        Assert.Equal(EntityCategory.Unit, t.Category);
        Assert.Equal("peon", t.LegacyClass);
        Assert.Equal("euro", t.Civilization);
        Assert.Equal(45, t.Cost[Resource("food")]);
        Assert.Equal(0, t.Cost[Resource("wood")]);
        Assert.Equal(9 * LegacyTime.MsPerTimeUnit, t.BuildTimeMs);
        Assert.Equal(7 * 2048, t.SightUnits);
        Assert.Equal(44, peon.MaxHealth);
        Assert.Equal(1, t.Defence);
        Assert.Equal(Locomotor.Foot, peon.Locomotor);
        Assert.Equal(40 * 64, peon.SpeedUnitsPerSecond); // measured 40 px/s whatever its SPEED (ADR 0172)
        Assert.Equal(5 * 10 * 64, Type(Classic, "arab.jihad_warrior").SpeedUnitsPerSecond); // not measured: SPEED 5 x 10 px/s (UNVERIFIED)
        Assert.Equal(7 * 64, peon.Radius);
        Assert.True(peon.LeavesCorpse);
        Assert.Equal(1, t.PopulationCost);
        Assert.NotNull(t.Armament);
        Assert.Equal(3, t.Armament!.Attack);
        Assert.Equal(0, t.Armament.RangeUnits);
        Assert.Equal(3000, t.Armament.CooldownMs); // 6,000 / RATE 2
        Assert.Equal(1500, t.Armament.HitDelayMs);
        Assert.False(t.Armament.AutoAcquire);
        Assert.Equal(DamageClass.Sword, t.Armament.DamageClass);
        Assert.Equal(10, t.Harvester!.Capacity);
        Assert.True(t.Harvester.CanGather(Resource("wood")));
        Assert.True(t.Harvester.CanGather(Resource("iron"))); // manual ch. 5: peasants carry iron too

        EntityTraits archer = Type(Classic, "euro.archer").Traits;
        Assert.Equal(7 * 2048, archer.Armament!.RangeUnits);
        Assert.Equal(DamageClass.Arrow, archer.Armament.DamageClass);
        Assert.True(archer.Armament.AutoAcquire);
        Assert.Equal(25, archer.Cost[Resource("wood")]);

        EntityTypeDefinition knight = Type(Classic, "euro.knight");
        Assert.Equal(Locomotor.Mounted, knight.Locomotor);
        Assert.Equal(10 * 64, knight.Radius);
        Assert.Equal("te_platearmor", knight.Traits.RequiredTechnology);
    }

    [Fact]
    public void Builders_producers_and_drop_offs_are_linked()
    {
        EntityTraits peon = Type(Classic, "euro.peon").Traits;
        string[] builds = peon.Builds.Select(id => Classic.EntityTypes[id].Key).ToArray();
        Assert.Contains("comm.field", builds);
        Assert.Contains("comm.wall_wood", builds);
        Assert.Contains("comm.gate_wood", builds);
        Assert.Contains("comm.bridge", builds);
        Assert.Contains("comm.tower_wood", builds);
        Assert.Contains("euro.castle", builds);
        Assert.Contains("euro.academy", builds);
        Assert.DoesNotContain("comm.tree", builds);
        Assert.DoesNotContain("comm.res_gold", builds);

        EntityTypeDefinition castle = Type(Classic, "euro.castle");
        Assert.Equal(["euro.peon"], castle.Traits.Producer!.Produces.Select(id => Classic.EntityTypes[id].Key));
        Assert.Equal(ProducerDefinition.MaxQueueCapacity, castle.Traits.Producer.QueueCapacity);
        Assert.Equal(24, castle.Traits.PopulationProvided);
        Assert.Equal(0b11111, castle.Traits.DropOffMask);
        Assert.Equal(EntityCategory.Building, castle.Traits.Category);
        Assert.Equal(700, castle.Traits.Resistance); // buildings TSV RESISTANCE
        Assert.Equal(0, Type(Classic, "comm.field").Traits.Resistance); // "*"
        Assert.Equal(1000, Classic.Gameplay.ResistanceScale);

        Assert.Equal(["euro.archer", "euro.footman", "euro.knight", "euro.maceman", "euro.pikeman"],
            Type(Classic, "euro.barrack").Traits.Producer!.Produces.Select(id => Classic.EntityTypes[id].Key));
        Assert.Equal(1 << Resource("wood"), Type(Classic, "euro.lumber").Traits.DropOffMask);
        Assert.Equal(12, Type(Classic, "euro.house").Traits.PopulationProvided);
        Assert.Null(Type(Classic, "euro.house").Traits.Producer);
    }

    [Fact]
    public void Footprints_come_from_the_sprite_boxes_and_dat_grids()
    {
        EntityTypeDefinition castle = Type(Classic, "euro.castle");
        Assert.Equal((5, 5), (castle.FootprintWidth, castle.FootprintHeight));
        byte[] mask = castle.Footprint.ToArray();
        Assert.Equal(0, mask[0]);
        Assert.Equal(0, mask[4]);
        Assert.Equal(0, mask[20]);
        Assert.Equal(1, mask[12]);
        Assert.Equal(21, mask.Sum(b => b));

        EntityTypeDefinition house = Type(Classic, "euro.house");
        Assert.Equal((2, 2), (house.FootprintWidth, house.FootprintHeight));
        Assert.Equal(4, house.Footprint.ToArray().Sum(b => b));

        EntityTypeDefinition field = Type(Classic, "comm.field"); // no object box: the selection box (UNVERIFIED)
        Assert.Equal((3, 3), (field.FootprintWidth, field.FootprintHeight));
    }

    [Fact]
    public void Resource_nodes_take_their_amounts_from_res_ini()
    {
        EntityTraits gold = Type(Classic, "comm.res_gold").Traits;
        Assert.Equal(EntityCategory.Resource, gold.Category);
        Assert.Equal(new ResourceNodeDefinition(Resource("gold"), 5000), gold.ResourceNode);
        Assert.Equal(3000, Type(Classic, "comm.res_stone").Traits.ResourceNode!.Amount);
        Assert.Equal(new ResourceNodeDefinition(Resource("wood"), 100), Type(Classic, "comm.tree").Traits.ResourceNode);
        EntityTraits field = Type(Classic, "comm.field").Traits;
        Assert.Equal(ResourceNodeDefinition.Unlimited, field.ResourceNode!.Amount);
        Assert.Equal(17 * LegacyTime.MsPerTimeUnit, field.BuildTimeMs);
        Assert.False(Type(Classic, "comm.tree").LeavesCorpse);

        // Nodes may carry their own load (ADR 0170): a peasant brings 50 food from a cow, the harvester's 10 elsewhere.
        Assert.Equal(50, Type(Classic, "comm.cow").Traits.ResourceNode!.CarryLoad);
        Assert.Equal(0, gold.ResourceNode!.CarryLoad);
    }

    [Fact]
    public void Gameplay_settings_and_starting_resources()
    {
        GameplaySettings g = Classic.Gameplay;
        Assert.Equal(GroupMoveSpeed.Slowest, g.GroupMoveSpeed);
        Assert.Equal(["low", "medium", "high"], g.StartingResources.Select(s => s.Key));
        Assert.Equal("medium", g.StartingResources[g.DefaultStartingResources].Key);
        Assert.Equal([500, 500, 500, 500, 500], g.StartingResources[1].Amounts.ToArray());
        Assert.Equal([210, 220, 20, 10, 5], g.StartingResources[0].Amounts.ToArray());
        Assert.Equal(32 * 64, g.InteractionReachUnits);
        Assert.Equal(8 * 64, g.MeleeReachUnits);
        Assert.Equal(200, g.AcquireIntervalMs);
    }

    [Fact]
    public void Bonus_ini_groups_become_type_pair_bonuses()
    {
        ushort footman = (ushort)Classic.EntityTypeId("euro.footman");
        ushort knight = (ushort)Classic.EntityTypeId("euro.knight");
        ushort archer = (ushort)Classic.EntityTypeId("euro.archer");
        Assert.Equal(5500, Classic.BonusBasisPoints(footman, knight));
        Assert.Equal(-2500, Classic.BonusBasisPoints(archer, footman));
        Assert.Equal(0, Classic.BonusBasisPoints(knight, footman));
        Assert.Equal(2, Classic.DamageBonuses.Count);
    }

    [Fact]
    [Trait("Rule", "WAR-014")]
    public void The_blow_lands_at_the_action_row_of_the_attack_animation()
    {
        // Footman: 6 frames of 10 before the action row, 840 of 1,398 ms (600 thousandths); archer: 943 of 1,200 ms (785).
        ArmamentDefinition Arm(string key) => Type(Classic, key).Traits.Armament!;
        Assert.Equal(Arm("euro.footman").CooldownMs * 600 / 1000, Arm("euro.footman").HitDelayMs);
        Assert.Equal(Arm("euro.archer").CooldownMs * 785 / 1000, Arm("euro.archer").HitDelayMs);
        Assert.Equal(Arm("euro.maceman").CooldownMs * 50 / 100, Arm("euro.maceman").HitDelayMs); // no animation data: hitDelayPercent
    }

    [Fact]
    public void Compilation_is_deterministic_and_rulesets_differ_by_hash()
    {
        Ruleset again = SyntheticLegacyContent.CompileRuleset();
        Assert.Equal(Classic.ContentHash, again.ContentHash);
        Ruleset remastered = SyntheticLegacyContent.CompileRuleset("remastered");
        Assert.Equal("remastered", remastered.Name);
        Assert.NotEqual(Classic.ContentHash, remastered.ContentHash);
        Assert.Equal(Classic.EntityTypes.Count, remastered.EntityTypes.Count);
        Assert.Equal(GroupMoveSpeed.Slowest, remastered.Gameplay.GroupMoveSpeed);
    }

    [Fact]
    public void Overrides_patch_fields_remove_traits_and_edit_lists()
    {
        Ruleset patched = SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions
        {
            OverrideTexts =
            [
                new("test-override.yaml", """
                    gameplay:
                      groupMoveSpeed: own
                    euro.footman:
                      Health: { max: 999 }
                      -Armament:
                    euro.house:
                      DropOff: { resources+=: [wood, food] }
                    euro.castle:
                      DropOff: { resources-=: [iron] }
                    ^class.peon:
                      Harvester: { capacity: 7 }
                    """),
            ],
        });
        Assert.Equal(999, Type(patched, "euro.footman").MaxHealth);
        Assert.Null(Type(patched, "euro.footman").Traits.Armament);
        Assert.Equal((1 << 0) | (1 << 1), Type(patched, "euro.house").Traits.DropOffMask);
        Assert.Equal(0b01111, Type(patched, "euro.castle").Traits.DropOffMask);
        Assert.Equal(7, Type(patched, "euro.peon").Traits.Harvester!.Capacity);
        Assert.Equal(GroupMoveSpeed.Own, patched.Gameplay.GroupMoveSpeed);
        Assert.NotEqual(Classic.ContentHash, patched.ContentHash);
    }

    [Fact]
    public void Override_files_come_from_the_vfs()
    {
        VirtualFileSystem vfs = SyntheticLegacyContent.CreateFileSystem();
        vfs.Mount(new MemorySource("mod").Add("rules/mod.yaml", Encoding.UTF8.GetBytes("euro.archer: { Armament: { range: 9 } }\n")));
        Ruleset ruleset = RulesetCompiler.Compile(vfs, "classic", new RulesetCompileOptions { OverrideFiles = ["rules/mod.yaml"] }).Ruleset;
        Assert.Equal(9 * 2048, Type(ruleset, "euro.archer").Traits.Armament!.RangeUnits);
    }

    [Theory]
    [InlineData("euro.footman: { Tooltip: { name: x } }", "test.yaml:1: euro.footman: unknown trait 'Tooltip'")]
    [InlineData("euro.footman:\n  Health: { maximum: 3 }", "test.yaml:2: euro.footman.Health: unknown field 'maximum'")]
    [InlineData("euro.footman:\n  Cost: { silver: 3 }", "test.yaml:2: euro.footman.Cost: unknown resource 'silver'")]
    [InlineData("^a: { inherits: [^b] }\n^b: { inherits: [^a] }\neuro.x: { new: true, inherits: [^a] }", "inheritance cycle")]
    [InlineData("euro.x: { new: true, inherits: [^missing] }", "inherits unknown template '^missing'")]
    [InlineData("euro.x: { new: true, inherits: [euro.footman] }", "can only inherit templates")]
    [InlineData("euro.x:\n  new: true\n  Producible: { by: [euro.nothing] }", "unknown entity 'euro.nothing'")]
    [InlineData("euro.footmn: { Health: { max: 5 } }", "euro.footmn: no such entry to patch")]
    [InlineData("euro.footman: { new: true }", "'new: true' but the entry already exists")]
    [InlineData("euro.footman: { RevealsShroud: { sight: 2000000 } }", "euro.footman.RevealsShroud.sight: 2000000 is too large")]
    [InlineData("euro.footman: { Armament: { range: 2000000 } }", "euro.footman.Armament.range: 2000000 is too large")]
    [InlineData("euro.footman: { Armament: { hitPermille: 1001 } }", "euro.footman.Armament.hitPermille must be 0..1000")]
    [InlineData("arab.jihad_warrior: { Mobile: { speed: 100000000 } }", "arab.jihad_warrior.Mobile.pixelsPerSecond: 1000000000 is too large")]
    [InlineData("euro.footman: { Collision: { radius: 100000000 } }", "euro.footman.Collision.radius: 100000000 is too large")]
    [InlineData("euro.footman: { Health: { max: 99999999999 } }", "is not an integer")]
    [InlineData("euro.footman: { inherits-=: [^Unit] }", "euro.footman.inherits-=: a trait must be a mapping")]
    [InlineData("gameplay: { groupMoveSpeed: fastest }", "'fastest' is not slowest/own")]
    [InlineData("gameplay: { colour: red }", "gameplay.colour: unknown setting")]
    [InlineData("euro.x:\n  new: true\n  Mobile: { locomotor: wheels }", "unknown locomotor 'wheels'")]
    public void Invalid_rules_fail_with_their_source(string yaml, string expected)
    {
        RulesException ex = Assert.Throws<RulesException>(() => SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions
        {
            OverrideTexts = [new("test.yaml", yaml)],
        }));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tables_that_differ_from_the_reference_build_give_a_warning_not_an_error()
    {
        CompiledRuleset compiled = RulesetCompiler.Compile(SyntheticLegacyContent.CreateFileSystem(), "classic");
        Assert.NotNull(compiled.LegacyDataHash);
        string warning = Assert.Single(compiled.Warnings);
        Assert.StartsWith("modified installation", warning, StringComparison.Ordinal);
        Assert.Contains("GOG v1.10 EN", warning, StringComparison.Ordinal);

        // The data hash covers only what comes from the tables: YAML patches on top do not change it.
        CompiledRuleset patched = RulesetCompiler.Compile(SyntheticLegacyContent.CreateFileSystem(), "classic", new RulesetCompileOptions
        {
            OverrideTexts = [new("t.yaml", "euro.footman: { Health: { max: 1 } }")],
        });
        Assert.Equal(compiled.LegacyDataHash, patched.LegacyDataHash);
        Assert.NotEqual(compiled.Ruleset.ContentHash, patched.Ruleset.ContentHash);
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(65)]
    public void Deeply_nested_yaml_is_a_typed_error_not_a_stack_overflow(int depth)
    {
        // Review B2/R9: rules are untrusted input (mods travel through lobbies); recursion is bounded.
        string flow = "euro.footman: { Builder: { builds: " + new string('[', depth) + new string(']', depth) + " } }";
        RulesException ex = Assert.Throws<RulesException>(() => SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions { OverrideTexts = [new("deep.yaml", flow)] }));
        Assert.Contains("nested deeper than 64", ex.Message, StringComparison.Ordinal);

        var block = new System.Text.StringBuilder("a:\n");
        for (int i = 1; i <= depth; i++)
        {
            block.Append(new string(' ', i)).Append('k').Append(i).Append(":\n");
        }

        YamlException yaml = Assert.Throws<YamlException>(() => RulesYaml.Parse(block.ToString()));
        Assert.Contains("nested deeper", yaml.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_up_to_the_limit_parses()
    {
        string flow = "x: " + new string('[', 60) + new string(']', 60);
        Assert.IsType<YamlMapping>(RulesYaml.Parse(flow));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(70)]
    public void A_long_template_chain_is_a_typed_error(int depth)
    {
        // Review B2/R10.
        var sb = new System.Text.StringBuilder("^t0: {}\n");
        for (int i = 1; i < depth; i++)
        {
            sb.Append("^t").Append(i).Append(": { inherits: [^t").Append(i - 1).Append("] }\n");
        }

        sb.Append("euro.footman: { inherits+=: [^t").Append(depth - 1).Append("] }\n");
        RulesException ex = Assert.Throws<RulesException>(() => SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions { OverrideTexts = [new("chain.yaml", sb.ToString())] }));
        Assert.Contains("deeper than 64 levels", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_manifests_are_typed_errors()
    {
        // Review S5: the sprite index and footprints are untrusted package content.
        VirtualFileSystem vfs = SyntheticLegacyContent.CreateFileSystem();
        vfs.Mount(new MemorySource("bad").Add("manifests/footprints.json", Encoding.UTF8.GetBytes("[{\"source\":\"x\",\"gridWidth\":1.5,\"gridHeight\":1,\"grid\":[0]}]")));
        Assert.Throws<RulesException>(() => RulesetCompiler.Compile(vfs, "classic"));
    }

    [Fact]
    public void Classic_without_the_imported_tables_explains_what_is_missing()
    {
        var vfs = new VirtualFileSystem();
        vfs.Mount(new EngineDefaultsSource());
        RulesException ex = Assert.Throws<RulesException>(() => RulesetCompiler.Compile(vfs, "classic"));
        Assert.Contains("t2 import", ex.Message, StringComparison.Ordinal);
        Assert.Throws<RulesException>(() => RulesetCompiler.Compile(vfs, "nonexistent"));
    }

    [Fact]
    public void Legacy_tables_are_patched_by_yaml_not_replaced()
    {
        // A YAML entry with the same key as an imported one changes only the fields it names.
        Ruleset patched = SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions
        {
            OverrideTexts = [new("t.yaml", "euro.archer:\n  Cost: { wood: 0 }\n")],
        });
        EntityTraits archer = Type(patched, "euro.archer").Traits;
        Assert.Equal(35, archer.Cost[Resource("food")]);
        Assert.Equal(0, archer.Cost[Resource("wood")]);
        Assert.Equal(7, archer.Armament!.Attack);
    }

    [Fact]
    public void Engine_defaults_serve_the_rules_files()
    {
        var source = new EngineDefaultsSource();
        Assert.Contains("rules/classic.yaml", source.Paths);
        Assert.Contains("rules/remastered.yaml", source.Paths);
        string text = Encoding.UTF8.GetString(source.ReadAllBytes("rules/classic.yaml"));
        Assert.Contains("UNVERIFIED", text, StringComparison.Ordinal);
    }
}

/// <summary>The rules YAML subset (ADR 0031).</summary>
public class RulesYamlTests
{
    [Fact]
    public void Parses_block_and_flow_collections_comments_and_quotes()
    {
        var root = (YamlMapping)RulesYaml.Parse("""
            a: 1   # comment
            b: { x: [1, 2], "y z": 'q' }
            c:
              - one
              - two: 2
                three: 3
            -Trait:
            d+=: [k]
            """);
        Assert.Equal("1", ((YamlScalar)root.Get("a")!).Value);
        var b = (YamlMapping)root.Get("b")!;
        Assert.Equal(2, ((YamlSequence)b.Get("x")!).Items.Count);
        Assert.True(((YamlScalar)b.Get("y z")!).Quoted);
        var c = (YamlSequence)root.Get("c")!;
        Assert.Equal("one", ((YamlScalar)c.Items[0]).Value);
        Assert.Equal("3", ((YamlScalar)((YamlMapping)c.Items[1]).Get("three")!).Value);
        Assert.True(((YamlScalar)root.Get("-Trait")!).IsNull);
        Assert.NotNull(root.Get("d+="));
    }

    [Theory]
    [InlineData("a: 1\na: 2", "duplicate key")]
    [InlineData("a: &x 1", "unsupported YAML feature")]
    [InlineData("a: [1, 2", "unterminated")]
    [InlineData("\ta: 1", "tabs")]
    public void Rejects_malformed_yaml(string text, string expected)
    {
        YamlException ex = Assert.Throws<YamlException>(() => RulesYaml.Parse(text));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }
}
