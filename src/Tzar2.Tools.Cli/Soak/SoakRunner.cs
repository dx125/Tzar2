using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Tzar2.Content.Compiled;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;
using Tzar2.Net.Relay;
using Tzar2.Replay;

namespace Tzar2.Tools.Cli.Soak;

/// <summary>Settings of a soak run (ADR 0167; TDD §22.7: headless bot clients through the relay with impaired UDP).</summary>
internal sealed class SoakOptions
{
    public int Matches { get; set; } = 10;

    public int Parallel { get; set; } = 2;

    public int Humans { get; set; } = 2;

    public int Computers { get; set; } = 2;

    /// <summary>Sim ticks per match (6,000 = 5 minutes of game time).</summary>
    public int Ticks { get; set; } = 6_000;

    /// <summary>Game speed in percent (the relay's tick rate; 400 = 80 ticks/s).</summary>
    public int Speed { get; set; } = 400;

    public int RttMs { get; set; } = 200;

    /// <summary>Jitter per direction in ms (default: 5 % of the RTT, as in ADR 0009).</summary>
    public int? JitterMs { get; set; }

    public double LossPercent { get; set; } = 5;

    /// <summary>Probability that a match has a random disconnect and reconnect of one player.</summary>
    public double Disconnects { get; set; } = 0.5;

    /// <summary>Drop timeout of the matches (short, so some reconnects come after an AI takeover).</summary>
    public int DropTimeoutSeconds { get; set; } = 3;

    public ulong Seed { get; set; } = 1;

    /// <summary>Test map side, or 0 for random maps (small, varying type).</summary>
    public int TestMapSize { get; set; } = 96;

    /// <summary>Re-simulate every recorded replay headlessly after the match.</summary>
    public bool VerifyReplays { get; set; } = true;

    /// <summary>Where replays are written (null: not kept).</summary>
    public string? ReplayDirectory { get; set; }

    /// <summary>Wall-clock limit per match.</summary>
    public int MatchTimeoutSeconds { get; set; } = 900;

    public required Ruleset Ruleset { get; init; }

    public SkirmishAi Ai { get; init; } = SkirmishAi.EngineDefault;
}

/// <summary>The outcome of one soak match.</summary>
internal sealed record SoakMatchResult
{
    public int Index { get; init; }

    public ulong Seed { get; init; }

    public string Map { get; init; } = string.Empty;

    public bool Completed { get; set; }

    public string? Failure { get; set; }

    public int Ticks { get; set; }

    public bool HashesAgree { get; set; }

    public bool Desync { get; set; }

    public int Disconnects { get; set; }

    public int Rejoins { get; set; }

    public bool TakenOver { get; set; }

    public int ReplaysVerified { get; set; }

    public int ReplayFailures { get; set; }

    public int Orders { get; set; }

    public long LateBatches { get; set; }

    public double MeanInputDelay { get; set; }

    public double UpKBps { get; set; }

    public double DownKBps { get; set; }

    public double Seconds { get; set; }

    public bool Ok => Completed && HashesAgree && !Desync && ReplayFailures == 0 && Failure is null;
}

/// <summary>
/// The multiplayer soak (ADR 0167, TDD §22.7, M6 exit criterion): N headless matches through one real relay over
/// loopback, every human player behind its own UDP impairment proxy (delay, jitter, loss), bots giving orders, computer
/// kingdoms on the Hard AI, and in some matches a player who disconnects and reconnects (sometimes after the drop
/// timeout, so the computer took the kingdom over meanwhile). Each match ends with a pause; then every peer must have
/// the identical full-state hash, the relay must not have seen a desync, and every recorded replay must re-simulate.
/// </summary>
internal sealed class SoakRunner(SoakOptions options, TextWriter log)
{
    private readonly object _logGate = new();

