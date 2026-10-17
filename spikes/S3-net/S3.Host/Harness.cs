using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Tzar2.Spikes.S3.Lockstep;

namespace Tzar2.Spikes.S3.Host;

/// <summary>One harness run: a relay process, N client processes (optionally some in Docker), one proxy per client.</summary>
internal sealed class Scenario
{
    public string Name { get; set; } = "bench";

    public int Clients { get; set; } = 2;

    /// <summary>RTT per client in ms.</summary>
    public int[] RttMs { get; set; } = [0];

    /// <summary>Loss per client and direction in percent.</summary>
    public double[] LossPercent { get; set; } = [0];

    public int Seconds { get; set; } = 20;

    public ClosureDelivery Delivery { get; set; } = ClosureDelivery.Reliable;

    public int HashInterval { get; set; } = 1;

    public int CommandsPerSecond { get; set; } = 2;

    /// <summary>Slot to Docker platform (e.g. linux/arm64) for clients that run in a Linux container.</summary>
    public Dictionary<int, string> Docker { get; } = [];

    public int DesyncSlot { get; set; } = -1;

    public int DesyncTick { get; set; } = -1;

    public int DropSlot { get; set; } = -1;

    public int DropTick { get; set; } = -1;

    public int DropForMs { get; set; } = 3000;

    public int Rtt(int slot) => RttMs[Math.Min(slot, RttMs.Length - 1)];

    public double Loss(int slot) => LossPercent[Math.Min(slot, LossPercent.Length - 1)];

    /// <summary>Jitter per direction: +-5% of the RTT.</summary>
    public Impairment Impairment(int slot) => new(Rtt(slot) / 2, Rtt(slot) / 20, Loss(slot));
}

/// <summary>Measured outcome of a <see cref="Scenario"/>.</summary>
internal sealed class BenchResult
{
    public required Scenario Scenario { get; init; }

    public required List<ClientReport> Clients { get; init; }

    public required RelayReport Relay { get; init; }

    /// <summary>Per slot: bytes/packets up and down as seen on the wire by the proxy.</summary>
    public required List<(long UpBytes, long UpPackets, long DownBytes, long DownPackets)> Wire { get; init; }

    public bool HashesAgree => Clients.Select(c => (c.FinalTick, c.FinalHash)).Distinct().Count() == 1;

    /// <summary>Mean per-client upstream rate in KB/s including IPv4+UDP headers.</summary>
    public double UpKBps => Wire.Average(w => (w.UpBytes + (w.UpPackets * UdpImpairmentProxy.IpUdpHeaderBytes)) / 1024.0) / Seconds;

    /// <summary>Mean per-client downstream rate in KB/s including IPv4+UDP headers.</summary>
    public double DownKBps => Wire.Average(w => (w.DownBytes + (w.DownPackets * UdpImpairmentProxy.IpUdpHeaderBytes)) / 1024.0) / Seconds;

    private double Seconds => Math.Max(0.001, Clients.Max(c => c.DurationMs) / 1000.0);
}

/// <summary>Spawns and measures scenarios.</summary>
internal static class Harness
{
    private const string DockerImage = "mcr.microsoft.com/dotnet/runtime:10.0";

    private static readonly int[] Rtts = [0, 100, 250];
    private static readonly double[] Losses = [0, 5];

    public static int Bench(Options o)
    {
        var scenario = new Scenario
        {
            Name = o.Get("name", "bench"),
            Clients = o.Int("clients", 2),
            RttMs = o.Get("rtt", "0").Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray(),
            LossPercent = o.Get("loss", "0").Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray(),
            Seconds = o.Int("seconds", 20),
            Delivery = Enum.Parse<ClosureDelivery>(o.Get("delivery", "Reliable"), ignoreCase: true),
            HashInterval = o.Int("hash-interval", 1),
            CommandsPerSecond = o.Int("cps", 2),
        };
        if (o.GetOrNull("docker") is { } docker)
        {
            // --docker 1=linux/arm64,2=linux/amd64
            foreach (string entry in docker.Split(','))
            {
                string[] parts = entry.Split('=');
                scenario.Docker[int.Parse(parts[0], CultureInfo.InvariantCulture)] = parts.Length > 1 ? parts[1] : "linux/arm64";
            }
        }

        BenchResult result = Run(scenario, OutDir(o));
        Console.WriteLine(TableHeader);
        Console.WriteLine(Row(result));
        return 0;
    }

