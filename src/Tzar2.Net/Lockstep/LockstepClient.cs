using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Tzar2.Core.Serialization;
using Tzar2.Net.Protocol;

namespace Tzar2.Net.Lockstep;

/// <summary>Where and as whom a <see cref="LockstepClient"/> connects.</summary>
public sealed class LockstepClientOptions
{
    /// <summary>Relay host name or address.</summary>
    public required string Host { get; init; }

    /// <summary>Relay UDP port.</summary>
    public required int Port { get; init; }

    /// <summary>Player name.</summary>
    public string Name { get; init; } = "Player";

    /// <summary>Engine version (lobbies only admit the host's version).</summary>
    public required string GameVersion { get; init; }

    /// <summary>A silent relay counts as lost after this long.</summary>
    public int DisconnectTimeoutMs { get; init; } = 10_000;
}

/// <summary>Where a <see cref="LockstepClient"/> is in the life cycle.</summary>
public enum ClientPhase
{
    /// <summary>Waiting for the welcome.</summary>
    Connecting,

    /// <summary>In the lobby.</summary>
    Lobby,

    /// <summary>Loading the match (the game reports <see cref="LockstepClient.Loaded"/>).</summary>
    Loading,

    /// <summary>Rejoined a running match; waiting for the resume snapshot.</summary>
    Resuming,

    /// <summary>The match runs: batches go up, closures come down.</summary>
    Running,

    /// <summary>Ended, refused or disconnected.</summary>
    Ended,
}

/// <summary>Something the relay told the client, in order (drained by the game with <see cref="LockstepClient.TryDequeue"/>).</summary>
public abstract record ClientEvent;

/// <summary>Accepted into a lobby.</summary>
public sealed record WelcomeEvent(Welcome Welcome) : ClientEvent;

/// <summary>The hello was refused.</summary>
public sealed record RefusedEvent(Refusal Refusal) : ClientEvent;

/// <summary>The lobby changed.</summary>
public sealed record LobbyEvent(LobbyState Lobby) : ClientEvent;

/// <summary>A chat line.</summary>
public sealed record ChatEvent(byte From, byte Recipients, string Text) : ClientEvent;

/// <summary>A notice for this member.</summary>
public sealed record NoticeEvent(NoticeKind Kind, string Text) : ClientEvent;

/// <summary>Load the match with these parameters.</summary>
public sealed record LoadEvent(MatchParameters Parameters) : ClientEvent;

/// <summary>Every peer loaded the same match; the first tick closes after the countdown.</summary>
public sealed record StartEvent(int DelayMs) : ClientEvent;

/// <summary>State hashes disagreed.</summary>
public sealed record DesyncEvent(DesyncNotice Notice) : ClientEvent;

/// <summary>The relay asks for snapshot(s); answer with <see cref="LockstepClient.SendSnapshot"/> between two ticks.</summary>
public sealed record SnapshotRequestEvent(SnapshotPurpose Purpose, uint RequestId) : ClientEvent;

/// <summary>A rejoin resumes from this snapshot; the backlog closures are already queued.</summary>
public sealed record ResumeEvent(int Tick, ulong Hash, byte[] Snapshot, int BacklogTicks) : ClientEvent;

/// <summary>The host proposed a save; answer with <see cref="LockstepClient.VoteSave"/>.</summary>
public sealed record SaveProposalEvent(SaveProposal Proposal) : ClientEvent;

/// <summary>A save proposal was decided.</summary>
public sealed record SaveOutcomeEvent(SaveOutcome Outcome) : ClientEvent;

/// <summary>The match was paused or resumed.</summary>
public sealed record PauseEvent(bool Paused, byte By, byte PausesLeft) : ClientEvent;

/// <summary>Every slot's connection, ping and controller.</summary>
public sealed record StatusEvent(PeerStatus[] Slots) : ClientEvent;

/// <summary>The lobby or match ended.</summary>
public sealed record EndEvent(EndMessage End) : ClientEvent;

/// <summary>The connection to the relay was lost.</summary>
public sealed record DisconnectedEvent(string Reason) : ClientEvent;

