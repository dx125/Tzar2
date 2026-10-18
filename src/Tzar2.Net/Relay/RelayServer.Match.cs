using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using LiteNetLib;
using Tzar2.Core.Serialization;
using Tzar2.Net.Protocol;

namespace Tzar2.Net.Relay;

/// <content>The match half: loading barrier, tick closing, hashes and desyncs, rejoin, drop policy, saves, pauses.</content>
public sealed partial class RelayServer
{
    // ---- loading barrier (REQ-NET-05) ----

    private void OnStartRequest(Lobby lobby, Slot slot, MatchParameters wanted, long now)
    {
        if (lobby.Phase != LobbyPhase.Lobby || slot.Index != lobby.HostSlot)
        {
            Notice(slot, NoticeKind.Refused, "Only the host starts the match.");
            return;
        }

        if (lobby.Slots.Any(s => s.State == SlotState.Human && s.Index != lobby.HostSlot && !s.Ready))
        {
            Notice(slot, NoticeKind.Refused, "Every player must be ready.");
            return;
        }

        if (wanted.TickMicroseconds is < 12_500 or > 200_000 || wanted.HashInterval is < 1 or > 100 || wanted.DropTimeoutMs > 600_000 || wanted.MaxPauses > 20)
        {
            Notice(slot, NoticeKind.Refused, "Match parameters out of range.");
            return;
        }

        Span<byte> id = stackalloc byte[8];
        RandomNumberGenerator.Fill(id);
        lobby.Parameters = wanted with { MatchId = BitConverter.ToUInt64(id) };
        lobby.Phase = LobbyPhase.Loading;
        lobby.LoadStartedUs = now;
        foreach (Slot s in lobby.Slots)
        {
            s.Loaded = false;
            s.Fingerprint = [];
            s.PausesLeft = wanted.MaxPauses;
            s.Control = s.State switch
            {
                SlotState.Human => SlotControl.Human,
                SlotState.Computer => SlotControl.Computer,
                _ => SlotControl.Absent,
            };
        }

        Log("match.load", lobby.Code, slot.Index, FormattableString.Invariant($"tick {wanted.TickMicroseconds} us, policy {wanted.Policy}"));
        BroadcastLobby(lobby);
        _w.Clear();
        Wire.Write(_w, lobby.Parameters);
        Broadcast(lobby, GameChannel, DeliveryMethod.ReliableOrdered);
    }

    private void OnLoaded(Lobby lobby, Slot slot, ulong[] fingerprint, ulong initialHash)
    {
        if (lobby.Phase != LobbyPhase.Loading)
        {
            return;
        }

        slot.Loaded = true;
        slot.Fingerprint = fingerprint;
        slot.InitialHash = initialHash;
        if (lobby.StartAtUs > 0)
        {
            // A member who rejoined during the countdown: check it and send it the rest of the countdown.
            if (Mismatch(lobby, slot) is { } detail)
            {
                EndLobby(lobby, EndReason.LoadMismatch, slot.Index, detail);
                return;
            }

            _w.Clear();
            Wire.WriteStart(_w, (int)Math.Max(0, (lobby.StartAtUs - MonotonicClock.NowMicroseconds()) / 1000));
            slot.Peer?.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
            return;
        }

        TryStart(lobby);
    }

    private void TryStart(Lobby lobby)
    {
        if (lobby.Phase != LobbyPhase.Loading || lobby.StartAtUs > 0)
        {
            return;
        }

        Slot[] present = lobby.Slots.Where(s => s.State == SlotState.Human && s.Peer is not null).ToArray();
        if (present.Length == 0 || !Array.TrueForAll(present, s => s.Loaded))
        {
            return;
        }

        foreach (Slot s in present)
        {
            if (Mismatch(lobby, s) is { } detail)
            {
                EndLobby(lobby, EndReason.LoadMismatch, s.Index, detail);
                return;
            }
        }

        lobby.StartAtUs = MonotonicClock.NowMicroseconds() + (_options.StartDelayMs * 1000L);
        Log("match.barrier", lobby.Code, -1, FormattableString.Invariant($"{present.Length} humans, initial hash {present[0].InitialHash:X16}"));
        _w.Clear();
        Wire.WriteStart(_w, _options.StartDelayMs);
        Broadcast(lobby, GameChannel, DeliveryMethod.ReliableOrdered);
    }

