using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Core.Serialization;
using Tzar2.Net.Lockstep;
using Tzar2.Net.Protocol;
using Tzar2.Net.Relay;
using Xunit;

namespace Tzar2.Net.Tests;

/// <summary>The relay and lobby over real UDP on loopback (ADR 0160): lobby, chat, barrier, ticks, desync, rejoin, drop, save, pause.</summary>
[Collection("network")]
public sealed class RelayTests
{
    [Fact]
    public void Lobby_slots_choices_settings_and_readiness()
    {
        using var h = new RelayHarness();
        TestPeer host = h.Create(slots: 3);
        h.Pump(() => host.Welcome is not null);
        Assert.Equal(0, host.Welcome!.Slot);
        Assert.NotEqual(0UL, host.Welcome.Token);
        Assert.Equal(6, host.Welcome.Code.Length);

        TestPeer guest = h.Join(host.Welcome.Code.ToLowerInvariant());
        h.Pump(() => guest.Welcome is not null && host.Lobby?.Slots[1].State == SlotState.Human);
        Assert.Equal(1, guest.Welcome!.Slot);

        host.Client.SetSlot(2, SlotState.Computer);
        host.Client.SetChoice(2, [7]);
        guest.Client.SetChoice(1, [3]);
        host.Client.SetSettings([1, 2, 3]);
        h.Pump(() => guest.Lobby is { } l && l.Slots[2].Choice.SequenceEqual(new byte[] { 7 }) && l.Slots[1].Choice.SequenceEqual(new byte[] { 3 }) && l.Settings.Length == 3);

        // Someone else's slot, settings by a non-host, and start before everyone is ready are refused.
        guest.Client.SetChoice(0, [9]);
        guest.Client.SetSettings([4]);
        host.Client.RequestStart(RelayHarness.Parameters());
        h.Pump(() => guest.Events.OfType<NoticeEvent>().Count() >= 2 && host.Events.OfType<NoticeEvent>().Any());
        Assert.Equal(3, guest.Lobby!.Settings.Length);

        guest.Client.SetReady(true);
        h.Pump(() => host.Lobby!.Slots[1].Ready);

        // Ready locks the own choice; new settings clear the readiness.
        guest.Client.SetChoice(1, [4]);
        h.Pump(() => guest.Events.OfType<NoticeEvent>().Count() >= 3);
        host.Client.SetSettings([9]);
        h.Pump(() => !host.Lobby!.Slots[1].Ready && host.Lobby.Settings.Length == 1);
        Assert.Equal([3], host.Lobby!.Slots[1].Choice);
    }

    [Fact]
    public void Joins_are_refused_with_reasons()
    {
        using var h = new RelayHarness();
        TestPeer host = h.Create(slots: 2);
        h.Pump(() => host.Welcome is not null);
        TestPeer unknown = h.Join("ZZZZZZ");
        TestPeer version = h.Join(host.Welcome!.Code, version: "other");
        TestPeer guest = h.Join(host.Welcome.Code);
        h.Pump(() => guest.Welcome is not null);
        TestPeer full = h.Join(host.Welcome.Code);
        TestPeer badToken = h.Rejoin(host.Welcome.Code, 1, guest.Welcome!.Token + 2);
        h.Pump(() => new[] { unknown, version, full, badToken }.All(p => p.Last<RefusedEvent>() is not null));
        Assert.Equal(RefusalReason.UnknownLobby, unknown.Last<RefusedEvent>()!.Refusal.Reason);
        Assert.Equal(RefusalReason.GameVersion, version.Last<RefusedEvent>()!.Refusal.Reason);
        Assert.Equal(RefusalReason.LobbyFull, full.Last<RefusedEvent>()!.Refusal.Reason);
        Assert.Equal(RefusalReason.BadToken, badToken.Last<RefusedEvent>()!.Refusal.Reason);
    }

