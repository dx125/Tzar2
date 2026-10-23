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
        0x047CFAB2B334CE1EUL, 0x150D21E0B9DBB7A1UL, 0xB449EEE3BAC2B86BUL, 0xBFA694F6E89887A6UL, 0x4460360F6D1FDF70UL, 0x48FCCD011463547FUL, 0xD4D2EEFF0C5CA548UL,
        // Random Summer
        0xEA38BB8F3200B00CUL, 0xA70496A3A2A7125AUL, 0x21E3B3E4BF6ADDE5UL, 0xD36EFC76B4972E9DUL, 0x7EA9940DFDE6C2FEUL, 0xA6DB2B848486DD07UL, 0x07D55C775C65C8B8UL,
        // Random Desert
        0xE4291EF55099BA40UL, 0xEEA07A20F2F0925BUL, 0x2A47255BA3AA726AUL, 0xB91BA3BA8D393923UL, 0xB5081AC4FBC3C269UL, 0x9B64266D8543EB7EUL, 0xF991C3F6FCA389A4UL,
        // Random Winter
        0x345FF713EA1B862DUL, 0x57E1C80807E671CAUL, 0x6843406B57400D88UL, 0x910154FC5CD04FCCUL, 0xED05F8FC68EABFFCUL, 0xF2038C7FCBB75953UL, 0xD4574A94057E30B2UL,
        // Archipelagos Grass
        0xD0A2C76BA24E0780UL, 0x5B94B48A7695DD0CUL, 0x256A1D4419F758AEUL, 0xCAD35C276F8AE307UL, 0x98AAA23D5A62F790UL, 0x786975A0A0D436B9UL, 0x1CD5765A0AB94287UL,
        // Archipelagos Summer
        0x52A7B6175DC58855UL, 0x0A927FC7B36E45D8UL, 0xBC4298BDE2AEBF24UL, 0x55AA22B0D7F38F7CUL, 0xF06571A13E6B71A3UL, 0xB3AEFC8498E5F94EUL, 0xE231D94775065524UL,
        // Archipelagos Desert
        0x6434E459C0817E29UL, 0x19316BA7025EF7D8UL, 0x9FA6D5190E3E7E31UL, 0xBEAA7A34A30A368EUL, 0xDAD5B564AD373144UL, 0x975A31E4A43D710FUL, 0xA1D884239C92A712UL,
        // Archipelagos Winter
        0x9DBBD5B54AE01EEDUL, 0x7EB9766CECEF2B4CUL, 0xA38C845F5654D7F1UL, 0x9E6338F0B281B04FUL, 0x62C8502DF0A6EC3AUL, 0xA855C4FA8860A194UL, 0x3CF14FBA52D43976UL,
        // Island Grass
        0xE98F28DCDB2B07B9UL, 0xB6738860A68D48E7UL, 0x99A4DC0EB71D1396UL, 0x18C41DC7AF1A20B1UL, 0x60EF7B1D57779C21UL, 0x7F050F72649DC8CDUL, 0xBE4F31E9139C5B44UL,
        // Island Summer
        0xC672ACEC3816844DUL, 0x6BF1C350B972B88EUL, 0x0C802B70BD25AA1EUL, 0x230FD69A7A582F7AUL, 0xA2B2380EF48356E0UL, 0x2B5BB507F19021E8UL, 0xA261ECBDC64A2C77UL,
        // Island Desert
        0x766AF7E0E576B108UL, 0xBF1CE1D67E06D637UL, 0xB6CAE38360CFD46AUL, 0x85596EC42CD78EB9UL, 0x593AE2D9E91B7862UL, 0x2D014AACF4F5195FUL, 0xEE828307A2E72A7EUL,
        // Island Winter
        0x4D23DE17A7FFA785UL, 0x7FF1806A366F9B52UL, 0x8BA977B43934E670UL, 0xE29C2DDFDAA0B443UL, 0x45DF3473ECE7C9DFUL, 0x960F0E244E09B219UL, 0x0B459681566C9B9CUL,
        // Continental Grass
        0x6688A02FF4B1628DUL, 0x09CC68215E8EE1FCUL, 0x8F6BFAF5EE4D574EUL, 0x76D4D6DE53718A96UL, 0xFE1071AB5EA2B128UL, 0x3494726C355FFEBBUL, 0x56D0746546EA129AUL,
        // Continental Summer
        0x8437D26F3338E5BAUL, 0xD7E4353818D826BAUL, 0x897944AE12763318UL, 0x209BA1ADBE3F7F8EUL, 0x978DA666F30A9C9DUL, 0xEAA9BB75DAC6E1A9UL, 0x6032926D385499F4UL,
        // Continental Desert
        0xCB54F921A24B7A9BUL, 0xE653BD7891CD1432UL, 0x287109C39BD06F84UL, 0xB4BABA8762E2FC83UL, 0x5214E87570A94228UL, 0x421D9836C976E3B0UL, 0x3108905AA86D110FUL,
        // Continental Winter
        0x9185B766B026C97AUL, 0xE4AA574405C727F1UL, 0xC557F973061E7040UL, 0x9E25E91664175797UL, 0xEE06C818FEDB9658UL, 0xDA3D0A74FD1BD705UL, 0x57CBB7E2A25174A3UL,
        // Lakes Grass
        0x6B651F4C2357B3A9UL, 0xAF90B7FBD6F96449UL, 0xDCE09F0F5CD1EDD4UL, 0xADB32273297B5B80UL, 0xD7289D56FC137B06UL, 0xE38AAC5A4617C4FDUL, 0xD49F1F80EB2173DBUL,
        // Lakes Summer
        0xB090884683628B06UL, 0xF6E4621B2520656CUL, 0xCE1EFF8B3A74575BUL, 0x79C877C8FF04BBE3UL, 0x7BF2A464172F0B09UL, 0x25262199492BBDDEUL, 0x5BB98B8157019374UL,
        // Lakes Desert
        0x0E4BD63E6C839D39UL, 0xED3D3EADC8314D76UL, 0x092F25296D8F0B71UL, 0xCDB353F139AE003AUL, 0x32ECCEB17B5306A2UL, 0x6B5A61F3BD0B8272UL, 0x3698DECFA527ED31UL,
        // Lakes Winter
        0xCB0087445C6816D3UL, 0xD86A6F9561A7FB49UL, 0x4E6EF9BE33E67224UL, 0x7A466B55BCB05E65UL, 0xF8EACEBC215BC3EAUL, 0x5DFEA0577CD19C95UL, 0xA1C403D9226D6062UL,
        // Dry Grass
        0x385559A99072F84AUL, 0x5C112C69167257F9UL, 0x2B5CB1A4680306AAUL, 0xDEBD86667DF084A1UL, 0x4539F7ECE56E85CBUL, 0xCD7FA00677144257UL, 0x25904F88B7EEE606UL,
        // Dry Summer
        0xA4C9F7B0CBD104A8UL, 0xD4482B6309F5B183UL, 0x12250654F38F6654UL, 0xF487B708B81B9C8BUL, 0xC4A794A6833214EEUL, 0x031C28A78257B9C7UL, 0xF7D77C89B07F6FEAUL,
        // Dry Desert
        0xA21343C42E829130UL, 0x15EDFB5B13C1EB89UL, 0x7AA3CF42F29E025CUL, 0x397D7BE142690375UL, 0x6E4F38BE2B3949E1UL, 0x75485E5006A5CC47UL, 0x1AB2A25D03D546B3UL,
        // Dry Winter
        0x0D351B346F9CA4A4UL, 0xDD7601813BBF8311UL, 0xF3B3BBF16F7B3BA2UL, 0xCC9A9BF7706D2D7BUL, 0xF204968D5521FF33UL, 0x16F6027E14886530UL, 0x4DB391B6611A5904UL,
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