    // The first difference between a slot's load report and the reference (the lowest loaded slot), or null.
    private static string? Mismatch(Lobby lobby, Slot slot)
    {
        Slot? reference = Array.Find(lobby.Slots, s => s.Loaded && s.State == SlotState.Human);
        if (reference is null || ReferenceEquals(reference, slot))
        {
            return null;
        }

        if (reference.Fingerprint.Length != slot.Fingerprint.Length)
        {
            return "fingerprint parts differ in number";
        }

        for (int i = 0; i < slot.Fingerprint.Length; i++)
        {
            if (reference.Fingerprint[i] != slot.Fingerprint[i])
            {
                return FormattableString.Invariant($"fingerprint part {i}");
            }
        }

        return reference.InitialHash != slot.InitialHash ? "initial state" : null;
    }

    // ---- the running match ----

    private void UpdateLobby(Lobby lobby, long now)
    {
        switch (lobby.Phase)
        {
            case LobbyPhase.Loading:
                if (lobby.StartAtUs > 0 && now >= lobby.StartAtUs)
                {
                    lobby.Phase = LobbyPhase.Running;
                    lobby.StartUs = lobby.StartAtUs;
                    Metrics.MatchStarted();
                    Log("match.start", lobby.Code, -1, FormattableString.Invariant($"match {lobby.Parameters!.MatchId:X16}"));
                    BroadcastLobby(lobby);
                }
                else if (now - lobby.LoadStartedUs > _options.LoadTimeoutMs * 1000L)
                {
                    EndLobby(lobby, EndReason.LoadTimeout, 255, "Loading took too long.");
                }

                break;
            case LobbyPhase.Running:
                UpdateMatch(lobby, now);
                break;
            case LobbyPhase.Ended when lobby.Parameters is not null && now >= lobby.NextTailResendUs:
                // Redundant delivery has no retransmission timer: keep repeating the tail until every peer has it.
                lobby.NextTailResendUs = now + lobby.Parameters.TickMicroseconds;
                foreach (Slot s in lobby.Slots)
                {
                    if (s.Peer is not null && !s.Resuming && s.AckTick < lobby.LastClosed)
                    {
                        SendRedundant(lobby, s);
                    }
                }

                break;
        }
    }

    private void UpdateMatch(Lobby lobby, long now)
    {
        MatchParameters p = lobby.Parameters!;
        if (lobby.Desync is { } desync)
        {
            if (now >= desync.DeadlineUs || desync.Waiting.Count == 0)
            {
                FinishDesync(lobby, desync);
            }

            return;
        }

        if (!lobby.Slots.Any(s => s.State == SlotState.Human && s.Peer is not null))
        {
            lobby.NoHumanSinceUs = lobby.NoHumanSinceUs == 0 ? now : lobby.NoHumanSinceUs;
            if (now - lobby.NoHumanSinceUs > _options.EmptyMatchTimeoutMs * 1000L)
            {
                EndLobby(lobby, EndReason.Closed, 255, "Every player left.");
                return;
            }
        }
        else
        {
            lobby.NoHumanSinceUs = 0;
        }

        if (lobby.Paused && now - lobby.PausedAtUs > _options.MaxPauseMs * 1000L)
        {
            SetPaused(lobby, false, lobby.PausedBy, now);
        }

        foreach (Slot s in lobby.Slots)
        {
            if (s.Control == SlotControl.Dropped
                && now - s.DroppedAtUs > (p.DropTimeoutMs + ((long)s.WaitVotes * _options.WaitVoteMs)) * 1000L)
            {
                Takeover(lobby, s, p.Policy);
            }

            if (s.Resuming && s.Peer is not null && (s.ResumeRequestedUs == 0 || now - s.ResumeRequestedUs > 3_000_000))
            {
                RequestResumeSnapshot(lobby, s, now);
            }
        }

        if (lobby.Save is { } save && now >= save.DeadlineUs)
        {
            DecideSave(lobby, accepted: false, refusedBy: 255);
        }

        if (now >= lobby.NextStatusUs)
        {
            lobby.NextStatusUs = now + 1_000_000;
            BroadcastStatus(lobby, now);
        }

        while (!lobby.Paused && lobby.Desync is null && lobby.Phase == LobbyPhase.Running && now >= Deadline(lobby, lobby.LastClosed + 1))
        {
            CloseTick(lobby);
        }
    }

