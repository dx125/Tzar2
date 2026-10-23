using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Game.Maps;
using Tzar2.Game.Session;
using Tzar2.Replay;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Game.Tests;

/// <summary>
/// End to end (ADR 0073): a skirmish of the local (human) kingdom against a Hard legacy-AI kingdom through
/// <see cref="GameSession"/> — lobby setup, the AI in the sim pipeline, the human's commands through the local
/// scheduler — runs for minutes, deterministically, and the AI builds, trains and attacks; the AI script is part of the
/// match identity (fingerprint, replay header, snapshots).
/// </summary>
public sealed class HumanVsAiTests(ITestOutputHelper output)
{
    private const int Minutes = 12;

    private static readonly Lazy<Ruleset> Rules = new(() => SyntheticLegacyContent.CompileRuleset());

    internal static Ruleset SyntheticRules => Rules.Value;

    internal sealed record Outcome(ulong Stream, ulong Final, int AiBuilt, int AiTrained, int AiHitsOnHuman, int HumanDelivered, long AiCommands);

    internal static MatchSetup Setup(ulong seed = 11) => new()
    {
        Map = new SkirmishTestMap(80),
        Players = [new PlayerSetup(0, "euro", ControllerKind.Human), new PlayerSetup(1, "euro", ControllerKind.Computer, Difficulty.Hard)],
        Seed = seed,
    };

    internal static Outcome Play(MatchSetup setup, Ruleset rules, int minutes)
    {
        using var session = new GameSession(setup, rules);
        return Play(session, minutes * 60 * 1000 / session.Simulation.World.Config.TickLengthMilliseconds, new StateHasher());
    }

