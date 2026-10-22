using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Ids;
using Tzar2.Game.Input;
using Tzar2.Game.Maps;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;
using Xunit;

namespace Tzar2.Game.Tests;

public sealed class SessionTests
{
    private static readonly Lazy<Ruleset> Rules = new(() => SyntheticLegacyContent.CompileRuleset());

    internal static MatchSetup Setup(int players = 2, int size = 64, ulong seed = 7) => new()
    {
        Map = new SkirmishTestMap(size),
        Players = Enumerable.Range(0, players)
            .Select(i => new PlayerSetup((byte)i, "euro", i == 0 ? ControllerKind.Human : ControllerKind.Computer, Difficulty.Hard))
            .ToArray(),
        Seed = seed,
    };

    [Fact]
    public void The_test_map_is_valid_and_deterministic()
    {
        MatchSetup setup = Setup(4, 96);
        MapData a = setup.Map.Create(Rules.Value, setup);
        MapData b = setup.Map.Create(Rules.Value, setup);
        a.Validate(Rules.Value);
        Assert.Equal(a.ContentHash, b.ContentHash);
        for (int p = 0; p < 4; p++)
        {
            Assert.Single(a.Objects, o => o.Owner == p && o.TypeKey == "euro.castle");
            Assert.Equal(6, a.Objects.Count(o => o.Owner == p && o.TypeKey == "euro.peon"));
        }

        Assert.True(a.Objects.Count(o => o.TypeKey == "comm.tree") > 100);
        Assert.Contains(a.Objects, o => o.TypeKey == "comm.res_gold");
        Assert.Contains(a.TerrainCorners.ToArray(), c => Rules.Value.TerrainTypes[c].IsWater);
        Assert.NotEqual(a.ContentHash, Setup(4, 96, seed: 8).Map.Create(Rules.Value, Setup(4, 96, seed: 8)).ContentHash);
    }

    [Fact]
    public void A_skirmish_runs_300_ticks_and_publishes_frames_and_events()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        GameFrame? first = session.AcquireFrame(out bool fresh);
        Assert.NotNull(first);
        Assert.True(fresh);
        Assert.Equal(0, first!.Tick.Value);
        Tzar2.Sim.World world = session.Simulation.World;
        var startAt = new Dictionary<EntityId, Tzar2.Sim.Components.Position>();
        foreach (EntityId e in world.Motions.Entities)
        {
            startAt[e] = world.Positions.Get(e);
        }

        session.Step(300);
        GameFrame frame = session.AcquireFrame(out fresh)!;
        Assert.True(fresh);
        Assert.Equal(300, frame.Tick.Value);
        Assert.Equal(300, session.TicksRun);
        // GAME-12: the frame shows what the local kingdom sees (units in sight, structures as last seen).
        Assert.True(frame.EntityCount <= session.Simulation.World.Identities.Count + 64);
        Assert.All(frame.Entities.ToArray(), e => Assert.True(e.Owner == 0 || e.Remembered || session.Simulation.World.Visibility.CanSee(session.Simulation.World, 0, e.Id)));
        Assert.Equal(2, frame.Players.Length);
        Assert.Equal(64 * 64, frame.Visibility.Length);
        Assert.Contains(frame.Visibility, v => v == 2);
        Assert.Contains(frame.Visibility, v => v == 0);
        Assert.Equal(MatchOutcome.None, frame.Outcome);