    // Tick T closes at start + paused time + (T + 1) x tick, whether or not everyone sent input (ADR 0009).
    private static long Deadline(Lobby lobby, int tick) =>
        lobby.StartUs + lobby.PausedTotalUs + ((long)(tick + 1) * lobby.Parameters!.TickMicroseconds);

    private void CloseTick(Lobby lobby)
    {
        int tick = lobby.LastClosed + 1;
        ClosureEntry[] entries = lobby.Pending.Remove(tick, out PendingTick? pending) ? [.. pending.Entries] : [];
        ControlEvent[] controls = [];
        if (lobby.PendingControls.Count > 0)
        {
            int n = Math.Min(lobby.PendingControls.Count, Limits.MaxControlsPerClosure);
            controls = [.. lobby.PendingControls.GetRange(0, n)];
            lobby.PendingControls.RemoveRange(0, n);
        }

        var closure = new TickClosure(tick, entries, controls);
        lobby.Log.Add(closure);
        lobby.LastClosed = tick;
        Metrics.TickClosed();
        foreach (Slot s in lobby.Slots)
        {
            if (s.Peer is not null && !s.Resuming)
            {
                SendRedundant(lobby, s);
            }
        }

        // Ticks whose hashes are still incomplete after the timeout are compared with what arrived.
        while (lobby.Hashes.Count > 0 && lobby.Hashes.Keys.First() < tick - _options.HashCompareTimeoutTicks)
        {
            KeyValuePair<int, ulong?[]> oldest = lobby.Hashes.First();
            Evaluate(lobby, oldest.Key, oldest.Value);
            if (lobby.Desync is not null)
            {
                return;
            }
        }

        // Keep LogTicks closures, and everything a connected peer has not acknowledged yet (bounded).
        int keepFrom = tick - _options.LogTicks + 1;
        foreach (Slot s in lobby.Slots)
        {
            if (s.Peer is not null && !s.Resuming)
            {
                keepFrom = Math.Min(keepFrom, s.AckTick + 1);
            }
        }

        lobby.Log.TrimBefore(Math.Max(keepFrom, tick - Limits.MaxBacklogClosures + 1));
    }

    private void SendRedundant(Lobby lobby, Slot slot)
    {
        NetPeer peer = slot.Peer!;
        int first = Math.Max(slot.AckTick + 1, lobby.Log.First);
        int last = Math.Min(lobby.LastClosed, first + _options.RedundancyWindow - 1);
        if (first > last && slot.SentBatchAck == slot.LastBatchSeq)
        {
            return;
        }

        int limit = peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable) - 16;
        _payload.Clear();
        int count = 0;
        for (int t = first; t <= last; t++)
        {
            int before = _payload.Length;
            Wire.WriteClosure(_payload, lobby.Log.Get(t));
            if (_payload.Length > limit && count > 0)
            {
                _payload.Truncate(before);
                break;
            }

            count++;
        }

        _w.Clear();
        Wire.WriteClosuresHeader(_w, slot.LastBatchSeq, count);
        _w.WriteBytes(_payload.WrittenSpan);
        slot.SentBatchAck = slot.LastBatchSeq;

        // A closure too large for one datagram goes reliably (LiteNetLib fragments it).
        peer.Send(_w.WrittenSpan, GameChannel, _w.Length <= limit + 16 ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }

    private void OnBatches(Lobby lobby, Member member, Batch[] batches)
    {
        Slot slot = member.Slot;
        if (lobby.Phase is not (LobbyPhase.Running or LobbyPhase.Ended) || slot.Resuming)
        {
            return;
        }

        foreach (Batch batch in batches)
        {
            // Redundant copies of batches already processed are skipped.
            if (batch.Seq <= slot.LastBatchSeq)
            {
                continue;
            }

            slot.LastBatchSeq = batch.Seq;
            slot.AckTick = Math.Max(slot.AckTick, Math.Min(batch.AckTick, lobby.LastClosed));
            if (lobby.Phase != LobbyPhase.Running || lobby.Desync is not null)
            {
                continue;
            }

            if (batch.Payload.Length > 0 && slot.Control == SlotControl.Human && !Enqueue(lobby, slot, batch))
            {
                Metrics.Limited();
                Kick(member, "command flood");
                return;
            }

            foreach (TickHash hash in batch.Hashes)
            {
                RecordHash(lobby, slot, hash);
            }
        }
    }

