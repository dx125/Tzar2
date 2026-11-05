using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tzar2.Content.Compiled;
using Tzar2.Content.MapGen;
using Tzar2.Content.Rules;
using Tzar2.Sim;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Determinism.Tests;

/// <summary>
/// Cross-platform determinism of the random map generator (ADR 0060, GAME-18): every type × terrain set × size gives a
/// byte-identical <see cref="MapData"/> (pinned by <see cref="MapData.ContentHash"/>) on Windows x64, Linux x64 and macOS
/// arm64, on .NET 8 and .NET 10, in Debug and Release.
/// </summary>
public class MapGenGoldenHashTests(ITestOutputHelper output)
{
    // Changing the generator, its parameters, the catalog, the terrain profiles or MapData's hash changes these values
    // on purpose: update them in the same commit and say why. Order: type (Random…Dry) × set (grass, summer, desert,
    // winter) × size (tiny…real-world). History: (M3 part C) first; (M3-C review) starting resources only where their
    // own castle is the nearest one, validator: castles block walking; (WORLD-013, ADR 0201) the original's forests from
    // the plasma field and [randomtreeset]; (ADR 0264) the original's three item classes, their counts and properties.
    private static readonly ulong[] Golden =
    [
        // Random Grass
        0xBBE418F52AD0C329UL, 0x6BC82BC1FF251FF2UL, 0x817218F9AF26F201UL, 0x0343254888CA4FA1UL, 0x57E1444AF4F5F50FUL, 0x01B09B0306A941FEUL, 0x47AD405D144A0239UL,
        // Random Summer
        0xDEC88DEDC49A1F9CUL, 0x7457E0A98011EDEEUL, 0x6CCA6B624D3BEF77UL, 0x56FCCF4699AD4F96UL, 0x9CBC17496E2C48D4UL, 0x9501C756FBEE9CBFUL, 0x3C1AD0415B5E7322UL,
        // Random Desert
        0x806DAED0938570B8UL, 0x10BE915775E158CCUL, 0x856CEC94325C8079UL, 0x285B59DE838393A6UL, 0x40676C6E7CC64724UL, 0x8FFFE19917D2750AUL, 0x7CF42A484B8EC893UL,
        // Random Winter
        0x28E44028A5E7810BUL, 0xCD516054C5CE6EB5UL, 0x49AE672C45E53EC2UL, 0x9E09FF403735F622UL, 0x1F2C6C0B8F5D1CFAUL, 0xD0070D89964D8920UL, 0xCFE78DF9FFB2C7D2UL,
        // Archipelagos Grass
        0x0D877DED70520884UL, 0x561174FCA57F9701UL, 0xB33FBCA56577C811UL, 0x442D440BE8B33C30UL, 0x6CF687F0F752CA90UL, 0xC24A54DFEFE50B48UL, 0x87F26CEDA84B9571UL,
        // Archipelagos Summer
        0x48382B6A140C37C7UL, 0x7176ABED3251BEC3UL, 0x9475C15F6112448DUL, 0x53E7D646F643D578UL, 0x36AD3FA878D04BEAUL, 0x7BAB82F6C5A24F30UL, 0xB8F42FB95E60958BUL,
        // Archipelagos Desert
        0xA060D93DBD7BB10FUL, 0xBDB16053D9BBFDCCUL, 0x3E3CD574478E82F6UL, 0x1D47FE97BE79EF7EUL, 0xC3B0409DC2C668C6UL, 0x78F99C92685AEC7FUL, 0x5717B13CA00ED913UL,
        // Archipelagos Winter
        0xFBF49464724693B4UL, 0xB337F58D863357C5UL, 0xE53CA748088875FEUL, 0x0E340F07085EC442UL, 0xE691169730A18F4EUL, 0x1B9FBA2CB92B58C3UL, 0x067D4D632C47998AUL,
        // Island Grass
        0xE9FF8328DF44ADF4UL, 0xCC9BCC1DA5268F02UL, 0xCC92BD58F4ABE6A2UL, 0x25DED848C38FF55FUL, 0x89DBCAACA0634457UL, 0x355ED06F53BB323FUL, 0xFAE70A57D9AD1290UL,
        // Island Summer
        0x422166FF8A6B60B5UL, 0x7A326402A0DBAF8BUL, 0xAA4DE60851401A88UL, 0x28434243D318FD57UL, 0x129CB8F07956A8F0UL, 0x22B060A672C4D4CBUL, 0x7939F656AFDD88ABUL,
        // Island Desert
        0xA6F3BBF02A8BD04EUL, 0xA8EB5FE67C1C63B6UL, 0xE4373741505E017EUL, 0x3996B597D7BCB5E0UL, 0x68523D3BF44D1723UL, 0xD07DFB73397AB4B9UL, 0xB8CC7903006A7C7AUL,
        // Island Winter
        0x9BAA7A86DAECDAAAUL, 0xED7675B5D8EC36ADUL, 0x7139426D81C39D61UL, 0x20B192B303CAF728UL, 0x5240F282E530AE0EUL, 0x52C1887EDDD097FAUL, 0x67A8FF7A33751DACUL,
        // Continental Grass
        0x76DADDDABC589B3AUL, 0x1B2DD6B147D8E018UL, 0x7580B36C3EBD7F24UL, 0xB20BC294709C257FUL, 0x69225F1E79F691ABUL, 0xAF146DDB80A9CA95UL, 0x62BCFAACD1CA877FUL,
        // Continental Summer
        0x7C6940BF20098D15UL, 0x573DF279DC2E4E7AUL, 0xAAECC585D04D0991UL, 0x10F7558707466693UL, 0xA70DBA6BB9C5FD9FUL, 0x77073B208256E888UL, 0xFA9F5A0986620035UL,
        // Continental Desert
        0x7BBF9EF9F524E027UL, 0xC30A1ADBF39C9148UL, 0x898A43E9BD0C9E8BUL, 0x66CEEE2EDB270B61UL, 0x70045F393F8FC70EUL, 0x6BCFC5CE0B617ADAUL, 0xFCAD2EF6C2EC1A6CUL,
        // Continental Winter
        0xC1D7F1CF0F5FC891UL, 0x14484D7F058B2CEFUL, 0x72D449CBA42F4D51UL, 0x5E1A060742F6C369UL, 0xD33272F4484EAC23UL, 0xA736958437B55829UL, 0x1CB040D1D5BCD555UL,
        // Lakes Grass
        0x4FA190186A6DA30EUL, 0xD3909FEDB8E48E76UL, 0xD4A4582AD0B0B2EAUL, 0x0D0106799F74A7D5UL, 0x34F915D3081C2734UL, 0x2F37965D79BBF999UL, 0x7BA69EEA92A9FDABUL,
        // Lakes Summer
        0x28BE245415574CFBUL, 0x2886069842072397UL, 0xE74612179DBE00CEUL, 0xF5EA432190F3DA74UL, 0x09A40DD52F52B0A3UL, 0x2953F81B934E2649UL, 0x2ED677932761E684UL,
        // Lakes Desert
        0x4EDE30FDF3F7087AUL, 0x4867A76DF39F65E6UL, 0x431E634C2D01A0C2UL, 0x780F39ADBF57044AUL, 0x77CCA4B2C8338E43UL, 0x045BC9394E6DA77DUL, 0xAB007EA4657B7779UL,
        // Lakes Winter
        0xAD634A06AD085914UL, 0xC24BEAAC94B98876UL, 0xA08C4D098C715C25UL, 0x447DDC6DA8E434E3UL, 0x174909F4B7EFEB6CUL, 0xF010212F0455E184UL, 0x6917E1DD70710E0EUL,
        // Dry Grass
        0xC7788A4159956487UL, 0x412E3B7AD9F30B96UL, 0xFB43ECCD9A9A2F81UL, 0x8F9621B6122EC445UL, 0x8B5EC19A0413C3EBUL, 0x64A5AE918B472CC3UL, 0x626FAB002FBC2809UL,
        // Dry Summer
        0x556EE130E2FA64D8UL, 0xAB60C79E453DD2BDUL, 0x29B8863516916994UL, 0x11C622DA464AE98CUL, 0xBE524D097FC7E1A1UL, 0x9D4829778D9782A7UL, 0x2ABE8F0B0D9A8540UL,
        // Dry Desert
        0x0C95226D2A1B01F8UL, 0x5AAA6828ABE4EF20UL, 0xA5F89270FB77485AUL, 0x08789CA15130BB2BUL, 0x3EE2140DF9C09805UL, 0xCC4DD3FF415E7DCEUL, 0xFA24A32A1D49A0DFUL,
        // Dry Winter
        0x61BF21F55803A13DUL, 0xDBD4F2B8821730B6UL, 0x86CE9BDA04F5BDB5UL, 0xB488264CDC67723CUL, 0x3EFA856C39B47F3DUL, 0x4EDD4F8023581F02UL, 0x7FA1108BF55235D9UL,
    ];

