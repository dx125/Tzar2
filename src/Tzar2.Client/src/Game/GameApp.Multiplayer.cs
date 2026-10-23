using System;
using System.Globalization;
using System.IO;
using Godot;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// Multiplayer and replays in the game mode (TDD §19, ADR 0168): the multiplayer menu (internet relay or LAN host and
/// browser, reconnect), the lobby, the network game with its overlay, and the replay browser and viewer. Every match is
/// recorded into <c>user://replays</c> (REQ-NET-09); multiplayer saves go to <c>user://saves/multiplayer</c>, desync
/// reports of a LAN host to <c>user://desync</c>.
/// </summary>
public partial class GameApp
{
    private readonly MultiplayerSettings _mp = new();
    private NetworkMatch? _network;
    private LanHost? _lanHost;
    private LobbyScreen? _lobby;
    private bool _networkGameShown;
    private ReplayPlayback? _playback;
    private int _playbackGeneration;

    private static string UserPath(string sub) => Path.Combine(ProjectSettings.GlobalizePath("user://"), sub);

    /// <summary>The network match, or null.</summary>
    public NetworkMatch? Network => _network;

    /// <summary>Shows the multiplayer menu.</summary>
    public void ShowMultiplayer()
    {
        LeaveNetwork();
        if (_rules is null)
        {
            ShowMainMenu();
            return;
        }

        var menu = new MultiplayerMenuScreen(_ui!, Screen, _mp);
        menu.CreateInternet += s =>
        {
            (string host, int port) = s.Endpoint();
            OpenLobby(() => NetworkMatch.Create(NetworkOptions(host, port), $"{s.Name}'s game", 4, isPublic: true));
        };
        menu.JoinCode += (s, code) =>
        {
            (string host, int port) = s.Endpoint();
            OpenLobby(() => NetworkMatch.Join(NetworkOptions(host, port), code));
        };
        menu.JoinListing += (_, l) => OpenLobby(() => NetworkMatch.Join(NetworkOptions(l.Host, l.Port), l.Code));
        menu.HostLan += s =>
        {
            _lanHost = LanHost.Start(desyncDirectory: UserPath("desync"));
            OpenLobby(() => NetworkMatch.Create(NetworkOptions("127.0.0.1", _lanHost.Port), $"{s.Name}'s LAN game", 4, isPublic: true));
        };
        menu.Reconnect += (_, ticket) => OpenLobby(() => NetworkMatch.Rejoin(NetworkOptions(ticket.Host, ticket.Port), ticket));
        menu.Replays += ShowReplays;
        menu.Back += ShowMainMenu;
        Replace(menu, music: "music/intro.mid");
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        WatchWindow();

        // The lobby polls the match; once the session exists (loading), the game screen takes over.
        if (_network is { Session: { } session } && _lobby is not null && !_networkGameShown)
        {
            StartNetworkGame(_network, session);
        }

        // A rejoin spends the stored token and the relay sends a new one (ADR 0230): keep it at once, so a crash while
        // the match loads again does not lock the player out.
        if (_network?.Ticket is { } ticket && _mp.Ticket is { } stored && stored.Code == ticket.Code && stored.Token != ticket.Token)
        {
            _mp.Ticket = ticket;
        }

        if (_playback is { } playback && playback.Generation != _playbackGeneration)
        {
            ShowPlayback(); // a backward seek restored a keyframe into a new session
        }
    }

    private NetworkMatchOptions NetworkOptions(string host, int port) => new()
    {
        Host = host,
        Port = port,
        PlayerName = _mp.Name,
        Ruleset = _rules!,
        Ai = _ai,
        SaveDirectory = UserPath(Path.Combine("saves", "multiplayer")),
        DeferLoadReport = true,
    };

    private void OpenLobby(Func<NetworkMatch> connect)
    {
        try
        {
            _network = connect();
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException)
        {
            GD.PrintErr("Multiplayer: " + e.Message);
            _lanHost?.Dispose();
            _lanHost = null;
            return;
        }

        _networkGameShown = false;
        _lobby = new LobbyScreen(_ui!, Screen, _network, PlayerColour, _rules);
        _lobby.Leave += ShowMultiplayer;
        Replace(_lobby, music: null);
    }