    // "Relay tick closing": a batch for an already closed tick is delayed, not lost; a full tick spills into the next.
    private bool Enqueue(Lobby lobby, Slot slot, Batch batch)
    {
        int open = lobby.LastClosed + 1;
        int assigned = Math.Clamp(batch.TargetTick, open, open + 64);
        if (batch.TargetTick < open)
        {
            slot.Late++;
            Metrics.Late();
            _w.Clear();
            Wire.WriteLate(_w, batch.Seq, batch.TargetTick, assigned);
            slot.Peer?.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
        }

        for (int t = assigned; t < open + 128; t++)
        {
            if (!lobby.Pending.TryGetValue(t, out PendingTick? pending))
            {
                pending = new PendingTick();
                lobby.Pending[t] = pending;
            }

            if (pending.Entries.Count < Limits.MaxEntriesPerClosure && pending.Bytes + batch.Payload.Length <= Limits.MaxClosureBytes)
            {
                pending.Entries.Add(new ClosureEntry(slot.Index, batch.Payload));
                pending.Bytes += batch.Payload.Length;
                return true;
            }
        }

        return false;
    }

    // ---- desync detection (REQ-NET-04) ----

    private void RecordHash(Lobby lobby, Slot slot, TickHash hash)
    {
        int interval = lobby.Parameters!.HashInterval;
        if (hash.Tick < 0 || hash.Tick > lobby.LastClosed || hash.Tick <= lobby.LastEvaluatedHashTick || hash.Tick <= slot.ResumeTick
            || (hash.Tick + 1) % interval != 0 || hash.Tick < lobby.LastClosed - (4 * _options.HashCompareTimeoutTicks))
        {
            return;
        }

        if (!lobby.Hashes.TryGetValue(hash.Tick, out ulong?[]? entry))
        {
            entry = new ulong?[lobby.Slots.Length];
            lobby.Hashes[hash.Tick] = entry;
        }

        entry[slot.Index] = hash.Hash;
        foreach (Slot s in lobby.Slots)
        {
            bool expected = s.Peer is not null && !s.Resuming && s.ResumeTick < hash.Tick;
            if (expected && !entry[s.Index].HasValue)
            {
                return;
            }
        }

        Evaluate(lobby, hash.Tick, entry);
    }

    private void Evaluate(Lobby lobby, int tick, ulong?[] entry)
    {
        lobby.Hashes.Remove(tick);
        lobby.LastEvaluatedHashTick = Math.Max(lobby.LastEvaluatedHashTick, tick);
        ulong[] reported = entry.Where(h => h.HasValue).Select(h => h!.Value).ToArray();
        if (reported.Length == 0)
        {
            return;
        }

        // Majority hash; ties go to the hash of the lowest slot.
        ulong majority = reported.GroupBy(h => h).OrderByDescending(g => g.Count()).ThenBy(g => Array.FindIndex(entry, e => e == g.Key)).First().Key;
        byte[] odd = Enumerable.Range(0, entry.Length).Where(i => entry[i].HasValue && entry[i] != majority).Select(i => (byte)i).ToArray();
        if (odd.Length == 0)
        {
            lobby.LastAgreedTick = tick;
            return;
        }

        Metrics.Desync();
        Log("match.desync", lobby.Code, odd[0], FormattableString.Invariant($"tick {tick}, majority {majority:X16}, odd [{string.Join(',', odd)}]"));
        _w.Clear();
        Wire.Write(_w, new DesyncNotice(tick, majority, odd));
        Broadcast(lobby, GameChannel, DeliveryMethod.ReliableOrdered);

        // Every peer uploads its last agreed keyframe and its current state; ticks stop closing meanwhile.
        uint id = lobby.NextRequestId++;
        var collect = new DesyncCollect(tick, majority, odd, id, MonotonicClock.NowMicroseconds() + (_options.DesyncCollectMs * 1000L));
        foreach (Slot s in lobby.Slots)
        {
            collect.Reported[s.Index] = entry[s.Index] ?? 0;
            if (s.Peer is not null && !s.Resuming)
            {
                collect.Waiting.Add(s.Index);
                s.SnapshotRequested = true;
                _w.Clear();
                Wire.WriteSnapshotRequest(_w, SnapshotPurpose.Desync, id);
                s.Peer.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
            }
        }

        lobby.Desync = collect;
    }

