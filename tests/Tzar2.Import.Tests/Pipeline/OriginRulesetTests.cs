using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Tzar2.Formats.Text;
using Tzar2.Formats.Tsv;
using Tzar2.Import.Pipeline;
using Tzar2.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Import.Tests.Pipeline;

/// <summary>
/// The <c>classic</c> ruleset compiled from a package imported from the reference build (skipped without
/// <c>TZAR2_ORIGIN</c>). Only the data files the compiler reads are imported (tables, IMAGES.INI, DATs), so the test runs
/// in seconds. The expected numbers are the published unit table of TDD Appendix E.2.1 and the data files' values.
/// </summary>
public sealed class OriginRulesetTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "tzar2-origin-rules-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    [OriginFact]
    public void Classic_compiles_from_the_imported_reference_tables()
    {
        string origin = OriginPaths.RequireOrigin();
        string subset = Path.Combine(_work, "origin");
        foreach (string file in Directory.EnumerateFiles(origin, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(origin, file).Replace('\\', '/');
            string lower = relative.ToLowerInvariant();
            bool wanted = (lower.StartsWith("data/", StringComparison.Ordinal) && (lower.EndsWith(".ini", StringComparison.Ordinal) || lower.EndsWith(".txt", StringComparison.Ordinal)))
                || (lower.StartsWith("images/", StringComparison.Ordinal) && lower.EndsWith(".dat", StringComparison.Ordinal));
            if (wanted)
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
        Ruleset classic = compiled.Ruleset;
        foreach (string d in compiled.Diagnostics)
        {
            output.WriteLine(d);
        }

        output.WriteLine($"{classic.EntityTypes.Count} types, hash {classic.ContentHash:X16}, legacy data hash {compiled.LegacyDataHash:X16}");

        // The reference build matches the pinned data hash: no "modified installation" warning (ADR 0032).
        Assert.Empty(compiled.Warnings);
        Assert.DoesNotContain(import.Report.Warnings, w => w.Source == "rules/classic.yaml");

        // 3 civilizations x (castle, house, barrack, storage, lumber, farm) + field + rocks + tree + the listed units.
        Assert.All(new[] { "euro", "asia", "arab" }, civ =>
        {
            foreach (string cls in new[] { "peon", "footman", "archer", "maceman", "castle", "house", "barrack", "storage", "lumber", "farm" })
            {
                Assert.True(classic.EntityTypeId($"{civ}.{cls}") >= 0, $"{civ}.{cls} missing");
            }
        });

        EntityTypeDefinition peon = Get(classic, "euro.peon");
        int food = classic.ResourceId("food");
        int wood = classic.ResourceId("wood");
        int gold = classic.ResourceId("gold");
        Assert.Equal(50, peon.Traits.Cost[food]);
        Assert.Equal(8 * LegacyTime.MsPerTimeUnit, peon.Traits.BuildTimeMs); // TIME 8 (ADR 0169)
        Assert.Equal(50, peon.MaxHealth);
        Assert.Equal(5, peon.Traits.Armament!.Attack);
        Assert.Equal(40 * 64, peon.SpeedUnitsPerSecond); // measured 40 px/s whatever its SPEED (ADR 0172)
        Assert.Equal(5 * 2048, peon.Traits.SightUnits);

        EntityTypeDefinition archer = Get(classic, "euro.archer");
        Assert.Equal((40, 20), (archer.Traits.Cost[food], archer.Traits.Cost[wood]));
        Assert.Equal(6 * 2048, archer.Traits.Armament!.RangeUnits);
        Assert.Equal(65, Get(classic, "euro.footman").MaxHealth);
        Assert.Equal(3, Get(classic, "euro.footman").Traits.Defence);
        EntityTypeDefinition knight = Get(classic, "euro.knight");
        Assert.Equal((120, 70), (knight.Traits.Cost[food], knight.Traits.Cost[gold]));
        Assert.Equal(Locomotor.Mounted, knight.Locomotor);
        Assert.Equal("te_platearmor", knight.Traits.RequiredTechnology);
        Assert.Equal(Locomotor.Mounted, Get(classic, "arab.heavy_cavalry").Locomotor);

        EntityTypeDefinition castle = Get(classic, "euro.castle");
        Assert.Equal(8000, castle.MaxHealth);
        Assert.Equal(20, castle.Traits.PopulationProvided);
        Assert.Equal((7, 7), (castle.FootprintWidth, castle.FootprintHeight)); // IMAGES.INI object 224 x 224 px
        Assert.Contains((ushort)peon.Id, castle.Traits.Producer!.Produces);
        Assert.Equal((5, 5), (Get(classic, "euro.barrack").FootprintWidth, Get(classic, "euro.barrack").FootprintHeight));
        Assert.Equal(10, Get(classic, "euro.house").Traits.PopulationProvided);
        Assert.Equal(20_000, Get(classic, "comm.res_gold").Traits.ResourceNode!.Amount);
        Assert.Equal(10_000, Get(classic, "comm.res_stone").Traits.ResourceNode!.Amount);
        Assert.Equal((4, 4), (Get(classic, "comm.res_gold").FootprintWidth, Get(classic, "comm.res_gold").FootprintHeight));

        // RES.INI starting levels; BONUS.INI: pikemen +600% against mounted units.
        Assert.Equal(400, classic.Gameplay.StartingResources[classic.Gameplay.DefaultStartingResources].Amounts[gold]);
        Assert.Equal(60_000, classic.BonusBasisPoints((ushort)classic.EntityTypeId("euro.pikeman"), (ushort)knight.Id));
        Assert.True(classic.DamageBonuses.Count > 20);

        // Deterministic: the same package compiles to the same hash.
        Assert.Equal(classic.ContentHash, RulesetCompiler.Compile(vfs, "classic").Ruleset.ContentHash);
    }

    /// <summary>
    /// GAME-06 (ADR 0040): every technology and upgrade of the reference build compiles with its cost, time, per-civ
    /// prerequisites and research sites, and every building of every civilization is in the ruleset.
    /// </summary>
    [OriginFact]
    public void Classic_compiles_every_technology_upgrade_and_building_of_every_civilization()
    {
        string origin = OriginPaths.RequireOrigin();
        Ruleset classic = CompileFromOrigin(origin, out IReadOnlyList<string> diagnostics);

        Assert.Equal(87, classic.Technologies.Count);
        Assert.Equal(12, classic.Upgrades.Count);
        Assert.Equal(["arab", "asia", "comm", "euro"], classic.Civilizations);

        // Every building row of every civilization table is a type.
        foreach (string file in Directory.EnumerateFiles(Path.Combine(origin, "DATA"), "* BUILDINGS.TXT"))
        {
            string civ = Path.GetFileName(file).Split(' ')[0].ToLowerInvariant();
            TsvTable table = TsvTable.Parse(File.ReadAllBytes(file), CodePage.Windows1252);
            foreach (TsvRow row in table.Rows.Where(r => !r.IsBlank && r[1].Trim().Length > 0))
            {
                EntityTypeDefinition type = Get(classic, $"{civ}.{row[1].Trim()}");
                Assert.True(type.HasFootprint, $"{type.Key} has no footprint");
            }
        }

        // Research sites: which building of which civilization researches each technology (the command cards).
        var researchable = new SortedSet<string>(StringComparer.Ordinal);
        var upgradable = new SortedSet<string>(StringComparer.Ordinal);
        foreach (EntityTypeDefinition t in classic.EntityTypes)
        {
            if (t.Traits.Producer is { } p)
            {
                researchable.UnionWith(p.Researches.Select(id => classic.Technologies[id].Key));
                upgradable.UnionWith(p.Upgrades.Select(id => classic.Upgrades[id].Key));
            }
        }

        string[] notResearchable = [.. classic.Technologies.Select(t => t.Key).Where(k => !researchable.Contains(k))];
        output.WriteLine($"{researchable.Count} technologies have a research site; none: {string.Join(", ", notResearchable)}");
        output.WriteLine($"upgrades with a site: {string.Join(", ", upgradable)}");
        Assert.True(researchable.Count >= 80, $"only {researchable.Count} technologies can be researched");
        Assert.All(classic.Technologies, t =>
        {
            Assert.True(t.TimeMs > 0, t.Key);
            Assert.True(t.Cost.Sum() > 0, t.Key);
        });

        // Per-civ prerequisites (TECHTREE): Halberd needs Temper Iron for the Europeans; pikemen need Halberd.
        TechnologyDefinition halberd = classic.Technologies[classic.TechnologyId("te_halberd")];
        ushort temper = (ushort)classic.TechnologyId("te_temperiron");
        Assert.Contains(halberd.Requirements, r => r.Civilization == "euro" && r.Technologies.Contains(temper));
        Assert.Contains(Get(classic, "euro.pikeman").Traits.Requirements, r => r.Technologies.Contains((ushort)classic.TechnologyId("te_halberd")));
        Assert.Contains(Get(classic, "comm.cow").Traits.Requirements, r => r.Technologies.Contains((ushort)classic.TechnologyId("te_livestock")));
        Assert.Contains(Get(classic, "asia.church").Traits.Requirements, r => r.Types.Contains((ushort)classic.EntityTypeId("asia.shaolinn")));
        Assert.Contains((ushort)classic.TechnologyId("te_halberd"), Get(classic, "euro.blacksmith").Traits.Producer!.Researches);

        // Upgrades convert towers, walls and gates in place; ship cannons are levels bought in order.
        UpgradeDefinition guard = classic.Upgrades[classic.UpgradeId("to_wtower2")];
        Assert.Equal(classic.EntityTypeId("comm.tower_wood2"), guard.ConvertedType((ushort)classic.EntityTypeId("comm.tower_wood")));
        Assert.Equal(classic.EntityTypeId("comm.wall_stone"), classic.Upgrades[classic.UpgradeId("to_wall_stone")].ConvertedType((ushort)classic.EntityTypeId("comm.wall_wood")));
        Assert.False(classic.Upgrades[classic.UpgradeId("to_ship_level3")].IsConversion);
        Assert.Contains(classic.OrderGates, g => g.Order == "spell_heal" && g.Civilization == "euro");

        // GAME-04: capture flags and EMIT influence; GAME-01: mana pools and mana costs; GAME-02: cows and fish.
        Assert.NotNull(Get(classic, "euro.castle").Traits.Capturable);
        Assert.Equal((400, -200), (Get(classic, "euro.castle").Traits.Influence!.Own, Get(classic, "euro.castle").Traits.Influence!.Other));
        Assert.Equal(100, Get(classic, "euro.mage").Traits.ManaPool!.Max);
        Assert.Equal(600, Get(classic, "comm.troll").Traits.ManaCost);
        Assert.Equal(50, Get(classic, "comm.cow").Traits.ResourceNode!.Amount);
        Assert.Equal(500, Get(classic, "comm.fishpatch").Traits.ResourceNode!.Amount);
        Assert.Equal(Locomotor.Ship, Get(classic, "comm.fishing_boat").Locomotor);
        Assert.Equal(Locomotor.Air, Get(classic, "comm.dragon").Locomotor);
        Assert.DoesNotContain(diagnostics, d => d.Contains("cannot train", StringComparison.Ordinal));
    }

    private Ruleset CompileFromOrigin(string origin, out IReadOnlyList<string> diagnostics)
    {
        string subset = Path.Combine(_work, "origin2");
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

        ImportResult import = ImportPipeline.Run(new ImportOptions { OriginPath = subset, OutputDirectory = Path.Combine(_work, "out2"), SkipEditionDetection = true });
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

    /// <summary>
    /// M3 stream F (ADRs 0103, 0104): the world traits land on the reference build's types: magic gateways summon dwarves
    /// and orcs (producer column "Gate") and ghosts and skeletons, caravans and the trading ships trade, markets and
    /// harbors are docks, taverns, wild animals.
    /// </summary>
    [OriginFact]
    public void Classic_gives_the_reference_types_their_world_traits()
    {
        Ruleset classic = CompileFromOrigin(OriginPaths.RequireOrigin(), out _);
        EntityTypeDefinition gateway = Get(classic, "comm.magicgateway");
        Assert.Equal(ClaimBy.Military, gateway.Traits.World.Claim);
        foreach (string unit in new[] { "comm.ork", "comm.dwarf", "comm.ghost", "comm.skeleton" })
        {
            Assert.True(gateway.Traits.Producer!.CanProduce((ushort)classic.EntityTypeId(unit)), unit);
        }

        Assert.Equal(DropOffAccess.Land, Get(classic, "euro.trader").Traits.World.Trader!.Access);
        Assert.Equal(DropOffAccess.Water, Get(classic, "comm.galleon").Traits.World.Trader!.Access);
        Assert.Null(Get(classic, "comm.fishing_boat").Traits.World.Trader);
        Assert.Equal(DropOffAccess.Land, Get(classic, "asia.market").Traits.World.TradeDock);
        Assert.Equal(DropOffAccess.Water, Get(classic, "arab.harbor").Traits.World.TradeDock);
        Assert.True(Get(classic, "euro.tavern").Traits.World.IsTavern);
        Assert.True(Get(classic, "comm.bear").Traits.World.Guards);
        Assert.True(Get(classic, "comm.bird").Traits.World.IsAmbient);
        Assert.True(Get(classic, "euro.knight").Traits.World.IsMilitary);
        Assert.False(Get(classic, "euro.peon").Traits.World.IsMilitary);
        Assert.True(Get(classic, "euro.spy").Traits.World.DetectUnits > 0);
    }

    private static EntityTypeDefinition Get(Ruleset ruleset, string key)
    {
        int id = ruleset.EntityTypeId(key);
        Assert.True(id >= 0, $"{key} missing (types: {string.Join(", ", ruleset.EntityTypes.Select(t => t.Key))})");
        return ruleset.EntityTypes[id];
    }
}