    public static int Matrix(Options o)
    {
        string outDir = OutDir(o);
        int seconds = o.Int("seconds", 20);
        int hashInterval = o.Int("hash-interval", 1);
        int[] clientCounts = o.Get("clients", "2,4,8").Split(',').Select(c => int.Parse(c, CultureInfo.InvariantCulture)).ToArray();
        ClosureDelivery[] deliveries = o.Get("deliveries", "reliable,redundant").Split(',')
            .Select(d => Enum.Parse<ClosureDelivery>(d, ignoreCase: true)).ToArray();
        var table = new StringBuilder();
        table.AppendLine(TableHeader);
        foreach (ClosureDelivery delivery in deliveries)
        {
            foreach (int clients in clientCounts)
            {
                foreach (int rtt in Rtts)
                {
                    foreach (double loss in Losses)
                    {
                        var scenario = new Scenario
                        {
                            Name = string.Create(CultureInfo.InvariantCulture, $"{delivery}-{clients}c-{rtt}ms-{loss}pct-h{hashInterval}").ToLowerInvariant(),
                            Clients = clients,
                            RttMs = [rtt],
                            LossPercent = [loss],
                            Seconds = seconds,
                            Delivery = delivery,
                            HashInterval = hashInterval,
                        };
                        string row = Row(Run(scenario, outDir));
                        table.AppendLine(row);
                        Console.WriteLine(row);
                        File.WriteAllText(Path.Combine(outDir, o.Get("table", "matrix.md")), table.ToString());
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(table);
        return 0;
    }

    public static int Faults(Options o)
    {
        string outDir = OutDir(o);
        ClosureDelivery delivery = Enum.Parse<ClosureDelivery>(o.Get("delivery", "Redundant"), ignoreCase: true);
        int failures = 0;
        var summary = new StringBuilder();

        string only = o.Get("only", string.Empty);
        bool Want(string name) => only.Length == 0 || name.Contains(only, StringComparison.Ordinal);

        void Check(string scenario, bool ok, string detail)
        {
            string line = $"{(ok ? "PASS" : "FAIL")} {scenario}: {detail}";
            Console.WriteLine(line);
            summary.AppendLine(line);
            failures += ok ? 0 : 1;
        }

        // 1. Disconnect + rejoin under 100 ms RTT and 5% loss (REQ-NET-06).
        var rejoin = new Scenario
        {
            Name = "fault-rejoin", Delivery = delivery, Clients = 4, RttMs = [100], LossPercent = [5], Seconds = 30, DropSlot = 2, DropTick = 100, DropForMs = 3000,
        };
        if (Want(rejoin.Name))
        {
            BenchResult r = Run(rejoin, outDir);
            ClientReport rejoiner = r.Clients[2];
            Check(rejoin.Name, rejoiner.RejoinSnapshotVerified && rejoiner.RejoinMs > 0 && r.HashesAgree && r.Relay.Desyncs.Count == 0 && r.Relay.Rejoins == 1,
                Invariant($"snapshot verified={rejoiner.RejoinSnapshotVerified}, away {rejoin.DropForMs} ms, then reconnect-to-caught-up {rejoiner.RejoinMs:F0} ms with {rejoiner.RejoinBacklogTicks} ticks fast-forwarded; relay served resume {string.Join('/', r.Relay.RejoinServeMs.Select(x => x.ToString("F0", CultureInfo.InvariantCulture)))} ms after hello; final hashes agree={r.HashesAgree}; others' stalls={r.Clients.Where(c => c.Slot != 2).Sum(c => c.Stalls)} (max gap {r.Clients.Where(c => c.Slot != 2).Max(c => c.MaxGapMs):F0} ms)"));
        }

        // 2. Forced desync on slot 1 at tick 150 must be detected at exactly that tick.
        var desync = new Scenario { Name = "fault-desync", Delivery = delivery, Clients = 4, RttMs = [50], LossPercent = [0], Seconds = 20, DesyncSlot = 1, DesyncTick = 150 };
        if (Want(desync.Name))
        {
            BenchResult r = Run(desync, outDir);
            DesyncEntry? found = r.Relay.Desyncs.FirstOrDefault();
            Check(desync.Name, found is { Tick: 150 } && found.OddSlots.SequenceEqual([1]) && r.Clients.TrueForAll(c => c.DesyncTicks.Contains(150)),
                found is null ? "no desync detected" : Invariant($"detected at tick {found.Tick}, odd slots [{string.Join(',', found.OddSlots)}], {found.DetectedAfterMs:F0} ms after the relay closed the tick; match ended at tick {r.Relay.LastClosedTick}"));
        }

        // 3. One slow player (250 ms RTT, 5% loss) must not slow down the others (TDD 19.2 relay tick closing).
        var slow = new Scenario { Name = "fault-slow-player", Delivery = delivery, Clients = 4, RttMs = [0, 0, 0, 250], LossPercent = [0, 0, 0, 5], Seconds = 20 };
        if (Want(slow.Name))
        {
            BenchResult r = Run(slow, outDir);
            List<ClientReport> fast = r.Clients.Where(c => c.Slot != 3).ToList();
            Check(slow.Name, fast.TrueForAll(c => c.Stalls == 0 && c.DelayMax <= 2) && r.HashesAgree,
                Invariant($"fast players: stalls={fast.Sum(c => c.Stalls)}, D max={fast.Max(c => c.DelayMax)}, latency {fast.Average(c => c.LatencyMeanMs):F0} ms; slow player: D mean={r.Clients[3].DelayMean:F2}, latency {r.Clients[3].LatencyMeanMs:F0} ms, stalls={r.Clients[3].Stalls}; hashes agree={r.HashesAgree}"));
        }

        // 4. Headless Linux clients in Docker (arm64 native, amd64 emulated) against macOS arm64 clients.
        if (Want("linux-docker") && DockerAvailable())
        {
            var linux = new Scenario { Name = "linux-docker", Delivery = delivery, Clients = 3, RttMs = [100], LossPercent = [5], Seconds = 20 };
            linux.Docker[1] = "linux/arm64";
            linux.Docker[2] = "linux/amd64";
            BenchResult r = Run(linux, outDir);
            Check(linux.Name, r.HashesAgree && r.Relay.Desyncs.Count == 0 && r.Relay.HashTicksCompared > 300,
                Invariant($"platforms [{string.Join(" | ", r.Clients.Select(c => c.Platform))}], {r.Relay.HashTicksCompared} tick hashes compared, desyncs={r.Relay.Desyncs.Count}, final {r.Clients[0].FinalTick}:{r.Clients[0].FinalHash} agree={r.HashesAgree}"));
        }
        else if (Want("linux-docker"))
        {
            Check("linux-docker", true, "SKIPPED: Docker not available; run in CI on ubuntu-latest instead");
        }

        File.WriteAllText(Path.Combine(outDir, "faults.md"), summary.ToString());
        return failures == 0 ? 0 : 1;
    }

    public static BenchResult Run(Scenario s, string outDir)
    {
        string dir = Path.Combine(outDir, s.Name);
        Directory.CreateDirectory(dir);
        foreach (string stale in Directory.GetFiles(dir, "*.json"))
        {
            File.Delete(stale);
        }

        Console.Error.WriteLine($"[harness] {s.Name}: {s.Clients} clients, RTT [{string.Join(',', s.RttMs)}] ms, loss [{string.Join(',', s.LossPercent.Select(l => l.ToString(CultureInfo.InvariantCulture)))}] %, {s.Delivery}, {s.Seconds} s");
        int ticks = s.Seconds * 20;
        string relayReport = Path.Combine(dir, "relay.json");
        using Child relay = Child.Self("relay", Path.Combine(dir, "relay.log"),
            ["relay", "--ticks", Inv(ticks), "--delivery", s.Delivery.ToString(), "--hash-interval", Inv(s.HashInterval), "--report", relayReport, "--exit-after-match"]);
        int relayPort = int.Parse(relay.WaitFor("LISTENING ", 10).Split(' ')[1], CultureInfo.InvariantCulture);

        var proxies = Enumerable.Range(0, s.Clients)
            .Select(i => new UdpImpairmentProxy(new IPEndPoint(IPAddress.Loopback, relayPort), s.Impairment(i)))
            .ToList();
        var clients = new List<Child>();
        try
        {
            string code = string.Empty;
            for (int slot = 0; slot < s.Clients; slot++)
            {
                bool docker = s.Docker.ContainsKey(slot);
                var args = new List<string>
                {
                    "client", "--host", docker ? "host.docker.internal" : "127.0.0.1", "--port", Inv(proxies[slot].Port), "--name", "bot" + Inv(slot),
                    "--cps", Inv(s.CommandsPerSecond),
                    "--report", docker ? $"/results/client-{slot}.json" : Path.Combine(dir, $"client-{slot}.json"),
                };
                args.AddRange(slot == 0 ? ["--create", Inv(s.Clients)] : ["--join", code]);
                if (slot == s.DesyncSlot)
                {
                    args.AddRange(["--desync-at", Inv(s.DesyncTick)]);
                }

                if (slot == s.DropSlot)
                {
                    args.AddRange(["--drop-at", Inv(s.DropTick), "--drop-for", Inv(s.DropForMs)]);
                }

                string log = Path.Combine(dir, $"client-{slot}.log");
                Child child = docker ? Child.Docker(s.Docker[slot], dir, log, args) : Child.Self($"client{slot}", log, args);
                clients.Add(child);
                if (slot == 0)
                {
                    // Slots are assigned in join order, so wait for each join before starting the next client.
                    code = child.WaitFor("CODE ", 15).Split(' ')[1];
                }
                else
                {
                    child.WaitFor("WELCOME ", docker ? 60 : 15);
                }
            }

            foreach (Child client in clients)
            {
                client.WaitForExit(s.Seconds + 90);
            }

            relay.WaitForExit(15);
        }
        finally
        {
            foreach (UdpImpairmentProxy proxy in proxies)
            {
                proxy.Dispose();
            }

            foreach (Child client in clients)
            {
                client.Dispose();
            }
        }

        var reports = Enumerable.Range(0, s.Clients)
            .Select(i => JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir, $"client-{i}.json")), ReportJson.Default.ClientReport)!)
            .ToList();
        RelayReport relayResult = JsonSerializer.Deserialize(File.ReadAllText(relayReport), ReportJson.Default.ListRelayReport)!.Single();
        return new BenchResult
        {
            Scenario = s,
            Clients = reports,
            Relay = relayResult,
            Wire = proxies.Select(p => (p.UpBytes, p.UpPackets, p.DownBytes, p.DownPackets)).ToList(),
        };
    }

    public static string TableHeader =>
        "| Delivery | Hash every | Clients | RTT ms | Loss % | Tick rate (min) Hz | Stalls (sum ms) | Max gap ms | Input latency mean / p95 ms | D mean (min-max) | Late batches | Up / down KB/s per client | Desyncs | Hashes agree |\n" +
        "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|";

    public static string Row(BenchResult r)
    {
        Scenario s = r.Scenario;
        List<ClientReport> c = r.Clients;
        return Invariant(
            $"| {s.Delivery} | {s.HashInterval} | {s.Clients} | {string.Join('/', s.RttMs.Distinct())} | {string.Join('/', s.LossPercent.Distinct())} | {c.Min(x => x.TickRate):F2} | {c.Sum(x => x.Stalls)} ({c.Sum(x => x.StallMs):F0}) | {c.Max(x => x.MaxGapMs):F0} | {c.Average(x => x.LatencyMeanMs):F0} / {c.Max(x => x.LatencyP95Ms):F0} | {c.Average(x => x.DelayMean):F2} ({c.Min(x => x.DelayMin)}-{c.Max(x => x.DelayMax)}) | {c.Sum(x => x.LateBatches)} | {r.UpKBps:F2} / {r.DownKBps:F2} | {r.Relay.Desyncs.Count} | {(r.HashesAgree ? "yes" : "NO")} |");
    }

    private static string OutDir(Options o)
    {
        string dir = Path.GetFullPath(o.Get("out-dir", Path.Combine("spikes", "S3-net", "results")));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool DockerAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "info") { RedirectStandardOutput = true, RedirectStandardError = true });
            return p is not null && p.WaitForExit(20_000) && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>A child process whose output goes to a log file and can be waited on for marker lines.</summary>
    private sealed class Child : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _log;
        private readonly List<(string Prefix, TaskCompletionSource<string> Line)> _waits = [];
        private readonly List<string> _lines = [];
        private readonly string _name;
        private bool _closed;

