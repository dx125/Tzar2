using System.Linq;
using Tzar2.AI.Legacy.Syntax;
using Tzar2.AI.Runtime;
using Tzar2.AI.Scenarios;
using Tzar2.Sim.Events;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.AI.Tests;

/// <summary>
/// AI against AI on a synthetic map (M3 exit "skirmish vs. Hard AI", REQ-AI-02): with nothing but the AI issuing
/// commands, every kingdom gathers, builds, trains and attacks, and no command the AI issues is malformed.
/// </summary>
public class AiSkirmishTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(AiDifficulty.Hard)]
    [InlineData(AiDifficulty.Easy)]
    public void Two_AIs_gather_build_train_and_attack(AiDifficulty difficulty)
    {
        Assert.DoesNotContain(AiFixtures.Skirmish.Diagnostics, d => d.Severity == AiDiagnosticSeverity.Error);
        using var scenario = new AiSkirmishScenario(AiFixtures.Classic, AiFixtures.Skirmish, new AiSkirmishOptions { Players = 2, MapSize = 96, Difficulty = difficulty });
        int rejectedUnknown = 0;
        for (int t = 0; t < 12 * 60 * 20; t++)
        {
            scenario.RunTick();
            rejectedUnknown += scenario.Simulation.World.Events.Events.ToArray().Count(e => e.Kind == SimEventKind.CommandRejected
                && (CommandRejection)e.Arg is CommandRejection.UnknownKind or CommandRejection.InvalidPlayer);
            if (t % 4800 == 0)
            {
                output.WriteLine($"t={t / 20}s {scenario.Describe()}");
            }
        }

        output.WriteLine(scenario.Describe());
        output.WriteLine(AiDebugView.Describe(scenario.Simulation.World, scenario.Ai, 0));
        Assert.Equal(0, rejectedUnknown);
        for (int p = 0; p < 2; p++)
        {
            Assert.True(scenario.Delivered(p) > 5000, scenario.Describe());
            Assert.True(scenario.Built(p) >= 10, scenario.Describe());
            Assert.True(scenario.Trained(p) >= 30, scenario.Describe());
            Assert.True(scenario.Ai.Players[p].Waves >= 1, scenario.Describe());
            Assert.True(scenario.DamageDealt(p) > 0 && scenario.Kills(p) > 0, scenario.Describe());
        }
    }

    [Fact]
    public void Eight_AIs_play_on_one_map()
    {
        using var scenario = new AiSkirmishScenario(AiFixtures.Classic, AiFixtures.Skirmish, new AiSkirmishOptions { Players = 8, MapSize = 192, TreesPerPlayer = 100 });
        for (int t = 0; t < 6 * 60 * 20; t++)
        {
            scenario.RunTick();
        }

        output.WriteLine(scenario.Describe());
        for (int p = 0; p < 8; p++)
        {
            Assert.True(scenario.Delivered(p) > 0 && scenario.Built(p) > 0 && scenario.Trained(p) > 0, scenario.Describe());
        }
    }

    [Fact]
    public void AIs_never_attack_their_allies()
    {
        // Slots 0 and 1 are a team (GAME-13); slot 2 is everyone's enemy.
        using var scenario = new AiSkirmishScenario(AiFixtures.Classic, AiFixtures.Skirmish, new AiSkirmishOptions { Players = 3, MapSize = 96, Teams = 0x11 });
        var damage = new long[3, 3];
        for (int t = 0; t < 14 * 60 * 20; t++)
        {
            scenario.RunTick();
            Sim.World world = scenario.Simulation.World;
            foreach (SimEvent e in world.Events.Events)
            {
                if (e.Kind != SimEventKind.Damaged || (uint)e.X > Core.Ids.EntityId.MaxIndex)
                {
                    continue;
                }

                Core.Ids.EntityId attacker = world.Entities.AtIndex(e.X);
                if (!attacker.IsNone && world.Identities.IndexOf(attacker) is >= 0 and var a && world.Identities.IndexOf(e.Entity) is >= 0 and var v
                    && world.Identities.At(a).Owner < 3 && world.Identities.At(v).Owner < 3)
                {
                    damage[world.Identities.At(a).Owner, world.Identities.At(v).Owner] += e.Arg;
                }
            }
        }

        output.WriteLine(scenario.Describe());
        Assert.Equal(0, damage[0, 1]);
        Assert.Equal(0, damage[1, 0]);
        Assert.True(damage[0, 2] + damage[1, 2] + damage[2, 0] + damage[2, 1] > 0, scenario.Describe());
    }
}