    private void FinishDesync(Lobby lobby, DesyncCollect collect)
    {
        lobby.Desync = null;
        int from = lobby.Log.First;
        if (collect.Snapshots.Count > 0 && collect.Snapshots.Values.All(s => s.BeforeTick >= 0))
        {
            from = Math.Max(from, collect.Snapshots.Values.Min(s => s.BeforeTick) + 1);
        }

        var log = new List<TickClosure>();
        for (int t = from; t <= lobby.LastClosed; t++)
        {
            log.Add(lobby.Log.Get(t));
        }

        DesyncSlotReport[] slots = collect.Snapshots.OrderBy(p => p.Key).Select(p => new DesyncSlotReport(
            p.Key, lobby.Slots[p.Key].Name, collect.Reported[p.Key], p.Value.BeforeTick, p.Value.BeforeData, p.Value.Tick, p.Value.Hash, p.Value.Data)).ToArray();
        var bundle = new DesyncBundle(lobby.Code, lobby.Parameters!.MatchId, collect.Tick, collect.Majority, collect.Odd, lobby.Settings, slots, [.. log])
        {
            LobbySlots = lobby.Slots.Select(s => new DesyncLobbySlot(s.State, s.Name, s.Choice)).ToArray(),
        };
        try
        {
            _options.DesyncSink?.Store(bundle);
        }
#pragma warning disable CA1031 // A failing sink must not take the relay down.
        catch (Exception e)
#pragma warning restore CA1031
        {
            Log("desync.sink-failed", lobby.Code, -1, e.Message);
        }

        Log("desync.report", lobby.Code, -1, FormattableString.Invariant($"{slots.Length} snapshots, {log.Count} ticks of log"));
        EndLobby(lobby, EndReason.Desync, collect.Odd[0], FormattableString.Invariant($"State hashes disagree at tick {collect.Tick}."));
    }

    private void OnSnapshot(Lobby lobby, Slot slot, SnapshotMessage snapshot, long now)
    {
        if (!slot.SnapshotRequested || lobby.Phase != LobbyPhase.Running)
        {
            return;
        }

        slot.SnapshotRequested = false;
        if (snapshot.Purpose == SnapshotPurpose.Desync)
        {
            if (lobby.Desync is { } collect && collect.RequestId == snapshot.RequestId && collect.Waiting.Remove(slot.Index))
            {
                collect.Snapshots[slot.Index] = snapshot;
            }

            return;
        }

        Slot? rejoiner = Array.Find(lobby.Slots, s => s.Resuming && s.Peer is not null && s.ResumeRequest == snapshot.RequestId);
        if (rejoiner is null)
        {
            return;
        }

        if (snapshot.Tick > lobby.LastClosed || snapshot.Tick + 1 < lobby.Log.First || snapshot.Data.Length == 0)
        {
            // Unusable (too old for the log): ask another donor at the next update.
            rejoiner.ResumeRequestedUs = 0;
            return;
        }

        var backlog = new TickClosure[lobby.LastClosed - snapshot.Tick];
        for (int i = 0; i < backlog.Length; i++)
        {
            backlog[i] = lobby.Log.Get(snapshot.Tick + 1 + i);
        }

        _w.Clear();
        Wire.Write(_w, new ResumeMessage(snapshot.Tick, snapshot.Hash, snapshot.Data, backlog));
        rejoiner.Peer!.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
        rejoiner.Resuming = false;
        rejoiner.ResumeTick = snapshot.Tick;
        rejoiner.AckTick = lobby.LastClosed;
        rejoiner.LastBatchSeq = 0;
        rejoiner.SentBatchAck = 0;
        if (rejoiner.TakenOver)
        {
            // The kingdom goes back to its human in a closed tick, on every peer at once.
            rejoiner.TakenOver = false;
            rejoiner.Control = SlotControl.Human;
            lobby.PendingControls.Add(new ControlEvent(ControlKind.Returned, rejoiner.Index, 0));
        }

        Metrics.Rejoin();
        Log("match.resume", lobby.Code, rejoiner.Index, FormattableString.Invariant(
            $"snapshot tick {snapshot.Tick} ({snapshot.Data.Length} B) + {backlog.Length} ticks, {(now - rejoiner.RejoinHelloUs) / 1000} ms after hello"));
        BroadcastStatus(lobby, now);
    }

