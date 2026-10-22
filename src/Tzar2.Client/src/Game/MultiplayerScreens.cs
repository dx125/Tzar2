using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>Multiplayer preferences kept in <c>user://multiplayer.cfg</c>: name, relay address, and a reconnect ticket.</summary>
public sealed class MultiplayerSettings
{
    private const string Section = "multiplayer";
    private const string Path = "user://multiplayer.cfg";
    private readonly ConfigFile _file = new();

    /// <summary>Loads the file (missing = defaults).</summary>
    public MultiplayerSettings() => _file.Load(Path);

    /// <summary>Player name.</summary>
    public string Name { get => (string)_file.GetValue(Section, "name", "Player"); set => Set("name", value); }

    /// <summary>Relay address <c>host:port</c> (Q-09: no official server yet; self-hosted or LAN).</summary>
    public string Server { get => (string)_file.GetValue(Section, "server", "127.0.0.1:47625"); set => Set("server", value); }

    /// <summary>The ticket of the last running match (reconnect after a crash), or null.</summary>
    public ReconnectTicket? Ticket
    {
        get
        {
            string text = (string)_file.GetValue(Section, "ticket", string.Empty);
            string[] p = text.Split('|');
            return p.Length == 5 && int.TryParse(p[1], CultureInfo.InvariantCulture, out int port) && byte.TryParse(p[3], CultureInfo.InvariantCulture, out byte slot)
                && ulong.TryParse(p[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong token)
                ? new ReconnectTicket(p[0], port, p[2], slot, token)
                : null;
        }

        set => Set("ticket", value is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{value.Host}|{value.Port}|{value.Code}|{value.Slot}|{value.Token:X16}"));
    }

    /// <summary>Splits <see cref="Server"/> into host and port (default port 47625).</summary>
    public (string Host, int Port) Endpoint()
    {
        string s = Server.Trim();
        int colon = s.LastIndexOf(':');
        return colon > 0 && int.TryParse(s[(colon + 1)..], CultureInfo.InvariantCulture, out int port) ? (s[..colon], port) : (s, 47625);
    }

    private void Set(string key, string value)
    {
        _file.SetValue(Section, key, value);
        if (_file.Save(Path) != Error.Ok)
        {
            GD.PushWarning("Could not save " + Path);
        }
    }
}

/// <summary>
/// The multiplayer menu (REQ-NET-08, ADR 0168): name and relay address, create or join an internet lobby (by code, or
/// from the relay's public list), host or join a LAN game (LAN browser), reconnect to a running match, and replays.
/// </summary>
public partial class MultiplayerMenuScreen : ArtScreen
{
    private readonly MultiplayerSettings _settings;
    private readonly LineEdit _name;
    private readonly LineEdit _server;
    private readonly LineEdit _code;
    private readonly ItemList _list;
    private readonly Label _status;
    private readonly LanBrowserService? _lan;
    private IReadOnlyList<LobbyListing> _shown = [];
    private bool _lanMode = true;

    /// <summary>Creates the menu.</summary>
    public MultiplayerMenuScreen(UiAssets ui, Vector2I screen, MultiplayerSettings settings)
        : base(ui, screen, "art/screens/s_singlesetup/back.t2img")
    {
        _settings = settings;
        Text(new Vector2I(66, 40), "MULTIPLAYER", UiAssets.TitleFont);
        AddChild(Caption(66, 82, "Your name"));
        _name = Field(170, 78, 200, settings.Name);
        _name.TextChanged += t => _settings.Name = t;
        AddChild(Caption(66, 116, "Relay server"));
        _server = Field(170, 112, 200, settings.Server);
        _server.TextChanged += t => _settings.Server = t;
        AddChild(Caption(66, 150, "Lobby code"));
        _code = Field(170, 146, 110, string.Empty);
        _code.PlaceholderText = "ABC234";
        _list = new ItemList { Position = Origin + new Vector2I(66, 220), Size = new Vector2(460, 300), SelectMode = ItemList.SelectModeEnum.Single };
        AddChild(_list);
        _list.ItemActivated += i => JoinListed((int)i);
        _status = Caption(66, 530, string.Empty);
        AddChild(_status);

        const string Btn = "art/screens/s_singlesetup/btn.t2img";
        var size = new Vector2I(210, 27);
        Button(new Vector2I(555, 70), size, Btn, "CREATE INTERNET GAME", () => CreateInternet?.Invoke(_settings));
        Button(new Vector2I(555, 110), size, Btn, "JOIN BY CODE", () => JoinCode?.Invoke(_settings, _code.Text));
        Button(new Vector2I(555, 150), size, Btn, "INTERNET LOBBIES", RefreshInternet);
        Button(new Vector2I(555, 210), size, Btn, "HOST LAN GAME", () => HostLan?.Invoke(_settings));
        Button(new Vector2I(555, 250), size, Btn, "LAN GAMES", () =>
        {
            _lanMode = true;
            _status.Text = "Looking for games on the local network...";
        });
        Button(new Vector2I(555, 290), size, Btn, "JOIN SELECTED", () => JoinListed(_list.GetSelectedItems().FirstOrDefault(-1)));
        if (settings.Ticket is { } ticket)
        {
            Button(new Vector2I(555, 350), size, Btn, "RECONNECT", () => Reconnect?.Invoke(_settings, ticket));
            AddChild(Caption(555, 380, $"to lobby {ticket.Code}"));
        }

        Button(new Vector2I(555, 440), size, Btn, "REPLAYS", () => Replays?.Invoke());
        Button(new Vector2I(555, 554), size, Btn, "BACK", () => Back?.Invoke());
        try
        {
            _lan = new LanBrowserService();
        }
        catch (System.Net.Sockets.SocketException e)
        {
            _status.Text = "LAN browser unavailable: " + e.Message;
        }
    }

    /// <summary>CREATE INTERNET GAME.</summary>
    public event Action<MultiplayerSettings>? CreateInternet;

    /// <summary>JOIN BY CODE (relay from the settings).</summary>
    public event Action<MultiplayerSettings, string>? JoinCode;

    /// <summary>JOIN of a listed lobby (internet or LAN).</summary>
    public event Action<MultiplayerSettings, LobbyListing>? JoinListing;

    /// <summary>HOST LAN GAME.</summary>
    public event Action<MultiplayerSettings>? HostLan;

    /// <summary>RECONNECT.</summary>
    public event Action<MultiplayerSettings, ReconnectTicket>? Reconnect;

    /// <summary>REPLAYS.</summary>
    public event Action? Replays;

    /// <summary>BACK.</summary>
    public event Action? Back;

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (!_lanMode || _lan is null)
        {
            return;
        }

        _lan.Poll();
        Show(_lan.Lobbies);
    }

    /// <inheritdoc/>
    public override void _ExitTree() => _lan?.Dispose();

    private void RefreshInternet()
    {
        _lanMode = false;
        (string host, int port) = _settings.Endpoint();
        IReadOnlyList<LobbyListing>? lobbies = LobbyDirectory.Query(host, port, 1_500);
        _status.Text = lobbies is null ? $"No answer from {host}:{port}." : $"{lobbies.Count} public lobbies on {host}:{port}.";
        Show(lobbies ?? []);
    }

    private void Show(IReadOnlyList<LobbyListing> lobbies)
    {
        if (lobbies.SequenceEqual(_shown))
        {
            return;
        }

        _shown = lobbies;
        int selected = _list.GetSelectedItems().FirstOrDefault(-1);
        _list.Clear();
        foreach (LobbyListing l in lobbies)
        {
            _list.AddItem(string.Create(CultureInfo.InvariantCulture,
                $"{l.Name}   {l.Code}   {l.Players}/{l.MaxPlayers}{(l.InMatch ? "   in match" : string.Empty)}{(l.Version == Tzar2.Game.GameInfo.Version ? string.Empty : "   other version")}   {l.Host}:{l.Port}"));
        }

        if (selected >= 0 && selected < lobbies.Count)
        {
            _list.Select(selected);
        }
    }

    private void JoinListed(int index)
    {
        if (index >= 0 && index < _shown.Count)
        {
            JoinListing?.Invoke(_settings, _shown[index]);
        }
    }

    private LineEdit Field(int x, int y, int width, string text)
    {
        var field = new LineEdit { Position = Origin + new Vector2I(x, y), Size = new Vector2(width, 26), Text = text, MaxLength = 32 };
        AddChild(field);
        return field;
    }

    internal Label Caption(int x, int y, string text) => new() { Position = Origin + new Vector2I(x, y), Text = text, MouseFilter = MouseFilterEnum.Ignore };
}

/// <summary>
/// The multiplayer lobby (REQ-NET-08, ADR 0168): slots with player, civilization, team, ready flag and ping; the host
/// opens, closes and fills slots with computers and sets the map and options (settings are locked while ready, and
/// changing them clears everyone's ready flag); lobby chat; READY, START (host) and LEAVE.
/// </summary>
public partial class LobbyScreen : ArtScreen
{
    private readonly string[] _civilizations;
    private static readonly string[] SlotChoices = ["Open", "Closed", "Computer (Easy)", "Computer (Normal)", "Computer (Hard)"];
    private static readonly int[] Speeds = [50, 75, 100, 150, 200, 300];

