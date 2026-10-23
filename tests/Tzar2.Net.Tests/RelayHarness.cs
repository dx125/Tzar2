using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Tzar2.Core.Hashing;
using Tzar2.Net.Lockstep;
using Tzar2.Net.Protocol;
using Tzar2.Net.Relay;

namespace Tzar2.Net.Tests;

/// <summary>A relay on a loopback port and its members, all pumped from the test thread.</summary>
internal sealed class RelayHarness : IDisposable
{
    public const string Version = "test-1";

    // Issue #179: a 1 s relay and a 2 s client timeout dropped members on loaded Windows runners (several CI jobs at
    // once) before a test got going. The defaults are generous; the keep-alive test sets its own, tighter ones.
    public RelayHarness(Action<RelayOptions>? configure = null, int clientTimeoutMs = 8_000)
    {
        ClientTimeoutMs = clientTimeoutMs;
        Options = new RelayOptions { StartDelayMs = 50, DisconnectTimeoutMs = 5_000, DesyncCollectMs = 3_000, Log = e => LogLines.Add(e) };
        configure?.Invoke(Options);
        Relay = new RelayServer(Options);
        Relay.Start();
    }

    public RelayOptions Options { get; }

    /// <summary>The clients' disconnect timeout.</summary>
    public int ClientTimeoutMs { get; }

    public RelayServer Relay { get; }

    public List<RelayLogEntry> LogLines { get; } = [];

    public List<TestPeer> Peers { get; } = [];

    public TestPeer Create(string name = "host", byte slots = 2, bool isPublic = true)
    {
        var peer = new TestPeer(LockstepClient.Create(ClientOptions(name), new LobbyCreate("Test lobby", slots, isPublic)));
        Peers.Add(peer);
        return peer;
    }

    public TestPeer Join(string code, string name = "guest", string version = Version)
    {
        var peer = new TestPeer(LockstepClient.Join(ClientOptions(name, version), code));
        Peers.Add(peer);
        return peer;
    }

    public TestPeer Rejoin(string code, byte slot, ulong token)
    {
        var peer = new TestPeer(LockstepClient.Rejoin(ClientOptions("again"), code, slot, token));
        Peers.Add(peer);
        return peer;
    }

    public LockstepClientOptions ClientOptions(string name, string version = Version) =>
        new() { Host = "127.0.0.1", Port = Relay.Port, Name = name, GameVersion = version, DisconnectTimeoutMs = ClientTimeoutMs };

    /// <summary>Pumps the relay and every peer until <paramref name="done"/> holds.</summary>
    /// <exception cref="TimeoutException">It did not within <paramref name="timeoutMs"/>.</exception>
    public void Pump(Func<bool> done, int timeoutMs = 10_000, string? what = null)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Timed out waiting for {what ?? "the condition"}; relay log:\n"
                    + string.Join('\n', LogLines.TakeLast(30).Select(l => $"{l.Event} {l.Lobby} {l.Slot} {l.Detail}")));
            }

            Step();
        }
    }

    /// <summary>Pumps for <paramref name="ms"/> milliseconds.</summary>
    public void PumpFor(int ms)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ms)
        {
            Step();
        }
    }

    public void Step()
    {
        Relay.Update();
        foreach (TestPeer peer in Peers)
        {
            peer.Pump();
        }

        Thread.Sleep(1);
    }

    /// <summary>Creates a lobby with <paramref name="guests"/> joined, starts it and waits for the first closures.</summary>
    public (TestPeer Host, TestPeer[] Guests) Running(int guests = 1, MatchParameters? parameters = null)
    {
        TestPeer host = Create(slots: (byte)(guests + 1));
        Pump(() => host.Welcome is not null, what: "welcome");
        TestPeer[] joined = Enumerable.Range(0, guests).Select(i => Join(host.Welcome!.Code, "guest" + i)).ToArray();
        Pump(() => joined.All(g => g.Welcome is not null), what: "joins");
        foreach (TestPeer g in joined)
        {
            g.Client.SetReady(true);
        }

        Pump(() => host.Lobby is { } l && l.Slots.Count(s => s.Ready) == guests + 1, what: "ready");
        host.Client.RequestStart(parameters ?? Parameters());
        Pump(() => Peers.All(p => p.Load is not null), what: "load");
        foreach (TestPeer p in Peers)
        {
            p.Client.Loaded([1, 2, 3], 42);
        }

        Pump(() => Peers.All(p => p.Executed >= 5), what: "first ticks");
        return (host, joined);
    }

    public static MatchParameters Parameters(int tickUs = 20_000, int dropMs = 30_000, DropPolicy policy = DropPolicy.AiTakeover) =>
        new(0, tickUs, 10, dropMs, policy, 2);

    public void Dispose()
    {
        foreach (TestPeer peer in Peers)
        {
            peer.Client.Dispose();
        }

        Relay.Dispose();
    }
}