    public IReadOnlyList<SoakMatchResult> Run(CancellationToken cancellation)
    {
        using var relay = new RelayServer(new RelayOptions { MaxLobbiesPerAddress = 1024, ConnectsPerSecond = 1000, ConnectBurst = 2000, DisconnectTimeoutMs = 2_000, StartDelayMs = 500 });
        relay.Start();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var relayThread = new Thread(() => relay.Run(stop.Token)) { IsBackground = true, Name = "soak relay" };
        relayThread.Start();
        var results = new ConcurrentBag<SoakMatchResult>();
        int next = -1;
        var workers = Enumerable.Range(0, Math.Max(1, options.Parallel)).Select(_ => new Thread(() =>
        {
            int index;
            while ((index = Interlocked.Increment(ref next)) < options.Matches && !stop.IsCancellationRequested)
            {
                SoakMatchResult result = RunMatch(index, relay.Port, stop.Token);
                results.Add(result);
                Log(Line(result));
            }
        }) { IsBackground = true, Name = "soak match" }).ToList();
        workers.ForEach(t => t.Start());
        workers.ForEach(t => t.Join());
        stop.Cancel();
        relayThread.Join(TimeSpan.FromSeconds(5));
        Log(FormattableString.Invariant($"relay: {relay.Metrics.MatchesStarted} matches, {relay.Metrics.TicksClosed} ticks, {relay.Metrics.Desyncs} desyncs, {relay.Metrics.Rejoins} rejoins, {relay.Metrics.Takeovers} takeovers, {relay.Metrics.LateBatches} late batches, {relay.Metrics.Malformed} malformed"));
        return [.. results.OrderBy(r => r.Index)];
    }