    private void StartNetworkGame(NetworkMatch match, GameSession session)
    {
        _networkGameShown = true;
        _lobby = null;
        MatchSettings settings = match.Lobby?.Settings ?? new MatchSettings();
        var screen = new GameScreen(_store, _ui!, session, Content.MapGen.RandomMapNames.SetId(settings.Terrain), _args.SoundFont, _settings) { KeepSpeed = true };
        var overlay = new MultiplayerOverlay(match, session);
        screen.Finished += (outcome, stats) =>
        {
            SaveReplay(session, "mp");
            _mp.Ticket = null;
            ShowResults(outcome, stats);
            LeaveNetwork();
        };
        screen.QuitRequested += () =>
        {
            SaveReplay(session, "mp");
            _mp.Ticket = null;
            ShowMultiplayer();
        };
        overlay.LeaveRequested += () =>
        {
            SaveReplay(session, "mp");
            _mp.Ticket = null;
            ShowMultiplayer();
        };
        Replace(screen, music: null);
        var layer = new CanvasLayer { Layer = 5 };
        layer.AddChild(overlay);
        screen.AddChild(layer);
        if (screen.Hud is { } hud)
        {
            hud.ChatPressed += overlay.OpenChat;
        }

        // The sprites and sounds are loaded: now this machine passes the loading barrier (REQ-NET-05, ORIG-06).
        _mp.Ticket = match.Ticket;
        match.ReportLoaded();
    }

    private void LeaveNetwork()
    {
        _network?.Leave();
        _network?.Dispose();
        _network = null;
        _lanHost?.Dispose();
        _lanHost = null;
        _lobby = null;
        _networkGameShown = false;
        _playback = null;
    }

    // Every match is recorded (REQ-NET-09): stop its sim thread and keep the replay.
    private static void SaveReplay(GameSession session, string kind)
    {
        session.Stop();
        if (session.IsRunning || !session.IsRecording)
        {
            return;
        }

        try
        {
            byte[]? replay = session.FinishReplay();
            if (replay is null)
            {
                return;
            }

            string dir = UserPath("replays");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.t2rep"));
            File.WriteAllBytes(path, replay);
            ClientLog.Info("replay saved: " + path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            GD.PrintErr("Replay not saved: " + e.Message);
        }
    }

    /// <summary>Shows the replay browser.</summary>
    public void ShowReplays()
    {
        LeaveNetwork();
        var browser = new ReplayBrowserScreen(_ui!, Screen, UserPath("replays"));
        browser.Play += PlayReplay;
        browser.Back += ShowMultiplayer;
        Replace(browser, music: null);
    }

    /// <summary>Opens a replay in the viewer.</summary>
    public void PlayReplay(string file)
    {
        if (_rules is null)
        {
            return;
        }

        try
        {
            _playback = ReplayPlayback.Open(File.ReadAllBytes(file), _rules, _ai);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            GD.PrintErr($"Replay {file}: {e.Message}");
            return;
        }

        ShowPlayback();
    }

    private void ShowPlayback()
    {
        ReplayPlayback playback = _playback!;
        _playbackGeneration = playback.Generation;
        MatchSettings settings = playback.Session.Setup.Descriptor?.Settings ?? new MatchSettings();
        var screen = new GameScreen(_store, _ui!, playback.Session, Content.MapGen.RandomMapNames.SetId(settings.Terrain), _args.SoundFont, _settings) { KeepSpeed = true };
        screen.QuitRequested += ShowReplays;
        Replace(screen, music: null);
        var controls = new ReplayControls(playback);
        controls.LeaveRequested += ShowReplays;
        var layer = new CanvasLayer { Layer = 5 };
        layer.AddChild(controls);
        screen.AddChild(layer);
    }
}
