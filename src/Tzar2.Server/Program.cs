using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using Tzar2.Net.Relay;

namespace Tzar2.Server;

internal static class Program
{
    /// <summary>Exit code for invalid options.</summary>
    internal const int UsageError = 2;

    /// <summary>Exit code when a port cannot be bound.</summary>
    internal const int StartFailed = 1;

    private static int Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        return Run(args, Console.Out, Console.Error, Environment.GetEnvironmentVariable, stop.Token);
    }

    /// <summary>Runs the service until <paramref name="cancellation"/> fires.</summary>
    internal static int Run(string[] args, TextWriter output, TextWriter error, Func<string, string?> environment, CancellationToken cancellation)
    {
        ServerOptions options;
        try
        {
            options = ServerOptions.Parse(args, environment);
        }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            error.WriteLine(ServerOptions.Usage);
            return UsageError;
        }

        if (options.Help)
        {
            output.WriteLine(ServerOptions.Usage);
            return 0;
        }

        var log = new JsonLog(output, () => DateTimeOffset.UtcNow);
        try
        {
            using var host = new ServerHost(options, log);
            host.Start(cancellation);
            log.Write("service.ready", string.Empty, -1, $"udp {host.UdpPort}, http {(options.HttpPort == 0 ? "off" : host.HttpPort)}");
            cancellation.WaitHandle.WaitOne();
            log.Write("service.stop", string.Empty, -1, string.Empty);
            return 0;
        }
        catch (Exception e) when (e is InvalidOperationException or HttpListenerException or IOException or UnauthorizedAccessException)
        {
            log.Write("service.failed", string.Empty, -1, e.Message);
            return StartFailed;
        }
    }
}

/// <summary>The relay on its own thread, the desync report directory and the HTTP API.</summary>
internal sealed class ServerHost : IDisposable
{
    private readonly ServerOptions _options;
    private readonly JsonLog _log;
    private readonly DesyncDirectory _desync;
    private readonly RelayServer _relay;
    private HttpApi? _http;
    private Thread? _thread;
    private CancellationTokenSource? _stop;

    public ServerHost(ServerOptions options, JsonLog log)
    {
        _options = options;
        _log = log;
        _desync = new DesyncDirectory(options.DesyncPath, options.MaxDesyncReports, detail => log.Write("desync.file", string.Empty, -1, detail));
        _relay = new RelayServer(new RelayOptions
        {
            Port = options.UdpPort,
            MaxLobbies = options.MaxLobbies,
            MaxPeers = options.MaxPeers,
            DesyncSink = _desync,
            Log = log.Write,
        });
    }

    /// <summary>The bound UDP port.</summary>
    public int UdpPort => _relay.Port;

    /// <summary>The HTTP port.</summary>
    public int HttpPort => _options.HttpPort;

    /// <summary>The relay (tests read its metrics).</summary>
    public RelayServer Relay => _relay;

    /// <summary>The desync report directory.</summary>
    public DesyncDirectory Desync => _desync;

    /// <summary>The HTTP API, or null when off.</summary>
    public HttpApi? Http => _http;

    /// <summary>Binds the ports and starts the relay thread.</summary>
    public void Start(CancellationToken cancellation)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _relay.Start();
        if (_options.HttpPort != 0)
        {
            _http = new HttpApi(_options.HttpHost, _options.HttpPort, _relay, () => _desync.Written);
            _http.Start(_stop.Token);
        }

        CancellationToken token = _stop.Token;
        _thread = new Thread(() => RunRelay(token)) { IsBackground = true, Name = "relay" };
        _thread.Start();
    }

    private void RunRelay(CancellationToken token)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                TimerResolution.Begin(); // 1 ms sleeps for steady tick deadlines (ADR 0009)
            }

            _relay.Run(token);
        }
#pragma warning disable CA1031 // A relay crash is logged; the process supervisor restarts the container.
        catch (Exception e)
#pragma warning restore CA1031
        {
            _log.Write("relay.crash", string.Empty, -1, e.ToString());
            Environment.Exit(StartFailedCode);
        }
    }

    private const int StartFailedCode = 1;

    /// <inheritdoc />
    public void Dispose()
    {
        _stop?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _http?.Dispose();
        _relay.Dispose();
        _desync.Dispose();
        _stop?.Dispose();
    }
}

/// <summary>Windows timer resolution (<c>timeBeginPeriod(1)</c>, ADR 0009).</summary>
internal static partial class TimerResolution
{
    public static void Begin() => _ = TimeBeginPeriod(1);

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);
}