        // The Hard legacy AI sends its peasants to work: some of them have moved (they may be standing at their work by
        // tick 300, so this does not ask for one that is moving right now).
        var events = new List<SimEvent>();
        session.DrainEvents(events);
        Assert.NotEmpty(events);
        Assert.Contains(startAt, s => world.Entities.IsAlive(s.Key) && world.Positions.Has(s.Key) && (world.Positions.Get(s.Key).X != s.Value.X || world.Positions.Get(s.Key).Y != s.Value.Y));
        events.Clear();
        session.DrainEvents(events);
        Assert.Empty(events);
    }

    [Fact]
    [Trait("Rule", "CTRL-025")]
    public void The_frame_carries_the_local_units_way_for_the_waypoints_shown_with_shift()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        GameFrame frame = session.AcquireFrame(out _)!;
        FrameEntity peon = frame.Entities.ToArray().First(e => e.Owner == 0 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "peon");
        var batch = new CommandList();
        batch.Add(CommandKind.Move, [peon.Id], peon.X + 6000, peon.Y);
        batch.Add(CommandKind.Move, [peon.Id], peon.X + 6000, peon.Y + 6000, flags: CommandFlags.Queue);
        batch.Add(CommandKind.Move, [peon.Id], peon.X, peon.Y + 6000, flags: CommandFlags.Queue);
        session.Submit(batch);
        session.Step(3);
        frame = session.AcquireFrame(out _)!;
        FrameWaypoint[] way = [.. frame.Waypoints.ToArray().Where(w => w.Unit == peon.Id.Value)];
        Assert.Equal([(peon.X + 6000, peon.Y), (peon.X + 6000, peon.Y + 6000), (peon.X, peon.Y + 6000)], way.Select(w => (w.X, w.Y)));
        Assert.All(way, w => Assert.Equal(FrameOrderKind.Move, w.Kind));
        Assert.DoesNotContain(frame.Waypoints.ToArray(), w => frame.Entities.ToArray().Any(e => e.Id.Value == w.Unit && e.Owner != 0));
    }

    [Fact]
    [Trait("Rule", "ITEM-012")]
    public void A_unit_that_picks_up_a_spell_book_shows_the_stars()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        World world = session.Simulation.World;
        GameFrame frame = session.AcquireFrame(out _)!;
        FrameEntity peon = frame.Entities.ToArray().First(e => e.Owner == 0 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "peon");
        EntityId footman = world.Spawn((ushort)Rules.Value.EntityTypeId("euro.footman"), 0, peon.X, peon.Y + 2048);
        EntityId book = world.Spawn((ushort)Rules.Value.EntityTypeId("comm.item_magic"), MapObject.Nature, peon.X + (5 * 2048), peon.Y + 2048);
        var batch = new CommandList();
        batch.Add(CommandKind.PickItem, [footman], target: book);
        session.Submit(batch);
        session.Step(1);
        Assert.False(session.AcquireFrame(out _)!.Entities.ToArray().Single(e => e.Id == footman).CarriesBook);
        session.Step(200);
        Assert.True(world.IsHeld(book));
        Assert.True(session.AcquireFrame(out _)!.Entities.ToArray().Single(e => e.Id == footman).CarriesBook);
    }

    [Fact]
    [Trait("Rule", "HUD-032")]
    public void A_working_peasant_is_named_by_its_job()
    {
        Ruleset r = Rules.Value;
        EntityTypeDefinition Type(string key) => r.EntityTypes[r.EntityTypeId(key)];
        Worker Harvest(string resource) => new() { Task = WorkerTask.Harvest, NodeResource = (byte)r.ResourceId(resource) };
        Assert.Equal(FrameJob.Chop, FrameBuilder.JobOf(r, Harvest("wood"), Type("comm.tree")));
        Assert.Equal(FrameJob.Dig, FrameBuilder.JobOf(r, Harvest("food"), Type("comm.field")));
        Assert.Equal(FrameJob.TendCows, FrameBuilder.JobOf(r, Harvest("food"), Type("comm.cow")));
        Assert.Equal(FrameJob.Collect, FrameBuilder.JobOf(r, Harvest("food"), Type("comm.meat")));
        Assert.Equal(FrameJob.Mine, FrameBuilder.JobOf(r, Harvest("gold"), Type("comm.res_gold")));
        Assert.Equal(FrameJob.Build, FrameBuilder.JobOf(r, new Worker { Task = WorkerTask.Build }, null));
        Assert.Equal(FrameJob.Repair, FrameBuilder.JobOf(r, new Worker { Task = WorkerTask.Repair }, null));
        Assert.Equal(FrameJob.None, FrameBuilder.JobOf(r, default, null));
        Assert.Equal(["chop", "dig", "mine", "build", "tend cows"], new[] { FrameJob.Chop, FrameJob.Dig, FrameJob.Mine, FrameJob.Build, FrameJob.TendCows }.Select(FrameJobs.StringKey));

        // In a match: a peasant sent to a tree is a woodcutter.
        using var session = new GameSession(Setup(), Rules.Value);
        GameFrame frame = session.AcquireFrame(out _)!;
        FrameEntity peon = frame.Entities.ToArray().First(e => e.Owner == 0 && r.EntityTypes[e.TypeId].Traits.LegacyClass == "peon");
        FrameEntity tree = frame.Entities.ToArray().Where(e => r.EntityTypes[e.TypeId].Key == "comm.tree").MinBy(e => Math.Abs(e.X - peon.X) + Math.Abs(e.Y - peon.Y));
        var batch = new CommandList();
        batch.Add(CommandKind.Harvest, [peon.Id], target: tree.Id);
        session.Submit(batch);
        session.Step(2);
        Assert.Equal(FrameJob.Chop, session.AcquireFrame(out _)!.Entities.ToArray().Single(e => e.Id == peon.Id).Job);
    }

    [Fact]
    public void Submitted_commands_are_stamped_with_the_local_slot_and_applied()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        GameFrame frame = session.AcquireFrame(out _)!;
        FrameEntity peon = frame.Entities.ToArray().First(e => e.Owner == 0 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "peon");
        var batch = new CommandList();
        batch.Add(CommandKind.Move, [peon.Id], peon.X + 6000, peon.Y, player: 5);
        session.Submit(batch);
        session.Step(40);
        frame = session.AcquireFrame(out _)!;
        FrameEntity moved = frame.Entities.ToArray().Single(e => e.Id == peon.Id);
        Assert.True(moved.X > peon.X + 2000, $"peon at {moved.X}, started at {peon.X}");
    }

    [Fact]
    public void A_load_looks_like_what_it_was_taken_from()
    {
        Ruleset r = Rules.Value;
        EntityTypeDefinition Type(string key) => r.EntityTypes[r.EntityTypeId(key)];
        int food = r.ResourceId("food");
        Assert.Equal(FrameCarry.Wood, FrameBuilder.CarryOf(r, r.ResourceId("wood"), null));
        Assert.Equal(FrameCarry.Gold, FrameBuilder.CarryOf(r, r.ResourceId("gold"), null));
        Assert.Equal(FrameCarry.Stone, FrameBuilder.CarryOf(r, r.ResourceId("stone"), null));
        Assert.Equal(FrameCarry.Milk, FrameBuilder.CarryOf(r, food, Type("comm.cow")));
        Assert.Equal(FrameCarry.Meat, FrameBuilder.CarryOf(r, food, Type("comm.meat")));
        Assert.Equal(FrameCarry.Food, FrameBuilder.CarryOf(r, food, Type("comm.field")));
        Assert.Equal(FrameCarry.Food, FrameBuilder.CarryOf(r, food, null));
    }

    [Fact]
    public void The_buildable_layer_matches_the_sim_placement_query()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        session.Step(1);
        GameFrame frame = session.AcquireFrame(out _)!;
        World world = session.Simulation.World;
        ushort house = (ushort)Rules.Value.EntityTypeId("euro.house");
        EntityTypeDefinition type = Rules.Value.EntityTypes[house];
        int checkedLegal = 0;
        for (int ty = 0; ty < world.Map.Height; ty += 3)
        {
            for (int tx = 0; tx < world.Map.Width; tx += 3)
            {
                bool expected = PlacementQueries.CanPlace(world, house, tx, ty);
                Assert.Equal(expected, Placement.IsLegal(frame.Buildable, world.Map.Width, world.Map.Height, type, tx, ty));
                Assert.Equal(expected, Placement.BlockedCells(frame.Buildable, world.Map.Width, world.Map.Height, type, tx, ty).Count == 0);
                checkedLegal += expected ? 1 : 0;
            }
        }

        Assert.True(checkedLegal > 50);
        Assert.Equal(type.FootprintWidth * type.FootprintHeight, Placement.BlockedCells(frame.Buildable, world.Map.Width, world.Map.Height, type, -10, -10).Count);
    }

    [Theory]
    [InlineData("euro.harbor", false)]
    [InlineData("comm.bridge", false)]
    [InlineData("comm.bridge", true)]
    [InlineData("comm.gate_wood", true)]
    [InlineData("comm.gate_wood", false)]
    [Trait("Rule", "BLD-018")]
    [Trait("Rule", "BLD-019")]
    [Trait("Rule", "CTRL-024")]
    public void Coast_bridge_and_turned_placements_match_the_sim_placement_query(string key, bool rotated)
    {
        ushort id = (ushort)Rules.Value.EntityTypeId(key);
        EntityTypeDefinition type = Rules.Value.EntityTypes[id];
        int legal = 0;
        foreach (IMapSource map in new IMapSource[] { new SkirmishTestMap(64), new RiverMap() })
        {
            using var session = new GameSession(Setup() with { Map = map }, Rules.Value);
            session.Step(1);
            GameFrame frame = session.AcquireFrame(out _)!;
            World world = session.Simulation.World;
            for (int ty = 0; ty < world.Map.Height; ty++)
            {
                for (int tx = 0; tx < world.Map.Width; tx++)
                {
                    bool expected = PlacementQueries.CanPlaceFor(world, 0, id, tx, ty, rotated);
                    Assert.True(expected == Placement.IsLegal(frame.Buildable, frame.Terrain, world.Map.Width, world.Map.Height, type, tx, ty, rotated), $"{key} at {tx},{ty} on {map.Name}: the sim says {expected}");
                    legal += expected ? 1 : 0;
                }
            }
        }

        Assert.True(legal > 0, $"{key} fits somewhere");
    }

    [Fact]
    public void The_frame_says_what_the_local_player_can_make_and_what_it_has_researched()
    {
        using var session = new GameSession(Setup() with { ComputerAi = false }, Rules.Value, fog: false);
        session.Step(1);
        GameFrame frame = session.AcquireFrame(out _)!;
        World world = session.Simulation.World;
        Assert.Equal(Rules.Value.EntityTypes.Count, frame.Makeable.Length);
        for (int t = 0; t < frame.Makeable.Length; t++)
        {
            EntityTypeDefinition type = Rules.Value.EntityTypes[t];
            if (type.Traits.Category is EntityCategory.Unit or EntityCategory.Building)
            {
                Assert.Equal(PlacementQueries.RequirementsMet(world, session.LocalPlayer, (ushort)t), frame.Makeable[t] != 0);
            }
        }

        Assert.Contains(frame.Makeable, m => m != 0);
        Assert.Contains(frame.Makeable, m => m == 0);
        Assert.Equal(Rules.Value.Technologies.Count, frame.Researchable.Length);

        // Research queued in the castle shows as a research item and as "researching" for the kingdom.
        int feudalism = Rules.Value.TechnologyId("te_feudalism");
        Assert.Equal(1, frame.Researchable[feudalism]);
        FrameEntity castle = frame.Entities.ToArray().First(e => e.Owner == session.LocalPlayer && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "castle");
        var research = new CommandList();
        research.Add(CommandKind.Research, [castle.Id], arg0: feudalism, player: session.LocalPlayer);
        session.Submit(research);
        session.Step(2);
        frame = session.AcquireFrame(out _)!;
        FrameEntity researching = frame.Entities.ToArray().Single(e => e.Id == castle.Id);
        Assert.Equal((FrameQueueKind.Research, (ushort)feudalism), (researching.QueueKind, researching.QueueHead));
        Assert.True(frame.Players[session.LocalPlayer].IsResearching(feudalism));
        Assert.False(frame.Players[session.LocalPlayer].HasTechnology(feudalism));
        int tech = Rules.Value.Technologies.Count - 1;
        Assert.False(frame.Players[session.LocalPlayer].HasTechnology(tech));
        world.Players[session.LocalPlayer].AddTechnology(tech);
        session.Step(1);
        frame = session.AcquireFrame(out _)!;
        Assert.True(frame.Players[session.LocalPlayer].HasTechnology(tech));
    }

    [Fact]
    public void The_frame_carries_the_mana_of_casters()
    {
        var b = new Tzar2.Tests.Scenario.ScenarioBuilder();
        b.Structure("euro.castle", 0, 4, 4);
        b.Unit("euro.mage", 0, 12, 12);
        b.Unit("euro.footman", 0, 14, 12);
        b.Structure("euro.castle", 1, 34, 20);
        var mission = new Campaigns.MissionContent(b.Map(), b.Scenario());
        using var session = new GameSession(Campaigns.ScenarioMatch.Setup(mission, Rules.Value, computerAi: false), Rules.Value, fog: false);
        session.Step(1);
        FrameEntity[] entities = session.AcquireFrame(out _)!.Entities.ToArray();
        FrameEntity mage = entities.Single(e => Rules.Value.EntityTypes[e.TypeId].Key == "euro.mage");
        FrameEntity footman = entities.Single(e => Rules.Value.EntityTypes[e.TypeId].Key == "euro.footman");
        Assert.Equal(Rules.Value.EntityTypes[mage.TypeId].Traits.ManaPool!.Max, mage.MaxMana);
        Assert.InRange(mage.Mana, 0, mage.MaxMana);
        Assert.Equal((0, 0), (footman.Mana, footman.MaxMana));
    }

    [Fact]
    public void The_frame_carries_projectiles_in_flight_and_burning_buildings()
    {
        var b = new Tzar2.Tests.Scenario.ScenarioBuilder();
        b.Structure("euro.castle", 0, 4, 4);
        b.Unit("euro.footman", 0, 16, 10);
        b.Structure("comm.tower_wood", 1, 22, 10);
        b.Structure("euro.house", 1, 30, 20);
        b.Structure("euro.castle", 1, 38, 4);
        var mission = new Campaigns.MissionContent(b.Map(), b.Scenario());
        using var session = new GameSession(Campaigns.ScenarioMatch.Setup(mission, Rules.Value, computerAi: false), Rules.Value, fog: false);
        World world = session.Simulation.World;
        EntityId house = world.Identities.Entities.ToArray().Single(e => world.TypeOf(e).Key == "euro.house");
        ref Sim.Components.Health health = ref world.Healths.Get(house);
        health.Current = health.Max / 5; // below the burn threshold
        bool shot = false;
        for (int t = 0; t < 200 && !shot; t++)
        {
            session.Step(1);
            GameFrame frame = session.AcquireFrame(out _)!;
            foreach (FrameProjectile p in frame.Projectiles)
            {
                shot |= Rules.Value.Projectiles[p.Kind].Key == "arrow" && p.TargetX > 0;
            }
        }

        Assert.True(shot, "the tower's arrows never showed in a frame");
        FrameEntity burning = session.AcquireFrame(out _)!.Entities.ToArray().Single(e => e.Id == house);
        Assert.True(burning.Burning);
        Assert.DoesNotContain(session.AcquireFrame(out _)!.Entities.ToArray(), e => e.Burning && e.Id != house);
    }

    [Fact]
    public void The_frame_carries_the_tavern_offers_of_the_period_with_their_prices()
    {
        using var session = new GameSession(Setup() with { ComputerAi = false }, Rules.Value, fog: false);
        session.Step(1);
        GameFrame frame = session.AcquireFrame(out _)!;
        World world = session.Simulation.World;
        int expected = 0;
        Span<int> cost = stackalloc int[Ruleset.MaxResourceTypes];
        for (int slot = 0; slot < TavernOffers.SlotCount(world); slot++)
        {
            if (TavernOffers.Offer(world, session.LocalPlayer, slot) is not { } offer)
            {
                continue;
            }

            FrameTavernOffer shown = frame.TavernOffers[expected];
            Assert.Equal((slot, offer.TypeId, offer.Count, offer.IsHero, false), (shown.Slot, shown.TypeId, shown.Count, shown.IsHero, shown.Taken));
            TavernOffers.CostOf(world, session.LocalPlayer, offer, cost);
            for (int r = 0; r < frame.TavernCostStride; r++)
            {
                Assert.Equal(cost[r], frame.TavernCosts[(expected * frame.TavernCostStride) + r]);
            }

            expected++;
        }

        Assert.Equal(expected, frame.TavernOfferCount);
    }

    [Fact]
    public void The_frame_carries_each_producers_queue_entries_with_their_counts()
    {
        // ADR 0180: the card shows on each order button how many of its item are queued.
        using var session = new GameSession(Setup() with { ComputerAi = false }, Rules.Value, fog: false);
        World world = session.Simulation.World;
        EntityId castle = world.Identities.Entities.ToArray().First(e => world.Identities.Get(e).Owner == 0 && world.TypeOf(e).Traits.LegacyClass == "castle");
        ushort peon = world.TypeOf(castle).Traits.Producer!.Produces[0];
        var batch = new CommandList();
        batch.Add(CommandKind.Train, [castle], arg0: peon, arg1: 3);
        session.Submit(batch);
        session.Step(2);
        GameFrame frame = session.AcquireFrame(out _)!;
        FrameQueueEntry entry = Assert.Single(frame.QueueEntries.ToArray(), q => q.Entity == castle.Value);
        Assert.Equal((FrameQueueKind.Unit, peon, 3), (entry.Kind, entry.Id, entry.Count));
        Assert.Equal(3, frame.Entities.ToArray().Single(e => e.Id == castle).QueueCount);
    }

    [Fact]
    [Trait("Rule", "BLD-021")]
    public void A_repairing_peasant_is_shown_at_work()
    {
        // Fourth play-test: peasants repairing a bridge stood still, and the repair looked instant.
        using var session = new GameSession(Setup() with { ComputerAi = false }, Rules.Value, fog: false);
        World world = session.Simulation.World;
        EntityId castle = world.Identities.Entities.ToArray().First(e => world.Identities.Get(e).Owner == 0 && world.TypeOf(e).Traits.LegacyClass == "castle");
        EntityId peon = world.Identities.Entities.ToArray().First(e => world.Identities.Get(e).Owner == 0 && world.TypeOf(e).Traits.Harvester is not null);
        world.Healths.Get(castle).Current = world.Healths.Get(castle).Max / 2;
        var batch = new CommandList();
        batch.Add(CommandKind.Repair, [peon], target: castle);
        session.Submit(batch);
        for (int t = 0; t < 400 && world.Workers.Get(peon).Phase != WorkerPhase.Repairing; t++)
        {
            session.Step(1);
        }

        session.Step(2);
        Assert.Equal(WorkerPhase.Repairing, world.Workers.Get(peon).Phase);
        GameFrame frame = session.AcquireFrame(out _)!;
        Assert.Equal(UnitActivity.Build, frame.Entities.ToArray().Single(e => e.Id == peon).Activity);
    }

    [Theory]
    [InlineData(Tzar2.Content.MapGen.RandomMapType.Continental)]
    [InlineData(Tzar2.Content.MapGen.RandomMapType.Lakes)]
    public void A_skirmish_runs_on_a_random_map(Tzar2.Content.MapGen.RandomMapType type)
    {
        MatchSetup setup = Setup() with { Map = new RandomMapSource(type, size: Tzar2.Content.MapGen.RandomMapSize.Tiny) };
        using var session = new GameSession(setup, Rules.Value, fog: false);
        Assert.NotNull(session.Map.TerrainSet);
        session.Step(100);
        GameFrame frame = session.AcquireFrame(out _)!;
        Assert.Contains(frame.Entities.ToArray(), e => e.Owner == 0 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "castle");
        Assert.Contains(frame.Entities.ToArray(), e => e.Owner == 1 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "castle");
    }

    private static readonly Lazy<Ruleset> StayingIdle = new(() => SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions
    {
        OverrideTexts = [new("idle.yaml", "gameplay: { idleWorkAfterMs: 0 }")],
    }));

    [Fact]
    public void A_right_click_on_an_enemy_unit_ends_in_a_kill()
    {
        // The client's order path end to end: frame entities -> EntityInfo -> OrderResolver -> session -> sim. The victim
        // stays where it is: without its AI, an idle peasant would find work by itself (ADR 0180) under its towers.
        using var session = new GameSession(Setup() with { ComputerAi = false }, StayingIdle.Value, fog: false);
        World world = session.Simulation.World;
        FrameEntity[] all = session.AcquireFrame(out _)!.Entities.ToArray();
        EntityInfo Info(FrameEntity e)
        {
            EntityTypeDefinition t = Rules.Value.EntityTypes[e.TypeId];
            return new EntityInfo(e.Id, e.TypeId, e.Owner, t.Traits.Category, t.Traits.Harvester is null && t.Traits.Armament is not null, 0, e.Health, e.MaxHealth,
                t.Traits.Harvester is not null, t.Traits.IsBuilder, t.Traits.Armament is not null, t.Traits.Producer is not null, t.Traits.ResourceNode is not null, false, t.IsMobile);
        }

        EntityInfo[] army = all.Where(e => e.Owner == 0 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "footman").Select(Info).ToArray();
        FrameEntity victim = all.First(e => e.Owner == 1 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "peon");
        Assert.NotEmpty(army);
        var events = new List<SimEvent>();
        int kills = 0;
        for (int round = 0; round < 40 && kills == 0; round++)
        {
            var batch = new CommandList();
            foreach (PlannedCommand c in OrderResolver.RightClick(army, Info(victim), victim.X, victim.Y, KeyMods.None, 0))
            {
                Assert.Equal(CommandKind.Attack, c.Kind);
                c.AddTo(batch);
            }

            session.Submit(batch);
            session.Step(100);
            session.DrainEvents(events);
            kills += events.Count(e => e.Kind == SimEventKind.Killed && e.Entity == victim.Id);
            events.Clear();
        }

        Assert.Equal(1, kills);
        Assert.False(world.Entities.IsAlive(victim.Id));
    }

    [Fact]
    public void Running_on_the_sim_thread_advances_ticks_until_paused()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        session.SpeedIndex = GameSession.SpeedSteps.Length - 1;
        session.Start();
        System.Threading.SpinWait.SpinUntil(() => session.TicksRun >= 10, TimeSpan.FromSeconds(20));
        Assert.True(session.TicksRun >= 10);
        session.Paused = true;
        System.Threading.Thread.Sleep(100);
        int paused = session.TicksRun;
        System.Threading.Thread.Sleep(200);
        Assert.InRange(session.TicksRun, paused, paused + 1);
        Assert.Null(session.Fault);
    }

    [Fact]
    public void A_kingdom_without_units_and_buildings_is_defeated_and_its_ai_stops()
    {
        // Three kingdoms, 1 and 2 run by the AI: 1 deletes everything it owns (as its peer would send it); 0 (human)
        // and 2 stay. 1 is defeated, its AI issues nothing more, the match goes on.
        using var session = new GameSession(Setup(players: 3), Rules.Value);
        Eliminate(session, 1);
        GameFrame frame = session.AcquireFrame(out _)!;
        Assert.True(frame.Players[1].Defeated);
        Assert.False(frame.Players[0].Defeated);
        Assert.False(frame.Players[2].Defeated);
        Assert.Equal(MatchOutcome.None, frame.Outcome);
        long issued = session.Ai!.Issued(0);
        session.Step(400);
        Assert.Equal(issued, session.Ai.Issued(0));
        Assert.True(session.Ai.Issued(1) > 0);

        // The human deletes everything: defeat.
        var batch = new CommandList();
        batch.Add(CommandKind.Delete, Own(session, 0), flags: CommandFlags.Queue); // Shift + Del: the castle too
        session.Submit(batch);
        session.Step(40);
        Assert.Equal(MatchOutcome.Defeat, session.AcquireFrame(out _)!.Outcome);
    }

    [Fact]
    public void The_last_kingdom_standing_wins()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        Eliminate(session, 1);
        GameFrame frame = session.AcquireFrame(out _)!;
        Assert.Equal(MatchOutcome.Victory, frame.Outcome);
        Assert.True(frame.Players[1].Defeated);
    }

    [Fact]
    public void Frames_cross_threads_without_tearing()
    {
        // Writer fills the back buffer with one value per frame; the reader must never see a mix.
        var exchange = new FrameExchange();
        using var stop = new System.Threading.CancellationTokenSource();
        var writer = new System.Threading.Thread(() =>
        {
            for (int n = 1; !stop.IsCancellationRequested; n++)
            {
                GameFrame back = exchange.Back;
                FrameEntity[] buffer = back.EntityBuffer(64);
                for (int i = 0; i < 64; i++)
                {
                    buffer[i] = new FrameEntity { X = n, Y = n };
                }

                back.EntityCount = 64;
                back.Tick = new Tick(n);
                exchange.Publish();
            }
        });
        writer.Start();
        int seen = 0, last = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 500)
        {
            GameFrame? f = exchange.AcquireLatest(out bool fresh);
            if (f is null || !fresh)
            {
                continue;
            }

            int value = f.Entities[0].X;
            foreach (FrameEntity e in f.Entities)
            {
                Assert.Equal(value, e.X);
            }

            Assert.Equal(value, f.Tick.Value);
            Assert.True(value > last);
            last = value;
            seen++;
        }

        stop.Cancel();
        writer.Join();
        Assert.True(seen > 10);
    }

    // From the world (the frame hides enemies in the fog).
    private static EntityId[] Own(GameSession session, byte owner)
    {
        World w = session.Simulation.World;
        return w.Identities.Entities.ToArray().Where(e => w.Identities.Get(e).Owner == owner).ToArray();
    }

    // Deletes a kingdom until nothing is left (its AI may have just started a site or a unit in the same tick).
    private static void Eliminate(GameSession session, byte slot)
    {
        for (int i = 0; i < 10 && Own(session, slot).Length > 0; i++)
        {
            DeleteAll(session, slot);
            session.Step(20);
        }

        session.Step(40);
    }

    // Shift+Del of everything a slot owns (castles too), submitted as that slot's peer would send it.
    private static void DeleteAll(GameSession session, byte slot)
    {
        var batch = new CommandList();
        batch.Add(CommandKind.Delete, Own(session, slot), flags: CommandFlags.Queue);
        session.SubmitAs(slot, batch);
    }

    [Fact]
    public void Interpolation_blends_between_frames_and_snaps_long_jumps()
    {
        var interpolator = new FrameInterpolator();
        GameFrame a = Frame((1, 0, 0), (2, 0, 0));
        interpolator.Push(a);
        GameFrame b = Frame((1, 1000, 500), (2, 50_000, 0));
        interpolator.Push(b);
        FrameEntity[] e = b.Entities.ToArray();
        Assert.Equal((500, 250), interpolator.Position(e[0], 500));
        Assert.Equal((1000, 500), interpolator.Position(e[0], 1000));
        Assert.Equal((0, 0), interpolator.Position(e[0], 0));
        Assert.Equal((50_000, 0), interpolator.Position(e[1], 500));
        Assert.Equal(1, interpolator.TickSpan);
        Assert.Equal(500, FrameInterpolator.Alpha(25, 50));
        Assert.Equal(1000, FrameInterpolator.Alpha(80, 50));

        static GameFrame Frame(params (int Id, int X, int Y)[] entities)
        {
            var frame = new GameFrame();
            FrameEntity[] buffer = frame.EntityBuffer(entities.Length);
            for (int i = 0; i < entities.Length; i++)
            {
                buffer[i] = new FrameEntity { Id = EntityId.Create(entities[i].Id, 1), X = entities[i].X, Y = entities[i].Y };
            }

            frame.EntityCount = entities.Length;
            return frame;
        }
    }

    // Two rivers three tiles wide across a 40 x 24 map, one down and one across, a castle on two of the banks, and a
    // wall of kingdom 0 with a gap and one of kingdom 1.
    private sealed class RiverMap : IMapSource
    {
        public string Name => "river";

        public MapData Create(Ruleset ruleset, MatchSetup setup)
        {
            var rows = new string[24];
            for (int y = 0; y < rows.Length; y++)
            {
                rows[y] = y is >= 10 and <= 12 ? new string('~', 40) : new string('.', 18) + "~~~" + new string('.', 19);
            }

            return Tzar2.Sim.Scenarios.ScenarioMaps.WithObjects(
                Tzar2.Sim.Scenarios.ScenarioMaps.FromAscii("river", rows),
                [
                    MapObject.AtTile("euro.castle", 0, 4, 4),
                    MapObject.AtTile("euro.castle", 1, 30, 16),
                    .. Enumerable.Range(2, 12).Where(x => x != 7).Select(x => MapObject.AtTile("comm.wall_wood", 0, x, 16)),
                    .. Enumerable.Range(2, 6).Select(y => MapObject.AtTile("comm.wall_wood", 0, 14, y)),
                    .. Enumerable.Range(24, 8).Select(x => MapObject.AtTile("comm.wall_wood", 1, x, 4)),
                ]);
        }
    }
}
