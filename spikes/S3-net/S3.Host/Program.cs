using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Tzar2.Spikes.S3.Lockstep;

namespace Tzar2.Spikes.S3.Host;

/// <summary>
/// <c>s3 relay | client | bench | matrix | faults</c>. See spikes/S3-net/README.md.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: S3.Host relay|client|bench|matrix|faults [--option value ...]");
            return 2;
        }

        if (OperatingSystem.IsWindows())
        {
            // Thread.Sleep(1) sleeps a full 15.6 ms scheduler quantum on Windows unless the timer resolution is raised.
            _ = NativeMethods.TimeBeginPeriod(1);
        }

        var options = Options.Parse(args.Skip(1));
        return args[0] switch
        {
            "relay" => RunRelay(options),
            "client" => RunClient(options),
            "bench" => Harness.Bench(options),
            "matrix" => Harness.Matrix(options),
            "faults" => Harness.Faults(options),
            _ => 2,
        };
    }

    private static int RunRelay(Options o)
    {
        var relayOptions = new RelayOptions
        {
            Port = o.Int("port", 0),
            TickMicroseconds = o.Int("tick-ms", 50) * 1000,
            HashInterval = o.Int("hash-interval", 1),
            Delivery = Enum.Parse<ClosureDelivery>(o.Get("delivery", "Reliable"), ignoreCase: true),
            MatchTicks = o.Int("ticks", 400),
            EndOnDesync = o.Get("end-on-desync", "true") == "true",
        };
        using var relay = new RelayServer(relayOptions, Console.Out);
        relay.Start();
        bool exitAfterMatch = o.Has("exit-after-match");
        while (!(exitAfterMatch && relay.AllMatchesFinished))
        {
            relay.Update();
            Thread.Sleep(1);
        }

        string? report = o.GetOrNull("report");
        if (report is not null)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(relay.Reports.ToList(), ReportJson.Default.ListRelayReport));
        }

        return 0;
    }

    private static int RunClient(Options o)
    {
        var clientOptions = new ClientOptions
        {
            Host = o.Get("host", "127.0.0.1"),
            Port = o.Int("port", 0),
            CreatePlayers = o.Int("create", 0),
            Code = o.Get("join", string.Empty),
            Name = o.Get("name", "bot"),
            CommandsPerSecond = o.Int("cps", 2),
            DesyncAtTick = o.Int("desync-at", -1),
            DropAtTick = o.Int("drop-at", -1),
            DropForMs = o.Int("drop-for", 3000),
        };
        var client = new LockstepClient(clientOptions, Console.Out);
        client.LobbyCreated += code => Console.Out.WriteLine("CODE " + code);
        ClientReport report = client.Run(CancellationToken.None);
        string? path = o.GetOrNull("report");
        if (path is not null)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(report, ReportJson.Default.ClientReport));
        }

        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"DONE slot={report.Slot} ticks={report.TicksExecuted} rate={report.TickRate:F2} stalls={report.Stalls} latency={report.LatencyMeanMs:F1}ms D={report.DelayMean:F2} final={report.FinalTick}:{report.FinalHash}"));
        return 0;
    }
}

/// <summary>Windows timer resolution.</summary>
internal static class NativeMethods
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint TimeBeginPeriod(uint milliseconds);
}

/// <summary>Minimal <c>--key value</c> / <c>--flag</c> parser.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static Options Parse(IEnumerable<string> args)
    {
        var options = new Options();
        string? key = null;
        foreach (string arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (key is not null)
                {
                    options._values[key] = "true";
                }

                key = arg[2..];
            }
            else if (key is not null)
            {
                options._values[key] = arg;
                key = null;
            }
        }

        if (key is not null)
        {
            options._values[key] = "true";
        }

        return options;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string Get(string key, string fallback) => _values.GetValueOrDefault(key, fallback);

    public string? GetOrNull(string key) => _values.GetValueOrDefault(key);

    public int Int(string key, int fallback) =>
        _values.TryGetValue(key, out string? value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

    public double Double(string key, double fallback) =>
        _values.TryGetValue(key, out string? value) ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;
}
