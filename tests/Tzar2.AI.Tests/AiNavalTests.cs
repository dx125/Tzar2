using System.IO;
using System.Linq;
using Tzar2.AI.Runtime;
using Tzar2.AI.Scenarios;
using Tzar2.Content.MapGen;
using Tzar2.Core.Hashing;
using Tzar2.Core.Serialization;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.AI.Tests;

/// <summary>
/// The naval executor (REQ-AI-02, ADR 0144): on Archipelagos the AI builds a harbor, ships and fishing boats, and
/// carries its waves to the enemy's island; the state survives snapshots.
/// </summary>
public class AiNavalTests(ITestOutputHelper output)
{
    private static AiSkirmishScenario Archipelagos(out GeneratedMap generated)
    {
        generated = MapGenerator.Generate(new MapGenOptions { Type = RandomMapType.Archipelagos, Size = RandomMapSize.Medium, Players = 2, Seed = 7 }.BindTo(AiFixtures.Classic));
        return new AiSkirmishScenario(AiFixtures.Classic, AiFixtures.Skirmish, generated.Map, new AiSkirmishOptions { Players = 2, Civilization = generated.Starts[0].Civilization });
    }

    [Fact]
    public void Hard_AIs_land_their_waves_on_the_enemy_island()
    {
        using AiSkirmishScenario scenario = Archipelagos(out _);
        World w = scenario.Simulation.World;
        bool fished = false;
        bool ashore = false;
        // 30 minutes (15 before one peasant per field, ECO-021; 20 before the original's body size, WORLD-015, whose crowds
        // slow the economy and the first wave sails at about 25 minutes): a transport sunk while loading makes a second try (ADR 0146)
        for (int t = 0; t < 30 * 60 * 20; t++)
        {
            scenario.RunTick();
            fished |= w.Workers.Entities.ToArray().Any(e => w.IsShip(e) && w.Workers.Get(e).CarryAmount > 0);
            ashore |= t % 200 == 0 && Enumerable.Range(0, 2).Any(p => OnEnemyIsland(w, p) > 0);
        }

        output.WriteLine(scenario.Describe());
        Assert.True(fished, "fishing boats fish");
        // Both fight; at least one lands. Since one peasant tends each field (ECO-021) the small islands feed fewer
        // farmers, and the kingdom landed on first may stay home defending until the other's next wave.
        Assert.True(scenario.Ai.Players[0].Landings + scenario.Ai.Players[1].Landings >= 1, scenario.Describe());
        for (int p = 0; p < 2; p++)
        {
            Assert.True(scenario.DamageDealt(p) > 0, scenario.Describe());
        }

        Assert.True(ashore, "landed soldiers stood on the other kingdom's island");
    }

    private static int OnEnemyIsland(World w, int owner)
    {
        Tzar2.Core.Ids.EntityId[] all = w.Identities.Entities.ToArray();
        Tzar2.Core.Ids.EntityId castle = all.FirstOrDefault(e => w.Identities.Get(e).Owner == 1 - owner && w.TypeOf(e).Traits.Category == Tzar2.Content.Compiled.EntityCategory.Building);
        if (castle.IsNone)
        {
            return 0;
        }

        Tzar2.Sim.Components.Position town = w.Positions.Get(castle);
        return all.Count(e => w.Identities.Get(e).Owner == owner && !w.IsHeld(e) && !w.IsShip(e) && w.Combats.Has(e) && w.Motions.Has(e)
            && Tzar2.Sim.Systems.Shore.LandComponentAt(w, w.Identities.Get(e).TypeId, w.Positions.Get(e).X, w.Positions.Get(e).Y)
               == Tzar2.Sim.Systems.Shore.LandComponentAt(w, w.Identities.Get(e).TypeId, town.X, town.Y));
    }

    [Fact]
    public void A_landing_survives_a_snapshot()
    {
        using AiSkirmishScenario straight = Archipelagos(out _);
        for (int t = 0; t < 30 * 60 * 20 && !straight.Ai.Players.Any(p => p.Landings == 0 && IsAtSea(straight, p)); t++)
        {
            straight.RunTick();
        }

        Assert.Contains(straight.Ai.Players, p => IsAtSea(straight, p));
        byte[] snapshot = straight.Simulation.CaptureSnapshot();
        using AiSkirmishScenario restored = straight.Restore(snapshot, workerCount: 2);
        Assert.Equal(snapshot, restored.Simulation.CaptureSnapshot());
        var a = new StateHasher();
        var b = new StateHasher();
        var wire = new BinaryBufferWriter(1024);
        for (int t = 0; t < 1200; t++)
        {
            a.Add(straight.RunTick().StateHash);
            wire.Clear();
            CommandCodec.WriteBatch(wire, straight.Ai.LastCommands);
            a.Add(wire.WrittenSpan);
            b.Add(restored.RunTick().StateHash);
            wire.Clear();
            CommandCodec.WriteBatch(wire, restored.Ai.LastCommands);
            b.Add(wire.WrittenSpan);
        }

        Assert.Equal(a.GetCurrentHash(), b.GetCurrentHash());
    }

    [Theory]
    [InlineData("phase 7")]
    [InlineData("transports without a landing")]
    [InlineData("too many transports")]
    public void Invalid_naval_AI_state_is_rejected(string corruption)
    {
        using AiSkirmishScenario scenario = Archipelagos(out _);
        for (int t = 0; t < 100; t++)
        {
            scenario.RunTick();
        }

        AiPlayerState state = scenario.Ai.Players[0];
        switch (corruption)
        {
            case "phase 7": state.NavalPhase = (AiNavalPhase)7; break;
            case "transports without a landing": state.Transports.Add(1); break;
            case "too many transports":
                state.NavalPhase = AiNavalPhase.Sailing;
                state.Transports.AddRange(Enumerable.Range(1, AiPlayerState.MaxTransports + 1).Select(i => (uint)i));
                break;
        }

        var writer = new BinaryBufferWriter();
        scenario.Ai.WriteTo(writer);
        Assert.Throws<InvalidDataException>(() => scenario.Ai.ReadFrom(writer.ToArray()));
    }

    private static bool IsAtSea(AiSkirmishScenario s, AiPlayerState p) => p.NavalPhase == AiNavalPhase.Sailing && p.Transports.Count > 0;
}