    private readonly NetworkMatch _match;
    private readonly Control _rows = new() { MouseFilter = MouseFilterEnum.Pass };
    private readonly Control _hostPanel = new() { MouseFilter = MouseFilterEnum.Pass };
    private readonly RichTextLabel _chatLog;
    private readonly LineEdit _chatLine;
    private readonly Label _title;
    private readonly Label _status;
    private readonly TextureButton _ready;
    private readonly TextureButton _start;
    private readonly List<ChatLine> _lines = [];
    private LobbyView? _shown;
    private DropRule _drop = DropRule.AiTakeover;

    /// <summary>Creates the lobby screen for <paramref name="match"/>; the civilizations offered are <paramref name="rules"/>' (ADR 0211).</summary>
    public LobbyScreen(UiAssets ui, Vector2I screen, NetworkMatch match, Tzar2.Content.Compiled.Ruleset? rules = null)
        : base(ui, screen, "art/screens/s_singlesetup/back.t2img")
    {
        _match = match;
        _civilizations = SkirmishOptions.CivilizationsOf(rules);
        _title = new Label { Position = Origin + new Vector2I(66, 36), Text = "Connecting...", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_title);
        AddChild(_rows);
        AddChild(_hostPanel);
        _chatLog = new RichTextLabel { Position = Origin + new Vector2I(66, 380), Size = new Vector2(460, 140), ScrollFollowing = true, BbcodeEnabled = false };
        AddChild(_chatLog);
        _chatLine = new LineEdit { Position = Origin + new Vector2I(66, 524), Size = new Vector2(460, 26), PlaceholderText = "Chat (Enter to send)", MaxLength = 200 };
        _chatLine.TextSubmitted += text =>
        {
            if (text.Trim().Length > 0)
            {
                _match.Chat(text);
            }

            _chatLine.Clear();
        };
        AddChild(_chatLine);
        _status = new Label { Position = Origin + new Vector2I(66, 556), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_status);

        const string Btn = "art/screens/s_singlesetup/btn.t2img";
        var size = new Vector2I(210, 27);
        _ready = Button(new Vector2I(555, 474), size, Btn, "READY", ToggleReady);
        _start = Button(new Vector2I(555, 514), size, Btn, "START", () => _match.Start(new MatchRules(_drop)));
        Button(new Vector2I(555, 554), size, Btn, "LEAVE", () => Leave?.Invoke());
    }

