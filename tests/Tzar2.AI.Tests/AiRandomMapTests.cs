using System.Linq;
using Tzar2.AI.Scenarios;
using Tzar2.Content.MapGen;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.AI.Tests;

/// <summary>M3 exit "skirmish vs. Hard AI on all map types": the AI plays every land random-map type (GAME-18).</summary>
public class AiRandomMapTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(RandomMapType.Continental, 2)]
    [InlineData(RandomMapType.Lakes, 4)]
    [InlineData(RandomMapType.Dry, 2)]
    [InlineData(RandomMapType.Island, 2)]
    [InlineData(RandomMapType.Random, 3)]
    [InlineData(RandomMapType.Archipelagos, 2)]
    public void Hard_AIs_develop_on_generated_maps(RandomMapType type, int players)
    {
        GeneratedMap generated = MapGenerator.Generate(new MapGenOptions { Type = type, Size = RandomMapSize.Medium, Players = players, Seed = 7 }.BindTo(AiFixtures.Classic));
        string civ = generated.Starts[0].Civilization;
        Assert.All(generated.Starts, s => Assert.Equal(civ, s.Civilization));
        using var scenario = new AiSkirmishScenario(AiFixtures.Classic, AiFixtures.Skirmish, generated.Map, new AiSkirmishOptions { Players = players, Civilization = civ });
        for (int t = 0; t < 8 * 60 * 20; t++)
        {
            scenario.RunTick();
        }

        output.WriteLine($"{type} ({generated.ResolvedType}): {scenario.Describe()}");
        for (int p = 0; p < players; p++)
        {
            Assert.True(scenario.Delivered(p) > 1000 && scenario.Built(p) > 2 && scenario.Trained(p) > 5, scenario.Describe());
        }

        // Land maps: the kingdoms reach each other within eight minutes. Islands need ships (not in the sim yet).
        if (generated.ResolvedType is not (RandomMapType.Archipelagos or RandomMapType.Island))
        {
            Assert.True(Enumerable.Range(0, players).Sum(scenario.DamageDealt) > 0, scenario.Describe());
        }
    }
}