    public static TheoryData<RandomMapType, MapTerrainSet> Cases()
    {
        var data = new TheoryData<RandomMapType, MapTerrainSet>();
        foreach (RandomMapType type in Enum.GetValues<RandomMapType>())
        {
            foreach (MapTerrainSet set in Enum.GetValues<MapTerrainSet>())
            {
                data.Add(type, set);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_size_produces_the_golden_hash(RandomMapType type, MapTerrainSet set)
    {
        var actual = new StringBuilder();
        var failures = new List<string>();
        foreach (RandomMapSize size in Enum.GetValues<RandomMapSize>())
        {
            int index = (((int)type * 4) + (int)set) * 7 + (int)size;
            GeneratedMap map = MapGenerator.Generate(OptionsFor(type, set, size, index));
            Assert.Empty(MapGenValidator.Validate(map));
            ulong hash = map.Map.ContentHash;
            actual.Append(CultureInfo.InvariantCulture, $"0x{hash:X16}UL, ");
            if (index >= Golden.Length || Golden[index] != hash)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"{size}: 0x{hash:X16}"));
            }
        }

        output.WriteLine($"{type} {set} runtime={Environment.Version}: {actual}");
        Assert.True(failures.Count == 0, $"{type} {set} differs from the golden hashes: {string.Join(", ", failures)}");
    }

