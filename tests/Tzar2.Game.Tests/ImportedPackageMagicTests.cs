using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Vfs;
using Tzar2.Game.Session;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>Magic rules that live in the original's data, read from an imported package (<c>TZAR2_PACKAGE</c>; local only).</summary>
public sealed class ImportedPackageMagicTests
{
    [PackageFact]
    [Trait("Rule", "MAG-010")]
    public void Priests_strike_mages_and_magic_creatures_harder()
    {
        Ruleset rules = Classic();
        foreach ((string priest, string target) in new[]
        {
            ("euro.priest", "euro.mage"), ("arab.priest", "arab.mage"), ("asia.priest", "asia.mage"),
            ("euro.priest", "comm.dragon"), ("euro.priest", "comm.bat"),
        })
        {
            int bonus = rules.BonusBasisPoints((ushort)rules.EntityTypeId(priest), (ushort)rules.EntityTypeId(target));
            Assert.True(bonus > 0, $"{priest} has no bonus against {target} (BONUS.INI)");
        }
    }

    [PackageFact]
    [Trait("Rule", "MAG-011")]
    public void Asian_priests_are_made_from_monks()
    {
        Ruleset rules = Classic();
        ConversionDefinition from = rules.EntityTypes[rules.EntityTypeId("asia.priest")].Traits.ConvertsFrom!;
        Assert.Equal([(ushort)rules.EntityTypeId("asia.monk")], from.From.ToArray());
        Assert.Equal("become_priest", from.Order);
        Assert.Null(rules.EntityTypes[rules.EntityTypeId("euro.priest")].Traits.ConvertsFrom); // the others are trained
    }

    private static Ruleset Classic()
    {
        using PackageSource package = PackageSource.Open(ImportedPackageSkirmishTests.PackagePath!);
        return RulesetLoader.Compile(package).Ruleset;
    }
}