    [Fact]
    public void Public_lobbies_are_browsable()
    {
        using var h = new RelayHarness();
        TestPeer host = h.Create(isPublic: true);
        TestPeer hidden = h.Create(isPublic: false);
        h.Pump(() => host.Welcome is not null && hidden.Welcome is not null);
        h.PumpFor(300); // the listing refreshes every 250 ms
        BrowseReply? reply = null;
        var thread = new System.Threading.Thread(() => reply = LobbyBrowser.Query("127.0.0.1", h.Relay.Port));
        thread.Start();
        h.Pump(() => !thread.IsAlive);
        Assert.NotNull(reply);
        LobbySummary lobby = Assert.Single(reply!.Lobbies);
        Assert.Equal(host.Welcome!.Code, lobby.Code);
        Assert.Equal(RelayHarness.Version, lobby.GameVersion);
    }

    [Fact]
    public void Chat_goes_to_the_recipients_and_is_throttled()
    {
        using var h = new RelayHarness();
        TestPeer host = h.Create(slots: 3);
        h.Pump(() => host.Welcome is not null);
        TestPeer a = h.Join(host.Welcome!.Code, "a");
        TestPeer b = h.Join(host.Welcome.Code, "b");
        h.Pump(() => a.Welcome is not null && b.Welcome is not null);

        host.Client.Chat(SlotMask.Of(1), "only for a\u0007");
        h.Pump(() => a.Last<ChatEvent>() is not null && host.Last<ChatEvent>() is not null);
        Assert.Equal("only for a", a.Last<ChatEvent>()!.Text);
        h.PumpFor(100);
        Assert.Null(b.Last<ChatEvent>());

        for (int i = 0; i < 6; i++)
        {
            a.Client.Chat(ChatRecipients.All, "line " + i);
        }

        h.Pump(() => a.Events.OfType<NoticeEvent>().Any(n => n.Kind == NoticeKind.ChatThrottled) && b.Events.OfType<ChatEvent>().Count() == 5);
        Assert.Equal("line 4", b.Last<ChatEvent>()!.Text);
    }

    [Fact]
    public void Ticks_close_on_the_deadline_with_every_members_commands_in_the_same_order()
    {
        using var h = new RelayHarness();
        (TestPeer host, TestPeer[] guests) = h.Running(guests: 2);
        host.Client.Submit([1, 1]);
        guests[0].Client.Submit([2]);
        guests[1].Client.Submit([3, 3, 3]);
        h.Pump(() => h.Peers.All(p => p.Closures.Sum(c => c.Entries.Length) >= 3 && p.Executed > 30));

        // Every peer executed the same closures, so their states agree, and no desync was reported.
        int upTo = h.Peers.Min(p => p.Executed);
        foreach (TestPeer p in h.Peers)
        {
            Assert.Equal(
                host.Closures.Take(upTo).SelectMany(c => c.Entries.Select(e => (c.Tick, e.Slot, e.Payload.Length))),
                p.Closures.Take(upTo).SelectMany(c => c.Entries.Select(e => (c.Tick, e.Slot, e.Payload.Length))));
            Assert.Null(p.Last<DesyncEvent>());
        }

        Assert.Equal(new byte[] { 0, 1, 2 }, host.Closures.SelectMany(c => c.Entries).Select(e => e.Slot).Order().ToArray());
        Assert.True(host.Client.InputDelay >= 2);
        Assert.True(h.Relay.Metrics.TicksClosed > 30);
    }