    // ---- rejoin (REQ-NET-06) and drop policy (REQ-NET-07) ----

    private void BeginResume(Lobby lobby, Slot slot, long now)
    {
        slot.Resuming = true;
        slot.ResumeRequestedUs = 0;
        slot.RejoinHelloUs = now;
        slot.AckTick = -1;
        if (slot.Control == SlotControl.Dropped)
        {
            slot.Control = SlotControl.Human;
        }

        RequestResumeSnapshot(lobby, slot, now);
    }

    private void RequestResumeSnapshot(Lobby lobby, Slot slot, long now)
    {
        Slot? donor = lobby.Slots.Where(s => s != slot && s.Peer is not null && !s.Resuming).OrderBy(s => s.Peer!.RoundTripTime).FirstOrDefault();
        if (donor is null)
        {
            return;
        }

        slot.ResumeRequest = lobby.NextRequestId++;
        slot.ResumeRequestedUs = now;
        donor.SnapshotRequested = true;
        _w.Clear();
        Wire.WriteSnapshotRequest(_w, SnapshotPurpose.Rejoin, slot.ResumeRequest);
        donor.Peer!.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
        Log("match.snapshot-request", lobby.Code, donor.Index, FormattableString.Invariant($"for slot {slot.Index}"));
    }

    private void Takeover(Lobby lobby, Slot slot, DropPolicy policy)
    {
        slot.Control = policy switch
        {
            DropPolicy.AiTakeover => SlotControl.Computer,
            DropPolicy.Neutral => SlotControl.Neutral,
            _ => SlotControl.Defeated,
        };
        slot.TakenOver = policy != DropPolicy.Defeat;
        lobby.PendingControls.Add(new ControlEvent(ControlKind.Takeover, slot.Index, (int)policy));
        Metrics.Takeover();
        Log("match.takeover", lobby.Code, slot.Index, policy.ToString());
        BroadcastStatus(lobby, MonotonicClock.NowMicroseconds());
    }

    private void OnWaitVote(Lobby lobby, Slot voter, long target)
    {
        if (lobby.Phase != LobbyPhase.Running || target < 0 || target >= lobby.Slots.Length)
        {
            return;
        }

        Slot slot = lobby.Slots[target];
        if (slot.Control == SlotControl.Dropped && slot.WaitVotes < _options.MaxWaitVotes && voter.Control == SlotControl.Human)
        {
            slot.WaitVotes++;
            Log("match.wait-vote", lobby.Code, slot.Index, FormattableString.Invariant($"by {voter.Index}, {slot.WaitVotes} votes"));
            BroadcastStatus(lobby, MonotonicClock.NowMicroseconds());
        }
    }

    // ---- pause ----

    private void OnPause(Lobby lobby, Slot slot, bool paused, long now)
    {
        if (lobby.Phase != LobbyPhase.Running || paused == lobby.Paused || slot.Control != SlotControl.Human)
        {
            return;
        }

        if (paused && slot.PausesLeft == 0)
        {
            Notice(slot, NoticeKind.NoPausesLeft, "You have no pauses left.");
            return;
        }

        if (paused)
        {
            slot.PausesLeft--;
        }

        SetPaused(lobby, paused, slot.Index, now);
    }

    private void SetPaused(Lobby lobby, bool paused, byte by, long now)
    {
        if (paused)
        {
            lobby.PausedAtUs = now;
        }
        else
        {
            lobby.PausedTotalUs += now - lobby.PausedAtUs;
        }

        lobby.Paused = paused;
        lobby.PausedBy = by;
        _w.Clear();
        Wire.WritePauseState(_w, paused, by, lobby.Slots[by].PausesLeft);
        Broadcast(lobby, GameChannel, DeliveryMethod.ReliableOrdered);
        Log(paused ? "match.pause" : "match.resume-play", lobby.Code, by, string.Empty);
    }

    // ---- multiplayer saves (REQ-NET-10) ----