    /// <summary>LEAVE (or the lobby ended).</summary>
    public event Action? Leave;

    /// <summary>The match: it starts loading when <see cref="NetworkMatch.Session"/> appears.</summary>
    public NetworkMatch Match => _match;

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _match.Poll();
        _match.DrainChat(_lines);
        foreach (ChatLine line in _lines)
        {
            _chatLog.AddText(line.Kind == ChatLineKind.System ? "* " + line.Text : $"{line.Sender}: {line.Text}");
            _chatLog.Newline();
        }

        _lines.Clear();
        if (_match.Stage == LobbyStage.Ended)
        {
            _status.Text = _match.EndReason ?? "The lobby ended.";
        }
        else if (_match.Stage == LobbyStage.Loading)
        {
            _status.Text = "Loading the match...";
        }

        if (_match.Lobby is { } lobby && !ReferenceEquals(lobby, _shown))
        {
            _shown = lobby;
            Rebuild(lobby);
        }
    }

    private void ToggleReady()
    {
        bool ready = _shown?.Slots.FirstOrDefault(s => s.IsLocal)?.Ready ?? false;
        _match.SetReady(!ready);
    }

    private void Rebuild(LobbyView lobby)
    {
        _title.Text = $"{lobby.Name}   —   code {lobby.Code}{(lobby.LocalIsHost ? "   (you host)" : string.Empty)}";
        foreach (Node child in _rows.GetChildren())
        {
            child.QueueFree();
        }

        for (int i = 0; i < lobby.Slots.Count; i++)
        {
            AddRow(lobby, lobby.Slots[i], 70 + (i * 34));
        }

        foreach (Node child in _hostPanel.GetChildren())
        {
            child.QueueFree();
        }

        AddSettings(lobby);
        LobbySlotView? me = lobby.Slots.FirstOrDefault(s => s.IsLocal);
        _ready.Visible = !lobby.LocalIsHost;
        _ready.Disabled = me is null;
        _start.Visible = lobby.LocalIsHost;
        _start.Disabled = lobby.Slots.Any(s => s.Kind == SlotKind.Human && !s.Ready);
        _status.Text = me is { Ready: true } && !lobby.LocalIsHost ? "You are ready (your choices are locked)." : string.Empty;
    }

