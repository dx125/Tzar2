using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LiteNetLib;
using Tzar2.Core.Rng;
using Tzar2.Core.Serialization;
using Tzar2.Net.Lockstep;
using Tzar2.Spikes.S3.Sim;

namespace Tzar2.Spikes.S3.Lockstep;

/// <summary>Lockstep client configuration (bot player + fault injection).</summary>
public sealed class ClientOptions
{
    /// <summary>Relay (or impairment proxy) host.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Relay (or impairment proxy) port.</summary>
    public int Port { get; set; }

    /// <summary>Create a lobby for this many players (0 = join <see cref="Code"/>).</summary>
    public int CreatePlayers { get; set; }

    /// <summary>Lobby code to join.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "bot";

    /// <summary>Bot commands per second (Poisson-ish).</summary>
    public int CommandsPerSecond { get; set; } = 2;

    /// <summary>Corrupt the local state right after executing this tick (-1 = never).</summary>
    public int DesyncAtTick { get; set; } = -1;

    /// <summary>Simulate a crash/network loss right after executing this tick (-1 = never).</summary>
    public int DropAtTick { get; set; } = -1;

    /// <summary>How long to stay away before rejoining.</summary>
    public int DropForMs { get; set; } = 3_000;

    /// <summary>A gap between two executed ticks longer than this many ticks counts as a stall.</summary>
    public int StallThresholdTicks { get; set; } = 3;
}

/// <summary>
/// Lockstep client prototype: tick scheduling with adaptive input delay D, per-tick command batches (empty batches
/// are heartbeats), execution only of ticks the relay closed ("tick T complete" barrier), RTT measurement, state-hash
/// reporting, and rejoin from a peer snapshot. Single-threaded: call <see cref="Run"/> on a dedicated thread.
/// </summary>
public sealed class LockstepClient
{
    private const long PingIntervalUs = 200_000;

    private readonly ClientOptions _options;
    private readonly TextWriter _log;
    private readonly BinaryBufferWriter _w = new(2048);
    private readonly RttEstimator _rtt = new();
    private readonly Dictionary<int, SimCommand[]> _closed = [];
    private readonly Dictionary<uint, long> _issuedAt = [];
    private readonly List<SimCommand> _outbox = [];
    private readonly List<TickHash> _hashOutbox = [];
    private readonly List<double> _latenciesMs = [];
    private readonly List<(uint Seq, byte[] Body)> _unacked = [];
    private readonly ClientReport _report = new();

    private NetManager? _net;
    private NetPeer? _relay;
    private Welcome? _welcome;
    private ToySim? _sim;
    private InputDelayController? _delay;
    private Xoshiro256StarStar _bot;
    private bool _started;
    private bool _awaitingResume;
    private int _endTick = int.MaxValue;
    private uint _batchSeq;
    private uint _nextCommandId = 1;
    private long _nextBatchUs;
    private long _nextPingUs;
    private long _lastBotUs;
    private long _lastExecUs;
    private long _firstExecUs;
    private long _clockBaseUs = long.MaxValue;
    private long _delaySum;
    private long _rejoinStartUs;
    private int _rejoinCatchUpTick = -1;
    private bool _dropped;
    private string? _error;

