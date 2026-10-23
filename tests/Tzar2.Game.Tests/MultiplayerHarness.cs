using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;
using Tzar2.Net.Relay;

namespace Tzar2.Game.Tests;

/// <summary>An in-process relay on loopback and network matches pumped from the test thread.</summary>
internal sealed class MultiplayerHarness : IDisposable
{
    public MultiplayerHarness(string? saveDirectory = null)
    {
        SaveDirectory = saveDirectory;
        Relay = new RelayServer(new RelayOptions { StartDelayMs = 50, DisconnectTimeoutMs = 1_000, DesyncCollectMs = 5_000, DesyncSink = Sink });
        Relay.Start();
    }

    public RelayServer Relay { get; }

    public ListSink Sink { get; } = new();

    public string? SaveDirectory { get; }

    public List<NetworkMatch> Matches { get; } = [];

    /// <summary>The computer players' scripts every peer uses.</summary>
    public SkirmishAi Ai { get; init; } = SkirmishAi.EngineDefault;

    /// <summary>The custom maps every peer has (unless <see cref="Options"/> is given others).</summary>
    public IReadOnlyList<Campaigns.CustomMapEntry> CustomMaps { get; init; } = [];

    /// <summary>When false, the relay is not updated (no new ticks close; peers converge on the last closed tick).</summary>
    public bool RelayRunning { get; set; } = true;

    public NetworkMatchOptions Options(string name, string? saveDirectory = null, IReadOnlyList<Campaigns.CustomMapEntry>? maps = null) => new()
    {
        Host = "127.0.0.1",
        Port = Relay.Port,
        PlayerName = name,
        Ruleset = HumanVsAiTests.SyntheticRules,
        Ai = Ai,
        CustomMaps = maps ?? CustomMaps,
        GameVersion = "test",
        SaveDirectory = saveDirectory ?? SaveDirectory,
        AutoAcceptSaves = true,
        Fog = false,
    };

    public NetworkMatch Host(int slots = 3, string name = "host")
    {
        NetworkMatch m = NetworkMatch.Create(Options(name), "test", slots, isPublic: true);
        Matches.Add(m);
        Pump(() => m.Lobby is not null, what: "lobby");
        return m;
    }

    public NetworkMatch Join(string code, string name = "guest", string? saveDirectory = null)
    {
        NetworkMatch m = NetworkMatch.Join(Options(name, saveDirectory), code);
        Matches.Add(m);
        return m;
    }

    public NetworkMatch Rejoin(ReconnectTicket ticket, string? saveDirectory = null)
    {
        NetworkMatch m = NetworkMatch.Rejoin(Options("again", saveDirectory), ticket);
        Matches.Add(m);
        return m;
    }

    public static MatchSettings Settings(ulong seed = 21) => new() { Map = MapSourceKind.TestMap, TestMapSize = 80, Seed = seed };

    /// <summary>A host, <paramref name="guests"/> players and computers in the remaining slots, started and running.</summary>
    public (NetworkMatch Host, NetworkMatch[] Guests) Start(int guests = 1, int computers = 1, MatchRules? rules = null, MatchSettings? settings = null)
    {
        NetworkMatch host = Host(slots: 1 + guests + computers);
        host.SetSettings(settings ?? Settings());
        for (int i = 0; i < computers; i++)
        {
            host.SetSlot((byte)(1 + guests + i), SlotKind.Computer, new SlotChoice("euro", 0, Difficulty.Hard));
        }

        NetworkMatch[] joined = Enumerable.Range(0, guests).Select(i => Join(host.Code!, "guest" + i)).ToArray();
        Pump(() => host.Lobby!.Slots.Count(s => s.Kind == SlotKind.Human) == guests + 1 && host.Lobby.Settings.TestMapSize == 80, what: "joins");
        foreach (NetworkMatch g in joined)
        {
            g.SetReady(true);
        }

        Pump(() => host.Lobby!.Slots.All(s => s.Kind != SlotKind.Human || s.Ready), what: "ready");
        host.Start(rules ?? new MatchRules());
        Pump(() => Matches.All(m => m.Session is { } s && s.Simulation.World.Tick.Value > 5), timeoutMs: 30_000, what: "first ticks");
        return (host, joined);
    }

    /// <summary>Pumps until <paramref name="done"/> holds.</summary>
    public void Pump(Func<bool> done, int timeoutMs = 20_000, string? what = null)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Timed out waiting for {what ?? "the condition"} (stages {string.Join(", ", Matches.Select(m => $"{m.Stage}/{m.Session?.Simulation.World.Tick.Value}/{m.EndReason}"))}).");
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
        if (RelayRunning)
        {
            Relay.Update();
        }

        foreach (NetworkMatch m in Matches)
        {
            m.Poll();
        }

        Thread.Sleep(1);
    }

    /// <summary>Stops closing ticks and pumps until every running peer executed every closed tick; returns that tick.</summary>
    public int Settle(IEnumerable<NetworkMatch> peers)
    {
        NetworkMatch[] list = peers.ToArray();
        RelayRunning = false;
        int last = -1;
        int stable = 0;
        var watch = Stopwatch.StartNew();
        while (stable < 200 && watch.ElapsedMilliseconds < 20_000)
        {
            Step();
            int[] ticks = list.Select(m => m.Session!.Simulation.World.Tick.Value).ToArray();
            stable = ticks.All(t => t == ticks[0]) && ticks[0] == last ? stable + 1 : 0;
            last = ticks[0];
        }

        return last;
    }

    public void Dispose()
    {
        foreach (NetworkMatch m in Matches)
        {
            m.Dispose();
        }

        Relay.Dispose();
    }

    internal sealed class ListSink : IDesyncSink
    {
        public List<DesyncBundle> Bundles { get; } = [];

        public void Store(DesyncBundle bundle) => Bundles.Add(bundle);
    }
}