        private Child(string name, string fileName, IEnumerable<string> args, string logPath)
        {
            _name = name;
            _log = new StreamWriter(logPath) { AutoFlush = true };
            var info = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            _process = new Process { StartInfo = info };
            _process.OutputDataReceived += (_, e) => OnLine(e.Data);
            _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public static Child Self(string name, string logPath, IEnumerable<string> args)
        {
            string exe = Environment.ProcessPath!;
            bool viaDotnet = Path.GetFileNameWithoutExtension(exe) == "dotnet";
            IEnumerable<string> all = viaDotnet ? args.Prepend(typeof(Harness).Assembly.Location) : args;
            return new Child(name, exe, all, logPath);
        }

        public static Child Docker(string platform, string resultsDir, string logPath, IEnumerable<string> args)
        {
            string bin = Path.GetDirectoryName(typeof(Harness).Assembly.Location)!;
            string[] docker =
            [
                "run", "--rm", "--platform", platform, "-v", bin + ":/app:ro", "-v", resultsDir + ":/results", DockerImage, "dotnet", "/app/S3.Host.dll",
            ];
            return new Child("docker-" + platform, "docker", docker.Concat(args), logPath);
        }

        public string WaitFor(string prefix, int seconds)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waits)
            {
                string? seen = _lines.Find(l => l.StartsWith(prefix, StringComparison.Ordinal));
                if (seen is not null)
                {
                    return seen;
                }

                _waits.Add((prefix, tcs));
            }

            if (!tcs.Task.Wait(TimeSpan.FromSeconds(seconds)))
            {
                throw new TimeoutException($"{_name}: no '{prefix}' line within {seconds} s");
            }

            return tcs.Task.Result;
        }

        public void WaitForExit(int seconds)
        {
            if (!_process.WaitForExit(TimeSpan.FromSeconds(seconds)))
            {
                Console.Error.WriteLine($"[harness] {_name} did not exit in {seconds} s; killing it");
                _process.Kill(entireProcessTree: true);
            }

            _process.WaitForExit();
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            _process.Dispose();
            lock (_waits)
            {
                _closed = true;
                _log.Dispose();
            }
        }

        private void OnLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_waits)
            {
                if (_closed)
                {
                    return;
                }

                _log.WriteLine(line);
                _lines.Add(line);
                foreach ((string prefix, TaskCompletionSource<string> tcs) in _waits)
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        tcs.TrySetResult(line);
                    }
                }
            }
        }
    }
}