/// <summary>A member that executes closures like a sim: its "state" is a hash over every executed entry.</summary>
internal sealed class TestPeer(LockstepClient client)
{
    private readonly StateHasher _hasher = new();
    private ulong _state;

    public LockstepClient Client { get; } = client;

    public List<ClientEvent> Events { get; } = [];

    public List<TickClosure> Closures { get; } = [];

    public Welcome? Welcome { get; private set; }

    public LobbyState? Lobby { get; private set; }

    public MatchParameters? Load { get; private set; }

    public int Executed { get; private set; } = -1;

    public ulong Hash => _state;

    /// <summary>Added to the hash after <see cref="CorruptAtTick"/> (forced desync).</summary>
    public int CorruptAtTick { get; set; } = -1;

    /// <summary>Answers snapshot requests with this (null: answer with the hash as data).</summary>
    public bool AnswerSnapshots { get; set; } = true;

    public bool Paused { get; set; }

    public void Pump()
    {
        Client.Poll();
        while (Client.TryDequeue(out ClientEvent? e))
        {
            Events.Add(e!);
            switch (e)
            {
                case WelcomeEvent w:
                    Welcome = w.Welcome;
                    break;
                case LobbyEvent l:
                    Lobby = l.Lobby;
                    break;
                case LoadEvent l:
                    Load = l.Parameters;
                    break;
                case SnapshotRequestEvent s when AnswerSnapshots:
                    byte[] data = BitConverter.GetBytes(Hash);
                    Client.SendSnapshot(new SnapshotMessage(s.Purpose, s.RequestId, Executed, Hash, data, s.Purpose == SnapshotPurpose.Desync ? 9 : -1, s.Purpose == SnapshotPurpose.Desync ? data : []));
                    break;
                case ResumeEvent r:
                    _state = BitConverter.ToUInt64(r.Snapshot);
                    Executed = r.Tick;
                    break;
            }
        }

        if (Paused)
        {
            return;
        }

        while (Client.TryTakeClosure(Executed + 1, out TickClosure? closure))
        {
            Executed++;
            Closures.Add(closure!);
            _hasher.Reset();
            _hasher.Add(_state).Add(closure!.Tick);
            foreach (ClosureEntry entry in closure.Entries)
            {
                _hasher.Add(entry.Slot).Add(entry.Payload);
            }

            foreach (ControlEvent c in closure.Controls)
            {
                _hasher.Add((int)c.Kind).Add(c.Slot).Add(c.Arg);
            }

            if (Executed == CorruptAtTick)
            {
                _hasher.Add(0xBAD);
            }

            _state = _hasher.GetCurrentHash();

            if ((Executed + 1) % (Load?.HashInterval ?? 10) == 0)
            {
                Client.ReportHash(Executed, Hash);
            }
        }
    }

    public T? Last<T>()
        where T : ClientEvent => Events.OfType<T>().LastOrDefault();
}