    private SoakMatchResult RunMatch(int index, int relayPort, CancellationToken cancellation)
    {
        ulong seed = options.Seed + (ulong)index * 7919UL;
        var random = new Random(unchecked((int)seed));
        MatchSettings settings = Settings(seed, index);
        var result = new SoakMatchResult { Index = index, Seed = seed, Map = settings.Map == MapSourceKind.TestMap ? $"test {settings.TestMapSize}" : $"{settings.MapType} {settings.MapSize}" };
        var watch = Stopwatch.StartNew();
        var proxies = new List<UdpImpairmentProxy>();
        var peers = new List<Peer>();
        try
        {
            var relay = new IPEndPoint(IPAddress.Loopback, relayPort);
            int jitter = options.JitterMs ?? options.RttMs / 20;
            for (int h = 0; h < options.Humans; h++)
            {
                proxies.Add(new UdpImpairmentProxy(relay, new Impairment(options.RttMs / 2, jitter, options.LossPercent), random.Next()));
            }

            Peer host = new(NetworkMatch.Create(Options(proxies[0].Port, "bot0"), FormattableString.Invariant($"soak {index}"), options.Humans + options.Computers, isPublic: false), new SoakBot(random.Next(), 40), 0);
            peers.Add(host);
            Pump(peers, () => host.Match.Lobby is not null, 20_000, "lobby");
            host.Match.SetSettings(settings);
            for (int c = 0; c < options.Computers; c++)
            {
                host.Match.SetSlot((byte)(options.Humans + c), SlotKind.Computer, new SlotChoice("euro", 0, Difficulty.Hard));
            }

            for (int h = 1; h < options.Humans; h++)
            {
                peers.Add(new Peer(NetworkMatch.Join(Options(proxies[h].Port, $"bot{h}"), host.Match.Code!), new SoakBot(random.Next(), 40), h));
            }

            Pump(peers, () => host.Match.Lobby!.Slots.Count(s => s.Kind == SlotKind.Human) == options.Humans && host.Match.Lobby.Settings.Seed == seed, 30_000, "joins");
            foreach (Peer p in peers.Skip(1))
            {
                p.Match.SetReady(true);
            }

            Pump(peers, () => host.Match.Lobby!.Slots.All(s => s.Kind != SlotKind.Human || s.Ready), 30_000, "ready");
            host.Match.Start(new MatchRules(DropRule.AiTakeover, options.DropTimeoutSeconds, PausesPerPlayer: 3));
            Pump(peers, () => peers.All(p => p.Match.Session is not null && p.Match.Stage == LobbyStage.Running), 60_000, "start");

            // One player may crash at a random tick and come back after 1-2x the drop timeout.
            int dropAt = random.NextDouble() < options.Disconnects ? random.Next(options.Ticks / 5, options.Ticks * 3 / 5) : -1;
            int dropWho = random.Next(options.Humans);
            int awayMs = random.Next(500, options.DropTimeoutSeconds * 2_000 + 1);
            long rejoinAtMs = -1;
            ReconnectTicket? ticket = null;
            var delays = new List<int>();
            var deadline = Stopwatch.StartNew();
            while (!cancellation.IsCancellationRequested)
            {
                if (deadline.Elapsed.TotalSeconds > options.MatchTimeoutSeconds)
                {
                    throw new TimeoutException("match timeout");
                }

                foreach (Peer p in peers)
                {
                    p.Match.Poll();
                    if (p.Match.Session is { } s && p.Match.Stage == LobbyStage.Running)
                    {
                        p.Bot.Act(s);
                    }

                    if (p.Match.Desynced)
                    {
                        result.Desync = true;
                    }

                    result.TakenOver |= p.Match.Players.Any(v => v.Control == PlayerControl.Computer && v.Slot < options.Humans);
                }

                Peer? dropping = dropAt >= 0 && ticket is null ? peers.FirstOrDefault(p => p.Slot == dropWho && p.Match.Session is { } s && s.Simulation.World.Tick.Value >= dropAt) : null;
                if (dropping is not null)
                {
                    ticket = dropping.Match.Ticket;
                    CollectTraffic(dropping, result);
                    dropping.Match.Dispose();
                    peers.Remove(dropping);
                    result.Disconnects++;
                    rejoinAtMs = deadline.ElapsedMilliseconds + awayMs;
                }

                if (ticket is not null && rejoinAtMs >= 0 && deadline.ElapsedMilliseconds >= rejoinAtMs)
                {
                    peers.Add(new Peer(NetworkMatch.Rejoin(Options(proxies[ticket.Slot].Port, $"bot{ticket.Slot}"), ticket), new SoakBot(random.Next(), 40), ticket.Slot));
                    rejoinAtMs = -1;
                    result.Rejoins++;
                }

                if (peers.Any(p => p.Match.Stage == LobbyStage.Ended))
                {
                    break;
                }

                if (rejoinAtMs < 0 && peers.Count == options.Humans && peers.All(p => p.Match.Session is { } s && s.Simulation.World.Tick.Value >= options.Ticks))
                {
                    break;
                }

                delays.Add(peers[0].Match.InputDelay);
                Thread.Sleep(1);
            }

            if (peers.FirstOrDefault(p => p.Match.Stage == LobbyStage.Ended) is { } ended)
            {
                result.Failure = "ended: " + ended.Match.EndReason;
            }
            else
            {
                // Stop the clock (a pause), let every peer execute every closed tick, then compare the full states.
                Peer pauser = peers.First();
                pauser.Match.Session!.Paused = true;
                int tick = Settle(peers);
                result.Ticks = tick;
                ulong[] hashes = peers.Select(p => p.Match.Session!.Simulation.ComputeFullHash()).ToArray();
                result.HashesAgree = hashes.Distinct().Count() == 1 && peers.All(p => p.Match.Session!.Simulation.World.Tick.Value == tick);
                result.Completed = true;
                VerifyReplays(peers, result);
            }

            result.MeanInputDelay = delays.Count > 0 ? delays.Average() : 0;
            result.Orders = peers.Sum(p => p.Bot.Orders);
            foreach (Peer p in peers)
            {
                CollectTraffic(p, result);
            }

            double seconds = watch.Elapsed.TotalSeconds;
            result.UpKBps = proxies.Average(x => (x.UpBytes + (x.UpPackets * UdpImpairmentProxy.IpUdpHeaderBytes)) / 1024.0) / seconds;
            result.DownKBps = proxies.Average(x => (x.DownBytes + (x.DownPackets * UdpImpairmentProxy.IpUdpHeaderBytes)) / 1024.0) / seconds;
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or IOException or ArgumentException or System.Net.Sockets.SocketException)
        {
            result.Failure = e.GetType().Name + ": " + e.Message;
        }
        finally
        {
            foreach (Peer p in peers)
            {
                p.Match.Dispose();
            }

            foreach (UdpImpairmentProxy proxy in proxies)
            {
                proxy.Dispose();
            }

            result.Seconds = watch.Elapsed.TotalSeconds;
        }

        return result;
    }

    private MatchSettings Settings(ulong seed, int index)
    {
        var settings = new MatchSettings { Seed = seed, SpeedPercent = options.Speed, Map = MapSourceKind.TestMap, TestMapSize = options.TestMapSize };
        if (options.TestMapSize == 0)
        {
            Content.MapGen.RandomMapType[] types = [Content.MapGen.RandomMapType.Continental, Content.MapGen.RandomMapType.Lakes, Content.MapGen.RandomMapType.Dry];
            settings = settings with { Map = MapSourceKind.RandomMap, MapType = types[index % types.Length], MapSize = Content.MapGen.RandomMapSize.Small };
        }

        return settings;
    }