/// <summary>Traffic and lockstep counters of a client.</summary>
/// <param name="BatchesSent">Batches sent.</param>
/// <param name="LateBatches">Batches the relay moved to a later tick.</param>
/// <param name="ClosuresReceived">Distinct closures received.</param>
/// <param name="BytesSent">UDP bytes sent (LiteNetLib statistics).</param>
/// <param name="BytesReceived">UDP bytes received.</param>
public readonly record struct ClientStatistics(long BatchesSent, long LateBatches, long ClosuresReceived, long BytesSent, long BytesReceived);

/// <summary>
/// The peer side of the lockstep (REQ-NET-02, ADR 0009/0160): lobby membership and chat, the loading barrier, one batch
/// per tick stamped <c>estimated open tick + D</c> with an RTT-driven adaptive input delay D, redundant unreliable
/// upstream, the "tick T complete" closures the game executes in order, state-hash reports, snapshot requests and the
/// rejoin resume. Command payloads are opaque bytes (the game's command codec).
/// </summary>
/// <remarks>Thread-safe: every member locks; LiteNetLib events are dispatched inside <see cref="Poll"/>.</remarks>
public sealed class LockstepClient : IDisposable
{
    private const byte GameChannel = 0;
    private const byte ChatChannel = 1;
    private const long PingIntervalUs = 200_000;
    private const int MaxQueuedPayloads = 64;
    private const int MaxQueuedEvents = 4096;

    private readonly object _gate = new();
    private readonly LockstepClientOptions _options;
    private readonly NetManager _net;
    private readonly BinaryBufferWriter _w = new(2048);
    private readonly RttEstimator _rtt = new();
    private readonly ConcurrentQueue<ClientEvent> _events = new();
    private readonly Dictionary<int, TickClosure> _closed = [];
    private readonly Queue<byte[]> _payloads = new();
    private readonly List<TickHash> _hashes = [];
    private readonly List<(uint Seq, byte[] Body)> _unacked = [];
    private NetPeer? _relay;
    private InputDelayController? _delay;
    private int _receivedThrough = -1;
    private uint _batchSeq;
    private long _nextBatchUs = long.MaxValue;
    private long _nextPingUs;
    private long _clockBaseUs = long.MaxValue;
    private long _batchesSent;
    private long _lateBatches;
    private long _closuresReceived;

    private LockstepClient(LockstepClientOptions options, Hello hello)
    {
        _options = options;
        var listener = new EventBasedNetListener();
        listener.NetworkReceiveEvent += OnReceive;
        listener.PeerDisconnectedEvent += OnDisconnected;
        _net = new NetManager(listener)
        {
            AutoRecycle = true,
            UpdateTime = 1,
            EnableStatistics = true,
            DisconnectTimeout = options.DisconnectTimeoutMs,
            ChannelsCount = 2,
        };
        if (!_net.Start())
        {
            throw new InvalidOperationException("Cannot open a UDP socket.");
        }

        Wire.Write(_w, hello);
        _relay = _net.Connect(new IPEndPoint(Resolve(options.Host), options.Port), _w.WrittenSpan);
    }

