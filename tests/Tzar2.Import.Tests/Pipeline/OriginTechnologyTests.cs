using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Packages;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Tzar2.Import.Pipeline;
using Tzar2.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Import.Tests.Pipeline;

/// <summary>
/// The owner's check of 2026-10-21 on the original's own data (local only): every technology of the reference build
/// does something in Tzar2, and every effect reaches units or buildings of the original. The synthetic tables of CI
/// lack some classes (monks); this test sees them all.
/// </summary>
public sealed class OriginTechnologyTests(ITestOutputHelper output) : IDisposable
{
    // What the simulation does with these technologies is in its code (loans, gambling, the tavern, cow poaching,
    // Leadership and Honor), not in effects, unlocks or gated orders.
    private static readonly HashSet<string> Mechanics =
        ["te_usury", "te_gambling", "te_mercenaries", "te_heroesquest", "te_fame", "te_cowpoaching", "te_nobility", "te_honor"];

    // Technologies of the data that do nothing in Tzar2 yet: each is a rule of the ledger. (Jewelry gates the Asian
    // market's item orders, which are missing too: TECH-030. Piracy and Boarding Ram are in the manual and the texts
    // only, not in the data: TECH-031.)
    private static readonly Dictionary<string, string> Missing = new(StringComparer.Ordinal)
    {
        ["te_tournaments"] = "TECH-029",
    };