    private static Outcome Play(GameSession session, int ticks, StateHasher stream)
    {
        var events = new List<SimEvent>();
        int built = 0, trained = 0, hits = 0, delivered = 0;
        for (int t = 0; t < ticks; t++)
        {
            if (session.Simulation.World.Tick.Value == 5)
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
        Outcome a = Play(Setup(), Rules.Value, Minutes);
        Outcome b = Play(Setup(), Rules.Value, Minutes);
        output.WriteLine(a.ToString());
        Assert.Equal(a, b);
        Assert.True(a.AiCommands > 50, $"the AI issued {a.AiCommands} commands");
        Assert.True(a.AiBuilt > 0, "the AI completed no building");
        Assert.True(a.AiTrained > 0, "the AI trained no unit");
        Assert.True(a.AiHitsOnHuman > 0, "the AI never hit the human kingdom");
        Assert.True(a.HumanDelivered > 0, "the human's harvest order did not reach the sim");
    }

    /// <summary>
    /// Game minutes per map type: long enough for the Hard AI's first wave (after 4 minutes, ADR 0052) to land; 6 until
    /// one peasant tended each field (ECO-021) and the first fields had to be built before the food came in; 8 until the
    /// units had the original's body (WORLD-015), whose crowds slow the economy: the first hits came between 8 and 9.
    /// </summary>
    private const int MapTypeMinutes = 10;

    /// <summary>
    /// Game minutes on Archipelagos, where the first wave must also board a transport, cross the sea and land (ADR 0144;
    /// on this map a little after 9 minutes, between 12 and 14 since the measured walking speeds of ADR 0172, between 16 and
    /// 18 since the original's forests of WORLD-013 and the movement of ADR 0220 together): a passive human keeps nothing
    /// within a ship's range of the water before.
    /// </summary>
    private const int SeaMapTypeMinutes = 18;

    private static int MinutesFor(Tzar2.Content.MapGen.RandomMapType type) => type == Tzar2.Content.MapGen.RandomMapType.Archipelagos ? SeaMapTypeMinutes : MapTypeMinutes;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Tzar2.Content.MapGen.RandomMapType, Lazy<(Outcome A, Outcome B, string Map)>> MapTypeRuns = new();

    internal static MatchSetup MapTypeSetup(Tzar2.Content.MapGen.RandomMapType type)
        => Setup() with { Map = new RandomMapSource(type, size: Tzar2.Content.MapGen.RandomMapSize.Tiny) };

    // Two runs per map type, shared by the theories below (each map type is played twice, once).
    private static (Outcome A, Outcome B, string Map) MapTypeRun(Tzar2.Content.MapGen.RandomMapType type)
        => MapTypeRuns.GetOrAdd(type, t => new Lazy<(Outcome, Outcome, string)>(() =>
        {
            MatchSetup setup = MapTypeSetup(t);
            string map = setup.Map.Create(Rules.Value, setup).Name;
            return (Play(setup, Rules.Value, MinutesFor(t)), Play(setup, Rules.Value, MinutesFor(t)), map);
        })).Value;

    [Theory]
    [InlineData(Tzar2.Content.MapGen.RandomMapType.Random)]
    [InlineData(Tzar2.Content.MapGen.RandomMapType.Archipelagos)]
    [InlineData(Tzar2.Content.MapGen.RandomMapType.Dry)]
    [InlineData(Tzar2.Content.MapGen.RandomMapType.Island)]
    [InlineData(Tzar2.Content.MapGen.RandomMapType.Continental)]
    [InlineData(Tzar2.Content.MapGen.RandomMapType.Lakes)]
    public void A_human_against_a_hard_ai_plays_every_random_map_type(Tzar2.Content.MapGen.RandomMapType type)
    {
        (Outcome a, Outcome b, string map) = MapTypeRun(type);
        output.WriteLine($"{type} ({map}): {a}");
        Assert.Equal(a, b);
        Assert.True(a.AiBuilt > 0, $"{map}: the AI completed no building");
        Assert.True(a.AiTrained > 0, $"{map}: the AI trained no unit");
    }

    /// <summary>
    /// The attack check follows reachability, not the map type: wherever the AI can reach the human's castle — on foot,
    /// or by ship from its coast to the human's (harbors, landings and warships, ADR 0144) — it must attack. Rows whose
    /// castles neither land nor a shared sea connects are skipped (<see cref="ReachableMapTypeAttribute"/>).
    /// </summary>
    [Theory]
    [ReachableMapType(Tzar2.Content.MapGen.RandomMapType.Random)]
    [ReachableMapType(Tzar2.Content.MapGen.RandomMapType.Archipelagos)]
    [ReachableMapType(Tzar2.Content.MapGen.RandomMapType.Dry)]
    [ReachableMapType(Tzar2.Content.MapGen.RandomMapType.Island)]
    [ReachableMapType(Tzar2.Content.MapGen.RandomMapType.Continental)]
    [ReachableMapType(Tzar2.Content.MapGen.RandomMapType.Lakes)]
    public void The_hard_ai_attacks_the_human_wherever_its_castle_is_reachable(Tzar2.Content.MapGen.RandomMapType type)
    {
        (Outcome a, _, string map) = MapTypeRun(type);
        output.WriteLine($"{type} ({map}): {a.AiHitsOnHuman} AI hits on the human");
        Assert.True(a.AiHitsOnHuman > 0, $"{map}: the castles are reachable, but the AI never hit the human kingdom in {MinutesFor(type)} minutes");
    }

    /// <summary>Whether a foot unit of slot a can walk from next to a's castle to next to b's castle (the peasants' nav layer).</summary>
    internal static bool CastlesLandConnected(World world, byte a, byte b)
    {
        world.Navigation.EnsureUpToDate();
        EntityId peasant = world.Identities.Entities.ToArray().First(e => world.TypeOf(e).Traits.Harvester is not null);
        Tzar2.Sim.Navigation.NavLayer layer = world.Navigation.ForType(world.Identities.Get(peasant).TypeId)!;
        int Near(byte owner)
        {
            EntityId castle = world.Identities.Entities.ToArray().First(e => world.Identities.Get(e).Owner == owner && world.TypeOf(e).Traits.LegacyClass == "castle");
            Sim.Components.Position p = world.Positions.Get(castle);
            return Tzar2.Sim.Navigation.NavQueries.NearestWalkable(layer, p.X / MapData.UnitsPerTile, p.Y / MapData.UnitsPerTile, 8);
        }

        int from = Near(a), to = Near(b);
        if (from < 0 || to < 0)
        {
            // A castle walled in without a walkable tile within 8 tiles is a broken map, not a water crossing.
            throw new InvalidOperationException($"No walkable tile within 8 tiles of the castle of kingdom {(from < 0 ? a : b) + 1}: the map is broken.");
        }

        return layer.AreConnected(from, to);
    }

    /// <summary>
    /// Whether slot a can reach b's castle: by land (<see cref="CastlesLandConnected"/>), or by water — one sea touches
    /// the coasts of both castles' land (a ship-layer tile within the shore reach of a land tile of each component), so a
    /// harbor on each side can send ships across (ADRs 0140, 0144).
    /// </summary>
    internal static bool CastlesReachable(World world, byte a, byte b)
    {
        if (CastlesLandConnected(world, a, b))
        {
            return true;
        }

        EntityId peasant = world.Identities.Entities.ToArray().First(e => world.TypeOf(e).Traits.Harvester is not null && !world.IsShip(e));
        Tzar2.Sim.Navigation.NavLayer land = world.Navigation.ForType(world.Identities.Get(peasant).TypeId)!;
        Tzar2.Sim.Navigation.NavLayer? sea = null;
        for (int t = 0; t < world.Ruleset.EntityTypes.Count && sea is null; t++)
        {
            if (world.Ruleset.EntityTypes[t] is { IsMobile: true, Locomotor: Tzar2.Content.Compiled.Locomotor.Ship })
            {
                sea = world.Navigation.ForType((ushort)t);
            }
        }

        if (sea is null)
        {
            return false; // the ruleset has no ships
        }

        HashSet<int> Seas(byte owner)
        {
            EntityId castle = world.Identities.Entities.ToArray().First(e => world.Identities.Get(e).Owner == owner && world.TypeOf(e).Traits.LegacyClass == "castle");
            Sim.Components.Position p = world.Positions.Get(castle);
            int component = Tzar2.Sim.Systems.Shore.LandComponentAt(world, world.Identities.Get(peasant).TypeId, p.X, p.Y);
            var seas = new HashSet<int>();
            for (int tile = 0; tile < land.Width * land.Height; tile++)
            {
                if (land.ComponentOf(tile) != component)
                {
                    continue;
                }

                int lx = land.CenterX(tile), ly = land.CenterY(tile);
                for (int dy = -Tzar2.Sim.Systems.Shore.ReachTiles; dy <= Tzar2.Sim.Systems.Shore.ReachTiles; dy++)
                {
                    for (int dx = -Tzar2.Sim.Systems.Shore.ReachTiles; dx <= Tzar2.Sim.Systems.Shore.ReachTiles; dx++)
                    {
                        int sx = sea.TileX(lx) + dx, sy = sea.TileY(ly) + dy;
                        if (sea.IsWalkable(sx, sy))
                        {
                            seas.Add(sea.ComponentOf((sy * sea.Width) + sx));
                        }
                    }
                }
            }

            return seas;
        }

        return Seas(a).Overlaps(Seas(b));
    }

    [Fact]
    public void Another_seed_gives_another_match_of_the_same_length()
    {
        Outcome a = Play(Setup(seed: 11), Rules.Value, 2);
        Outcome b = Play(Setup(seed: 12), Rules.Value, 2);
        Assert.NotEqual(a.Stream, b.Stream);
        Assert.NotEqual(a.Final, b.Final);
    }

    [Fact]
    public void A_snapshot_taken_mid_match_continues_with_the_same_hash_stream()
    {
        // Straight: 4 minutes. Split: 2 minutes, snapshot, restore (the AI is re-attached and reads its state back), 2
        // more minutes. The per-tick hashes and the final full hash must match.
        int half = 2 * 60 * 20;
        using var straight = new GameSession(Setup(), Rules.Value);
        Outcome whole = Play(straight, 2 * half, new StateHasher());

        var stream = new StateHasher();
        using var first = new GameSession(Setup(), Rules.Value);
        Play(first, half, stream);
        byte[] snapshot = first.CaptureSnapshot();
        using GameSession second = GameSession.Restore(Setup(), Rules.Value, snapshot);
        Assert.NotNull(second.Ai);
        Outcome resumed = Play(second, half, stream);
        Assert.Equal(whole.Stream, resumed.Stream);
        Assert.Equal(whole.Final, resumed.Final);

        // A snapshot saved with one AI script is refused by a setup with another.
        Assert.Throws<InvalidDataException>(() => GameSession.Restore(Setup() with { Ai = OtherScript() }, Rules.Value, snapshot));
    }

    [Fact]
    public void Another_script_changes_the_fingerprint_the_replay_header_and_the_full_hash()
    {
        SkirmishAi other = OtherScript();
        Assert.True(other.IsOriginal);
        Assert.NotEqual(SkirmishAi.EngineDefault.ContentHash, other.ContentHash);

        using var a = new GameSession(Setup(), Rules.Value);
        using var b = new GameSession(Setup() with { Ai = other }, Rules.Value);
        Assert.Equal([new ReplayAiProgram(1, SkirmishAi.EngineDefault.ContentHash)], a.Fingerprint.AiPrograms.ToArray());
        Assert.NotEqual(a.Fingerprint.Value, b.Fingerprint.Value);
        Assert.Equal(a.Fingerprint.AiPrograms.ToArray(), a.ReplayHeader().AiPrograms.ToArray());

        // Already at tick 1 the full-state hash differs (the AI state carries the program hash).
        a.Step(1);
        b.Step(1);
        Assert.NotEqual(a.Simulation.ComputeFullHash(), b.Simulation.ComputeFullHash());

        // A peer with another script is refused at match start, naming the kingdom.
        var e = Assert.Throws<MatchMismatchException>(() => new GameSession(Setup(), Rules.Value, peers: [b.Fingerprint]));
        Assert.Contains("kingdom 2", e.Message, StringComparison.Ordinal);
        using var agreed = new GameSession(Setup(), Rules.Value, peers: [a.Fingerprint]);

        // The replay header keeps the AI programs.
        var recorder = new ReplayRecorder(a.ReplayHeader());
        Assert.Equal(a.Fingerprint.AiPrograms.ToArray(), ReplayFile.Parse(recorder.Finish()).Header.AiPrograms.ToArray());
    }

    [PackageFact]
    public void A_human_against_the_original_hard_ai_plays_on_the_imported_package()
    {
        using PackageSource package = PackageSource.Open(ImportedPackageSkirmishTests.PackagePath!);
        Ruleset rules = RulesetLoader.Compile(package).Ruleset;
        SkirmishAi ai = SkirmishAi.Resolve(package);
        Assert.True(ai.IsOriginal, ai.Notice);
        MatchSetup setup = Setup() with
        {
            Map = new SkirmishTestMap(96),
            Players = [new PlayerSetup(0, "euro", ControllerKind.Human), new PlayerSetup(1, "arab", ControllerKind.Computer, Difficulty.Hard)],
            Ai = ai,
        };
        Outcome a = Play(setup, rules, 15);
        output.WriteLine(a.ToString());
        Assert.Equal(a, Play(setup, rules, 15));
        Assert.True(a.AiBuilt > 0 && a.AiTrained > 0, a.ToString());
    }

    [Fact]
    public void Without_usable_scripts_in_the_package_the_engine_default_is_used_with_a_notice()
    {
        Assert.Null(SkirmishAi.Resolve(null).Notice);
        SkirmishAi empty = SkirmishAi.Resolve(new MemorySource("empty"));
        Assert.False(empty.IsOriginal);
        Assert.Equal(SkirmishAi.EngineDefault.ContentHash, empty.ContentHash);
        Assert.Contains("not in the package", empty.Notice, StringComparison.Ordinal);
        SkirmishAi broken = SkirmishAi.Resolve(new MemorySource("broken").Add(SkirmishAi.PackageFolder + "random.ais", "this is not a script ("u8));
        Assert.False(broken.IsOriginal);
        Assert.Equal(SkirmishAi.EngineDefault.ContentHash, broken.ContentHash);
        Assert.Contains("do not compile", broken.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ai_runs_on_the_sim_thread_at_top_speed_without_faults()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        session.SpeedIndex = GameSession.SpeedSteps.Length - 1;
        session.Start();
        System.Threading.SpinWait.SpinUntil(() => session.TicksRun >= 600 || session.Fault is not null, TimeSpan.FromSeconds(60));
        Assert.Null(session.Fault);
        Assert.True(session.TicksRun >= 600);
    }

    // A "package" whose random.ais is the engine default plus one rule: another program, still valid.
    private static SkirmishAi OtherScript()
    {
        string text = Encoding.UTF8.GetString(new EngineDefaultsSource().ReadAllBytes(SkirmishAi.EngineDefaultPath))
            + "\nrule say \"another script\" forget THIS end\n";
        return SkirmishAi.Resolve(new MemorySource("other").Add(SkirmishAi.PackageFolder + "random.ais", Encoding.UTF8.GetBytes(text)));
    }

    internal static void HumanHarvest(GameSession session)
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

/// <summary>
/// A map-type row of the attack theory, skipped with the reason when the generated map's castles (AI kingdom 2, human
/// kingdom 1) are reachable neither by land nor by ship across a shared sea: decided from the map's navigation layers,
/// not from the map type. A castle without a walkable tile near it throws (a broken map).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ReachableMapTypeAttribute : Xunit.Sdk.DataAttribute
{
    private readonly Tzar2.Content.MapGen.RandomMapType _type;

    public ReachableMapTypeAttribute(Tzar2.Content.MapGen.RandomMapType type)
    {
        _type = type;
        MatchSetup setup = HumanVsAiTests.MapTypeSetup(type);
        using var probe = new GameSession(setup with { ComputerAi = false }, HumanVsAiTests.SyntheticRules);
        if (!HumanVsAiTests.CastlesReachable(probe.Simulation.World, 1, 0))
        {
            Skip = $"{probe.Map.Name}: neither land nor a shared sea connects the AI's castle with the human's";
        }
    }

    public override IEnumerable<object[]> GetData(System.Reflection.MethodInfo testMethod) => [[_type]];
}
