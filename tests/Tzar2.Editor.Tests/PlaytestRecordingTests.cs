using System;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Editing;
using Tzar2.Game.Session;
using Tzar2.Replay;
using Tzar2.Sim.Commands;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>A playtest saved as a file and watched again (ADR 0291).</summary>
public class PlaytestRecordingTests
{
    private const int Tile = MapData.UnitsPerTile;

    // A kingdom against a computer kingdom with a trigger: the orders, the AI and the triggers all shape the match.
    private static MapDocument Mission()
    {
        MapDocument doc = MapDocument.CreateEmpty("Recorded", 40, 24, Fixtures.RulesFor(null));
        doc.Apply("Objects", s => s
            .Add(new MapObject("euro.footman", 0, (4 * Tile) + 1024, (10 * Tile) + 1024), null, out _)
            .Add(new MapObject("euro.peon", 0, (6 * Tile) + 1024, (12 * Tile) + 1024), null, out _)
            .Add(new MapObject("euro.peon", 1, (30 * Tile) + 1024, (10 * Tile) + 1024), null, out _));
        doc.Apply("Scenario", s => s.WithScenario(m => m
            .WithKingdom(ScenarioModel.DefaultKingdom(1) with { StartTileX = 4, StartTileY = 10 })
            .WithKingdom(ScenarioModel.DefaultKingdom(2) with { StartTileX = 30, StartTileY = 10 })
            .AddAction(new ExpressionAction("Mark", ScenarioModel.Source("$Seen = $Seen + 1")), out _)
            .AddRule(new RuleDef("count", ScenarioModel.Source("1"), new[] { 0 }), out _)));
        return doc;
    }

    // The tester's move order for the object of index 0 (the footman).
    private static void Move(GameSession session, int tileX, int tileY)
    {
        var batch = new CommandList();
        batch.Add(CommandKind.Move, [session.Scenario!.EntityOf(0)], (tileX * Tile) + 1024, (tileY * Tile) + 1024);
        session.Submit(batch);
    }

    [Fact]
    public void A_recorded_playtest_replays_the_timeline_it_ended_on()
    {
        MapDocument doc = Mission();
        var options = new PlaytestOptions(RevealMap: true);
        using GameSession first = Playtest.Start(doc, Fixtures.Classic, options);
        using var rewind = new MatchRewind(first);
        using var recorder = new PlaytestRecorder(first, doc, options, rewind);
        Move(first, 20, 10);
        first.Step(500);
        Assert.Equal(500, recorder.Ticks);
        ulong abandoned = first.LastTickResult.StateHash;

        // Back to 0:10 and another order: the recording drops what came after the point.
        using GameSession second = rewind.RewindTo(250)!;
        Assert.Equal(200, recorder.Ticks);
        Move(second, 4, 20);
        second.Step(300);
        Assert.Equal(500, recorder.Ticks);
        ulong final = second.LastTickResult.StateHash;
        Assert.NotEqual(abandoned, final);

        PlaytestRecording read = PlaytestRecording.Parse(recorder.Finish().ToBytes());
        Assert.Equal(options, read.Options);
        ReplayVerification verified = read.Verify(read.Document(Fixtures.RulesFor), Fixtures.Classic);
        Assert.True(verified.Succeeded, $"mismatch at {verified.FirstMismatch}");
        Assert.Equal(500, verified.TicksRun);
        Assert.Equal(final, verified.FinalStateHash);
    }

