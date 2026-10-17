using Tzar2.Content.Compiled;
using Tzar2.Sim.Scenarios;

namespace Tzar2.Sim.Tests;

internal static class TestWorlds
{
    public static Ruleset Ruleset { get; } = SyntheticScenario.CreateRuleset();

    /// <summary>A flat all-grass map.</summary>
    public static MapData Flat(int size = 16) => new("flat", size, size, new byte[(size + 1) * (size + 1)], [], []);

    public static World NewWorld(int size = 16, int players = 2, bool cheats = false)
        => new(Ruleset, Flat(size), new SimulationConfig { MatchSeed = 1, PlayerCount = players, CheatsAllowed = cheats });

    public static SyntheticScenarioOptions SmallScenario { get; } = new()
    {
        MapSize = 64,
        EntityCount = 1500,
        Players = 4,
        Seed = 99,
        OrdersPerPlayerPerTick = 8,
        ChurnPerPlayerPerTick = 2,
    };
}