    [Fact]
    public void A_generated_map_bound_to_a_ruleset_loads_and_runs_in_the_simulation()
    {
        Ruleset ruleset = SyntheticLegacyContent.CompileRuleset();
        var options = new MapGenOptions { Type = RandomMapType.Lakes, Size = RandomMapSize.Medium, Players = 4, Seed = 0x5EED, Features = MapFeatures.All }.BindTo(ruleset);
        GeneratedMap map = MapGenerator.Generate(options);
        Assert.Empty(MapGenValidator.Validate(map));
        map.Map.Validate(ruleset);
        using var sim = new Simulation(ruleset, map.Map, new SimulationConfig { PlayerCount = options.Players, MatchSeed = 0x5EED });
        var none = new Tzar2.Sim.Commands.CommandList();
        for (int t = 0; t < 40; t++)
        {
            sim.Tick(none);
        }

        Assert.Equal(map.Map.Objects.Length, sim.World.Entities.AliveCount);
    }

    private static MapGenOptions OptionsFor(RandomMapType type, MapTerrainSet set, RandomMapSize size, int index) => new()
    {
        Type = type,
        TerrainSet = set,
        Size = size,
        Players = size == RandomMapSize.RealWorld ? 8 : Math.Min(4, MapGenerator.MaxPlayersFor(size)),
        Seed = 0x7A2_0000UL + (ulong)index,
        Resources = (MapResourceLevel)(index % 3),
        Features = MapFeatures.All,
    };
}
