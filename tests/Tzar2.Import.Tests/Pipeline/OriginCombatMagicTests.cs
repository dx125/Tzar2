using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Tzar2.Core.Ids;
using Tzar2.Import.Pipeline;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Scenarios;
using Tzar2.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Import.Tests.Pipeline;

/// <summary>
/// GAME-07…11 compiled from the reference build (skipped without <c>TZAR2_ORIGIN</c>; ADRs 0090-0096): every unit, spell
/// and item of every civilization compiles; the casters' spells, the holders, inventories, minimum ranges, damage
/// rolls and items match the data files (Appendix E, OBJECTS); and a short match with the real rules runs every spell.
/// </summary>
public sealed class OriginCombatMagicTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "tzar2-origin-e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    [OriginFact]
    public void Every_unit_spell_and_item_of_every_civilization_compiles_from_the_data()
    {
        Ruleset rules = Compile(OriginPaths.RequireOrigin(), out IReadOnlyList<string> diagnostics);
        foreach (string d in diagnostics.Where(d => d.Contains("spell", StringComparison.Ordinal) || d.Contains("Holder", StringComparison.Ordinal) || d.Contains("Item", StringComparison.Ordinal)))
        {
            output.WriteLine(d);
        }

        // Every spell on a command card is a rules spell (none left out).
        Assert.DoesNotContain(diagnostics, d => d.Contains("no spell", StringComparison.Ordinal));

        // Appendix E.4: the casters' spells come from their command cards.
        AssertSpells(rules, "euro.mage", "spell_cloak", "spell_freeze", "spell_lightning", "spell_spawnbat", "spell_spawnstonegolem");
        AssertSpells(rules, "asia.mage", "spell_berserk", "spell_flamebolt", "spell_spawndragon", "spell_turntostone", "spell_whirlpool");
        AssertSpells(rules, "arab.mage", "raisedead", "spell_firerain", "spell_magicvision", "spell_spawncarpet", "spell_spawnjin");
        AssertSpells(rules, "euro.priest", "spell_bless", "spell_heal");
        AssertSpells(rules, "asia.priest", "spell_heal", "spell_raiseexp");
        AssertSpells(rules, "asia.ninja", "cloak", "murder");

        // TECHTREE [orders] gates the spells per civilization.
        Assert.Equal(rules.TechnologyId("te_lightning"), rules.OrderTechnology("spell_lightning", "euro"));
        Assert.Equal(rules.TechnologyId("te_necromancy"), rules.OrderTechnology("raisedead", "arab"));
        Assert.Equal(rules.TechnologyId("te_healing"), rules.OrderTechnology("spell_heal", "asia"));

        // Every summon exists; summoned types carry their mana cost (cost suffix m).
        foreach (SpellDefinition s in rules.Spells.Where(s => s.Summon >= 0))
        {
            Assert.InRange(s.Summon, 0, rules.EntityTypes.Count - 1);
        }

        Assert.True(Get(rules, "comm.bat").Traits.ManaCost > 0);

        // Holders: OBJECTS max_obj_in_holder, [accept] and [damage].
        HolderDefinition castle = Get(rules, "euro.castle").Traits.Holder!;
        Assert.Equal(50, castle.Capacity);
        Assert.True(castle.CanHold(Get(rules, "euro.footman").Id));
        Assert.Equal(new DamageShare(90, 10), castle.ShareFor(DamageClass.Sword));
        Assert.Equal(new DamageShare(80, 20), castle.ShareFor(DamageClass.Cannon));
        Assert.Equal(50, Get(rules, "arab.outpost").Traits.Holder!.Capacity);
        Assert.Equal(24, Get(rules, "comm.galleon").Traits.Holder!.Capacity);
        Assert.Equal(16, Get(rules, "comm.hulk").Traits.Holder!.Capacity);
        Assert.Equal(8, Get(rules, "comm.xebec").Traits.Holder!.Capacity);
        Assert.True(Get(rules, "comm.galleon").Traits.Holder!.CanHold(Get(rules, "comm.catapult").Id)); // earthunits
        HolderDefinition carpet = Get(rules, "comm.carpet").Traits.Holder!;
        Assert.True(carpet.CanHold(Get(rules, "asia.archer").Id) && carpet.CanHold(Get(rules, "euro.knight").Id));
        Assert.False(carpet.CanHold(Get(rules, "comm.catapult").Id));
        Assert.True(Get(rules, "comm.carpet").IsMobile);

        // Inventories, minimum ranges, damage rolls, projectiles.
        Assert.Equal(8, Get(rules, "euro.footman").Traits.InventorySlots);
        Assert.Equal(4 * 2048, Get(rules, "comm.catapult").Traits.Armament!.MinRangeUnits);
        Assert.Equal(2 * 2048, Get(rules, "comm.tower_stone2").Traits.Armament!.MinRangeUnits);
        Assert.Equal(14, Get(rules, "comm.tower_wood").Traits.Armament!.MaxAttack);
        Assert.Equal(DamageClass.Cannon, Get(rules, "comm.hcatapult").Traits.Armament!.DamageClass);
        Assert.Equal("stone", rules.Projectiles[Get(rules, "comm.catapult").Traits.Armament!.Projectile].Key);
        Assert.Equal("arrow", rules.Projectiles[Get(rules, "euro.longbowman").Traits.Armament!.Projectile].Key);
        Assert.NotNull(Get(rules, "arab.jihad_warrior").Traits.Armament);
        Assert.NotNull(Get(rules, "euro.priest").Traits.Armament);

        // Items: the eight item classes, carried by who their [enter] lists.
        foreach (string key in new[] { "comm.item", "comm.item_armor", "comm.item_flag", "comm.item_health", "comm.item_magic", "comm.item_plusplus", "comm.item_scroll", "comm.item_soul", "comm.resitem" })
        {
            Assert.NotNull(Get(rules, key).Traits.Item);
        }

        Assert.True(Get(rules, "comm.item_flag").Traits.Item!.CanBeHeldBy(Get(rules, "arab.peon").Id));
        Assert.False(Get(rules, "comm.item_flag").Traits.Item!.CanBeHeldBy(Get(rules, "arab.footman").Id));
        Assert.True(Get(rules, "comm.item_health").Traits.Item!.CanBeHeldBy(Get(rules, "euro.knight").Id));

        // Conversions, crafting, crusades, heroes.
        Assert.Contains(Get(rules, "arab.peon").Id, Get(rules, "arab.jihad_warrior").Traits.ConvertsFrom!.From);
        Assert.Contains(Get(rules, "asia.monk").Id, Get(rules, "asia.priest").Traits.ConvertsFrom!.From);
        Assert.NotNull(Get(rules, "arab.magicworkshop").Traits.Crafter);
        Assert.Equal(Get(rules, "euro.crusadeanchor").Id, rules.Combat.CrusadeAnchor);
        Assert.True(Get(rules, "euro.crusadeanchor").Traits.Cost.Sum() > 0);
        Assert.All(new[] { "euro", "asia", "arab" }, civ => Assert.True(rules.Combat.NamesOf(civ).Length >= 10, civ));
    }

    [OriginFact]
    public void Every_spell_runs_in_a_match_with_the_real_rules()
    {
        Ruleset rules = Compile(OriginPaths.RequireOrigin(), out _);
        var objects = new List<MapObject> { MapObject.AtTile("euro.castle", 0, 26, 2), MapObject.AtTile("euro.castle", 0, 32, 2), MapObject.AtTile("euro.castle", 0, 26, 17), MapObject.AtTile("euro.castle", 0, 32, 17) }; // population room for the summons
        string[] casters = ["euro.mage", "asia.mage", "arab.mage", "euro.priest", "asia.priest", "asia.ninja"];
        for (int i = 0; i < casters.Length; i++)
        {
            objects.Add(new MapObject(casters[i], 0, (4 + (i * 3)) * 2048, 10 * 2048));
        }

        objects.Add(new MapObject("asia.peon", 0, 17 * 2048, 11 * 2048)); // a pupil for the Asian priest's Mastery (TECH-028)
        for (int i = 0; i < 16; i++)
        {
            objects.Add(new MapObject("euro.knight", 1, (2 + i) * 2048, 15 * 2048));
            objects.Add(new MapObject("euro.footman", 0, (2 + i) * 2048, 7 * 2048));
        }

        // A pond of 6 x 6 tiles at (34, 8): the whirlpool forms only on water (MAG-024, ReTzar 0x4733c4).
        byte[] water = new byte[40 * 24];
        for (int y = 8; y < 14; y++)
        {
            for (int x = 34; x < 40; x++)
            {
                water[(y * 40) + x] = 1;
            }
        }

        MapData map = ScenarioMaps.WithObjects(ScenarioMaps.Build("origin-magic", 40, 24, water, []), objects);
        using var sim = new Simulation(rules, map, new SimulationConfig { MatchSeed = 3, PlayerCount = 2, PopulationLimit = 1000 });
        World w = sim.World;
        for (int t = 0; t < rules.Technologies.Count; t++)
        {
            w.Players[0].AddTechnology(t);
        }

        // Only the technology bits matter here (the order gates of the spells); plenty of resources for the summons.
        for (int r = 0; r < rules.ResourceTypes.Count; r++)
        {
            w.Players[0].Resources[r] = 1_000_000;
        }

        var list = new CommandList();
        var cast = new HashSet<int>();
        var next = new Dictionary<uint, int>(); // per caster: spells cast so far
        var rejections = new SortedSet<CommandRejection>();
        for (int tick = 0; tick < 2400; tick++)
        {
            list.Clear();
            if (tick == 0)
            {
                var hold = new CommandList();
                hold.Add(CommandKind.SetStance, [.. w.Combats.Entities.ToArray().Where(e => w.Identities.Get(e).Owner == 1)], arg0: (int)CombatStance.HoldFire);
                list.AppendStamped(1, hold);
            }

            if (tick % 20 == 0)
            {
                var batch = new CommandList();
                foreach (EntityId c in w.Casters.Entities.ToArray())
                {
                    EntityTraits traits = w.TypeOf(c).Traits;
                    if (w.Identities.Get(c).Owner != 0 || traits.Spells.IsEmpty || w.Casters.Get(c).Phase != CastPhase.None)
                    {
                        continue; // a new order would start the cast again (no spell waits, ADR 0266)
                    }

                    ushort spell = traits.Spells[next.GetValueOrDefault(c.Value) % traits.Spells.Length]; // the next spell once one took effect
                    SpellDefinition s = rules.Spells[spell];
                    EntityId target = Nearest(w, c, own: s.Affects == SpellAffects.Own, s);
                    if (s.Effect == SpellEffect.Experience)
                    {
                        w.Experiences.Get(c).Level = 5; // a teacher above its pupil (ReTzar, 0x473bf0)
                    }

                    Position at = w.Positions.Get(c);
                    Position aim = target.IsNone ? at : w.Positions.Get(target);
                    if (s.MoveMs > 0)
                    {
                        (target, aim) = (EntityId.None, new Position(37 * 2048, 11 * 2048)); // the middle of the pond
                    }
                    batch.Add(CommandKind.CastSpell, [c], aim.X, aim.Y, target, arg0: spell);
                    w.Manas.Get(c).RaisedMax = 1_000; // summons cost up to 800 (ADR 0267): a pool that holds them
                    w.Manas.Get(c).MilliMana = 100_000_000 / 100; // plenty of mana for the test
                }

                list.AppendStamped(0, batch);
            }

            foreach (EntityId c in w.Casters.Entities.ToArray())
            {
                if (w.Identities.Get(c).Owner == 0)
                {
                    w.Healths.Get(c).Current = w.Healths.Get(c).Max; // confused knights and fire rain spare no one; the casters must last
                }
            }

            sim.Tick(list);
            foreach (SimEvent e in w.Events.Events)
            {
                if (e.Kind == SimEventKind.SpellCast)
                {
                    cast.Add(e.Arg);
                    next[e.Entity.Value] = next.GetValueOrDefault(e.Entity.Value) + 1;
                }
                else if (e.Kind == SimEventKind.CommandRejected)
                {
                    rejections.Add((CommandRejection)e.Arg);
                }
            }
        }

        output.WriteLine($"spells cast: {string.Join(", ", cast.Select(i => rules.Spells[i].Key).OrderBy(k => k))}; rejections: {string.Join(",", rejections)}");
        string[] expected =
        [
            "spell_lightning", "spell_freeze", "spell_spawnbat", "spell_spawnstonegolem", "spell_cloak", "spell_heal", "spell_bless", "spell_raiseexp", "spell_turntostone",
            "spell_flamebolt", "spell_spawndragon", "spell_berserk", "spell_whirlpool", "spell_firerain", "spell_magicvision", "spell_spawncarpet", "spell_spawnjin", "raisedead", "cloak",
        ];
        Assert.All(expected, k => Assert.Contains(rules.SpellId(k), cast));
        output.WriteLine($"spells cast: {string.Join(", ", cast.Select(i => rules.Spells[i].Key).OrderBy(k => k))}");
    }

    private static EntityId Nearest(World w, EntityId caster, bool own, SpellDefinition s)
    {
        byte owner = w.Identities.Get(caster).Owner;
        Position at = w.Positions.Get(caster);
        EntityId best = EntityId.None;
        long bestD = long.MaxValue;
        foreach (EntityId e in w.Combats.Entities.ToArray())
        {
            if (e == caster || (w.Identities.Get(e).Owner == owner) != own || !w.TypeOf(e).IsMobile || w.IsHeld(e) || !s.AffectsType(w.Identities.Get(e).TypeId))
            {
                continue;
            }

            Position p = w.Positions.Get(e);
            long d = Core.Numerics.IntMath.DistanceSquared(at.X, at.Y, p.X, p.Y);
            if (d < bestD)
            {
                bestD = d;
                best = e;
            }
        }

        return best;
    }

    private static void AssertSpells(Ruleset rules, string key, params string[] spells)
        => Assert.Equal(spells.Order(StringComparer.Ordinal), Get(rules, key).Traits.Spells.Select(s => rules.Spells[s].Key).Order(StringComparer.Ordinal));

    private Ruleset Compile(string origin, out IReadOnlyList<string> diagnostics)
    {
        string subset = Path.Combine(_work, "origin");
        foreach (string file in Directory.EnumerateFiles(origin, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(origin, file).Replace('\\', '/');
            string lower = relative.ToLowerInvariant();
            if ((lower.StartsWith("data/", StringComparison.Ordinal) && (lower.EndsWith(".ini", StringComparison.Ordinal) || lower.EndsWith(".txt", StringComparison.Ordinal)))
                || (lower.StartsWith("images/", StringComparison.Ordinal) && lower.EndsWith(".dat", StringComparison.Ordinal)))
            {
                string target = Path.Combine(subset, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        ImportResult import = ImportPipeline.Run(new ImportOptions { OriginPath = subset, OutputDirectory = Path.Combine(_work, "out"), SkipEditionDetection = true });
        Assert.True(import.Report.Succeeded, string.Join("\n", import.Report.Errors));
        using PackageSource package = PackageSource.Open(import.PackagePath);
        var vfs = new VirtualFileSystem();
        vfs.Mount(new EngineDefaultsSource());
        vfs.Mount(package);
        CompiledRuleset compiled = RulesetCompiler.Compile(vfs, "classic");
        Assert.Empty(compiled.Warnings);
        diagnostics = compiled.Diagnostics;
        return compiled.Ruleset;
    }

    private static EntityTypeDefinition Get(Ruleset ruleset, string key)
    {
        int id = ruleset.EntityTypeId(key);
        Assert.True(id >= 0, $"{key} missing");
        return ruleset.EntityTypes[id];
    }
}