    /// <summary>Connects and creates a lobby (this member becomes its host).</summary>
    public static LockstepClient Create(LockstepClientOptions options, LobbyCreate lobby)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lobby);
        return new LockstepClient(options, new Hello(HelloKind.Create, options.GameVersion, options.Name, string.Empty, 0, 0, lobby));
    }

    /// <summary>Connects and joins the lobby with <paramref name="code"/>.</summary>
    public static LockstepClient Join(LockstepClientOptions options, string code)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(code);
        return new LockstepClient(options, new Hello(HelloKind.Join, options.GameVersion, options.Name, code, 0, 0, null));
    }

    /// <summary>Reconnects to slot <paramref name="slot"/> of lobby <paramref name="code"/> with its session token (REQ-NET-06).</summary>
    public static LockstepClient Rejoin(LockstepClientOptions options, string code, byte slot, ulong token)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(code);
        return new LockstepClient(options, new Hello(HelloKind.Rejoin, options.GameVersion, options.Name, code, slot, token, null));
    }

    /// <summary>The life-cycle phase.</summary>
    public ClientPhase Phase { get; private set; }

    /// <summary>The welcome (slot, session token, code), or null before it.</summary>
    public Welcome? Welcome { get; private set; }

    /// <summary>The latest lobby state, or null.</summary>
    public LobbyState? Lobby { get; private set; }

    /// <summary>The match parameters, or null before loading.</summary>
    public MatchParameters? Parameters { get; private set; }

    /// <summary>The current input delay in ticks.</summary>
    public int InputDelay
    {
        get
        {
            lock (_gate)
            {
                return _delay?.Delay ?? 0;
            }
        }
    }

    /// <summary>The smoothed RTT to the relay in milliseconds.</summary>
    public int RttMs
    {
        get
        {
            lock (_gate)
            {
                return (int)(_rtt.SmoothedMicroseconds / 1000);
            }
        }
    }

    /// <summary>The highest tick every closure up to which has arrived.</summary>
    public int ReceivedThrough
    {
        get
        {
            lock (_gate)
            {
                return _receivedThrough;
            }
        }
    }

    /// <summary>Counters.</summary>
    public ClientStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new ClientStatistics(_batchesSent, _lateBatches, _closuresReceived, _net.Statistics.BytesSent, _net.Statistics.BytesReceived);
            }
        }
    }

    /// <summary>The next event, if any.</summary>
    public bool TryDequeue(out ClientEvent? e) => _events.TryDequeue(out e);

    /// <summary>
    /// Dispatches network events, sends the batch when its tick interval is due (empty = heartbeat) and pings. Call
    /// about every millisecond (the match loop does it between ticks).
    /// </summary>
    public void Poll()
    {
        lock (_gate)
        {
            _net.PollEvents();
            long now = MonotonicClock.NowMicroseconds();
            if (_relay is null || Phase == ClientPhase.Ended)
            {
                return;
            }

            if (Phase == ClientPhase.Running && now >= _nextBatchUs)
            {
                SendBatch(now);
                _nextBatchUs = Math.Max(_nextBatchUs + Parameters!.TickMicroseconds, now + 1_000);
            }

            if (now >= _nextPingUs && Welcome is not null)
            {
                _nextPingUs = now + PingIntervalUs;
                _w.Clear();
                Wire.WriteValue(_w, MessageType.Ping, now);
                _relay.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.Unreliable);
            }
        }
    }

    /// <summary>Takes the closure of <paramref name="tick"/> if it arrived (the game executes ticks strictly in order).</summary>
    public bool TryTakeClosure(int tick, out TickClosure? closure)
    {
        lock (_gate)
        {
            return _closed.Remove(tick, out closure);
        }
    }

    /// <summary>Reports an executed checkpoint tick's state hash (REQ-NET-04).</summary>
    public void ReportHash(int tick, ulong hash)
    {
        lock (_gate)
        {
            if (_hashes.Count < Limits.MaxHashesPerBatch)
            {
                _hashes.Add(new TickHash(tick, hash));
            }
        }
    }

    /// <summary>Queues encoded local commands; each queued payload rides the next batch.</summary>
    /// <exception cref="ArgumentException">The payload exceeds <see cref="Limits.MaxPayloadBytes"/>.</exception>
    public bool Submit(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > Limits.MaxPayloadBytes)
        {
            throw new ArgumentException("Payload too large.", nameof(payload));
        }

        lock (_gate)
        {
            if (payload.Length == 0 || _payloads.Count >= MaxQueuedPayloads)
            {
                return false;
            }

            _payloads.Enqueue(payload);
            return true;
        }
    }

    /// <summary>Whether no payload is waiting for a batch.</summary>
    public bool OutboxEmpty
    {
        get
        {
            lock (_gate)
            {
                return _payloads.Count == 0;
            }
        }
    }

    /// <summary>Sets a slot's choice bytes (own slot, or a computer slot as host).</summary>
    public void SetChoice(byte slot, ReadOnlySpan<byte> choice)
    {
        byte[] bytes = choice.ToArray();
        Send(w => Wire.WriteSetChoice(w, slot, bytes), GameChannel);
    }

    /// <summary>Sets the match settings bytes (host).</summary>
    public void SetSettings(ReadOnlySpan<byte> settings)
    {
        byte[] bytes = settings.ToArray();
        Send(w => Wire.WriteSetSettings(w, bytes), GameChannel);
    }

    /// <summary>Opens, closes, adds a computer to or kicks a slot (host).</summary>
    public void SetSlot(byte slot, SlotState state) => Send(w => Wire.WriteSetSlot(w, slot, state), GameChannel);

    /// <summary>Sets the ready flag.</summary>
    public void SetReady(bool ready) => Send(w => Wire.WriteFlag(w, MessageType.SetReady, ready), GameChannel);

    /// <summary>Starts loading (host; every human must be ready).</summary>
    public void RequestStart(MatchParameters wanted) => Send(w => Wire.WriteStartRequest(w, wanted), GameChannel);

    /// <summary>Sends a chat line to the slots in <paramref name="recipients"/> (it is echoed to the sender).</summary>
    public void Chat(byte recipients, string text) => Send(w => Wire.WriteChat(w, recipients, text), ChatChannel);

    /// <summary>Reports the loaded match (loading barrier): fingerprint parts and the initial state hash.</summary>
    public void Loaded(ReadOnlySpan<ulong> fingerprint, ulong initialHash)
    {
        ulong[] parts = fingerprint.ToArray();
        Send(w => Wire.WriteLoaded(w, parts, initialHash), GameChannel);
    }

    /// <summary>Answers a <see cref="SnapshotRequestEvent"/>.</summary>
    public void SendSnapshot(SnapshotMessage snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var w = new BinaryBufferWriter(snapshot.Data.Length + snapshot.BeforeData.Length + 64);
        Wire.Write(w, snapshot);
        lock (_gate)
        {
            _relay?.Send(w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>Proposes a save (host).</summary>
    public void RequestSave(string label) => Send(w => Wire.WriteSaveRequest(w, label), GameChannel);

    /// <summary>Consents to or refuses a proposed save.</summary>
    public void VoteSave(uint id, bool accept) => Send(w => Wire.WriteSaveVote(w, id, accept), GameChannel);

    /// <summary>Pauses or resumes the match.</summary>
    public void Pause(bool paused) => Send(w => Wire.WriteFlag(w, MessageType.Pause, paused), GameChannel);

    /// <summary>Votes to wait longer for dropped slot <paramref name="slot"/>.</summary>
    public void VoteWait(byte slot) => Send(w => Wire.WriteValue(w, MessageType.WaitVote, slot), GameChannel);

    /// <summary>Leaves: disconnects (a running match then applies the drop policy to this slot).</summary>
    public void Leave()
    {
        lock (_gate)
        {
            if (_relay is not null)
            {
                _net.DisconnectPeer(_relay);
            }

            Phase = ClientPhase.Ended;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _net.Stop(true);
            _relay = null;
            Phase = ClientPhase.Ended;
        }
    }

    private void Send(Action<BinaryBufferWriter> write, byte channel)
    {
        lock (_gate)
        {
            if (_relay is null)
            {
                return;
            }

            _w.Clear();
            write(_w);

            // LiteNetLib drops sends on a connection that is not established yet: keep them until the welcome.
            if (Welcome is null)
            {
                if (_early.Count < 64)
                {
                    _early.Add((_w.ToArray(), channel));
                }

                return;
            }

            _relay.Send(_w.WrittenSpan, channel, DeliveryMethod.ReliableOrdered);
        }
    }

    private readonly List<(byte[] Bytes, byte Channel)> _early = [];

    private void Raise(ClientEvent e)
    {
        if (_events.Count < MaxQueuedEvents)
        {
            _events.Enqueue(e);
        }
    }

    // ---- lockstep ----

    private void SendBatch(long now)
    {
        if (_rtt.SampleCount > 0)
        {
            _delay!.Update(_rtt);
        }

        byte[] payload = _payloads.Count > 0 ? _payloads.Dequeue() : [];
        var batch = new Batch(++_batchSeq, EstimateRelayOpenTick(now) + _delay!.Delay, _receivedThrough, payload, [.. _hashes]);
        _hashes.Clear();
        _batchesSent++;

        // Redundant upstream (ADR 0009): repeat every unacknowledged batch, oldest first, in one unreliable datagram.
        var body = new BinaryBufferWriter(payload.Length + 32);
        Wire.WriteBatch(body, batch);
        _unacked.Add((batch.Seq, body.ToArray()));
        if (_unacked.Count > Limits.MaxBatchesPerMessage * 4)
        {
            _unacked.RemoveAt(0); // a relay that stops acknowledging is gone; bound the memory
        }

        int limit = _relay!.GetMaxSinglePacketSize(DeliveryMethod.Unreliable) - 16;
        int count = 0;
        int size = 0;
        foreach ((uint _, byte[] bytes) in _unacked)
        {
            if (count == Limits.MaxBatchesPerMessage || (count > 0 && size + bytes.Length > limit))
            {
                break;
            }

            count++;
            size += bytes.Length;
        }

        _w.Clear();
        Wire.WriteBatchesHeader(_w, count);
        for (int i = 0; i < count; i++)
        {
            _w.WriteBytes(_unacked[i].Body);
        }

        _relay.Send(_w.WrittenSpan, GameChannel, _w.Length <= limit + 16 ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }

    // The tick the relay is closing now, seen from here: the earliest-arriving closure fixes the relay clock offset plus
    // the minimum one-way delay; add half the smoothed RTT for the trip itself (ADR 0009).
    private int EstimateRelayOpenTick(long now)
    {
        if (_clockBaseUs == long.MaxValue)
        {
            return _receivedThrough + 1;
        }

        long elapsed = now - _clockBaseUs + (_rtt.SmoothedMicroseconds / 2);
        return (int)Math.Clamp(elapsed / Parameters!.TickMicroseconds, 0, Limits.MaxTick - 64);
    }

    private void AcceptClosure(TickClosure closure, long now)
    {
        if (closure.Tick <= _receivedThrough || _closed.ContainsKey(closure.Tick))
        {
            return; // a redundant repeat
        }

        _closed[closure.Tick] = closure;
        _closuresReceived++;
        while (_closed.ContainsKey(_receivedThrough + 1))
        {
            _receivedThrough++;
        }

        // Relay clock: closure T is sent at start + (T + 1) x tick; keep the earliest arrival seen.
        _clockBaseUs = Math.Min(_clockBaseUs, now - ((long)(closure.Tick + 1) * Parameters!.TickMicroseconds));
    }

    // ---- receive ----

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            var r = new BinaryBufferReader(reader.GetRemainingBytesSpan());
            Handle(ref r);
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            // The relay is trusted to order, not to be well-formed: a malformed message ends the session.
            Raise(new DisconnectedEvent("Malformed message from the relay: " + e.Message));
            Phase = ClientPhase.Ended;
            _net.DisconnectPeer(peer);
        }
    }

    private void Handle(ref BinaryBufferReader r)
    {
        long now = MonotonicClock.NowMicroseconds();
        switch (Wire.ReadType(ref r))
        {
            case MessageType.Welcome:
                Welcome = Wire.ReadWelcome(ref r);
                Phase = ClientPhase.Lobby;
                foreach ((byte[] bytes, byte channel) in _early)
                {
                    _relay?.Send(bytes, channel, DeliveryMethod.ReliableOrdered);
                }

                _early.Clear();
                Raise(new WelcomeEvent(Welcome));
                break;
            case MessageType.LobbyState:
                LobbyState lobby = Wire.ReadLobbyState(ref r);
                Lobby = lobby;
                if (lobby.Phase == LobbyPhase.Running && Phase is ClientPhase.Lobby or ClientPhase.Connecting)
                {
                    Phase = ClientPhase.Resuming; // a rejoin into a running match
                }

                Raise(new LobbyEvent(lobby));
                break;
            case MessageType.ChatDelivery:
                (byte from, byte recipients, string text) = Wire.ReadChatDelivery(ref r);
                Raise(new ChatEvent(from, recipients, text));
                break;
            case MessageType.Notice:
                (NoticeKind kind, string notice) = Wire.ReadNotice(ref r);
                Raise(new NoticeEvent(kind, notice));
                break;
            case MessageType.Load:
                Parameters = Wire.ReadLoad(ref r);
                _delay = new InputDelayController(Parameters.TickMicroseconds);

                // A rejoin into a running match loads the same match, then waits for the resume snapshot.
                Phase = Lobby?.Phase == LobbyPhase.Running ? ClientPhase.Resuming : ClientPhase.Loading;
                Raise(new LoadEvent(Parameters));
                break;
            case MessageType.Start:
                int delay = Wire.ReadStart(ref r);
                Phase = ClientPhase.Running;
                _nextBatchUs = now + (delay * 1000L);
                Raise(new StartEvent(delay));
                break;
            case MessageType.Closures:
                TickClosure[] closures = Wire.ReadClosures(ref r, out uint batchAck);
                _unacked.RemoveAll(b => b.Seq <= batchAck);
                if (Parameters is not null && Phase is ClientPhase.Running or ClientPhase.Ended)
                {
                    foreach (TickClosure closure in closures)
                    {
                        AcceptClosure(closure, now);
                    }
                }

                break;
            case MessageType.Late:
                Wire.ReadLate(ref r);
                _lateBatches++;
                _delay?.OnLateBatch();
                break;
            case MessageType.Pong:
                _rtt.AddSample(Math.Max(0, now - Wire.ReadValue(ref r)));
                break;
            case MessageType.Status:
                Raise(new StatusEvent(Wire.ReadStatus(ref r)));
                break;
            case MessageType.Desync:
                Raise(new DesyncEvent(Wire.ReadDesync(ref r)));
                break;
            case MessageType.SnapshotRequest:
                (SnapshotPurpose purpose, uint id) = Wire.ReadSnapshotRequest(ref r);
                Raise(new SnapshotRequestEvent(purpose, id));
                break;
            case MessageType.Resume:
                OnResume(Wire.ReadResume(ref r), now);
                break;
            case MessageType.SaveProposal:
                Raise(new SaveProposalEvent(Wire.ReadSaveProposal(ref r)));
                break;
            case MessageType.SaveOutcome:
                Raise(new SaveOutcomeEvent(Wire.ReadSaveOutcome(ref r)));
                break;
            case MessageType.PauseState:
                (bool paused, byte by, byte left) = Wire.ReadPauseState(ref r);
                if (!paused)
                {
                    _clockBaseUs = long.MaxValue; // the relay's deadlines moved by the pause
                }

                Raise(new PauseEvent(paused, by, left));
                break;
            case MessageType.End:
                EndMessage end = Wire.ReadEnd(ref r);
                Phase = ClientPhase.Ended;
                Raise(new EndEvent(end));
                break;
            default:
                throw new InvalidDataException("Unexpected message type.");
        }
    }

    private void OnResume(ResumeMessage resume, long now)
    {
        if (Parameters is null)
        {
            throw new InvalidDataException("Resume before the match parameters.");
        }

        _closed.Clear();
        _unacked.Clear();
        _payloads.Clear();
        _hashes.Clear();
        _batchSeq = 0;
        _clockBaseUs = long.MaxValue;
        _receivedThrough = resume.Tick;
        _delay ??= new InputDelayController(Parameters.TickMicroseconds);
        foreach (TickClosure closure in resume.Backlog)
        {
            AcceptClosure(closure, now);
        }

        _clockBaseUs = long.MaxValue; // backlog arrival times say nothing about the relay clock
        Phase = ClientPhase.Running;
        _nextBatchUs = now;
        Raise(new ResumeEvent(resume.Tick, resume.Hash, resume.Data, resume.Backlog.Length));
    }

    private void OnDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (info.Reason == DisconnectReason.ConnectionRejected && info.AdditionalData is { AvailableBytes: > 0 } data)
        {
            try
            {
                Raise(new RefusedEvent(Wire.ReadRefusal(data.GetRemainingBytesSpan())));
            }
            catch (InvalidDataException)
            {
                Raise(new DisconnectedEvent("Refused by the relay."));
            }
        }
        else if (Phase != ClientPhase.Ended)
        {
            Raise(new DisconnectedEvent(info.Reason.ToString()));
        }

        Phase = ClientPhase.Ended;
        _relay = null;
    }

    private static IPAddress Resolve(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return address;
        }

        return Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new SocketException((int)SocketError.HostNotFound);
    }
}