    [Fact]
    public void The_recording_opens_in_the_replay_viewer_with_the_map_as_it_was_played()
    {
        MapDocument doc = Mission();
        using GameSession session = Playtest.Start(doc, Fixtures.Classic, new PlaytestOptions());
        using var recorder = new PlaytestRecorder(session, doc, new PlaytestOptions());
        Move(session, 12, 6);
        session.Step(120);

        // Edits after the playtest started are not what was played.
        doc.Apply("Later edit", s => s.Add(new MapObject("euro.peon", 0, (8 * Tile) + 1024, (14 * Tile) + 1024), null, out _));
        PlaytestRecording recording = recorder.Finish();
        MapDocument played = recording.Document(Fixtures.RulesFor);
        Assert.Equal(3, played.State.Objects.Count);

        ReplayPlayback playback = recording.Playback(played, Fixtures.Classic);
        using GameSession shown = playback.Session;
        Assert.Equal(120, playback.Length);

        // The map as it is now is another map: the replay does not play on it.
        Assert.Throws<InvalidDataException>(() => recording.Verify(doc, Fixtures.Classic));
        Assert.Throws<InvalidDataException>(() => recording.Playback(doc, Fixtures.Classic));
    }

    [Fact]
    public void A_recorder_starts_with_the_first_tick_and_damaged_files_are_refused()
    {
        MapDocument doc = Mission();
        using GameSession session = Playtest.Start(doc, Fixtures.Classic, new PlaytestOptions());
        session.Step(1);
        Assert.Throws<InvalidOperationException>(() => new PlaytestRecorder(session, doc, new PlaytestOptions()));

        Assert.Throws<InvalidDataException>(() => PlaytestRecording.Parse([1, 2, 3, 4, 5]));
        byte[] good = new PlaytestRecording(new PlaytestOptions(Kingdom: 2), [1, 2, 3], [4, 5]).ToBytes();
        Assert.Equal(2, PlaytestRecording.Parse(good).Options.Kingdom);
        Assert.Throws<InvalidDataException>(() => PlaytestRecording.Parse(good.AsSpan(0, good.Length - 1)));
    }

    [Fact]
    public void Recent_files_keep_the_newest_first_once_each()
    {
        RecentFiles recent = RecentFiles.Empty.Add("C:/maps/a.t2map").Add("C:/campaigns/b.t2camp").Add("c:/MAPS/A.t2map");
        Assert.Equal(["c:/MAPS/A.t2map", "C:/campaigns/b.t2camp"], recent.Paths.ToArray());
        Assert.Equal(RecentKind.Campaign, RecentFiles.KindOf(recent.Paths[1]));
        Assert.Equal(RecentKind.PlaytestRecording, RecentFiles.KindOf("x/bug.T2PLAY"));
        Assert.Equal(recent.Paths.ToArray(), RecentFiles.Parse(recent.Write()).Paths.ToArray());
        Assert.Equal(["C:/campaigns/b.t2camp"], recent.Remove("C:/maps/a.t2map").Paths.ToArray());

        for (int i = 0; i < 20; i++)
        {
            recent = recent.Add($"m{i}.t2map");
        }

        Assert.Equal(RecentFiles.Max, recent.Paths.Length);
        Assert.Equal("m19.t2map", recent.Paths[0]);
        Assert.Equal(RecentFiles.Max, RecentFiles.Parse(string.Join("\r\n", recent.Paths) + "\n\nextra.t2map").Paths.Length);
    }

    [Fact]
    public void The_pickers_offer_the_types_orders_and_campaign_maps()
    {
        EditorPalette palette = EditorPalette.Build(Fixtures.Classic);
        Assert.Contains(palette.Search("foot"), e => e.Key == "euro.footman");
        Assert.All(palette.Search("euro peon"), e => Assert.Contains("peon", e.Key, StringComparison.Ordinal));
        Assert.Equal(palette.Entries.Length, palette.Search(" ").Length);
        Assert.Empty(palette.Search("no-such-type"));

        Assert.Equal(["move", "attack", "patrol", "stop", "default"], TriggerSchema.OrderKeys.ToArray());
        Assert.Empty(TriggerSchema.MapNames(null));
        CampaignDocument campaign = CampaignDocument.New("C");
        campaign.AddMap(Mission(), "First");
        campaign.AddMap(Mission(), "Second");
        Assert.Equal(["First", "Second"], TriggerSchema.MapNames(campaign).ToArray());
    }
}