    private void AddRow(LobbyView lobby, LobbySlotView slot, int y)
    {
        Color colour = Palette(slot.Slot);
        _rows.AddChild(new ColorRect { Position = Origin + new Vector2I(66, y + 4), Size = new Vector2(14, 14), Color = colour, MouseFilter = MouseFilterEnum.Ignore });
        string who = slot.Kind switch
        {
            SlotKind.Human => $"{slot.Name}{(slot.IsHost ? " (host)" : string.Empty)}{(slot.Connected ? string.Empty : " (dropped)")}",
            SlotKind.Computer => $"Computer ({slot.Choice.Difficulty})",
            SlotKind.Closed => "Closed",
            _ => "Open",
        };
        bool mine = slot.IsLocal && !slot.Ready;
        bool hostOther = lobby.LocalIsHost && !slot.IsHost;
        if (hostOther && slot.Kind != SlotKind.Human)
        {
            var kind = new OptionButton { Position = Origin + new Vector2I(86, y), Size = new Vector2(170, 24) };
            foreach (string c in SlotChoices)
            {
                kind.AddItem(c);
            }

            kind.Selected = slot.Kind switch { SlotKind.Closed => 1, SlotKind.Computer => 2 + (int)slot.Choice.Difficulty, _ => 0 };
            kind.ItemSelected += index => SetSlotKind(slot, (int)index);
            _rows.AddChild(kind);
        }
        else
        {
            _rows.AddChild(new Label { Position = Origin + new Vector2I(86, y + 2), Text = who, MouseFilter = MouseFilterEnum.Ignore });
        }

        if (hostOther && slot.Kind == SlotKind.Human)
        {
            var kick = new Button { Position = Origin + new Vector2I(260, y), Size = new Vector2(40, 24), Text = "kick" };
            kick.Pressed += () => _match.SetSlot(slot.Slot, SlotKind.Open);
            _rows.AddChild(kick);
        }

        if (slot.Kind is SlotKind.Human or SlotKind.Computer)
        {
            bool editable = mine || (lobby.LocalIsHost && slot.Kind == SlotKind.Computer);
            var civ = new OptionButton { Position = Origin + new Vector2I(306, y), Size = new Vector2(90, 24), Disabled = !editable };
            foreach (string c in _civilizations)
            {
                civ.AddItem(c);
            }

            civ.Selected = Math.Max(0, Array.IndexOf(_civilizations, slot.Choice.Civilization));
            civ.ItemSelected += index => SetChoice(slot, slot.Choice with { Civilization = _civilizations[(int)index] });
            _rows.AddChild(civ);
            var team = new OptionButton { Position = Origin + new Vector2I(400, y), Size = new Vector2(70, 24), Disabled = !editable };
            team.AddItem("no team");
            for (int t = 1; t <= 4; t++)
            {
                team.AddItem($"team {t}");
            }

            team.Selected = Math.Clamp((int)slot.Choice.Team, 0, 4);
            team.ItemSelected += index => SetChoice(slot, slot.Choice with { Team = (byte)index });
            _rows.AddChild(team);
            string state = slot.Kind == SlotKind.Human ? $"{(slot.Ready ? "ready" : "not ready")}  {slot.PingMs} ms" : string.Empty;
            _rows.AddChild(new Label { Position = Origin + new Vector2I(476, y + 2), Text = state, MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    private void AddSettings(LobbyView lobby)
    {
        MatchSettings s = lobby.Settings;
        bool host = lobby.LocalIsHost;
        _hostPanel.AddChild(new Label { Position = Origin + new Vector2I(555, 40), Text = "MATCH SETTINGS", MouseFilter = MouseFilterEnum.Ignore });
        int y = 66;
        Option("Map", Enum.GetNames<Content.MapGen.RandomMapType>(), (int)s.MapType, i => _match.SetSettings(s with { Map = MapSourceKind.RandomMap, MapType = (Content.MapGen.RandomMapType)i }), ref y, host);
        Option("Size", Enum.GetNames<Content.MapGen.RandomMapSize>(), (int)s.MapSize, i => _match.SetSettings(s with { MapSize = (Content.MapGen.RandomMapSize)i }), ref y, host);
        Option("Terrain", Enum.GetNames<Content.MapGen.MapTerrainSet>(), (int)s.Terrain, i => _match.SetSettings(s with { Terrain = (Content.MapGen.MapTerrainSet)i }), ref y, host);
        Option("Speed", Speeds.Select(v => v + " %").ToArray(), Math.Max(0, Array.IndexOf(Speeds, s.SpeedPercent)), i => _match.SetSettings(s with { SpeedPercent = Speeds[i] }), ref y, host);
        Option("Dropped players", ["computer takes over", "neutral", "defeated"], (int)_drop, i => _drop = (DropRule)i, ref y, host);
        Check("No fog", s.NoFog, v => _match.SetSettings(s with { NoFog = v }), ref y, host);
        Check("AI Assist", s.AiAssist, v => _match.SetSettings(s with { AiAssist = v }), ref y, host);
        Check("Cheats allowed", s.CheatsAllowed, v => _match.SetSettings(s with { CheatsAllowed = v && !s.Ranked }), ref y, host && !s.Ranked);
        Check("Ranked (never cheats)", s.Ranked, v => _match.SetSettings(s with { Ranked = v, CheatsAllowed = false }), ref y, host);
        if (host)
        {
            var reseed = new Button { Position = Origin + new Vector2I(555, y), Size = new Vector2(210, 24), Text = $"New map seed ({s.Seed % 100000})" };
            reseed.Pressed += () => _match.SetSettings(s with { Seed = (ulong)Random.Shared.NextInt64() });
            _hostPanel.AddChild(reseed);
        }
    }

    private void Option(string caption, string[] items, int selected, Action<int> changed, ref int y, bool enabled)
    {
        _hostPanel.AddChild(new Label { Position = Origin + new Vector2I(555, y + 2), Text = caption, MouseFilter = MouseFilterEnum.Ignore });
        var option = new OptionButton { Position = Origin + new Vector2I(660, y), Size = new Vector2(150, 24), Disabled = !enabled };
        foreach (string item in items)
        {
            option.AddItem(item);
        }

        option.Selected = Math.Clamp(selected, 0, items.Length - 1);
        option.ItemSelected += index => changed((int)index);
        _hostPanel.AddChild(option);
        y += 30;
    }

    private void Check(string caption, bool value, Action<bool> changed, ref int y, bool enabled)
    {
        var box = new CheckBox { Position = Origin + new Vector2I(555, y), Text = caption, ButtonPressed = value, Disabled = !enabled };
        box.Toggled += changed.Invoke;
        _hostPanel.AddChild(box);
        y += 28;
    }

    private void SetSlotKind(LobbySlotView slot, int choice)
    {
        switch (choice)
        {
            case 0: _match.SetSlot(slot.Slot, SlotKind.Open); break;
            case 1: _match.SetSlot(slot.Slot, SlotKind.Closed); break;
            default: _match.SetSlot(slot.Slot, SlotKind.Computer, slot.Choice with { Difficulty = (Difficulty)(choice - 2) }); break;
        }
    }

    private void SetChoice(LobbySlotView slot, SlotChoice choice)
    {
        if (slot.IsLocal)
        {
            _match.SetChoice(choice);
        }
        else
        {
            _match.SetComputerChoice(slot.Slot, choice);
        }
    }

    private static Color Palette(int slot) => slot switch
    {
        0 => new Color(0.85f, 0.15f, 0.1f),
        1 => new Color(0.1f, 0.3f, 0.9f),
        2 => new Color(0.1f, 0.7f, 0.2f),
        3 => new Color(0.9f, 0.85f, 0.1f),
        4 => new Color(0.6f, 0.2f, 0.8f),
        5 => new Color(0.95f, 0.5f, 0.1f),
        6 => new Color(0.1f, 0.8f, 0.8f),
        _ => new Color(0.9f, 0.9f, 0.9f),
    };
}

/// <summary>The replay browser: the recorded matches in <c>user://replays</c> (REQ-NET-09), newest first.</summary>
public partial class ReplayBrowserScreen : ArtScreen
{
    private readonly ItemList _list;
    private readonly string[] _files;

    /// <summary>Creates the browser over <paramref name="directory"/>.</summary>
    public ReplayBrowserScreen(UiAssets ui, Vector2I screen, string directory)
        : base(ui, screen, "art/screens/s_singlesetup/back.t2img")
    {
        Text(new Vector2I(66, 40), "REPLAYS", UiAssets.TitleFont);
        _files = System.IO.Directory.Exists(directory)
            ? System.IO.Directory.GetFiles(directory, "*.t2rep").OrderByDescending(System.IO.File.GetLastWriteTimeUtc).ToArray()
            : [];
        _list = new ItemList { Position = Origin + new Vector2I(66, 80), Size = new Vector2(460, 460) };
        foreach (string f in _files)
        {
            _list.AddItem($"{System.IO.Path.GetFileNameWithoutExtension(f)}   {System.IO.File.GetLastWriteTime(f):yyyy-MM-dd HH:mm}");
        }

        _list.ItemActivated += i => Play?.Invoke(_files[(int)i]);
        AddChild(_list);
        if (_files.Length == 0)
        {
            AddChild(new Label { Position = Origin + new Vector2I(70, 90), Text = "No replays yet: every match is recorded here.", MouseFilter = MouseFilterEnum.Ignore });
        }

        const string Btn = "art/screens/s_singlesetup/btn.t2img";
        var size = new Vector2I(210, 27);
        Button(new Vector2I(555, 499), size, Btn, "PLAY", () =>
        {
            int selected = _list.GetSelectedItems().FirstOrDefault(-1);
            if (selected >= 0)
            {
                Play?.Invoke(_files[selected]);
            }
        });
        Button(new Vector2I(555, 554), size, Btn, "BACK", () => Back?.Invoke());
    }

    /// <summary>PLAY of a replay file.</summary>
    public event Action<string>? Play;

    /// <summary>BACK.</summary>
    public event Action? Back;
}
