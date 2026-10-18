using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.AI.Legacy.Program;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Game.Maps;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Game.Tests;

/// <summary>
/// End to end (ADR 0073): a skirmish of the local (human) kingdom against a Hard legacy-AI kingdom through
/// <see cref="GameSession"/> — lobby setup, the AI in the sim pipeline, the human's commands through the local
/// scheduler — runs for minutes, deterministically, and the AI builds, trains and attacks.
/// </summary>
public sealed class HumanVsAiTests(ITestOutputHelper output)
{
    private const int Minutes = 12;

    private static readonly Lazy<Ruleset> Rules = new(() => SyntheticLegacyContent.CompileRuleset());

    internal sealed record Outcome(ulong Stream, ulong Final, int AiBuilt, int AiTrained, int AiHitsOnHuman, int HumanDelivered, long AiCommands);

    internal static MatchSetup Setup(ulong seed = 11) => new()
    {
        Map = new SkirmishTestMap(80),
        Players = [new PlayerSetup(0, "euro", ControllerKind.Human), new PlayerSetup(1, "euro", ControllerKind.Computer, Difficulty.Hard)],
        Seed = seed,
    };

    internal static Outcome Play(MatchSetup setup, Ruleset rules, AiProgram? program, int minutes)
    {
        using var session = new GameSession(setup, rules, program);
        World world = session.Simulation.World;
        var stream = new StateHasher();
        var events = new List<SimEvent>();
        int built = 0, trained = 0, hits = 0, delivered = 0;
        int ticks = minutes * 60 * 1000 / world.Config.TickLengthMilliseconds;
        for (int t = 0; t < ticks; t++)
        {
            if (t == 5)
            {
                // The human sends its peasants to the nearest trees, as a player's first right click would.
                HumanHarvest(session);
            }

            session.Step(1);
            stream.Add(session.Simulation.ComputeStateHash());
            session.DrainEvents(events);
            foreach (SimEvent e in events)
            {
                switch (e.Kind)
                {
                    case SimEventKind.ConstructionCompleted when e.Player == 1: built++; break;
                    case SimEventKind.UnitTrained when e.Player == 1: trained++; break;
                    case SimEventKind.Damaged when e.Player == 0: hits++; break;
                    case SimEventKind.ResourceDelivered when e.Player == 0: delivered += e.Arg; break;
                }
            }

            events.Clear();
        }

        return new Outcome(stream.GetCurrentHash(), session.Simulation.ComputeFullHash(), built, trained, hits, delivered, session.Ai!.Issued(0));
    }

    [Fact]
    public void A_human_against_a_hard_ai_plays_deterministically_and_the_ai_builds_trains_and_attacks()
    {
        Outcome a = Play(Setup(), Rules.Value, program: null, Minutes);
        Outcome b = Play(Setup(), Rules.Value, program: null, Minutes);
        output.WriteLine(a.ToString());
        Assert.Equal(a, b);
        Assert.True(a.AiCommands > 50, $"the AI issued {a.AiCommands} commands");
        Assert.True(a.AiBuilt > 0, "the AI completed no building");
        Assert.True(a.AiTrained > 0, "the AI trained no unit");
        Assert.True(a.AiHitsOnHuman > 0, "the AI never hit the human kingdom");
        Assert.True(a.HumanDelivered > 0, "the human's harvest order did not reach the sim");
        Assert.NotEqual(Play(Setup(seed: 12), Rules.Value, program: null, 1).Final, a.Final);
    }

    [PackageFact]
    public void A_human_against_the_original_hard_ai_plays_on_the_imported_package()
    {
        using PackageSource package = PackageSource.Open(ImportedPackageSkirmishTests.PackagePath!);
        Ruleset rules = RulesetLoader.Compile(package).Ruleset;
        AiProgram program = AiScripts.ForSkirmish(package, out bool original);
        Assert.True(original, "the package's random.ais did not compile");
        MatchSetup setup = Setup() with
        {
            Map = new SkirmishTestMap(96),
            Players = [new PlayerSetup(0, "euro", ControllerKind.Human), new PlayerSetup(1, "arab", ControllerKind.Computer, Difficulty.Hard)],
        };
        Outcome a = Play(setup, rules, program, 15);
        output.WriteLine(a.ToString());
        Assert.Equal(a, Play(setup, rules, program, 15));
        Assert.True(a.AiBuilt > 0 && a.AiTrained > 0, a.ToString());
    }

    [Fact]
    public void Without_scripts_in_the_package_the_engine_default_is_used()
    {
        AiProgram program = AiScripts.ForSkirmish(new MemorySource("empty"), out bool original);
        Assert.False(original);
        Assert.False(program.HasErrors);
        AiProgram broken = AiScripts.ForSkirmish(new MemorySource("broken").Add(AiScripts.PackageFolder + "random.ais", "this is not a script ("u8), out original);
        Assert.False(original);
        Assert.False(broken.HasErrors);
    }

    private static void HumanHarvest(GameSession session)
    {
        World w = session.Simulation.World;
        ushort tree = (ushort)w.Ruleset.EntityTypeId("comm.tree");
        EntityId[] peasants = w.Identities.Entities.ToArray().Where(e => w.Identities.Get(e).Owner == 0 && w.TypeOf(e).Traits.Harvester is not null).ToArray();
        Sim.Components.Position at = w.Positions.Get(peasants[0]);
        EntityId nearest = w.Identities.Entities.ToArray().Where(e => w.Identities.Get(e).TypeId == tree)
            .OrderBy(e => Distance(w.Positions.Get(e), at)).First();
        var batch = new CommandList();
        batch.Add(CommandKind.Harvest, peasants, target: nearest);
        session.Submit(batch);
    }

    private static long Distance(Sim.Components.Position a, Sim.Components.Position b)
        => ((long)(a.X - b.X) * (a.X - b.X)) + ((long)(a.Y - b.Y) * (a.Y - b.Y));
}