    private readonly string _work = Path.Combine(Path.GetTempPath(), "tzar2-origin-tech-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    [OriginFact]
    [Trait("Rule", "TECH-002")]
    public void Every_technology_of_the_original_does_something_and_every_effect_has_targets()
    {
        Ruleset rules = CompileFromOrigin();
        var unlocks = new HashSet<int>();
        foreach (EntityTypeDefinition type in rules.EntityTypes)
        {
            Collect(type.Traits.Requirements, unlocks);
        }

        // Being only the prerequisite of other technologies is not enough: each technology has a use of its own.
        foreach (UpgradeDefinition u in rules.Upgrades)
        {
            Collect(u.Requirements, unlocks);
        }

        var gated = new HashSet<int>(rules.OrderGates.Select(g => (int)g.Technology));
        var idle = new List<string>();
        foreach (TechnologyDefinition t in rules.Technologies)
        {
            foreach (ModifierDefinition effect in t.Effects)
            {
                Assert.True(!effect.Types.IsEmpty || t.Key == "te_ironplus", $"{t.Key}: its {effect.Stat} effect reaches nothing"); // no iron rocks (UNK-14)
            }

            if (t.Effects.IsEmpty && !unlocks.Contains(t.Id) && !gated.Contains(t.Id) && !Mechanics.Contains(t.Key))
            {
                idle.Add(t.Key);
            }
        }

        output.WriteLine($"{rules.Technologies.Count} technologies; doing nothing yet: {string.Join(", ", idle)}");
        Assert.Equal(Missing.Keys.OrderBy(k => k, StringComparer.Ordinal), idle.OrderBy(k => k, StringComparer.Ordinal));
    }

    private static void Collect(IEnumerable<RequirementSet> sets, HashSet<int> into)
    {
        foreach (RequirementSet set in sets)
        {
            foreach (ushort tech in set.Technologies)
            {
                into.Add(tech);
            }
        }
    }

    [OriginFact]
    [Trait("Rule", "MAG-012")]
    public void The_spy_copies_men_but_no_machine_ship_or_magic_creature()
    {
        // The owner (2026-10-21) with BONUS.INI's groups: the mage is among the magic creatures there but is a man.
        Ruleset rules = CompileFromOrigin();
        var off = rules.EntityTypes.First(t => t.Traits.LegacyClass == "spy").Traits.NotImpersonated.ToHashSet();
        foreach (string cls in new[] { "catapult", "ballista", "galleon", "xebec", "troll", "dragon", "jinn", "skeleton" })
        {
            Assert.All(rules.EntityTypes.Where(t => t.Traits.LegacyClass == cls).ToArray() is { Length: > 0 } types ? types : throw new Xunit.Sdk.XunitException(cls), t => Assert.Contains(t.Id, off));
        }

        foreach (string cls in new[] { "mage", "footman", "knight", "peon", "archer" })
        {
            Assert.All(rules.EntityTypes.Where(t => t.Traits.LegacyClass == cls).ToArray() is { Length: > 0 } types ? types : throw new Xunit.Sdk.XunitException(cls), t => Assert.DoesNotContain(t.Id, off));
        }
    }

    [OriginFact]
    [Trait("Rule", "BLD-022")]
    public void A_repair_blow_restores_what_batch_J_measured_on_the_mill_the_barracks_and_the_castle()
    {
        // capture-20261021-220250: 6, 5 and 16 hit points per blow, the original's max / (4 x TIME) rounded down.
        Ruleset rules = CompileFromOrigin();
        int Blow(string cls)
        {
            EntityTypeDefinition type = rules.EntityTypes.First(t => t.Traits.LegacyClass == cls && t.Key.StartsWith("euro.", StringComparison.Ordinal));
            return rules.Gameplay.RepairBlowHp(type.MaxHealth, type.Traits.BuildTimeMs);
        }

        Assert.Equal((6, 5, 16), (Blow("lumber"), Blow("barrack"), Blow("castle")));
    }

    [OriginFact]
    [Trait("Rule", "WAR-014")]
    public void A_blow_lands_at_the_action_row_of_each_attack_animation()
    {
        // IMAGES.INI: footman 840 of 1,398 ms, archer 943 of 1,200, pikeman 1,398 of 2,100, Arab spearman (pikeman) 1,325 of 2,100.
        Ruleset rules = CompileFromOrigin();
        int Permille(string key)
        {
            ArmamentDefinition arm = rules.EntityTypes.First(t => t.Key == key).Traits.Armament!;
            return (int)(((long)arm.HitDelayMs * 1000 + arm.CooldownMs - 1) / arm.CooldownMs); // back from the rounded-down time
        }

        Assert.Equal((600, 785, 665, 630), (Permille("euro.footman"), Permille("euro.archer"), Permille("euro.pikeman"), Permille("arab.pikeman")));
    }

    [OriginFact]
    [Trait("Rule", "WORLD-015")]
    public void Land_units_get_the_body_of_their_object_box_and_ships_keep_theirs()
    {
        // IMAGES.INI object boxes: footmen and knights 32 x 32, catapults 64 x 64 (7/16 of the side: 14 and 28 px).
        Ruleset rules = CompileFromOrigin();
        int Body(string key) => rules.EntityTypes.First(t => t.Key == key).Radius / 64;
        Assert.Equal((14, 14, 28), (Body("euro.footman"), Body("euro.knight"), Body("comm.catapult")));
        Assert.Equal(7, rules.EntityTypes.First(t => t.Key == "euro.footman").TerrainRadius / 64);
        Assert.Equal(16, Body("comm.galleon"));
    }

    private Ruleset CompileFromOrigin()
    {
        string origin = OriginPaths.RequireOrigin();
        string subset = Path.Combine(_work, "origin");
        foreach (string file in Directory.EnumerateFiles(Path.Combine(origin, "DATA"), "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(origin, file);
            string lower = relative.ToLowerInvariant();
            if (lower.EndsWith(".ini", StringComparison.Ordinal) || lower.EndsWith(".txt", StringComparison.Ordinal))
            {
                string target = Path.Combine(subset, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        foreach (string file in Directory.EnumerateFiles(Path.Combine(origin, "IMAGES"), "*.dat", SearchOption.AllDirectories))
        {
            string target = Path.Combine(subset, Path.GetRelativePath(origin, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        ImportResult import = ImportPipeline.Run(new ImportOptions { OriginPath = subset, OutputDirectory = Path.Combine(_work, "out"), SkipEditionDetection = true });
        Assert.True(import.Report.Succeeded, string.Join("\n", import.Report.Errors));
        using PackageSource package = PackageSource.Open(import.PackagePath);
        var vfs = new VirtualFileSystem();
        vfs.Mount(new EngineDefaultsSource());
        vfs.Mount(package);
        return RulesetCompiler.Compile(vfs, "classic").Ruleset;
    }
}
