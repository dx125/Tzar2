using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteNetLib;
using Tzar2.Core.Serialization;
using Tzar2.Spikes.S3.Sim;

namespace Tzar2.Spikes.S3.Lockstep;

/// <summary>Relay configuration.</summary>
public sealed class RelayOptions
{
    /// <summary>UDP port (0 = any).</summary>
    public int Port { get; set; }

    /// <summary>Tick length in microseconds (game speed 1.0 = 50 ms).</summary>
    public int TickMicroseconds { get; set; } = 50_000;

    /// <summary>Clients report a state hash every this many ticks.</summary>
    public int HashInterval { get; set; } = 1;

    /// <summary>How closed ticks are delivered.</summary>
    public ClosureDelivery Delivery { get; set; } = ClosureDelivery.Reliable;

    /// <summary>Match length in ticks.</summary>
    public int MatchTicks { get; set; } = 400;

    /// <summary>End the match when a desync is detected (else keep going).</summary>
    public bool EndOnDesync { get; set; } = true;

    /// <summary>Most closures per redundant packet.</summary>
    public int RedundancyWindow { get; set; } = 32;

    /// <summary>
    /// Time between the loading barrier and the first tick; clients measure RTT meanwhile, so the adaptive input delay
    /// starts settled (a real match spends this on the countdown).
    /// </summary>
    public int StartDelayMs { get; set; } = 1_500;

    /// <summary>LiteNetLib disconnect timeout in ms.</summary>
    public int DisconnectTimeoutMs { get; set; } = 3_000;
}

