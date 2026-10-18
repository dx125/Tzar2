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
    // own castle is the nearest one, validator: castles block walking.
    private static readonly ulong[] Golden =
    [
        // Random Grass
        0x4B5BD56811A9CAA2UL, 0xF1E91F7EED00B57AUL, 0x6759CA459992E50AUL, 0xC57E661730B8E929UL, 0x5B7C8D9D9E1F2FA8UL, 0x916B1CB36C6A3C8AUL, 0xDCE9970B77B21A96UL,
        // Random Summer
        0x911779D273E21B7EUL, 0x7571E65A508492A6UL, 0x8D3EDE7097841B3FUL, 0x1921A2EAD019BE96UL, 0x7BA4FF8B0BCFAD5AUL, 0xD277E191028F5A9EUL, 0xB42CE75FDD9DD9B2UL,
        // Random Desert
        0x6253CB019FCF0058UL, 0x2718349A16F3BE4FUL, 0x70246B36D48B4C12UL, 0xCD33AC40CBF802A1UL, 0x975B93CB7F2E5BBCUL, 0x6027A4BE2B0A0E13UL, 0xCE320CCC95EAAF3EUL,
        // Random Winter
        0x12F490C6655FD243UL, 0x6339B093D7B0650DUL, 0xDFC1A51B0F2871CBUL, 0x34CEB8E774346953UL, 0x4A1A613A667FD31DUL, 0x9F6D8425E2426936UL, 0xB92B5CF66AB05FE2UL,
        // Archipelagos Grass
        0x74266E7F38EEBC7CUL, 0xD9BE976378C7072CUL, 0x1F591F4446828AFFUL, 0xA56C63090AB26E10UL, 0x08A4EE5946A5FA80UL, 0x21070B05F49E74A2UL, 0xA227DA6FFA956985UL,
        // Archipelagos Summer
        0x6E422365B4D971C7UL, 0x7F2B6516DD6DD8ADUL, 0x94937F9D24482465UL, 0x6C42202EC9BC8AF5UL, 0x0C35AAB4D29DE741UL, 0x4EA6C07A5C9E4DD5UL, 0xB26F529A4EDB8C6DUL,
        // Archipelagos Desert
        0x9CEB5F8DD01DE709UL, 0xC26E5D02A0DF77D7UL, 0xB2CCE2B8E43C1177UL, 0xF655617CF387A393UL, 0x70D56D0CBF5FCB85UL, 0x4FBD380B3E0C0C37UL, 0x099AF32F7FF5E029UL,
        // Archipelagos Winter
        0x4466D878A64BBFD0UL, 0xBEA3CA3339DAB5F5UL, 0xCB06C9E91541C6A0UL, 0xA2BE77355E45FA35UL, 0xD1209C94EB0B5DC6UL, 0xE6E69C3802F12797UL, 0x10B8E18710BEF7A7UL,
        // Island Grass
        0xD504A8ED41FB9362UL, 0x84E34C6C8EFDF803UL, 0xA7467AAC53D637F2UL, 0xC5AD18F64F5E7008UL, 0x8258C1CE6090FCDDUL, 0xDD6F2A4CC794C9C6UL, 0x423E6932861618A0UL,
        // Island Summer
        0x097573B7D4365487UL, 0x282720F1AA74C117UL, 0xEF4D0CCF78223B56UL, 0x9CD60E8654939FD2UL, 0xDE71EF0A86B26821UL, 0x069162EB51FE8972UL, 0xC7F4AAC8CE0E4BA1UL,
        // Island Desert
        0x559E8D870CFE025DUL, 0xAF4596804C306DCBUL, 0x4ED63D464F96BDC1UL, 0xEBC1B0CE20E50FF5UL, 0xA394E80B87D9B443UL, 0xE42C1D448C483F8EUL, 0xC1DDDB1B173BD7EDUL,
        // Island Winter
        0xE197573B1F783210UL, 0xEFD52B8EC1E61B27UL, 0xF4D99161515A7567UL, 0x05B201D6BDA045C9UL, 0xBB48FF0B885AE3AEUL, 0x0C2BAA9AD10C8420UL, 0x1E35AE385D3A31BEUL,
        // Continental Grass
        0x8E45E47241D0D618UL, 0x792E9A7C01CF9D90UL, 0x621A7CCD8407A943UL, 0x988180AB2F7E4541UL, 0x802A954D8BC59818UL, 0xEA94E96F1994384FUL, 0x633FA823D1D7F337UL,
        // Continental Summer
        0xEBFEF1F2EF6E7790UL, 0xB604472E3E7B0203UL, 0x952EBEAC4574E86EUL, 0x8475E28701FEB5B0UL, 0x3EFF1F243330C43BUL, 0x9F7E6B0F254A8C06UL, 0x743594FD8F7647B9UL,
        // Continental Desert
        0xA75C603BC9777918UL, 0xB3FF97330EFB82D8UL, 0xC3AE74802D7DBF75UL, 0xDEE1C1DB04496FBDUL, 0x445E27F45DF1A07BUL, 0xDF76E883A00931AFUL, 0x4FE88436F73AA8A1UL,
        // Continental Winter
        0x2346B2E636D1B926UL, 0x79DF7DEF0BBB4345UL, 0x113BB17E0FBB9439UL, 0x9E905C3B8EB6956CUL, 0x03FAF1B3DD164802UL, 0x18F549B973028AFAUL, 0x1199484767F67853UL,
        // Lakes Grass
        0x0EDEBDF2ACFF49A2UL, 0x0A4D32CB48356588UL, 0x3EE935994EA1A49CUL, 0x4E70133A0AB99D2AUL, 0xB2EDA3B4C2A76FFDUL, 0xC96B4F542E543721UL, 0x8004BE80913168FAUL,
        // Lakes Summer
        0xAA092149B164CBA2UL, 0xAC2F1BD5C3032FBFUL, 0xDE41CEAA0FD7FABCUL, 0x17C9F8E073371E24UL, 0xD6C571C0DA843E22UL, 0x53A2834843815AB1UL, 0x9DB35AD29FD6DDE5UL,
        // Lakes Desert
        0x09BF12C59FEED401UL, 0xC6E95CD312F7FF78UL, 0xC0806842B4F2ACF8UL, 0xD159A3A7E18B0CBBUL, 0x9B3E3121263C182DUL, 0xD9976991B57A8677UL, 0x49DEED100C0D31F6UL,
        // Lakes Winter
        0x4D1EAB995AF74522UL, 0x5F520965A09F9F4CUL, 0xFF8E8B8B5F943E40UL, 0x3E6EFD7EB0AA2CEDUL, 0x36EA1C7DD911A1CDUL, 0xFF52D4F7E85EFC1DUL, 0x361BC558DAF46DBDUL,
        // Dry Grass
        0x202BEDC924857932UL, 0xD81053EC5C7C02E8UL, 0xE945E97EA77EB248UL, 0x926401D75DEAE05AUL, 0xA05C97431F436E83UL, 0x22F58C8FCFFA8E4FUL, 0xEA72F9FDCADC6038UL,
        // Dry Summer
        0xBB762C51FBD632E8UL, 0x2EDAE5A815C7D798UL, 0x2B4CBF0B67DB9169UL, 0xC144BAF5135F16ECUL, 0xEEE0D6F4542E1D83UL, 0x9C7196585000136FUL, 0xF000D8D825F249E7UL,
        // Dry Desert
        0xD6D5F8B038314B21UL, 0xA46B04BF606854A0UL, 0x8FD331B24434F088UL, 0x68A82D7A2AEFEED9UL, 0x8D9FA7B485A4FE22UL, 0x31907CAA5E6CF387UL, 0xC8C85B869C75E035UL,
        // Dry Winter
        0x25EA360F86568F56UL, 0x4ED05B693DBC85F0UL, 0xF3BC1C6231B9A5FEUL, 0xB7EC57FF8B033BC4UL, 0x1DA02A1EC127DA5DUL, 0x6B32301FD1C3D6C6UL, 0xB60F10A7F94AB988UL,
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