    private NetworkMatchOptions Options(int port, string name) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        PlayerName = name,
        Ruleset = options.Ruleset,
        Ai = options.Ai,
        GameVersion = Game.GameInfo.Version,
        AutoAcceptSaves = true,
        Fog = false,
    };

    private void VerifyReplays(List<Peer> peers, SoakMatchResult result)
    {
        foreach (Peer p in peers)
        {
            GameSession session = p.Match.Session!;
            if (!session.IsRecording)
            {
                continue; // a rejoined peer restored a snapshot (ADR 0164)
            }

            byte[] bytes = session.FinishReplay()!;
            if (options.ReplayDirectory is { } dir)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, FormattableString.Invariant($"soak-{result.Index:D4}-p{p.Slot}.t2rep")), bytes);
            }

            if (!options.VerifyReplays)
            {
                continue;
            }

            ReplayFile replay = ReplayFile.Parse(bytes);
            MatchSetup setup = MatchDescriptor.Parse(replay.Header.Setup.AsSpan()).ToSetup(options.Ai, multiplayer: true, localSlot: null);
            using GameSession check = GameSession.ForReplay(setup, options.Ruleset);
            ReplayVerification v = ReplayVerifier.Verify(replay, check.Simulation);
            if (v.Succeeded)
            {
                result.ReplaysVerified++;
            }
            else
            {
                result.ReplayFailures++;
                Log(FormattableString.Invariant($"match {result.Index}: replay of slot {p.Slot} differs at tick {v.FirstMismatch!.Value.Value}"));
            }
        }
    }

    private static void CollectTraffic(Peer p, SoakMatchResult result) => result.LateBatches += p.Match.Traffic.Late;

    private static void Pump(List<Peer> peers, Func<bool> done, int timeoutMs, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"timed out waiting for {what} ({string.Join(", ", peers.Select(p => $"{p.Match.Stage} {p.Match.EndReason}"))})");
            }

            foreach (Peer p in peers)
            {
                p.Match.Poll();
            }

            Thread.Sleep(1);
        }
    }

    // Pumps until every peer sits on the same tick for half a second (the relay is paused).
    private static int Settle(List<Peer> peers)
    {
        var watch = Stopwatch.StartNew();
        var stable = Stopwatch.StartNew();
        int last = -1;
        while (watch.ElapsedMilliseconds < 30_000)
        {
            foreach (Peer p in peers)
            {
                p.Match.Poll();
            }

            int[] ticks = peers.Select(p => p.Match.Session!.Simulation.World.Tick.Value).ToArray();
            if (!ticks.All(t => t == ticks[0]) || ticks[0] != last)
            {
                last = ticks[0];
                stable.Restart();
            }
            else if (stable.ElapsedMilliseconds > 500)
            {
                return last;
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException("the peers did not settle on one tick");
    }

    private void Log(string line)
    {
        lock (_logGate)
        {
            log.WriteLine(line);
            log.Flush();
        }
    }

    private static string Line(SoakMatchResult r) => FormattableString.Invariant(
        $"match {r.Index,4} {(r.Ok ? "ok  " : "FAIL")} seed {r.Seed} {r.Map} ticks {r.Ticks} hashes {(r.HashesAgree ? "agree" : "DIFFER")} desync {(r.Desync ? "YES" : "no")} disconnects {r.Disconnects} rejoins {r.Rejoins} replays {r.ReplaysVerified}/{r.ReplaysVerified + r.ReplayFailures} orders {r.Orders} late {r.LateBatches} D {r.MeanInputDelay:F1} up {r.UpKBps:F2} KB/s down {r.DownKBps:F2} KB/s {r.Seconds:F0} s{(r.Failure is null ? string.Empty : " " + r.Failure)}");

    private sealed class Peer(NetworkMatch match, SoakBot bot, int slot)
    {
        public NetworkMatch Match { get; } = match;

        public SoakBot Bot { get; } = bot;

        public int Slot { get; } = slot;
    }
}
