using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;
using Tzar2.Net.Relay;
using Tzar2.Replay;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Snapshots;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>
/// Multiplayer end to end over loopback (ADR 0162-0166): lobby to match through the real relay, lockstep identity of
/// every peer, replays that re-simulate, rejoin, the drop policy, desync reports with a component diff, saves with
/// consent and their resumption, chat and cheats.
/// </summary>
[Collection("multiplayer")]
public sealed class MultiplayerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tzar2-mp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Two_players_and_a_computer_stay_identical_and_their_replays_verify()
    {
        using var h = new MultiplayerHarness();
        (NetworkMatch host, NetworkMatch[] guests) = h.Start(guests: 1, computers: 1);
        Assert.Equal(LobbyStage.Running, host.Stage);
        Assert.True(host.Session!.IsMultiplayer);
        Assert.Equal(0, host.Session.LocalPlayer);
        Assert.Equal(1, guests[0].Session!.LocalPlayer);

        h.Pump(() => host.Session.Simulation.World.Tick.Value > 40);
        Harvest(host.Session, owner: 0);
        Harvest(guests[0].Session!, owner: 1);
        h.Pump(() => h.Matches.All(m => m.Session!.Simulation.World.Tick.Value > 400), timeoutMs: 60_000, what: "400 ticks");
        int tick = h.Settle(h.Matches);
        Assert.Equal(host.Session.Simulation.ComputeFullHash(), guests[0].Session!.Simulation.ComputeFullHash());

        // Both peers ran the same commands: the humans' orders and the computer's.
        World world = host.Session.Simulation.World;
        Assert.True(host.Session.Ai!.Issued(2) > 0, "the computer kingdom issued commands");
        Assert.All(new[] { host, guests[0] }, m => Assert.Null(m.EndReason));

        // Every peer's replay re-simulates headlessly from its setup description.
        foreach (NetworkMatch m in h.Matches)
        {
            ReplayFile replay = ReplayFile.Parse(m.Session!.FinishReplay()!);
            Assert.Equal(tick, replay.Ticks.Count);
            MatchDescriptor descriptor = MatchDescriptor.Parse(replay.Header.Setup.AsSpan());
            Assert.Equal(["host", "guest0", string.Empty], replay.Header.PlayerNames.ToArray());
            MatchSetup setup = descriptor.ToSetup(SkirmishAi.EngineDefault, multiplayer: true, localSlot: null);
            using var check = GameSession.ForReplay(setup, HumanVsAiTests.SyntheticRules);
            ReplayVerification v = ReplayVerifier.Verify(replay, check.Simulation);
            Assert.True(v.Succeeded, $"replay of {m.Lobby!.Slots.First(s => s.IsLocal).Name} desynced at {v.FirstMismatch}");
            Assert.True(v.HashesChecked >= tick / 10, $"{v.HashesChecked} hashes checked over {tick} ticks");
        }
    }

    [Fact]
    public void A_dropped_player_is_taken_over_by_the_computer_and_rejoins_with_an_identical_state()
    {
        using var h = new MultiplayerHarness();
        (NetworkMatch host, NetworkMatch[] guests) = h.Start(guests: 1, computers: 0, new MatchRules(DropRule.AiTakeover, DropTimeoutSeconds: 1));
        ReconnectTicket ticket = guests[0].Ticket!;
        h.Pump(() => host.Session!.Simulation.World.Tick.Value > 60);
        guests[0].Dispose();
        h.Matches.Remove(guests[0]);

        // The host keeps running; after the timeout the computer plays kingdom 1.
        h.Pump(() => (host.Session!.Simulation.World.Players[1].Flags & PlayerFlags.AiControlled) != 0, timeoutMs: 30_000, what: "takeover");
        int takenOver = host.Session!.Simulation.World.Tick.Value;
        h.Pump(() => host.Session.Simulation.World.Tick.Value > takenOver + 200 && host.Session.Ai!.Issued(1) > 0, timeoutMs: 30_000, what: "the AI plays kingdom 1");
        Assert.Contains(host.Players, p => p.Slot == 1 && p.Control == PlayerControl.Computer);

        // The player comes back: resume from the host's snapshot, catch up, the kingdom is theirs again.
        NetworkMatch back = h.Rejoin(ticket);
        h.Pump(() => back.Session is not null && back.Stage == LobbyStage.Running, what: "resume");
        h.Pump(() => (back.Session!.Simulation.World.Players[1].Flags & PlayerFlags.AiControlled) == 0
            && back.Session.Simulation.World.Tick.Value >= host.Session.Simulation.World.Tick.Value - 5, timeoutMs: 30_000, what: "catch-up and return");
        int tick = h.Settle([host, back]);
        Assert.True(tick > takenOver);
        Assert.Equal(host.Session.Simulation.ComputeFullHash(), back.Session!.Simulation.ComputeFullHash());
        Assert.Equal(PlayerFlags.Active, host.Session.Simulation.World.Players[1].Flags & ~PlayerFlags.Victorious);
    }

    [Fact]
    public void A_forced_desync_produces_a_report_whose_snapshots_diff_by_component()
    {
        using var h = new MultiplayerHarness();
        (NetworkMatch host, NetworkMatch[] guests) = h.Start(guests: 1, computers: 1);
        NetworkMatch odd = guests[0];
        World oddWorld = odd.Session!.Simulation.World;
        EntityId[] units = oddWorld.Identities.Entities.ToArray().Where(e => oddWorld.Identities.Get(e).Owner == 1 && oddWorld.TypeOf(e).IsMobile).Take(2).ToArray();
        int at = oddWorld.Tick.Value + 30;

        // A command only this peer executes: the relay sees different hashes at the next checkpoint.
        odd.TestTickHook((tick, commands) =>
        {
            if (tick == at)
            {
                commands.Add(CommandKind.Move, units, x: 10 * 2048, y: 10 * 2048, player: 1);
            }
        });
        h.Pump(() => h.Sink.Bundles.Count == 1 && h.Matches.All(m => m.Stage == LobbyStage.Ended), timeoutMs: 30_000, what: "desync report");
        Assert.True(host.Desynced && odd.Desynced);
        Assert.Contains("disagree", host.EndReason, StringComparison.Ordinal);

        DesyncBundle bundle = h.Sink.Bundles[0];
        Assert.InRange(bundle.Tick, at, at + 20);
        Assert.Equal(2, bundle.Slots.Length);
        Assert.Equal(3, bundle.LobbySlots.Length);
        Assert.All(bundle.Slots, s => Assert.True(s.BeforeTick < bundle.Tick && s.Before.Length > 0, "a keyframe from before the desync"));

        IReadOnlyList<SnapshotDifference> diff = DesyncReports.Diff(bundle, HumanVsAiTests.SyntheticRules, SkirmishAi.EngineDefault, 0, 1);
        Assert.Contains(diff, d => d.Section.StartsWith("store:", StringComparison.Ordinal) && d.Entity != EntityId.None);

        // The "before" keyframes agree: the divergence happened after them.
        Assert.Empty(DesyncReports.DiffBefore(bundle, HumanVsAiTests.SyntheticRules, SkirmishAi.EngineDefault, 0, 1));
    }

    [Fact]
    public void A_save_with_everyones_consent_is_written_identically_and_resumes()
    {
        string hostDir = Path.Combine(_dir, "host");
        string guestDir = Path.Combine(_dir, "guest");
        using var h = new MultiplayerHarness(hostDir);
        NetworkMatch host = h.Host(slots: 3);
        host.SetSettings(MultiplayerHarness.Settings(5));
        host.SetSlot(2, SlotKind.Computer, new SlotChoice("euro", 0, Difficulty.Normal));
        NetworkMatch guest = h.Join(host.Code!, "guest", guestDir);
        h.Pump(() => host.Lobby!.Slots[1].Kind == SlotKind.Human);
        guest.SetReady(true);
        h.Pump(() => host.Lobby!.Slots[1].Ready);
        host.Start(new MatchRules());
        h.Pump(() => h.Matches.All(m => m.Session is { } s && s.Simulation.World.Tick.Value > 100), timeoutMs: 30_000);

        host.RequestSave("before the battle");
        h.Pump(() => host.LastSave is not null && guest.LastSave is not null, what: "saves");
        Assert.Equal(host.LastSave!.SnapshotHash, guest.LastSave!.SnapshotHash);
        Assert.Equal(host.LastSave.Tick, guest.LastSave.Tick);
        Assert.True(File.Exists(host.LastSavePath) && File.Exists(guest.LastSavePath));
        MultiplayerSave save = MultiplayerSave.Parse(File.ReadAllBytes(host.LastSavePath!));
        Assert.Equal(["host", "guest", string.Empty], save.Descriptor.Players.Select(p => p.Name).ToArray());

        // Resume in a new lobby with the same slots.
        foreach (NetworkMatch m in h.Matches)
        {
            m.Dispose();
        }

        h.Matches.Clear();
        NetworkMatch host2 = h.Host(slots: 3);
        host2.PrepareResume(save);
        NetworkMatch guest2 = h.Join(host2.Code!, "guest", guestDir);
        h.Pump(() => host2.Lobby!.Slots[1].Kind == SlotKind.Human && host2.Lobby.Slots[2].Kind == SlotKind.Computer && host2.Lobby.Settings.ResumeSnapshotHash != 0);
        guest2.SetReady(true);
        h.Pump(() => host2.Lobby!.Slots[1].Ready);
        host2.StartResume(save, new MatchRules());
        h.Pump(() => h.Matches.All(m => m.Session is { } s && s.Simulation.World.Tick.Value > save.Tick + 50), timeoutMs: 30_000, what: "resumed match");
        h.Settle(h.Matches);
        Assert.Equal(host2.Session!.Simulation.ComputeFullHash(), guest2.Session!.Simulation.ComputeFullHash());
        Assert.True(host2.Session.Simulation.World.Tick.Value > save.Tick);
    }

    [Fact]
    public void A_player_without_the_save_cannot_join_its_resumption()
    {
        using var h = new MultiplayerHarness(Path.Combine(_dir, "host"));
        NetworkMatch host = h.Host(slots: 2);
        var descriptor = new MatchDescriptor(MultiplayerHarness.Settings(), [new MatchPlayer(0, "host", "euro", ControllerKind.Human, Difficulty.Normal, 0), new MatchPlayer(1, "guest", "euro", ControllerKind.Human, Difficulty.Normal, 0)]);
        var save = new MultiplayerSave("x", 1, descriptor, 10, [1, 2, 3]);
        save.Write(Path.Combine(_dir, "host"));
        host.PrepareResume(save);
        NetworkMatch guest = h.Join(host.Code!, "guest", Path.Combine(_dir, "empty"));
        h.Pump(() => host.Lobby!.Slots[1].Kind == SlotKind.Human && host.Lobby.Settings.ResumeSnapshotHash != 0);
        guest.SetReady(true);
        h.Pump(() => host.Lobby!.Slots[1].Ready);
        host.StartResume(save, new MatchRules());
        h.Pump(() => guest.Stage == LobbyStage.Ended && host.Stage == LobbyStage.Ended, what: "barrier refusal");
        Assert.Contains("differs", guest.EndReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_reaches_its_recipients_and_cheats_stay_off_unless_the_host_allows_them()
    {
        using var h = new MultiplayerHarness();
        NetworkMatch host = h.Host(slots: 3);
        host.SetSettings(MultiplayerHarness.Settings() with { CheatsAllowed = false });
        NetworkMatch a = h.Join(host.Code!, "a");
        NetworkMatch b = h.Join(host.Code!, "b");
        h.Pump(() => host.Lobby!.Slots.Count(s => s.Kind == SlotKind.Human) == 3);
        host.Chat("welcome");
        var lines = new List<ChatLine>();
        h.Pump(() =>
        {
            b.DrainChat(lines);
            return lines.Any(l => l.Text == "welcome");
        });

        a.SetReady(true);
        b.SetReady(true);
        h.Pump(() => host.Lobby!.Slots.All(s => s.Ready));
        host.Start(new MatchRules());
        h.Pump(() => h.Matches.All(m => m.Session is { } s && s.Simulation.World.Tick.Value > 5), timeoutMs: 30_000);

        // In the match: the host writes to its enemies only... everyone is an enemy without teams; then a cheat.
        Assert.True(host.Session!.SubmitChat("to my allies", ChatTarget.Allies));
        Assert.True(host.Session.SubmitChat("to everyone"));
        Assert.True(a.Session!.SubmitChat("hmgod"));
        var hostLines = new List<ChatLine>();
        var bLines = new List<ChatLine>();
        h.Pump(() =>
        {
            host.Session.DrainChat(hostLines);
            b.Session!.DrainChat(bLines);
            return hostLines.Any(l => l.Text == "to my allies") && bLines.Any(l => l.Text == "to everyone");
        });
        h.PumpFor(200);
        b.Session!.DrainChat(bLines);
        Assert.DoesNotContain(bLines, l => l.Text == "to my allies");
        Assert.Contains(bLines, l => l.Sender == "host" && l.Kind == ChatLineKind.Player);
        h.Pump(() => h.Matches.All(m => m.Session!.Simulation.World.Tick.Value > 60));
        int tick = h.Settle(h.Matches);
        Assert.True(tick > 60);
        Assert.Equal(host.Session.Simulation.ComputeFullHash(), a.Session.Simulation.ComputeFullHash());
        Assert.False(host.Session.Simulation.World.Config.CheatsAllowed);

        // Ranked lobbies never allow cheats, whatever the settings say.
        Assert.False(MatchSettings.Parse((MatchSettings.Parse(new MatchSettings { CheatsAllowed = true, Ranked = true }.ToBytes())).ToBytes()).CheatsAllowed);
    }

    [Fact]
    public void Descriptions_round_trip_and_reject_garbage()
    {
        var settings = new MatchSettings { Map = MapSourceKind.RandomMap, MapType = Content.MapGen.RandomMapType.Lakes, Seed = 99, NoFog = true, SpeedPercent = 150, ResumeSnapshotHash = 7, AiAssist = true };
        Assert.Equal(settings, MatchSettings.Parse(settings.ToBytes()));
        var descriptor = new MatchDescriptor(settings, [new MatchPlayer(0, "a", "euro", ControllerKind.Human, Difficulty.Easy, 1), new MatchPlayer(1, "b", "euro", ControllerKind.Computer, Difficulty.Hard, 1)]);
        MatchDescriptor back = MatchDescriptor.Parse(descriptor.ToBytes());
        Assert.Equal(descriptor.Settings, back.Settings);
        Assert.Equal(descriptor.Players.ToArray(), back.Players.ToArray());
        Assert.Equal(0x11UL, back.Teams);
        Assert.Throws<InvalidDataException>(() => MatchSettings.Parse([9, 9, 9]));
        Assert.Equal(SlotChoice.Default, SlotChoice.Parse([0xFF, 0xFF]));
        Assert.Equal(new SlotChoice("euro", 3, Difficulty.Hard), SlotChoice.Parse(new SlotChoice("euro", 3, Difficulty.Hard).ToBytes()));
        var random = new Random(4);
        byte[] bytes = descriptor.ToBytes();
        for (int i = 0; i < 2_000; i++)
        {
            byte[] mutated = (byte[])bytes.Clone();
            mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            try
            {
                MatchDescriptor.Parse(mutated);
            }
            catch (InvalidDataException)
            {
                // expected for most mutations
            }
        }
    }

    private static void Harvest(GameSession session, byte owner)
    {
        World w = session.Simulation.World;
        ushort tree = (ushort)w.Ruleset.EntityTypeId("comm.tree");
        EntityId[] peasants = w.Identities.Entities.ToArray().Where(e => w.Identities.Get(e).Owner == owner && w.TypeOf(e).Traits.Harvester is not null).ToArray();
        Sim.Components.Position at = w.Positions.Get(peasants[0]);
        EntityId nearest = w.Identities.Entities.ToArray().Where(e => w.Identities.Get(e).TypeId == tree)
            .OrderBy(e => ((long)(w.Positions.Get(e).X - at.X) * (w.Positions.Get(e).X - at.X)) + ((long)(w.Positions.Get(e).Y - at.Y) * (w.Positions.Get(e).Y - at.Y))).First();
        var batch = new CommandList();
        batch.Add(CommandKind.Harvest, peasants, target: nearest);
        session.Submit(batch);
    }
}

[CollectionDefinition("multiplayer", DisableParallelization = true)]
public sealed class MultiplayerCollection
{
}