    /// <summary>Creates a client.</summary>
    public LockstepClient(ClientOptions options, TextWriter log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _report.DelayMin = int.MaxValue;
        _report.Platform = $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}";
    }

    /// <summary>Raised once with the lobby code when this client created a lobby.</summary>
    public event Action<string>? LobbyCreated;

    /// <summary>Connects, plays the match to its end and returns the measurements.</summary>
    public ClientReport Run(CancellationToken cancellation)
    {
        Connect(_options.CreatePlayers > 0
            ? new Hello(HelloKind.Create, string.Empty, (byte)_options.CreatePlayers, 0, 0, _options.Name)
            : new Hello(HelloKind.Join, _options.Code, 0, 0, 0, _options.Name));

        long deadline = Clock.NowMicroseconds() + 15_000_000;
        while (!cancellation.IsCancellationRequested && _error is null)
        {
            long now = Clock.NowMicroseconds();
            if (_welcome is null && now > deadline)
            {
                throw new TimeoutException("No welcome from the relay.");
            }

            _net!.PollEvents();
            if (_started && !_awaitingResume)
            {
                Step(now);
            }
            else if (_welcome is not null && !_awaitingResume)
            {
                // Measure RTT during loading already, so D is right from the first tick.
                Ping(now);
            }

            if (_sim is not null && _sim.NextTick > _endTick)
            {
                break;
            }

            Thread.Sleep(1);
        }

        if (_error is not null)
        {
            throw new InvalidOperationException(_error);
        }

        Finish();
        _net!.Stop(true);
        return _report;
    }

    private void Connect(Hello hello)
    {
        var listener = new EventBasedNetListener();
        listener.NetworkReceiveEvent += OnReceive;
        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (!_dropped && _sim is not null && _sim.NextTick <= _endTick)
            {
                _error = $"Disconnected by relay: {info.Reason}";
            }
        };
        _net = new NetManager(listener) { AutoRecycle = true, UpdateTime = 1, EnableStatistics = true, DisconnectTimeout = 10_000 };
        _net.Start();
        _w.Clear();
        Wire.Write(_w, hello);
        _relay = _net.Connect(new System.Net.IPEndPoint(ResolveHost(_options.Host), _options.Port), _w.WrittenSpan);
    }

    private static System.Net.IPAddress ResolveHost(string host) =>
        System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address)
            ? address
            : System.Net.Dns.GetHostAddresses(host).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

    private void Step(long now)
    {
        GenerateBotInput(now);
        if (now >= _nextBatchUs)
        {
            SendBatch(now);
            _nextBatchUs = Math.Max(_nextBatchUs + _welcome!.TickMicroseconds, now + 1_000);
        }

        Ping(now);
        ExecuteClosedTicks();
    }

    private void Ping(long now)
    {
        if (now >= _nextPingUs)
        {
            _w.Clear();
            Wire.WriteValue(_w, MessageType.Ping, now);
            _relay!.Send(_w.WrittenSpan, DeliveryMethod.Unreliable);
            _nextPingUs = now + PingIntervalUs;
        }
    }

    private void GenerateBotInput(long now)
    {
        long elapsed = Math.Min(now - _lastBotUs, 100_000);
        _lastBotUs = now;
        if (_bot.NextInt(1_000_000) >= elapsed * _options.CommandsPerSecond)
        {
            return;
        }

        bool scatter = _bot.NextInt(8) == 0;
        ushort first = (ushort)_bot.NextInt(ToySim.UnitsPerPlayer);
        var command = new SimCommand(
            _welcome!.Slot,
            scatter ? SimCommandKind.Scatter : SimCommandKind.Move,
            first,
            (ushort)_bot.NextInt(1, ToySim.UnitsPerPlayer - first + 1),
            _bot.NextInt(ToySim.WorldSize),
            _bot.NextInt(ToySim.WorldSize),
            _nextCommandId++);
        if (_outbox.Count < Wire.MaxCommandsPerBatch)
        {
            _outbox.Add(command);
            _issuedAt[command.Id] = now;
            _report.CommandsIssued++;
        }
    }

    private void SendBatch(long now)
    {
        if (_rtt.SampleCount > 0)
        {
            _delay!.Update(_rtt);
        }

        int d = _delay!.Delay;
        _delaySum += d;
        _report.DelayMin = Math.Min(_report.DelayMin, d);
        _report.DelayMax = Math.Max(_report.DelayMax, d);
        int target = EstimateRelayOpenTick(now) + d;
        var batch = new Batch(++_batchSeq, target, _sim!.NextTick - 1, _outbox.ToArray(), _hashOutbox.ToArray());
        _outbox.Clear();
        _hashOutbox.Clear();
        _report.BatchesSent++;
        _w.Clear();
        if (_welcome!.Delivery == ClosureDelivery.Reliable)
        {
            Wire.WriteBatchesHeader(_w, 1);
            Wire.WriteBatch(_w, batch);
            _relay!.Send(_w.WrittenSpan, DeliveryMethod.ReliableOrdered);
            return;
        }

        // Redundant upstream: repeat every unacknowledged batch (oldest first) in one unreliable datagram.
        var body = new BinaryBufferWriter(64);
        Wire.WriteBatch(body, batch);
        _unacked.Add((batch.Seq, body.ToArray()));
        int limit = _relay!.GetMaxSinglePacketSize(DeliveryMethod.Unreliable) - 16;
        int count = 0;
        int size = 0;
        foreach ((uint _, byte[] bytes) in _unacked)
        {
            if (count == Wire.MaxBatchesPerMessage || (count > 0 && size + bytes.Length > limit))
            {
                break;
            }

            count++;
            size += bytes.Length;
        }

        Wire.WriteBatchesHeader(_w, count);
        for (int i = 0; i < count; i++)
        {
            _w.WriteBytes(_unacked[i].Body);
        }

        _relay.Send(_w.WrittenSpan, _w.Length <= limit + 16 ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// The tick the relay is closing now, seen from this client: the earliest-arriving closure fixes the relay clock
    /// offset plus the minimum one-way delay; add half the smoothed RTT for the trip itself.
    /// </summary>
    private int EstimateRelayOpenTick(long now)
    {
        if (_clockBaseUs == long.MaxValue)
        {
            return 0;
        }

        long relayElapsed = now - _clockBaseUs + (_rtt.SmoothedMicroseconds / 2);
        return (int)Math.Max(0, relayElapsed / _welcome!.TickMicroseconds);
    }

    private void ExecuteClosedTicks()
    {
        while (_sim!.NextTick <= _endTick && _closed.Remove(_sim.NextTick, out SimCommand[]? commands))
        {
            int tick = _sim.NextTick;
            _sim.Tick(commands);
            if (tick == _options.DesyncAtTick)
            {
                _sim.ForceDesync();
                Log($"FORCED DESYNC after tick {tick}");
            }

            long now = Clock.NowMicroseconds();
            if (tick % _welcome!.HashInterval == 0)
            {
                _hashOutbox.Add(new TickHash(tick, _sim.StateHash()));
            }

            foreach (SimCommand command in commands)
            {
                if (command.Player == _welcome.Slot && _issuedAt.Remove(command.Id, out long issued))
                {
                    _latenciesMs.Add((now - issued) / 1000.0);
                    _report.CommandsExecuted++;
                }
            }

            RecordExecution(now, tick);
            if (tick == _options.DropAtTick && !_dropped)
            {
                Drop();
                return;
            }
        }
    }

    private void RecordExecution(long now, int tick)
    {
        if (_rejoinCatchUpTick >= 0)
        {
            // Fast-forward after a rejoin is not a stall; the rejoin time is reported separately.
            if (tick >= _rejoinCatchUpTick)
            {
                _report.RejoinMs = (now - _rejoinStartUs) / 1000.0;
                _rejoinCatchUpTick = -1;
                Log($"CAUGHT UP at tick {tick}, rejoin took {_report.RejoinMs:F1} ms");
            }

            _lastExecUs = now;
            return;
        }

        if (_firstExecUs == 0)
        {
            _firstExecUs = now;
        }
        else
        {
            double gapMs = (now - _lastExecUs) / 1000.0;
            double tickMs = _welcome!.TickMicroseconds / 1000.0;
            _report.MaxGapMs = Math.Max(_report.MaxGapMs, gapMs);
            if (gapMs > _options.StallThresholdTicks * tickMs)
            {
                _report.Stalls++;
                _report.StallMs += gapMs - tickMs;
                if (_report.StallLog.Count < 50)
                {
                    _report.StallLog.Add(FormattableString.Invariant($"tick {tick}: {gapMs:F0} ms"));
                }
            }
        }

        _lastExecUs = now;
        _report.TicksExecuted++;
    }

    private void Drop()
    {
        Log($"DROP after tick {_sim!.NextTick - 1}: stopping the socket for {_options.DropForMs} ms");
        _dropped = true;
        AddTransportStatistics();
        _net!.Stop(false);
        Thread.Sleep(_options.DropForMs);
        _closed.Clear();
        _outbox.Clear();
        _hashOutbox.Clear();
        _issuedAt.Clear();
        _unacked.Clear();
        _awaitingResume = true;
        _rejoinStartUs = Clock.NowMicroseconds();
        _clockBaseUs = long.MaxValue;
        Connect(new Hello(HelloKind.Rejoin, _welcome!.Code, 0, _welcome.Slot, _welcome.Token, _options.Name));
        _dropped = false;
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            var r = new BinaryBufferReader(reader.GetRemainingBytesSpan());
            Handle(ref r);
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException or InvalidOperationException)
        {
            _error = $"Bad message from relay: {e.Message}";
        }
    }

    private void Handle(ref BinaryBufferReader r)
    {
        long now = Clock.NowMicroseconds();
        switch (Wire.ReadType(ref r))
        {
            case MessageType.Welcome:
                OnWelcome(Wire.ReadWelcome(ref r));
                break;

            case MessageType.Start:
                _started = true;
                _nextBatchUs = now;
                _lastBotUs = now;
                Log($"START");
                break;

            case MessageType.Closures:
                TickClosure[] closures = Wire.ReadClosures(ref r, out uint batchAck);
                _unacked.RemoveAll(b => b.Seq <= batchAck);
                foreach (TickClosure closure in closures)
                {
                    AcceptClosure(closure, now);
                }

                break;

            case MessageType.Pong:
                _rtt.AddSample(Math.Max(0, now - r.ReadVarInt64()));
                break;

            case MessageType.Late:
                r.ReadVarUInt64();
                r.ReadVarInt64();
                r.ReadVarInt64();
                _report.LateBatches++;
                _delay?.OnLateBatch();
                break;

            case MessageType.Desync:
                DesyncReport desync = Wire.ReadDesync(ref r);
                _report.DesyncTicks.Add(desync.Tick);
                Log($"DESYNC reported at tick {desync.Tick}, odd slots [{string.Join(',', desync.OddSlots)}]");
                break;

            case MessageType.SnapshotRequest:
                SendSnapshot();
                break;

            case MessageType.Resume:
                OnResume(Wire.ReadSnapshot(ref r), now);
                break;

            case MessageType.End:
                _endTick = (int)r.ReadVarInt64();
                Log($"END after tick {_endTick}");
                if (_endTick < 0)
                {
                    _error = "Match aborted by the relay.";
                }

                break;

            default:
                throw new InvalidDataException("Unexpected message type.");
        }
    }

    private void OnWelcome(Welcome welcome)
    {
        _welcome = welcome;
        _report.Slot = welcome.Slot;
        _bot = RngStreams.Derive(welcome.Seed ^ 0xB07, RngStreamKind.Misc, welcome.Slot); // presentation-side RNG, not a sim stream
        _sim = new ToySim(welcome.Seed, welcome.PlayerCount);
        _delay = new InputDelayController(welcome.TickMicroseconds);
        _endTick = welcome.EndTick;
        Log($"WELCOME lobby {welcome.Code} slot {welcome.Slot}/{welcome.PlayerCount} delivery={welcome.Delivery}");
        if (_options.CreatePlayers > 0)
        {
            LobbyCreated?.Invoke(welcome.Code);
        }

        _w.Clear();
        Wire.WriteReady(_w, _sim.StateHash());
        _relay!.Send(_w.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void AcceptClosure(TickClosure closure, long now)
    {
        if (closure.Tick < _sim!.NextTick)
        {
            return; // duplicate from a redundant packet
        }

        _closed.TryAdd(closure.Tick, closure.Commands);

        // Relay clock: closure T is sent at relayStart + (T + 1) * tick; keep the earliest arrival seen.
        long baseUs = now - ((long)(closure.Tick + 1) * _welcome!.TickMicroseconds);
        _clockBaseUs = Math.Min(_clockBaseUs, baseUs);
    }

    private void SendSnapshot()
    {
        var data = new BinaryBufferWriter(32 * 1024);
        _sim!.WriteSnapshot(data);
        var snapshot = new SnapshotMessage(_sim.NextTick - 1, _sim.StateHash(), data.ToArray(), []);
        _w.Clear();
        Wire.Write(_w, MessageType.Snapshot, snapshot);
        _relay!.Send(_w.WrittenSpan, DeliveryMethod.ReliableOrdered);
        Log($"SNAPSHOT sent at tick {snapshot.Tick} ({snapshot.Data.Length} B)");
    }

    private void OnResume(SnapshotMessage resume, long now)
    {
        _sim = ToySim.ReadSnapshot(resume.Data);
        _report.RejoinSnapshotVerified = _sim.StateHash() == resume.Hash && _sim.NextTick == resume.Tick + 1;
        if (!_report.RejoinSnapshotVerified)
        {
            _error = "Rejoin snapshot does not match its hash.";
            return;
        }

        foreach (TickClosure closure in resume.Backlog)
        {
            AcceptClosure(closure, now);
        }

        _report.RejoinBacklogTicks = resume.Backlog.Length;
        _rejoinCatchUpTick = resume.Backlog.Length > 0 ? resume.Backlog[^1].Tick : resume.Tick;
        _awaitingResume = false;
        _nextBatchUs = now;
        Log($"RESUME from snapshot tick {resume.Tick} (verified), fast-forwarding {resume.Backlog.Length} ticks");
        ExecuteClosedTicks();
    }

    private void Finish()
    {
        double[] sorted = _latenciesMs.Order().ToArray();
        _report.LatencyMeanMs = sorted.Length > 0 ? sorted.Average() : 0;
        _report.LatencyP50Ms = Percentile(sorted, 0.50);
        _report.LatencyP95Ms = Percentile(sorted, 0.95);
        _report.LatencyMaxMs = sorted.Length > 0 ? sorted[^1] : 0;
        _report.DurationMs = (_lastExecUs - _firstExecUs) / 1000.0;
        _report.TickRate = _report.DurationMs > 0 ? (_report.TicksExecuted - 1) * 1000.0 / _report.DurationMs : 0;
        _report.DelayMean = _report.BatchesSent > 0 ? (double)_delaySum / _report.BatchesSent : 0;
        _report.DelayMin = _report.DelayMin == int.MaxValue ? 0 : _report.DelayMin;
        _report.SrttMs = _rtt.SmoothedMicroseconds / 1000.0;
        _report.RttVarMs = _rtt.VariationMicroseconds / 1000.0;
        _report.FinalTick = _sim!.NextTick - 1;
        _report.FinalHash = _sim.StateHash().ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        AddTransportStatistics();
    }

    private void AddTransportStatistics()
    {
        _report.BytesSent += _net!.Statistics.BytesSent;
        _report.BytesReceived += _net.Statistics.BytesReceived;
        _report.PacketsSent += _net.Statistics.PacketsSent;
        _report.PacketsReceived += _net.Statistics.PacketsReceived;
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];

    private void Log(FormattableString message)
    {
        _log.WriteLine(FormattableString.Invariant(message));
        _log.Flush();
    }
}