    private void OnSaveRequest(Lobby lobby, Slot slot, string label, long now)
    {
        if (lobby.Phase != LobbyPhase.Running || slot.Index != lobby.HostSlot || lobby.Save is not null || lobby.Desync is not null)
        {
            Notice(slot, NoticeKind.Refused, "Only the host saves, one save at a time.");
            return;
        }

        var save = new SaveVote(lobby.NextSaveId++, slot.Index, now + (_options.SaveVoteTimeoutMs * 1000L));
        save.Accepted.Add(slot.Index);
        lobby.Save = save;
        _w.Clear();
        Wire.Write(_w, new SaveProposal(save.Id, slot.Index, label));
        Broadcast(lobby, GameChannel, DeliveryMethod.ReliableOrdered);
        CheckSave(lobby);
    }

    private void OnSaveVote(Lobby lobby, Slot slot, uint id, bool accept)
    {
        if (lobby.Save is not { } save || save.Id != id)
        {
            return;
        }

        if (!accept)
        {
            DecideSave(lobby, accepted: false, slot.Index);
            return;
        }

        save.Accepted.Add(slot.Index);
        CheckSave(lobby);
    }

    private void CheckSave(Lobby lobby)
    {
        SaveVote save = lobby.Save!;
        if (lobby.Slots.All(s => s.Peer is null || s.Resuming || s.Control != SlotControl.Human || save.Accepted.Contains(s.Index)))
        {
            DecideSave(lobby, accepted: true, 255);
        }
    }

    private void DecideSave(Lobby lobby, bool accepted, byte refusedBy)
    {
        SaveVote save = lobby.Save!;
        lobby.Save = null;
        if (accepted)
        {
            // Every peer writes the same snapshot after the same closed tick.
            lobby.PendingControls.Add(new ControlEvent(ControlKind.Save, save.By, (int)save.Id));
            Metrics.Save();
        }

        _w.Clear();
        Wire.Write(_w, new SaveOutcome(save.Id, accepted, refusedBy));
        Broadcast(lobby, GameChannel, DeliveryMethod.ReliableOrdered);
        Log("match.save", lobby.Code, save.By, accepted ? "accepted" : FormattableString.Invariant($"refused by {refusedBy}"));
    }

    // ---- status ----

    private void BroadcastStatus(Lobby lobby, long now)
    {
        Span<PeerStatus> status = stackalloc PeerStatus[lobby.Slots.Length];
        for (int i = 0; i < status.Length; i++)
        {
            Slot s = lobby.Slots[i];
            ushort dropped = s.Control == SlotControl.Dropped ? (ushort)Math.Min(ushort.MaxValue, (now - s.DroppedAtUs) / 1_000_000) : (ushort)0;
            ushort rtt = s.Peer is null ? (ushort)0 : (ushort)Math.Clamp(s.Peer.RoundTripTime, 0, ushort.MaxValue);
            status[i] = new PeerStatus(s.Control, s.Peer is not null, rtt, 0, dropped);
        }

        _w.Clear();
        Wire.WriteStatus(_w, status);
        Broadcast(lobby, GameChannel, DeliveryMethod.Unreliable);
    }

    private void Broadcast(Lobby lobby, byte channel, DeliveryMethod method)
    {
        foreach (Slot s in lobby.Slots)
        {
            s.Peer?.Send(_w.WrittenSpan, channel, method);
        }
    }

    // ---- state ----

    private sealed class Slot(byte index)
    {
        public byte Index { get; } = index;

        public SlotState State { get; set; }

        public string Name { get; set; } = string.Empty;

        public ulong Token { get; set; }

        public bool Ready { get; set; }

        public byte[] Choice { get; set; } = [];

        public NetPeer? Peer { get; set; }

        public bool Loaded { get; set; }

        public ulong[] Fingerprint { get; set; } = [];

        public ulong InitialHash { get; set; }

        public SlotControl Control { get; set; }

        public long DroppedAtUs { get; set; }

        public int WaitVotes { get; set; }

        public bool TakenOver { get; set; }

        public bool Resuming { get; set; }

        public uint ResumeRequest { get; set; }

        public long ResumeRequestedUs { get; set; }

        public long RejoinHelloUs { get; set; }

        public int ResumeTick { get; set; } = -1;

        public bool SnapshotRequested { get; set; }

        public int AckTick { get; set; } = -1;

        public uint LastBatchSeq { get; set; }

        public uint SentBatchAck { get; set; }

        public int Late { get; set; }

        public byte PausesLeft { get; set; }

