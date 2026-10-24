using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Rules;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Session;
using Tzar2.Sim.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Game.Tests;

/// <summary>Campaign progression, carry-over, profiles and saves (ADR 0133) on the built-in demo campaign (invented).</summary>
public sealed class CampaignTests(ITestOutputHelper output)
{
    private static readonly Lazy<Ruleset> Rules = new(() => SyntheticLegacyContent.CompileRuleset());

    [Fact]
    public void The_demo_campaign_plays_headless_to_completion_with_its_carry_over()
    {
        InMemoryCampaignSource source = DemoCampaign.Create();
        CampaignReport report = CampaignAutoplay.Run(source, Rules.Value, new AutoplayOptions { ComputerAi = true }, output.WriteLine);
        Assert.Null(report.Error);
        Assert.True(report.Completed);
        Assert.Equal(["ford", "mill", "watch"], report.Missions.Select(m => m.MapId));
        Assert.Equal([MissionResult.Moved, MissionResult.Won, MissionResult.Won], report.Missions.Select(m => m.Result));
        Assert.Equal(["mill", "watch", null], report.Missions.Select(m => m.NextMapId));
        PersistentHero oren = report.Progress.Heroes.Single();
        Assert.Equal(("Oren", 3), (oren.Name, oren.Level));
        Assert.Contains(new KeyValuePair<string, int>("#Crossed", 1), report.Progress.Variables);
        Assert.Equal(["ford", "mill", "watch"], report.Progress.Unlocked.ToArray());
        Assert.Null(report.Progress.Current);
        Assert.All(report.Missions, m => Assert.Equal(0, m.Faults));
    }

    [Fact]
    [Trait("Rule", "EXP-006")]
    public void A_persistent_hero_arrives_with_its_level_on_the_next_map()
    {
        InMemoryCampaignSource source = DemoCampaign.Create();
        CampaignProgress progress = CampaignFlow.Begin(source.Campaign) with
        {
            Current = "mill",
            Heroes = [new PersistentHero("Oren", "euro.footman", 4, 120, false, 2, 1, ImmutableArray<PersistentItem>.Empty)],
            Variables = [new("#Crossed", 1)],
        };
        MatchSetup setup = ScenarioMatch.Setup(source.Load("mill"), Rules.Value, progress, computerAi: false);
        using var session = new GameSession(setup, Rules.Value);
        Sim.World world = session.Simulation.World;
        Core.Ids.EntityId oren = Core.Ids.EntityId.FromValue(session.Scenario!.Triggers.State.MapEntities[0]);
        Assert.Equal(4, world.Experiences.Get(oren).Level);
        Assert.Equal(2, world.Experiences.Get(oren).BonusAttack);
        Assert.Equal(1, session.Scenario.Triggers.State.Variables[source.Load("mill").Scenario.Variables.Items.IndexOf("#Crossed")]);
        Assert.Equal(300, world.Players[0].Resources[Rules.Value.ResourceId("gold")]); // the kingdom table's stockpile
        Assert.Equal(Sim.Kingdoms.Stance.Ally, world.Diplomacy.StanceOf(0, 2));
    }

    [Fact]
    public void The_flow_retries_a_loss_follows_a_single_edge_and_matches_names_case_insensitively()
    {
        CampaignData campaign = DemoCampaign.Create().Campaign;
        CampaignProgress start = CampaignFlow.Begin(campaign);
        Assert.Equal("ford", start.Current);
        Assert.Equal("mill", CampaignFlow.Resolve(campaign, "  MILL "));
        Assert.Null(CampaignFlow.Resolve(campaign, "Nowhere"));

        MissionEnd lost = CampaignFlow.End(campaign, start, "ford", MissionResult.Lost, null, 100);
        Assert.Equal(("ford", false), (lost.Progress.Current, lost.Progress.Completed));

        MissionEnd won = CampaignFlow.End(campaign, start, "ford", MissionResult.Won, null, 100); // the ford's single edge
        Assert.Equal("mill", won.NextMapId);
        MissionEnd last = CampaignFlow.End(campaign, won.Progress with { Current = "watch" }, "watch", MissionResult.Won, null, 100);
        Assert.True(last.Progress.Completed);
        Assert.Null(last.Progress.Current);
        Assert.Equal(2, last.Progress.History.Length);
        Assert.Throws<ArgumentException>(() => CampaignFlow.Begin(new CampaignData("x", "x", "", new[] { new CampaignMap("a", "A", "a", "", false) }, EquatableArray<CampaignEdge>.Empty)));
    }

    [Fact]
    [Trait("Rule", "SHELL-004")]
    public void The_campaign_screen_lists_the_entry_missions_and_then_every_mission_reached()
    {
        CampaignData campaign = DemoCampaign.Create().Campaign;
        Assert.Equal(["ford"], CampaignFlow.EntryMissions(campaign, null).Select(m => m.Id));
        MissionEnd won = CampaignFlow.End(campaign, CampaignFlow.Begin(campaign), "ford", MissionResult.Won, null, 100);
        Assert.Equal(["ford", "mill"], CampaignFlow.EntryMissions(campaign, won.Progress).Select(m => m.Id)); // in the campaign's order
    }

