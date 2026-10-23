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
    // the plasma field and [randomtreeset].
    private static readonly ulong[] Golden =
    [
        // Random Grass
        0x96D1C279AC73B34CUL, 0xA99729C3376DC858UL, 0x3373EF2BEE1F33B0UL, 0x13A8A4134C1D9270UL, 0x92C148315A91C6B4UL, 0x4C372EA608CA36B0UL, 0x4A2C80A8BA1D9343UL,
        // Random Summer
        0x2C5C68588CF87584UL, 0x1FD9593DCCA90CF8UL, 0x5456C4F2A52CC358UL, 0x6B9FFD836C5D95B2UL, 0xC843860FBF3B33C7UL, 0xDAF890B332E216F8UL, 0x98B1BA7E1A074243UL,
        // Random Desert
        0xE4291EF55099BA40UL, 0xA40982365C05D932UL, 0x360F9B4C6C9F7939UL, 0x21E112A40CEF16A2UL, 0xE97DF22F8575F2FAUL, 0x03870A253798E8D4UL, 0xD5748075975287EBUL,
        // Random Winter
        0x345FF713EA1B862DUL, 0xAEABAEE006DF3F72UL, 0x91E8F9BD69A84AABUL, 0x560480CB0F2F2A09UL, 0x426176ED5B2F024BUL, 0x1CA63B1EAFF7D35AUL, 0x0C0E39252D225D44UL,
        // Archipelagos Grass
        0xD0A2C76BA24E0780UL, 0xFEFB67211CD7D36FUL, 0xC4CD33B86CB7319CUL, 0x31798F2876D31296UL, 0x359BAF6EDFB01C89UL, 0x2ADBFD6BCEB63D88UL, 0x1E586B675A373030UL,
        // Archipelagos Summer
        0x62B57DAF5989440EUL, 0x719411E2DA96C121UL, 0xABE795B69FFE11DEUL, 0xEBE1E83F4973231CUL, 0xA4288FFBCC967235UL, 0xBC01C72601BC1D16UL, 0x1916DC7F4BBA9BEAUL,
        // Archipelagos Desert
        0x6434E459C0817E29UL, 0xED237B6D46A46532UL, 0xE70A4023947CC5F5UL, 0x4020CE3D7E7C28A7UL, 0x6C3F44285C9A14D6UL, 0x4B3538368CB61EB6UL, 0x048AEC389A8EB992UL,
        // Archipelagos Winter
        0x9DBBD5B54AE01EEDUL, 0x3B78DC09AC6CB683UL, 0x8E1E82ECA00169E4UL, 0x3D8C473C6040AB89UL, 0xF21D44F9ED021596UL, 0xA01EC9D0BCE285EDUL, 0x46CB6B229F2D50F0UL,
        // Island Grass
        0xE98F28DCDB2B07B9UL, 0xC1C03B9BF7CD4E01UL, 0x85F04DB6B2173252UL, 0xB44829486C00B7F0UL, 0x3DE76F33804CA9ACUL, 0xEACF3A7E3DB086A1UL, 0x6653DF5FA2FBBE51UL,
        // Island Summer
        0xC672ACEC3816844DUL, 0xFA38A72EEF322881UL, 0xE5ED28FA2909CDAAUL, 0x70420B79F9304542UL, 0x77C9B0608CC70628UL, 0xC667B157DE10F551UL, 0xE38B1D0626BF3BE8UL,
        // Island Desert
        0x766AF7E0E576B108UL, 0x3631EE80D542F92EUL, 0xCCCD31DB3177A695UL, 0x7161BCF82969B715UL, 0xB5D015562566E057UL, 0xE3A32658C486A761UL, 0xC95E94D3D015C8DCUL,
        // Island Winter
        0x4D23DE17A7FFA785UL, 0xCA45BFEAECCDF053UL, 0x7EA5B16D147F314DUL, 0x9D559CF0B55DAE27UL, 0x7236DB78AFB681C8UL, 0x1304179DB1EE5D86UL, 0xFA0F7DC2472BFDDCUL,
        // Continental Grass
        0x81613C7ACA954B8FUL, 0xFA0C8556E8B4F5DAUL, 0xEDFB9EE697525711UL, 0xFA9A3AFD193F9FBCUL, 0xFA7B7928D6EBAA6BUL, 0x6DD7599E1A1AABE9UL, 0xE275E462F1F6B4F3UL,
        // Continental Summer
        0xA9F8E4109EC30E21UL, 0xB0331D235CEA9DD4UL, 0x71D543A05B0A8DF1UL, 0x85EDF142AF256C37UL, 0x97E224FCA0E33974UL, 0x8033274FDC9159C3UL, 0x80B6E569ACFBB649UL,
        // Continental Desert
        0xA6BED39C995B45D4UL, 0x5F039214EB7AB386UL, 0xCCE264C3C12C232DUL, 0x7A38F1CD29B25F01UL, 0xC6510E0694A14D8DUL, 0xE0BD9FB7A997060EUL, 0x9790A00EB252E83CUL,
        // Continental Winter
        0x9F00E27DCA3E6E34UL, 0xED0F1C3DD3A59947UL, 0x4DD39FE0BDC0D4F3UL, 0x19B00A63679BBDF6UL, 0x545A433BEBF037A9UL, 0x8F570328FAF21209UL, 0x22F6F273CA113E09UL,
        // Lakes Grass
        0xAFF9C434BF13E2FFUL, 0x389EBC880EC076A0UL, 0x60C5D62D516978CCUL, 0x40261E09F60CC775UL, 0xC33636632C144646UL, 0xB3E15F4C89196E02UL, 0xBA964788EFCD8D07UL,
        // Lakes Summer
        0x9871171279F4AD97UL, 0x604B9A4A3777B212UL, 0xD1D1CE5C26B77116UL, 0xFD53CE5652F2C57FUL, 0xE590C857752A5875UL, 0xB7EEEE84B31A2FFCUL, 0x752D22073F3FB35CUL,
        // Lakes Desert
        0xF3269F9C8F8C95E2UL, 0x9825314DF2C7A194UL, 0x559BD0A4663AA01AUL, 0x27D0247235AFDC89UL, 0x99D25B277F29CCE5UL, 0x5BA67D7621C03C61UL, 0xC645FCEA8DBD8BACUL,
        // Lakes Winter
        0xB8F6B88EACBA9D87UL, 0xA05FDC93205B5510UL, 0x560AFFBE243110B4UL, 0xD8A9C334D4FA2105UL, 0x0EEA53C8B38E758FUL, 0x1B5E2D5400C67F55UL, 0xC471DFCAE1779AD3UL,
        // Dry Grass
        0x1A15AA3748180B09UL, 0x081A5A44F6C82A6EUL, 0xCF53C8EAC9BE645CUL, 0x4C3116BD4AAB4E8DUL, 0x1653148F7213EF63UL, 0x9F077C5BB5495174UL, 0x0B06D65E433FAE98UL,
        // Dry Summer
        0x16B33BC7E9ED98B9UL, 0xA25D367871C5FD75UL, 0x353165DFC33B886FUL, 0x9F47C68098B1DFAEUL, 0x45808A48233798DDUL, 0x9A4694D32967FBA8UL, 0x1E52E61BD6489D10UL,
        // Dry Desert
        0x6A7B6ECC4FC957B6UL, 0x2C17C42DB84558AEUL, 0xF48A8959207A6E0EUL, 0x0850A75886A50371UL, 0x65A37051D6286BD5UL, 0x78B8311AEF61224BUL, 0x7972D9DAD394DEAAUL,
        // Dry Winter
        0xA6C23C32245BFD4BUL, 0x547E55C85C6F7214UL, 0xB4CD807BE8C3C73BUL, 0xA92EA7BD61E58039UL, 0xEF503D3517A614CEUL, 0x9A65C0F9250CA018UL, 0xAB129A757C887C37UL,
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