        public void Clear()
        {
            State = SlotState.Open;
            Name = string.Empty;
            Token = 0;
            Ready = false;
            Choice = [];
        }
    }

    private sealed class Lobby
    {
        public Lobby(string code, string name, int slots, bool isPublic, string gameVersion, IPAddress creator)
        {
            Code = code;
            Name = name;
            Public = isPublic;
            GameVersion = gameVersion;
            CreatorAddress = creator;
            Slots = Enumerable.Range(0, slots).Select(i => new Slot((byte)i)).ToArray();
        }

        public string Code { get; }

        public string Name { get; }

        public bool Public { get; }

        public string GameVersion { get; }

        public IPAddress CreatorAddress { get; }

        public Slot[] Slots { get; }

        public byte HostSlot { get; set; }

        public byte[] Settings { get; set; } = [];

        public int SettingsRevision { get; set; }

        public LobbyPhase Phase { get; set; }

        public MatchParameters? Parameters { get; set; }

        public long LoadStartedUs { get; set; }

        public long StartAtUs { get; set; }

        public long StartUs { get; set; }

        public long PausedTotalUs { get; set; }

        public bool Paused { get; set; }

        public byte PausedBy { get; set; }

        public long PausedAtUs { get; set; }

        public int LastClosed { get; set; } = -1;

        public int LastEvaluatedHashTick { get; set; } = -1;

        public int LastAgreedTick { get; set; } = -1;

        public Dictionary<int, PendingTick> Pending { get; } = [];

        public List<ControlEvent> PendingControls { get; } = [];

        public ClosureLog Log { get; } = new();

        public SortedDictionary<int, ulong?[]> Hashes { get; } = [];

        public DesyncCollect? Desync { get; set; }

        public SaveVote? Save { get; set; }

        public uint NextSaveId { get; set; } = 1;

        public uint NextRequestId { get; set; } = 1;

        public long EndedAtUs { get; set; }

        public long RemoveAtUs { get; set; }

        public long NoHumanSinceUs { get; set; }

        public long NextStatusUs { get; set; }

        public long NextTailResendUs { get; set; }

        public LobbyState State() => new(
            Code, Name, HostSlot, Phase, SettingsRevision, Settings,
            Slots.Select(s => new SlotInfo(
                s.State, s.Name, s.Ready || (s.Index == HostSlot && s.State == SlotState.Human), s.Peer is not null,
                s.Peer is null ? (ushort)0 : (ushort)Math.Clamp(s.Peer.RoundTripTime, 0, ushort.MaxValue), s.Choice)).ToArray());
    }

    private sealed class PendingTick
    {
        public List<ClosureEntry> Entries { get; } = [];

        public int Bytes { get; set; }
    }

    private sealed class DesyncCollect(int tick, ulong majority, byte[] odd, uint requestId, long deadlineUs)
    {
        public int Tick { get; } = tick;

        public ulong Majority { get; } = majority;

        public byte[] Odd { get; } = odd;

        public uint RequestId { get; } = requestId;

        public long DeadlineUs { get; } = deadlineUs;

        public HashSet<byte> Waiting { get; } = [];

        public Dictionary<byte, SnapshotMessage> Snapshots { get; } = [];

        public ulong[] Reported { get; } = new ulong[Limits.MaxSlots];
    }

    private sealed class SaveVote(uint id, byte by, long deadlineUs)
    {
        public uint Id { get; } = id;

        public byte By { get; } = by;

        public long DeadlineUs { get; } = deadlineUs;

        public HashSet<byte> Accepted { get; } = [];
    }

    /// <summary>The closed ticks from <see cref="First"/> to the last closed one.</summary>
    private sealed class ClosureLog
    {
        private readonly List<TickClosure> _closures = [];
        private int _offset; // index of First in _closures

        public int First { get; private set; }

        public void Add(TickClosure closure) => _closures.Add(closure);

        public TickClosure Get(int tick) => _closures[_offset + tick - First];

        public void TrimBefore(int tick)
        {
            int drop = Math.Min(tick - First, _closures.Count - _offset);
            if (drop <= 0)
            {
                return;
            }

            _offset += drop;
            First += drop;

            // Compact now and then rather than on every tick.
            if (_offset > 4096)
            {
                _closures.RemoveRange(0, _offset);
                _offset = 0;
            }
        }
    }
}
