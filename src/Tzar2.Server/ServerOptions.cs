using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Tzar2.Server;

/// <summary>Command-line and environment settings of the relay/lobby service (§19.7: the address is configurable for self-hosting).</summary>
internal sealed class ServerOptions
{
    /// <summary>Default UDP port of the relay.</summary>
    public const int DefaultUdpPort = 47625;

    /// <summary>Default TCP port of the HTTP API (lobby list, health, metrics).</summary>
    public const int DefaultHttpPort = 8080;

    /// <summary>UDP port of the relay.</summary>
    public int UdpPort { get; set; } = DefaultUdpPort;

    /// <summary>TCP port of the HTTP API (0 = off).</summary>
    public int HttpPort { get; set; } = DefaultHttpPort;

    /// <summary>Host part of the HTTP prefix (<c>+</c> = every interface, <c>localhost</c> = loopback only).</summary>
    public string HttpHost { get; set; } = "+";

    /// <summary>Where desync reports are written (REQ-NET-04).</summary>
    public string DesyncDirectory { get; set; } = "desync";

    /// <summary>Most desync reports kept (the oldest are deleted).</summary>
    public int MaxDesyncReports { get; set; } = 200;

    /// <summary>Most lobbies.</summary>
    public int MaxLobbies { get; set; } = 256;

    /// <summary>Most connected peers.</summary>
    public int MaxPeers { get; set; } = 1024;

    /// <summary>Print usage and exit.</summary>
    public bool Help { get; set; }

    /// <summary>The usage text.</summary>
    public const string Usage = """
        Usage: Tzar2.Server [options]

        Relay and lobby service for Tzar2 multiplayer (TDD §19.7). Every option can also be set as an environment
        variable (TZAR2_UDP_PORT, TZAR2_HTTP_PORT, TZAR2_HTTP_HOST, TZAR2_DESYNC_DIR, TZAR2_MAX_LOBBIES, TZAR2_MAX_PEERS).

          --udp-port <n>       relay UDP port (default 47625)
          --http-port <n>      HTTP API port: /lobbies, /healthz, /metrics (default 8080, 0 = off)
          --http-host <host>   HTTP listen host (default + = all interfaces; localhost = loopback only)
          --desync-dir <dir>   where desync reports (.t2desync) are written (default ./desync)
          --max-desync <n>     desync reports kept (default 200)
          --max-lobbies <n>    lobbies at a time (default 256)
          --max-peers <n>      connected players at a time (default 1024)
          -h, --help           this text
        """;

    /// <summary>Reads the environment, then the arguments (arguments win).</summary>
    /// <exception cref="ArgumentException">An option is unknown or its value invalid.</exception>
    public static ServerOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        var o = new ServerOptions();
        Apply(o, "--udp-port", environment("TZAR2_UDP_PORT"));
        Apply(o, "--http-port", environment("TZAR2_HTTP_PORT"));
        Apply(o, "--http-host", environment("TZAR2_HTTP_HOST"));
        Apply(o, "--desync-dir", environment("TZAR2_DESYNC_DIR"));
        Apply(o, "--max-lobbies", environment("TZAR2_MAX_LOBBIES"));
        Apply(o, "--max-peers", environment("TZAR2_MAX_PEERS"));
        for (int i = 0; i < args.Count; i++)
        {
            string name = args[i];
            if (name is "-h" or "--help")
            {
                o.Help = true;
                continue;
            }

            if (i + 1 >= args.Count)
            {
                throw new ArgumentException($"Option {name} needs a value.");
            }

            Apply(o, name, args[++i]);
        }

        return o;
    }

    private static void Apply(ServerOptions o, string name, string? value)
    {
        if (value is null)
        {
            return;
        }

        switch (name)
        {
            case "--udp-port":
                o.UdpPort = Int(name, value, 0, 65535);
                break;
            case "--http-port":
                o.HttpPort = Int(name, value, 0, 65535);
                break;
            case "--http-host":
                o.HttpHost = value.Length is > 0 and <= 253 && value.IndexOfAny(['/', ' ', ':']) < 0 ? value : throw new ArgumentException($"Invalid {name}.");
                break;
            case "--desync-dir":
                o.DesyncDirectory = value.Length > 0 ? value : throw new ArgumentException($"Invalid {name}.");
                break;
            case "--max-desync":
                o.MaxDesyncReports = Int(name, value, 1, 100_000);
                break;
            case "--max-lobbies":
                o.MaxLobbies = Int(name, value, 1, 100_000);
                break;
            case "--max-peers":
                o.MaxPeers = Int(name, value, 1, 100_000);
                break;
            default:
                throw new ArgumentException($"Unknown option {name}.");
        }
    }

    private static int Int(string name, string value, int min, int max) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= min && n <= max
            ? n
            : throw new ArgumentException($"{name} must be an integer in [{min}, {max}], not '{value}'.");

    /// <summary>The full desync directory.</summary>
    public string DesyncPath => Path.GetFullPath(DesyncDirectory);
}
