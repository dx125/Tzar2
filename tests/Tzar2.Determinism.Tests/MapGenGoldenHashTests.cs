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
        0x8FB35ACC407CDFD0UL, 0x95DDE494A490990DUL, 0x1B5105E27B57E8DFUL, 0xE640C58CAFF0080AUL, 0x52C54999C2F19844UL, 0x4C372EA608CA36B0UL, 0x07797480F4E67906UL,
        // Random Summer
        0xB5541C22F686EACFUL, 0xFA7F54F43B325BA2UL, 0x6B2E9CD689583EE3UL, 0x05746C5B5E9970FCUL, 0x5167716FCA34163DUL, 0xCA0486AC55F9722BUL, 0xB996F9522BB0118DUL,
        // Random Desert
        0xB329D9342D087779UL, 0x637B1E6C03D174E0UL, 0x5CA4263130075819UL, 0x1D567148ED452C30UL, 0x7FF592B0DAE5BF20UL, 0x616F192ED38D5E32UL, 0x35695E5EC521E84DUL,
        // Random Winter
        0x2CD9A3ED13E39DB4UL, 0xCA8282DBE3416B53UL, 0x489590261CCB0D33UL, 0x136365AB4A04A443UL, 0xFC65974FBBC5521EUL, 0x5CEF3BFA22CA089BUL, 0x146C06F4B6346EF0UL,
        // Archipelagos Grass
        0x59602DA49409D7D8UL, 0xF66133BB791C3C83UL, 0xFC05244BD6B1FE26UL, 0x471C7FB4884CFCDFUL, 0xEC5BE311EE3C4391UL, 0x04B24A1086599CDFUL, 0xBF534B57674AC366UL,
        // Archipelagos Summer
        0x69274E97189791FAUL, 0xCF597639A91997E7UL, 0xDDCA4185EDDC1D98UL, 0x661614859473EA59UL, 0x80C5E01C66264673UL, 0xC6E6DCB4F6EC78F4UL, 0x5FB42AB20A3ABDE6UL,
        // Archipelagos Desert
        0x52B19BB5ABB908E0UL, 0xC83A8BC55DB36A99UL, 0xE70A4023947CC5F5UL, 0xD99B67C664CD7A84UL, 0xEECA34C275FC1ABCUL, 0x2E1FB04758D55738UL, 0x9DC0033CC4078231UL,
        // Archipelagos Winter
        0xFC6C47590813F85EUL, 0xBB8050829CD72C97UL, 0xD674DA71E67A468BUL, 0xF03FEB20528B9D1EUL, 0x7E465089FF1B03C4UL, 0x9994892209EAB566UL, 0xD52C223963AD93AFUL,
        // Island Grass
        0x1EBF2579754F0736UL, 0x513C7B9826280750UL, 0x4EEDD60A031F1DA1UL, 0xF5B838C7A4B4FEC8UL, 0x8B358C217874E293UL, 0x733CF5356C46E984UL, 0x433686DD5B00AE5DUL,
        // Island Summer
        0x095E617E96A5A084UL, 0x14AA05083E97D013UL, 0x940B86EA11B50CCEUL, 0x765F0CF198EF32C5UL, 0x1D114D8F2A940DC0UL, 0x609F2879CA922517UL, 0x76EDE8A44578C008UL,
        // Island Desert
        0xF6BD5CDC19F14459UL, 0x73E05EC58C222710UL, 0x8769B7704AA9BC7AUL, 0x50D351AFC9E909A7UL, 0xE81EBDD8E75A17E6UL, 0x94836325E67F977EUL, 0xA1614E7768511897UL,
        // Island Winter
        0x66E6893073372637UL, 0x80114AEE6D805864UL, 0x0EE34255A608B6F6UL, 0xDA860586034EF58BUL, 0x70E1517CCB7982B7UL, 0x5A4EAE9D9FB259D4UL, 0xAB8CF74D065232F1UL,
        // Continental Grass
        0x1A3D1056EBD7536BUL, 0x1BBF69FBD5BBB65BUL, 0xCE644D3D14DF5A65UL, 0xA22ABC94F93982E0UL, 0xAA225BF178FAC50EUL, 0xE11B58B20E46D026UL, 0x9E539902984CF323UL,
        // Continental Summer
        0x553410E91245E517UL, 0xAAEB1577B0767B58UL, 0x0BAFBD2F2D13D7A3UL, 0x26FC99B6B83A9D7EUL, 0xC68E04C20C315D43UL, 0x7D020503715FB253UL, 0x609D6C6D9B02667CUL,
        // Continental Desert
        0x851E616651D2813AUL, 0xA969FC26F538915BUL, 0x1C42A6C066524435UL, 0x74F23FA8A27A2572UL, 0x3E8168FAA07B72DBUL, 0x149022E264BA2CB1UL, 0x5F9237FA888931DAUL,
        // Continental Winter
        0x6ED8B9ECEA82C4F0UL, 0x500FB23C29AB1D98UL, 0x072AB9AE469AB9B0UL, 0x09B984561A8CFBA9UL, 0x4DB5F94FFC4C967FUL, 0x552B9518C7AD9459UL, 0x2209D36F483AB9CCUL,
        // Lakes Grass
        0x9237DB5C4712CD76UL, 0x20936B7171D0288BUL, 0xD93D55D3B4578260UL, 0x170AA4753C815B6DUL, 0x9AA812D4B40D06ACUL, 0xAA218AE2F99355C9UL, 0x3AEE39BDF91864D2UL,
        // Lakes Summer
        0xCE855166AB9B1348UL, 0x01F71C32AEE132C0UL, 0xDEF4AB5B1752831CUL, 0xF823AE7F1366B08FUL, 0xE88E884AB5D9534EUL, 0x14267571FB22407CUL, 0x15C21412C1F1F149UL,
        // Lakes Desert
        0x7663201F658F65A0UL, 0x6550CF2F5FF4017FUL, 0xF756B41E9E6499D6UL, 0x27D0247235AFDC89UL, 0xACCD4D222340BF4DUL, 0x2FA6BDA41200AD3EUL, 0x652814BD154D0984UL,
        // Lakes Winter
        0x9AAA25FBEBF1E148UL, 0xD2E96E8BEF1F3AA8UL, 0x372148032DA370E9UL, 0x17991504591AC4E2UL, 0x1DA15408DE2769E8UL, 0x9FF55541EBAB4E79UL, 0xAFF0EA6D894B4CC7UL,
        // Dry Grass
        0x2707C27B89DDE262UL, 0x9551FA28140CDB51UL, 0xBAE6B7A656DDE6DAUL, 0x0C9C31A2CE1ED3AAUL, 0xFAE2CCC14108573AUL, 0x2978A3BF60E98298UL, 0xF2CFB6E677B04B1BUL,
        // Dry Summer
        0x821243E8FAD27BDBUL, 0x71C66A0DD2AB0F78UL, 0x0616161170FC635BUL, 0x04B26B159909E656UL, 0xD46199C43A487C5DUL, 0x1D890028CC12B2CDUL, 0x11839D1BE17E9F7AUL,
        // Dry Desert
        0xA0BAA8DFDEAE8CEAUL, 0x0DB1EAC57032C0E2UL, 0xA712C121BD8BD035UL, 0x02EB3AA6539C570BUL, 0x851B8C9C940B1E85UL, 0x12C8A5CF8D4E3E48UL, 0x8BC8515BF6E55026UL,
        // Dry Winter
        0xF8AD5FDC2139AA24UL, 0x42CD0B8EBA6F5223UL, 0xA6074844CFD3A234UL, 0x7D9B9519FE33E854UL, 0x668847C06D714C2DUL, 0x6C6C53905D5B887CUL, 0xD74549EC735A55B8UL,
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