    [Fact]
    public void Profiles_round_trip_as_readable_json_and_bad_ones_are_rejected()
    {
        CampaignProgress progress = CampaignFlow.Begin(DemoCampaign.Create().Campaign) with
        {
            History = [new MissionRecord("ford", MissionResult.Moved, 1234)],
            Heroes = [new PersistentHero("Oren", "euro.footman", 3, 50, false, 0, 0, [new PersistentItem("comm.item_magic", 5, 2)])],
            Variables = [new("#Crossed", 1)],
        };
        PlayerProfile profile = PlayerProfile.New("Ilka").With(progress);
        byte[] json = profile.ToJson();
        string text = Encoding.UTF8.GetString(json);
        Assert.Contains("\"name\": \"Ilka\"", text, StringComparison.Ordinal);
        Assert.Contains("\"#Crossed\": 1", text, StringComparison.Ordinal);
        PlayerProfile back = PlayerProfile.FromJson(json);
        Assert.Equal(json, back.ToJson());
        Assert.Equal("Oren", back.Progress(DemoCampaign.Id)!.Heroes.Single().Name);

        Assert.Throws<InvalidDataException>(() => PlayerProfile.FromJson(Encoding.UTF8.GetBytes("{ not json")));
        Assert.Throws<InvalidDataException>(() => PlayerProfile.FromJson(Encoding.UTF8.GetBytes(text.Replace("\"version\": 1", "\"version\": 99", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => PlayerProfile.FromJson(Encoding.UTF8.GetBytes(text.Replace("\"level\": 3", "\"level\": -3", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => PlayerProfile.FromJson(Encoding.UTF8.GetBytes(text.Replace("\"result\": \"moved\"", "\"result\": \"maybe\"", StringComparison.Ordinal))));

        string dir = Path.Combine(Path.GetTempPath(), "tzar2-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(dir);
            Assert.Equal("Player", store.Load().Name);
            store.Save(profile);
            Assert.Equal(json, store.Load().ToJson());
            Assert.False(File.Exists(store.Path + ".tmp"));
            Assert.EndsWith(Path.Combine("saves", "slot_1.t2sav"), store.SavePath("slot/1"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Saves_taken_while_the_sim_thread_runs_at_full_speed_restore_to_the_uninterrupted_state()
    {
        InMemoryCampaignSource source = DemoCampaign.Create();
        CampaignProgress progress = CampaignFlow.Begin(source.Campaign);
        // The watch map: no conversation, so single player never holds the clock.
        MatchSetup setup = ScenarioMatch.Setup(source.Load("watch"), Rules.Value, progress, computerAi: true);
        var saves = new List<SaveGame>();
        using (var live = new GameSession(setup, Rules.Value))
        {
            live.SpeedIndex = GameSession.SpeedSteps.Length - 1;
            live.Start();
            for (int i = 0; i < 5; i++)
            {
                SpinWait.SpinUntil(() => live.TicksRun >= (i + 1) * 15, TimeSpan.FromSeconds(30));
                saves.Add(SaveGame.Capture(live, source.Campaign.Id, "watch", progress, "live " + i));
            }
        }

        Assert.Equal(saves.Count, saves.Select(s => s.Tick).Distinct().Count());
        using var reference = new GameSession(setup, Rules.Value);
        foreach (SaveGame save in saves.OrderBy(s => s.Tick))
        {
            reference.Step(save.Tick - reference.Simulation.World.Tick.Value);
            using GameSession restored = SaveGame.FromBytes(save.ToBytes()).Resume(source, Rules.Value);
            Assert.Equal(reference.Simulation.ComputeFullHash(), restored.Simulation.ComputeFullHash());
            restored.Step(20);
            using var check = new GameSession(setup, Rules.Value);
            check.Step(save.Tick + 20);
            Assert.Equal(check.Simulation.ComputeFullHash(), restored.Simulation.ComputeFullHash());
        }
    }

    [Fact]
    public void A_mission_save_resumes_with_the_same_hash_stream()
    {
        InMemoryCampaignSource source = DemoCampaign.Create();
        CampaignProgress progress = CampaignFlow.Begin(source.Campaign);
        MatchSetup setup = ScenarioMatch.Setup(source.Load("watch"), Rules.Value, progress, computerAi: true);
        using var reference = new GameSession(setup, Rules.Value);
        using var played = new GameSession(setup, Rules.Value);
        var expected = new List<ulong>();
        for (int i = 0; i < 300; i++)
        {
            reference.Step(1);
            expected.Add(reference.Simulation.ComputeStateHash());
        }

        played.Step(120);
        byte[] bytes = SaveGame.Capture(played, source.Campaign.Id, "watch", progress, "test save").ToBytes();
        SaveGame save = SaveGame.FromBytes(bytes);
        Assert.Equal(("watch", 120, "test save"), (save.MapId, save.Tick, save.Label));
        using GameSession resumed = save.Resume(source, Rules.Value);
        var actual = new List<ulong>();
        for (int i = 120; i < 300; i++)
        {
            resumed.Step(1);
            actual.Add(resumed.Simulation.ComputeStateHash());
        }

        Assert.Equal(expected.Skip(120), actual);
        Assert.Single(resumed.AcquireFrame(out _)!.Scenario!.Quests);

        Assert.Throws<InvalidDataException>(() => SaveGame.FromBytes(bytes.AsSpan(0, bytes.Length - 3)));
        Assert.Throws<InvalidDataException>(() => SaveGame.FromBytes([.. bytes, 1]));
        Assert.Throws<InvalidDataException>(() => SaveGame.FromBytes(Encoding.ASCII.GetBytes("nope")));
        Assert.Throws<InvalidDataException>(() => (save with { CampaignId = "other" }).Resume(source, Rules.Value));
    }
}