/// <summary>
/// The relay of DEC-08 / REQ-NET-01: lobbies joined by code; per match it closes tick T at its deadline and forwards
/// the ordered commands to every peer. It never simulates; it only compares the state hashes the peers report.
/// </summary>
public sealed class RelayServer : IDisposable
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly RelayOptions _options;
    private readonly TextWriter _log;
    private readonly NetManager _net;
    private readonly List<Match> _matches = [];
    private readonly Dictionary<int, (Match Match, Slot Slot)> _byPeer = [];
    private readonly BinaryBufferWriter _w = new(4096);
    private readonly Random _random = new();

    /// <summary>Creates a relay; call <see cref="Start"/>.</summary>
    public RelayServer(RelayOptions options, TextWriter log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        var listener = new EventBasedNetListener();
        listener.ConnectionRequestEvent += OnConnectionRequest;
        listener.PeerDisconnectedEvent += OnPeerDisconnected;
        listener.NetworkReceiveEvent += OnReceive;
        _net = new NetManager(listener)
        {
            AutoRecycle = true,
            DisconnectTimeout = options.DisconnectTimeoutMs,
            UpdateTime = 1,
            EnableStatistics = true,
        };
    }

    /// <summary>The bound UDP port.</summary>
    public int LocalPort => _net.LocalPort;

    /// <summary>Summaries of every match so far.</summary>
    public IReadOnlyList<RelayReport> Reports => _matches.Select(m => m.Report).ToList();

    /// <summary>True when at least one match ran and every match has ended and lost its peers or timed out.</summary>
    public bool AllMatchesFinished
    {
        get
        {
            long now = Clock.NowMicroseconds();
            return _matches.Count > 0 && _matches.TrueForAll(m => m.EndedAtUs > 0
                && (Array.TrueForAll(m.Slots, s => s.Peer is null) || now - m.EndedAtUs > 3_000_000));
        }
    }

    /// <summary>Starts listening.</summary>
    public void Start()
    {
        if (!_net.Start(_options.Port))
        {
            throw new InvalidOperationException($"Cannot bind UDP port {_options.Port}.");
        }

        Log($"LISTENING {_net.LocalPort} tick={_options.TickMicroseconds}us delivery={_options.Delivery} hashInterval={_options.HashInterval}");
    }

    /// <summary>Processes network events and closes due ticks. Call in a loop.</summary>
    public void Update()
    {
        _net.PollEvents();
        long now = Clock.NowMicroseconds();
        foreach (Match match in _matches)
        {
            Update(match, now);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _net.Stop();

    private void OnConnectionRequest(ConnectionRequest request)
    {
        Hello hello;
        try
        {
            hello = Wire.ReadHello(request.Data.GetRemainingBytesSpan());
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException or InvalidOperationException)
        {
            request.Reject();
            return;
        }

        switch (hello.Kind)
        {
            case HelloKind.Create when hello.PlayerCount is >= 1 and <= 8:
            {
                var match = new Match(NewCode(), hello.PlayerCount, (ulong)_random.NextInt64(), _options.MatchTicks);
                _matches.Add(match);
                Log($"LOBBY {match.Code} created for {match.Slots.Length} players");
                Admit(request, match, match.Slots[0], hello.Name);
                break;
            }

            case HelloKind.Join:
            {
                Match? match = _matches.Find(m => m.Code == hello.Code && !m.Started);
                Slot? free = match is null ? null : Array.Find(match.Slots, s => s.Token == 0);
                if (match is null || free is null)
                {
                    request.Reject();
                    return;
                }

                Admit(request, match, free, hello.Name);
                break;
            }

            case HelloKind.Rejoin:
            {
                Match? match = _matches.Find(m => m.Code == hello.Code && m.Started && m.EndedAtUs == 0);
                if (match is null || hello.Slot >= match.Slots.Length || match.Slots[hello.Slot].Token != hello.Token)
                {
                    request.Reject();
                    return;
                }

                Slot slot = match.Slots[hello.Slot];
                if (slot.Peer is not null)
                {
                    _byPeer.Remove(slot.Peer.Id);
                    _net.DisconnectPeerForce(slot.Peer);
                }

                NetPeer peer = request.Accept();
                slot.Peer = peer;
                slot.Resuming = true;
                slot.RejoinHelloUs = Clock.NowMicroseconds();
                slot.SnapshotRequestedUs = 0;
                _byPeer[peer.Id] = (match, slot);
                Log($"REJOIN {match.Code} slot {slot.Index} at closed tick {match.LastClosed}");
                break;
            }

            default:
                request.Reject();
                break;
        }
    }

    private void Admit(ConnectionRequest request, Match match, Slot slot, string name)
    {
        NetPeer peer = request.Accept();
        slot.Peer = peer;
        slot.Token = ((ulong)_random.NextInt64() << 1) | 1;
        slot.Name = name;
        _byPeer[peer.Id] = (match, slot);
        _w.Clear();
        Wire.Write(_w, new Welcome(
            match.Code, slot.Index, slot.Token, (byte)match.Slots.Length, match.Seed, _options.TickMicroseconds,
            _options.HashInterval, _options.Delivery, match.EndTick));
        peer.Send(_w.WrittenSpan, DeliveryMethod.ReliableOrdered);
        Log($"JOIN {match.Code} slot {slot.Index} '{name}' from {peer}");
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (_byPeer.Remove(peer.Id, out (Match Match, Slot Slot) entry) && ReferenceEquals(entry.Slot.Peer, peer))
        {
            entry.Slot.Peer = null;
            Log($"DROP {entry.Match.Code} slot {entry.Slot.Index}: {info.Reason} at closed tick {entry.Match.LastClosed}");
        }
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (!_byPeer.TryGetValue(peer.Id, out (Match Match, Slot Slot) entry))
        {
            return;
        }

        try
        {
            var r = new BinaryBufferReader(reader.GetRemainingBytesSpan());
            Handle(entry.Match, entry.Slot, ref r);
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException or ArgumentException)
        {
            Log($"BAD MESSAGE from slot {entry.Slot.Index}: {e.Message}");
            _net.DisconnectPeer(peer);
        }
    }

    private void Handle(Match match, Slot slot, ref BinaryBufferReader r)
    {
        switch (Wire.ReadType(ref r))
        {
            case MessageType.Ping:
                _w.Clear();
                Wire.WriteValue(_w, MessageType.Pong, r.ReadVarInt64());
                slot.Peer?.Send(_w.WrittenSpan, DeliveryMethod.Unreliable);
                break;

            case MessageType.Ready:
                slot.Ready = true;
                slot.ReadyHash = r.ReadUInt64();
                TryStart(match);
                break;

            case MessageType.Batch:
                foreach (Batch batch in Wire.ReadBatches(ref r, slot.Index))
                {
                    // Redundant copies of batches already processed are skipped; ranges always start at ack + 1.
                    if (batch.Seq > slot.LastBatchSeq)
                    {
                        slot.LastBatchSeq = batch.Seq;
                        OnBatch(match, slot, batch);
                    }
                }

                break;

            case MessageType.Snapshot:
                OnSnapshot(match, Wire.ReadSnapshot(ref r));
                break;

            default:
                throw new InvalidDataException("Unexpected message type.");
        }
    }

    private void TryStart(Match match)
    {
        if (match.Started || !Array.TrueForAll(match.Slots, s => s.Ready))
        {
            return;
        }

        // Loading barrier (REQ-NET-05): everyone loaded the same initial state.
        ulong expected = match.Slots[0].ReadyHash;
        if (!Array.TrueForAll(match.Slots, s => s.ReadyHash == expected))
        {
            Log($"ABORT {match.Code}: initial state hashes differ");
            match.EndedAtUs = Clock.NowMicroseconds();
            Broadcast(match, w => Wire.WriteValue(w, MessageType.End, -1), DeliveryMethod.ReliableOrdered);
            return;
        }

        if (match.StartAtUs == 0)
        {
            match.StartAtUs = Clock.NowMicroseconds() + _options.StartDelayMs * 1000L;
            Log($"BARRIER {match.Code} players={match.Slots.Length} initialHash={expected:X16}");
        }
    }

    private void OnBatch(Match match, Slot slot, Batch batch)
    {
        if (!match.Started || slot.Resuming)
        {
            return;
        }

        slot.AckTick = Math.Max(slot.AckTick, Math.Min(batch.AckTick, match.LastClosed));
        if (match.EndedAtUs > 0)
        {
            return;
        }

        slot.Batches++;
        if (batch.Commands.Length > 0)
        {
            // "Relay tick closing": a batch for an already closed tick is delayed, not lost.
            int assigned = Math.Clamp(batch.TargetTick, match.LastClosed + 1, match.LastClosed + 1 + 64);
            if (batch.TargetTick <= match.LastClosed)
            {
                slot.Late++;
                _w.Clear();
                Wire.WriteLate(_w, batch.Seq, batch.TargetTick, assigned);
                slot.Peer?.Send(_w.WrittenSpan, DeliveryMethod.ReliableOrdered);
            }

            if (!match.Pending.TryGetValue(assigned, out List<SimCommand>? list))
            {
                list = [];
                match.Pending[assigned] = list;
            }

            list.AddRange(batch.Commands);
        }

        foreach (TickHash hash in batch.Hashes)
        {
            RecordHash(match, slot, hash);
        }
    }

    private void RecordHash(Match match, Slot slot, TickHash hash)
    {
        if (hash.Tick < 0 || hash.Tick > match.LastClosed || hash.Tick <= match.LastEvaluatedHashTick)
        {
            return;
        }

        if (!match.Hashes.TryGetValue(hash.Tick, out ulong?[]? entry))
        {
            entry = new ulong?[match.Slots.Length];
            match.Hashes[hash.Tick] = entry;
        }

        entry[slot.Index] = hash.Hash;
        bool complete = true;
        foreach (Slot s in match.Slots)
        {
            bool expected = s.Peer is not null && !s.Resuming && s.ResumeTick < hash.Tick;
            complete &= !expected || entry[s.Index].HasValue;
        }

        if (complete)
        {
            Evaluate(match, hash.Tick, entry);
        }
    }

    private void Evaluate(Match match, int tick, ulong?[] entry)
    {
        match.Hashes.Remove(tick);
        match.LastEvaluatedHashTick = Math.Max(match.LastEvaluatedHashTick, tick);
        ulong[] reported = entry.Where(h => h.HasValue).Select(h => h!.Value).ToArray();
        if (reported.Length == 0)
        {
            return;
        }

        match.Report.HashTicksCompared++;

        // Majority hash; ties go to the hash of the lowest slot.
        ulong majority = reported.GroupBy(h => h).OrderByDescending(g => g.Count()).ThenBy(g => Array.FindIndex(entry, e => e == g.Key)).First().Key;
        byte[] odd = Enumerable.Range(0, entry.Length).Where(i => entry[i].HasValue && entry[i] != majority).Select(i => (byte)i).ToArray();
        if (odd.Length == 0 || match.Report.Desyncs.Count > 0)
        {
            return;
        }

        double afterMs = (Clock.NowMicroseconds() - (match.StartUs + ((long)(tick + 1) * _options.TickMicroseconds))) / 1000.0;
        match.Report.Desyncs.Add(new DesyncEntry { Tick = tick, OddSlots = odd.Select(b => (int)b).ToList(), DetectedAfterMs = afterMs });
        Log($"DESYNC {match.Code} tick {tick}: majority {majority:X16}, odd slots [{string.Join(',', odd)}], detected {afterMs:F1} ms after closing");
        var report = new DesyncReport(tick, majority, odd);
        Broadcast(match, w => Wire.Write(w, report), DeliveryMethod.ReliableOrdered);
        if (_options.EndOnDesync)
        {
            match.EndTick = match.LastClosed;
            EndMatch(match);
        }
    }

    private void OnSnapshot(Match match, SnapshotMessage snapshot)
    {
        Slot? rejoiner = Array.Find(match.Slots, s => s.Resuming && s.Peer is not null && s.SnapshotRequestedUs > 0);
        if (rejoiner is null || snapshot.Tick > match.LastClosed || snapshot.Tick < -1)
        {
            return;
        }

        TickClosure[] backlog = match.Log.GetRange(snapshot.Tick + 1, match.LastClosed - snapshot.Tick).ToArray();
        _w.Clear();
        Wire.Write(_w, MessageType.Resume, snapshot with { Backlog = backlog });
        rejoiner.Peer!.Send(_w.WrittenSpan, DeliveryMethod.ReliableOrdered);
        rejoiner.Resuming = false;
        rejoiner.ResumeTick = snapshot.Tick;
        rejoiner.AckTick = match.LastClosed;
        double serveMs = (Clock.NowMicroseconds() - rejoiner.RejoinHelloUs) / 1000.0;
        match.Report.Rejoins++;
        match.Report.RejoinServeMs.Add(serveMs);
        Log($"RESUME {match.Code} slot {rejoiner.Index}: snapshot tick {snapshot.Tick} ({snapshot.Data.Length} B) + {backlog.Length} ticks backlog, served {serveMs:F1} ms after hello");
    }

    private void Update(Match match, long now)
    {
        if (!match.Started && match.StartAtUs > 0 && now >= match.StartAtUs && match.EndedAtUs == 0)
        {
            match.Started = true;
            match.StartUs = now;
            Broadcast(match, w => Wire.WriteEmpty(w, MessageType.Start), DeliveryMethod.ReliableOrdered);
            Log($"START {match.Code}");
        }

        if (!match.Started)
        {
            return;
        }

        if (match.EndedAtUs > 0)
        {
            // Redundant delivery has no retransmission of its own: keep repeating the tail until every peer has it.
            if (_options.Delivery == ClosureDelivery.Redundant && now >= match.NextTailResendUs)
            {
                match.NextTailResendUs = now + _options.TickMicroseconds;
                foreach (Slot slot in match.Slots)
                {
                    if (slot.Peer is not null && !slot.Resuming && slot.AckTick < match.LastClosed)
                    {
                        SendRedundant(match, slot);
                    }
                }
            }

            return;
        }

        foreach (Slot slot in match.Slots)
        {
            if (slot.Resuming && slot.Peer is not null && now - slot.SnapshotRequestedUs > 1_000_000)
            {
                Slot? donor = Array.Find(match.Slots, s => s != slot && s.Peer is not null && !s.Resuming);
                if (donor is not null)
                {
                    slot.SnapshotRequestedUs = now;
                    _w.Clear();
                    Wire.WriteEmpty(_w, MessageType.SnapshotRequest);
                    donor.Peer!.Send(_w.WrittenSpan, DeliveryMethod.ReliableOrdered);
                    Log($"SNAPSHOT-REQUEST {match.Code} slot {donor.Index} for slot {slot.Index}");
                }
            }
        }

        while (match.EndedAtUs == 0 && now >= match.StartUs + ((long)(match.LastClosed + 2) * _options.TickMicroseconds))
        {
            CloseTick(match);
        }
    }

    private void CloseTick(Match match)
    {
        int tick = match.LastClosed + 1;
        SimCommand[] commands = match.Pending.Remove(tick, out List<SimCommand>? list) ? list.ToArray() : [];
        var closure = new TickClosure(tick, commands);
        match.Log.Add(closure);
        match.LastClosed = tick;

        foreach (Slot slot in match.Slots)
        {
            if (slot.Peer is null || slot.Resuming)
            {
                continue;
            }

            if (_options.Delivery == ClosureDelivery.Reliable)
            {
                _w.Clear();
                Wire.WriteClosuresHeader(_w, slot.LastBatchSeq, 1);
                Wire.WriteClosure(_w, closure);
                slot.Peer.Send(_w.WrittenSpan, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                SendRedundant(match, slot);
            }
        }

        // Ticks whose hashes are still incomplete after 2 s are compared with what arrived.
        while (match.Hashes.Count > 0 && match.Hashes.Keys.First() < tick - 40)
        {
            KeyValuePair<int, ulong?[]> oldest = match.Hashes.First();
            Evaluate(match, oldest.Key, oldest.Value);
        }

        if (tick >= match.EndTick && match.EndedAtUs == 0)
        {
            EndMatch(match);
        }
    }

    private void SendRedundant(Match match, Slot slot)
    {
        NetPeer peer = slot.Peer!;
        int limit = peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable) - 16;
        int first = slot.AckTick + 1;
        int last = Math.Min(match.LastClosed, first + _options.RedundancyWindow - 1);
        var payload = new BinaryBufferWriter(1024);
        int count = 0;
        for (int t = first; t <= last; t++)
        {
            int before = payload.Length;
            Wire.WriteClosure(payload, match.Log[t]);
            if (payload.Length > limit && count > 0)
            {
                payload = Truncate(payload, before);
                break;
            }

            count++;
        }

        _w.Clear();
        Wire.WriteClosuresHeader(_w, slot.LastBatchSeq, count);
        _w.WriteBytes(payload.WrittenSpan);
        peer.Send(_w.WrittenSpan, _w.Length <= limit + 16 ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }

    private static BinaryBufferWriter Truncate(BinaryBufferWriter writer, int length)
    {
        var copy = new BinaryBufferWriter(length);
        copy.WriteBytes(writer.WrittenSpan[..length]);
        return copy;
    }

    private void EndMatch(Match match)
    {
        match.EndedAtUs = Clock.NowMicroseconds();
        match.Report.LastClosedTick = match.LastClosed;
        match.Report.LateBatches = match.Slots.Select(s => s.Late).ToList();
        match.Report.Batches = match.Slots.Select(s => s.Batches).ToList();
        int end = match.EndTick;
        Broadcast(match, w => Wire.WriteValue(w, MessageType.End, end), DeliveryMethod.ReliableOrdered);
        Log($"END {match.Code} at tick {match.LastClosed}");
    }

    private void Broadcast(Match match, Action<BinaryBufferWriter> write, DeliveryMethod method)
    {
        _w.Clear();
        write(_w);
        foreach (Slot slot in match.Slots)
        {
            slot.Peer?.Send(_w.WrittenSpan, method);
        }
    }

    private string NewCode()
    {
        Span<char> code = stackalloc char[6];
        for (int i = 0; i < code.Length; i++)
        {
            code[i] = CodeAlphabet[_random.Next(CodeAlphabet.Length)];
        }

        return new string(code);
    }

    private void Log(FormattableString message)
    {
        _log.WriteLine(FormattableString.Invariant(message));
        _log.Flush();
    }

    private sealed class Slot(byte index)
    {
        public byte Index { get; } = index;

        public ulong Token { get; set; }

        public string Name { get; set; } = string.Empty;

        public NetPeer? Peer { get; set; }

        public bool Ready { get; set; }

        public ulong ReadyHash { get; set; }

        public bool Resuming { get; set; }

        public int ResumeTick { get; set; } = -1;

        public long RejoinHelloUs { get; set; }

        public long SnapshotRequestedUs { get; set; }

        public int AckTick { get; set; } = -1;

        public uint LastBatchSeq { get; set; }

        public int Late { get; set; }

        public int Batches { get; set; }
    }

    private sealed class Match
    {
        public Match(string code, int players, ulong seed, int ticks)
        {
            Code = code;
            Seed = seed;
            EndTick = ticks - 1;
            Slots = Enumerable.Range(0, players).Select(i => new Slot((byte)i)).ToArray();
            Report = new RelayReport { Code = code, Players = players };
        }

        public string Code { get; }

        public ulong Seed { get; }

        public Slot[] Slots { get; }

        public RelayReport Report { get; }

        public int EndTick { get; set; }

        public bool Started { get; set; }

        public long StartUs { get; set; }

        public long StartAtUs { get; set; }

        public long EndedAtUs { get; set; }

        public long NextTailResendUs { get; set; }

        public int LastClosed { get; set; } = -1;

        public int LastEvaluatedHashTick { get; set; } = -1;

        public Dictionary<int, List<SimCommand>> Pending { get; } = [];

        public List<TickClosure> Log { get; } = [];

        public SortedDictionary<int, ulong?[]> Hashes { get; } = [];
    }
}
