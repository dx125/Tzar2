using System;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
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
        Assert.Equal(8000, peon.Traits.BuildTimeMs);
        Assert.Equal(50, peon.MaxHealth);
        Assert.Equal(5, peon.Traits.Armament!.Attack);
        Assert.Equal(4 * 12 * 64, peon.SpeedUnitsPerSecond);
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

    private static EntityTypeDefinition Get(Ruleset ruleset, string key)
    {
        int id = ruleset.EntityTypeId(key);
        Assert.True(id >= 0, $"{key} missing (types: {string.Join(", ", ruleset.EntityTypes.Select(t => t.Key))})");
        return ruleset.EntityTypes[id];
    }
}