    [Fact]
    public void A_fingerprint_mismatch_stops_the_match_at_the_barrier()
    {
        using var h = new RelayHarness();
        TestPeer host = h.Create();
        h.Pump(() => host.Welcome is not null);
        TestPeer guest = h.Join(host.Welcome!.Code);
        h.Pump(() => guest.Welcome is not null);
        guest.Client.SetReady(true);
        h.Pump(() => host.Lobby!.Slots[1].Ready);
        host.Client.RequestStart(RelayHarness.Parameters());
        h.Pump(() => guest.Load is not null && host.Load is not null);
        host.Client.Loaded([1, 2, 3], 42);
        guest.Client.Loaded([1, 9, 3], 42);
        h.Pump(() => guest.Last<EndEvent>() is not null);
        EndMessage end = guest.Last<EndEvent>()!.End;
        Assert.Equal(EndReason.LoadMismatch, end.Reason);
        Assert.Equal(1, end.Slot);
        Assert.Contains("part 1", end.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forced_desync_is_detected_and_reported_with_both_snapshots()
    {
        var sink = new ListSink();
        using var h = new RelayHarness(o => o.DesyncSink = sink);
        (TestPeer host, TestPeer[] guests) = h.Running(guests: 2);
        guests[1].CorruptAtTick = host.Executed + 12;
        h.Pump(() => sink.Bundles.Count == 1 && h.Peers.All(p => p.Last<EndEvent>() is not null), what: "desync report");
        DesyncBundle bundle = sink.Bundles[0];
        Assert.Equal([2], bundle.OddSlots);
        Assert.True(bundle.Tick >= guests[1].CorruptAtTick);
        Assert.Equal(3, bundle.Slots.Length);
        Assert.All(bundle.Slots, s => Assert.NotEmpty(s.After));
        Assert.Equal(bundle.Log[^1].Tick + 1 - bundle.Log[0].Tick, bundle.Log.Length);
        Assert.All(h.Peers, p => Assert.Equal(bundle.Tick, p.Last<DesyncEvent>()!.Notice.Tick));
        Assert.Equal(EndReason.Desync, host.Last<EndEvent>()!.End.Reason);
        DesyncBundle.Parse(bundle.ToBytes()); // the stored form is readable
    }

    [Fact]
    public void A_dropped_player_rejoins_from_a_peer_snapshot_and_catches_up()
    {
        using var h = new RelayHarness();
        (TestPeer host, TestPeer[] guests) = h.Running(guests: 1);
        Welcome welcome = guests[0].Welcome!;
        guests[0].Client.Dispose();
        h.Peers.Remove(guests[0]);
        h.PumpFor(300);
        int before = host.Executed;

        TestPeer back = h.Rejoin(welcome.Code, welcome.Slot, welcome.Token);
        h.Pump(() => back.Last<ResumeEvent>() is not null, what: "resume");
        h.Pump(() => back.Executed >= host.Executed - 2 && back.Executed > before + 20, what: "catch-up");

        // The rejoined peer continues with the host's hash (its state came from the host's snapshot plus the backlog).
        h.Pump(() => back.Executed == host.Executed, what: "same tick");
        Assert.Equal(host.Hash, back.Hash);
        Assert.Equal(1, h.Relay.Metrics.Rejoins);
        back.Client.Submit([5]);
        h.Pump(() => host.Closures.Any(c => c.Entries.Any(e => e.Slot == 1 && e.Payload[0] == 5)), what: "commands after the rejoin");
    }

    [Fact]
    public void After_the_drop_timeout_the_kingdom_is_taken_over_and_returned_on_rejoin()
    {
        using var h = new RelayHarness();
        (TestPeer host, TestPeer[] guests) = h.Running(guests: 1, RelayHarness.Parameters(dropMs: 400));
        Welcome welcome = guests[0].Welcome!;
        guests[0].Client.Dispose();
        h.Peers.Remove(guests[0]);

        // The host keeps running (no freeze) and sees the takeover in a closed tick.
        h.Pump(() => host.Closures.Any(c => c.Controls.Any(e => e.Kind == ControlKind.Takeover && e.Slot == 1)), timeoutMs: 15_000, what: "takeover");
        ControlEvent takeover = host.Closures.SelectMany(c => c.Controls).First();
        Assert.Equal((int)DropPolicy.AiTakeover, takeover.Arg);
        h.Pump(() => host.Last<StatusEvent>()?.Slots[1].Control == SlotControl.Computer, what: "status");

        TestPeer back = h.Rejoin(welcome.Code, welcome.Slot, welcome.Token);
        h.Pump(() => host.Closures.Any(c => c.Controls.Any(e => e.Kind == ControlKind.Returned && e.Slot == 1)), what: "returned");
        h.Pump(() => back.Executed == host.Executed && back.Closures.Any(c => c.Controls.Any(e => e.Kind == ControlKind.Returned)), what: "rejoiner sees the return");
        Assert.Equal(host.Hash, back.Hash);
    }

    [Fact]
    public void A_save_needs_every_humans_consent()
    {
        using var h = new RelayHarness();
        (TestPeer host, TestPeer[] guests) = h.Running(guests: 1);
        host.Client.RequestSave("first");
        h.Pump(() => guests[0].Last<SaveProposalEvent>() is not null);
        guests[0].Client.VoteSave(guests[0].Last<SaveProposalEvent>()!.Proposal.Id, accept: false);
        h.Pump(() => host.Last<SaveOutcomeEvent>() is not null);
        Assert.False(host.Last<SaveOutcomeEvent>()!.Outcome.Accepted);
        Assert.Equal(1, host.Last<SaveOutcomeEvent>()!.Outcome.RefusedBy);

        host.Client.RequestSave("second");
        h.Pump(() => guests[0].Events.OfType<SaveProposalEvent>().Count() == 2);
        SaveProposal second = guests[0].Last<SaveProposalEvent>()!.Proposal;
        guests[0].Client.VoteSave(second.Id, accept: true);
        h.Pump(() => h.Peers.All(p => p.Closures.Any(c => c.Controls.Any(e => e.Kind == ControlKind.Save && e.Arg == (int)second.Id))));
        int hostTick = host.Closures.First(c => c.Controls.Any(e => e.Kind == ControlKind.Save)).Tick;
        int guestTick = guests[0].Closures.First(c => c.Controls.Any(e => e.Kind == ControlKind.Save)).Tick;
        Assert.Equal(hostTick, guestTick);
    }

    [Fact]
    public void Pause_stops_the_tick_clock_until_resumed()
    {
        using var h = new RelayHarness();
        (TestPeer host, TestPeer[] guests) = h.Running(guests: 1);
        guests[0].Client.Pause(true);
        h.Pump(() => host.Last<PauseEvent>() is { Paused: true });
        h.PumpFor(150);
        int paused = host.Executed;
        h.PumpFor(300);
        Assert.InRange(host.Executed - paused, 0, 1);
        Assert.Equal(1, host.Last<PauseEvent>()!.PausesLeft);
        host.Client.Pause(false);
        h.Pump(() => host.Executed > paused + 10);
    }

    [Fact]
    public void Malformed_or_flooding_members_are_disconnected()
    {
        using var h = new RelayHarness(o =>
        {
            o.MessagesPerSecond = 50;
            o.MessageBurst = 60;
        });
        TestPeer host = h.Create(slots: 3);
        h.Pump(() => host.Welcome is not null);
        TestPeer flood = h.Join(host.Welcome!.Code, "flood");
        h.Pump(() => flood.Welcome is not null);
        for (int i = 0; i < 200; i++)
        {
            flood.Client.SetReady(i % 2 == 0);
        }

        h.Pump(() => flood.Last<DisconnectedEvent>() is not null && host.Lobby!.Slots[1].State == SlotState.Open, what: "flood kick");
        Assert.True(h.Relay.Metrics.RateLimited >= 1);
        Assert.Contains(h.LogLines, l => l.Event == "member.kick");
    }

    [Fact]
    public void The_host_role_passes_on_and_a_lobby_without_humans_ends()
    {
        using var h = new RelayHarness();
        TestPeer host = h.Create(slots: 2);
        h.Pump(() => host.Welcome is not null);
        TestPeer guest = h.Join(host.Welcome!.Code);
        h.Pump(() => guest.Welcome is not null);
        host.Client.Leave();
        h.Pump(() => guest.Lobby?.HostSlot == 1);
        guest.Client.Leave();
        h.Pump(() => h.Relay.Metrics.Lobbies == 0 || h.LogLines.Any(l => l.Event == "lobby.end"));
    }

    private sealed class ListSink : IDesyncSink
    {
        public List<DesyncBundle> Bundles { get; } = [];

        public void Store(DesyncBundle bundle) => Bundles.Add(bundle);
    }
}

[CollectionDefinition("network", DisableParallelization = true)]
public sealed class NetworkCollection
{
}
