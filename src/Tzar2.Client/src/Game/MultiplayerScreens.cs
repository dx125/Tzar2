using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;

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
/// The pieces of the original's multiplayer screens (UI-A6, UI-A7, UI-F1): Godot's controls in the original font
/// (<see cref="UiAssets.ScreenTheme"/>), the art's state buttons, check gems, colour cells and texts from STRINGS.INI.
/// </summary>
public abstract partial class MultiplayerArtScreen : ArtScreen
{
    /// <summary>The s_multisetup slot buttons and the s_singlesetup ones: two 210 × 27 states.</summary>
    protected const string SlotButton = "art/screens/s_multisetup/btn.t2img";

    private readonly Func<string, string, string, string>? _text;

    /// <summary>Creates the screen on <paramref name="background"/>.</summary>
    protected MultiplayerArtScreen(UiAssets ui, Vector2I screen, string background, Func<string, string, string, string>? text)
        : base(ui, screen, background)
    {
        _text = text;
        Theme = ui.ScreenTheme();
    }

    /// <summary>A STRINGS.INI text (the part before a <c>|</c>), or <paramref name="fallback"/>.</summary>
    protected string T(string section, string key, string fallback) => _text?.Invoke(section, key, fallback) ?? fallback;

    /// <summary>Gold, the colour of the screens' texts.</summary>
    protected static readonly Color Gold = new(1f, 0.92f, 0.65f);

    /// <summary>
    /// A line of text in the original font at a position relative to the art, cut to <paramref name="width"/> (0: not
    /// cut); <see cref="SetLine"/> changes it.
    /// </summary>
    protected TextureRect Caption(int x, int y, string text, int width = 0) => Line(this, Origin + new Vector2I(x, y), text, width);

    /// <summary>A line of text in <paramref name="parent"/> at <paramref name="at"/> (its coordinates).</summary>
    protected TextureRect Line(Control parent, Vector2I at, string text, int width = 0)
    {
        var rect = new TextureRect { Position = at, MouseFilter = MouseFilterEnum.Ignore };
        parent.AddChild(rect);
        SetLine(rect, text, width);
        return rect;
    }

    /// <summary>Changes a line's text.</summary>
    protected void SetLine(TextureRect line, string text, int width = 0)
    {
        ArgumentNullException.ThrowIfNull(line);
        line.Texture = text.Length == 0 ? null : Ui.Text(width > 0 ? Fit(text, width, UiAssets.SmallFont) : text, UiAssets.SmallFont, Gold);
    }

    /// <summary>Replaces <paramref name="box"/>'s lines with <paramref name="text"/> wrapped to <paramref name="width"/>.</summary>
    protected void SetParagraph(Control box, string text, int width)
    {
        ArgumentNullException.ThrowIfNull(box);
        foreach (Node child in box.GetChildren())
        {
            child.QueueFree();
        }

        int y = 0;
        foreach (string line in Wrap(text, width))
        {
            Line(box, new Vector2I(0, y), line);
            y += 14;
        }
    }

    /// <summary>A text field on the art (no box of its own).</summary>
    protected LineEdit Field(int x, int y, int width, string text, int max = 32)
    {
        var field = new LineEdit { Position = Origin + new Vector2I(x, y), Size = new Vector2(width, 18), Text = text, MaxLength = max };
        AddChild(field);
        return field;
    }

    /// <summary>A picture at a position relative to the art (null texture: nothing drawn).</summary>
    protected TextureRect Picture(Vector2I at, Texture2D? texture, Control? parent = null)
    {
        var rect = new TextureRect { Texture = texture, Position = (parent is null ? Origin : Vector2I.Zero) + at, MouseFilter = MouseFilterEnum.Ignore };
        (parent ?? this).AddChild(rect);
        return rect;
    }

    /// <summary>
    /// A button from a strip of four states side by side (normal, lit, pressed, disabled; s_multiplayer <c>btn_start</c>,
    /// <c>btn_cancel</c>), its label baked in.
    /// </summary>
    protected TextureButton StateButton(Vector2I at, string image, Vector2I size, Action pressed)
    {
        Texture2D? State(int i) => Ui.Region(image, new Rect2I(i * size.X, 0, size.X, size.Y));
        var button = new TextureButton
        {
            Position = Origin + at, Size = size, TextureNormal = State(0), TextureHover = State(1), TexturePressed = State(2), TextureDisabled = State(3),
            IgnoreTextureSize = true, StretchMode = TextureButton.StretchModeEnum.Keep,
        };
        button.Pressed += pressed;
        AddChild(button);
        return button;
    }

    /// <summary>Cell <paramref name="index"/> of a vertical strip of <paramref name="size"/> cells (btn_color, btn_digits, chk_check...).</summary>
    protected Texture2D? CellOf(string image, int index, Vector2I size) => Ui.Region(image, new Rect2I(0, index * size.Y, size.X, size.Y));
}

/// <summary>
/// The multiplayer menu (REQ-NET-08, ADR 0168) on the original's connection screen (<c>S_MULTISETUP</c>, UI-A6): the
/// player's name in the name slot, local network or internet, the relay server and a lobby code, the games found and
/// the selected game's information in the two boxes, and CREATE, SEARCH, JOIN and BACK in the four slots
/// (<c>mp_search</c>). Tzar2 adds the relay server, the lobby code, reconnecting and the replays.
/// </summary>
public partial class MultiplayerMenuScreen : MultiplayerArtScreen
{
    private readonly MultiplayerSettings _settings;
    private readonly LineEdit _name;
    private readonly LineEdit _server;
    private readonly LineEdit _code;
    private readonly ItemList _list;
    private readonly TextureRect _status;
    private readonly Control _info = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly TextureRect _lanCheck;
    private readonly TextureRect _netCheck;
    private readonly LanBrowserService? _lan;
    private IReadOnlyList<LobbyListing> _shown = [];
    private bool _lanMode = true;

    /// <summary>Creates the menu; <paramref name="text"/> looks up STRINGS.INI.</summary>
    public MultiplayerMenuScreen(UiAssets ui, Vector2I screen, MultiplayerSettings settings, Func<string, string, string, string>? text = null)
        : base(ui, screen, "art/screens/s_multisetup/back.t2img", text)
    {
        _settings = settings;
        Caption(70, 52, T("mp_search", "t_pl_name", "Player Name"));
        _name = Field(80, 75, 172, settings.Name);
        _name.TextChanged += t => _settings.Name = t;

        // Local network or the relay server (the original's protocols were IPX and TCP/IP).
        _lanCheck = Picture(new Vector2I(70, 118), null);
        Caption(96, 120, "Local network (LAN)");
        Area(new Rect2I(66, 114, 200, 26), () => SetMode(lan: true));
        _netCheck = Picture(new Vector2I(70, 146), null);
        Caption(96, 148, "Internet (relay server)");
        Area(new Rect2I(66, 142, 200, 26), () => SetMode(lan: false));
        Caption(70, 188, T("mp_search", "t_ip", "Enter IP Address"));
        Picture(new Vector2I(70, 204), Ui.Texture("art/screens/s_multisetup/big frame.t2img"));
        _server = Field(78, 208, 234, settings.Server);
        _server.TextChanged += t => _settings.Server = t;
        Caption(70, 240, "Lobby code");
        Picture(new Vector2I(70, 256), Ui.Texture("art/screens/s_multisetup/big frame.t2img"));
        _code = Field(78, 260, 234, string.Empty, 8);
        _code.PlaceholderText = "ABC234";
        if (settings.Ticket is { } ticket)
        {
            Caption(70, 300, $"RECONNECT to lobby {ticket.Code}");
            Area(new Rect2I(66, 296, 260, 20), () => Reconnect?.Invoke(_settings, ticket));
        }

        Caption(70, 324, "REPLAYS");
        Area(new Rect2I(66, 320, 120, 20), () => Replays?.Invoke());

        Caption(425, 50, T("mp_search", "t_games", "Games Found"));
        _list = new ItemList { Position = Origin + new Vector2I(428, 80), Size = new Vector2(320, 182), SelectMode = ItemList.SelectModeEnum.Single };
        AddChild(_list);
        _list.ItemActivated += i => JoinListed((int)i);
        _list.ItemSelected += i => ShowInfo((int)i);
        Caption(425, 294, T("mp_search", "t_inform", "Game Information"));
        _info.Position = Origin + new Vector2I(430, 324);
        AddChild(_info);
        _status = Caption(425, 280, string.Empty, 340);

        var size = new Vector2I(210, 27);
        Button(new Vector2I(72, 360), size, SlotButton, T("mp_search", "create", "CREATE"), Create);
        Button(new Vector2I(72, 424), size, SlotButton, T("mp_search", "search", "SEARCH"), Search);
        Button(new Vector2I(72, 488), size, SlotButton, T("mp_search", "join", "JOIN"), Join);
        Button(new Vector2I(72, 552), size, SlotButton, T("mp_search", "back", "BACK"), () => Back?.Invoke());
        try
        {
            _lan = new LanBrowserService();
        }
        catch (System.Net.Sockets.SocketException e)
        {
            Status = "LAN browser unavailable: " + e.Message;
        }

        SetMode(lan: true);
    }

    private string Status
    {
        set => SetLine(_status, value, 330);
    }

    /// <summary>CREATE in internet mode: a public lobby on the relay server.</summary>
    public event Action<MultiplayerSettings>? CreateInternet;

    /// <summary>JOIN with a lobby code (relay from the settings).</summary>
    public event Action<MultiplayerSettings, string>? JoinCode;

    /// <summary>JOIN of a listed lobby (internet or LAN).</summary>
    public event Action<MultiplayerSettings, LobbyListing>? JoinListing;

    /// <summary>CREATE in LAN mode: host a game on the local network.</summary>
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

    private void SetMode(bool lan)
    {
        _lanMode = lan;
        _lanCheck.Texture = CellOf("art/screens/s_multiplayer/chk_check.t2img", lan ? 1 : 0, new Vector2I(20, 20));
        _netCheck.Texture = CellOf("art/screens/s_multiplayer/chk_check.t2img", lan ? 0 : 1, new Vector2I(20, 20));
        Status = lan ? "Looking for games on the local network..." : string.Empty;
        if (!lan)
        {
            Show([]);
        }
    }

    private void Create()
    {
        if (_lanMode)
        {
            HostLan?.Invoke(_settings);
        }
        else
        {
            CreateInternet?.Invoke(_settings);
        }
    }

    private void Search()
    {
        if (_lanMode)
        {
            Status = "Looking for games on the local network...";
            return;
        }

        (string host, int port) = _settings.Endpoint();
        IReadOnlyList<LobbyListing>? lobbies = LobbyDirectory.Query(host, port, 1_500);
        Status = lobbies is null ? $"No answer from {host}:{port}." : $"{lobbies.Count} public lobbies on {host}:{port}.";
        Show(lobbies ?? []);
    }

    // JOIN: the selected game, else the lobby code on the relay server.
    private void Join()
    {
        int selected = _list.GetSelectedItems().FirstOrDefault(-1);
        if (selected >= 0)
        {
            JoinListed(selected);
        }
        else if (_code.Text.Trim().Length > 0)
        {
            JoinCode?.Invoke(_settings, _code.Text.Trim());
        }
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
            _list.AddItem(string.Create(CultureInfo.InvariantCulture, $"{l.Name}   {l.Players}/{l.MaxPlayers}{(l.InMatch ? "   in match" : string.Empty)}"));
        }

        if (selected >= 0 && selected < lobbies.Count)
        {
            _list.Select(selected);
        }

        ShowInfo(selected >= 0 && selected < lobbies.Count ? selected : -1);
    }

    private void ShowInfo(int index)
    {
        if (index < 0 || index >= _shown.Count)
        {
            SetParagraph(_info, string.Empty, 340);
            return;
        }

        LobbyListing l = _shown[index];
        SetParagraph(_info, string.Create(CultureInfo.InvariantCulture,
            $"{T("mp_search", "t_gamename", "Game Name")}: {l.Name}\nCode: {l.Code}\nPlayers: {l.Players}/{l.MaxPlayers}\n{(l.InMatch ? "In a match\n" : string.Empty)}Version: {l.Version}{(l.Version == Tzar2.Game.GameInfo.Version ? string.Empty : " (other)")}\n{l.Host}:{l.Port}"), 340);
    }

    private void JoinListed(int index)
    {
        if (index >= 0 && index < _shown.Count)
        {
            JoinListing?.Invoke(_settings, _shown[index]);
        }
    }
}

/// <summary>
/// The multiplayer lobby (REQ-NET-08, ADR 0168) on the original's setup screen (<c>S_MULTIPLAYER</c>, UI-A7). It draws
/// <see cref="LobbyScreenModel"/> (SHELL-016, SHELL-017, SHELL-018). One <c>row edit</c> per slot holds the colour
/// (<c>btn_color</c>), the name or the slot's kind (<c>btn_name</c>), the AI script, the ready gem (<c>chk_check</c>),
/// the civilization between arrows (<c>btn_civil</c>) and the team (<c>btn_digits</c>). The chat is in the lower box,
/// the settings in the right panel, and START GAME and CANCEL GAME (<c>btn_start</c>, <c>btn_cancel</c>) by them. Where
/// the original puts each piece waits for the owner's screenshot (UI review question 2).
/// </summary>
public partial class LobbyScreen : MultiplayerArtScreen
{
    private const string Art = "art/screens/s_multiplayer/";
    private static readonly Vector2I Cell = new(20, 20);

    private readonly string[] _civilizations;
    private readonly NetworkMatch _match;
    private readonly Func<int, Color> _colour;
    private readonly Control _rows = new() { MouseFilter = MouseFilterEnum.Pass };
    private readonly Control _settingsPanel = new() { MouseFilter = MouseFilterEnum.Pass };
    private readonly RichTextLabel _chatLog;
    private readonly LineEdit _chatLine;
    private readonly TextureRect _title;
    private readonly TextureRect _status;
    private readonly TextureButton _start;
    private readonly List<ChatLine> _lines = [];
    private LobbyView? _shown;
    private DropRule _drop = DropRule.AiTakeover;

    /// <summary>
    /// Creates the lobby screen for <paramref name="match"/>; <paramref name="colour"/> gives a colour ramp's swatch, and the
    /// civilizations offered are <paramref name="rules"/>' (ADR 0211).
    /// </summary>
    public LobbyScreen(UiAssets ui, Vector2I screen, NetworkMatch match, Func<int, Color> colour, Func<string, string, string, string>? text = null, Tzar2.Content.Compiled.Ruleset? rules = null)
        : base(ui, screen, Art + "back.t2img", text)
    {
        _match = match;
        _colour = colour;
        _civilizations = SkirmishOptions.CivilizationsOf(rules);
        _title = Caption(75, 28, "Connecting...", 470);
        _rows.Position = Origin;
        AddChild(_rows);
        Caption(575, 78, T("mp_mplayer", "settings", "SETTINGS"));
        var scroll = new ScrollContainer { Position = Origin + new Vector2I(572, 104), Size = new Vector2(208, 418), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _settingsPanel.CustomMinimumSize = new Vector2(196, 0);
        scroll.AddChild(_settingsPanel);
        AddChild(scroll);
        _chatLog = new RichTextLabel { Position = Origin + new Vector2I(74, 342), Size = new Vector2(440, 140), ScrollFollowing = true, BbcodeEnabled = false };
        AddChild(_chatLog);
        _chatLine = Field(74, 509, 468, string.Empty, 200);
        _chatLine.PlaceholderText = "Chat (Enter to send)";
        _chatLine.TextSubmitted += line =>
        {
            if (line.Trim().Length > 0)
            {
                _match.Chat(line);
            }

            _chatLine.Clear();
        };
        _status = Caption(74, 540, string.Empty, 330);
        var button = new Vector2I(128, 32);
        _start = StateButton(new Vector2I(611, 551), Art + "btn_start.t2img", button, () => _match.Start(new MatchRules(_drop)));
        StateButton(new Vector2I(414, 551), Art + "btn_cancel.t2img", button, () => Leave?.Invoke());
    }

    /// <summary>CANCEL GAME (or the lobby ended).</summary>
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
            SetLine(_status, _match.EndReason ?? "The lobby ended.", 330);
        }
        else if (_match.Stage == LobbyStage.Loading)
        {
            SetLine(_status, "Loading the match...", 330);
        }

        if (_match.Lobby is { } lobby && !ReferenceEquals(lobby, _shown))
        {
            _shown = lobby;
            Rebuild(lobby);
        }
    }

    private void Rebuild(LobbyView lobby)
    {
        var model = new LobbyScreenModel(lobby, _match.AiScripts, _match.CustomMaps);
        SetLine(_title, $"{lobby.Name}   —   code {lobby.Code}{(lobby.LocalIsHost ? "   (you host)" : string.Empty)}", 470);
        foreach (Node child in _rows.GetChildren())
        {
            child.QueueFree();
        }

        for (int i = 0; i < lobby.Slots.Count && i < 8; i++)
        {
            AddRow(lobby, lobby.Slots[i], model.Rows[i], model, new Vector2I(75, 50 + (i * 33)));
        }

        foreach (Node child in _settingsPanel.GetChildren())
        {
            child.QueueFree();
        }

        AddSettings(lobby, model);
        LobbySlotView? me = lobby.Slots.FirstOrDefault(s => s.IsLocal);
        _start.Visible = lobby.LocalIsHost;
        _start.Disabled = !model.CanStart;
        SetLine(_status, model.MapNotice ?? (me is { Ready: true } && !lobby.LocalIsHost ? "You are ready (your choices are locked)." : !lobby.LocalIsHost ? "Click your gem when you are ready." : string.Empty), 330);
    }

    private void AddRow(LobbyView lobby, LobbySlotView slot, LobbyRow row, LobbyScreenModel model, Vector2I at)
    {
        Picture(at, Ui.Texture(Art + "row edit.t2img"), _rows);

        // The colour, in the left square: a click takes the next free one.
        if (row.Colour >= 0)
        {
            Picture(at + new Vector2I(7, 7), CellOf(Art + "btn_color.t2img", row.Colour, Cell), _rows);
            if (row.CanChoose && row.Colours.Count > 1)
            {
                int next = row.Colours[(row.Colours.ToList().IndexOf(row.Colour) + 1) % row.Colours.Count];
                RowArea(at + new Vector2I(2, 2), new Vector2I(29, 29), () => SetChoice(slot, model.WithColour(slot.Slot, next)));
            }
        }

        // The name, or for the host the slot's kind: a click turns Open, Computer (easy, normal, hard) and Disabled.
        bool hostOther = lobby.LocalIsHost && !slot.IsHost;
        if (slot.Kind == SlotKind.Human)
        {
            Picture(at + new Vector2I(40, 0), CellOf(Art + "btn_name.t2img", 0, new Vector2I(120, 20)), _rows);
            string name = $"{slot.Name}{(slot.IsHost ? " (host)" : string.Empty)}{(slot.Connected ? string.Empty : " (dropped)")}  {slot.PingMs} ms";
            RowLabel(at + new Vector2I(40, 3), name, 170);
            Picture(at + new Vector2I(214, 7), CellOf(Art + "chk_check.t2img", slot.Ready || slot.IsHost ? 1 : 0, Cell), _rows);
            if (slot.IsLocal && !slot.IsHost && (slot.Ready || model.HasMap))
            {
                RowArea(at + new Vector2I(210, 3), new Vector2I(28, 27), () => _match.SetReady(!slot.Ready));
            }

            if (hostOther)
            {
                RowLabel(at + new Vector2I(150, 20), "kick", 40);
                RowArea(at + new Vector2I(146, 20), new Vector2I(40, 13), () => _match.SetSlot(slot.Slot, SlotKind.Open));
            }
        }
        else
        {
            int kindCell = slot.Kind switch { SlotKind.Computer => 1, SlotKind.Closed => 2, _ => 3 }; // cell 0 is the empty name box
            Picture(at + new Vector2I(40, 0), CellOf(Art + "btn_name.t2img", kindCell, new Vector2I(120, 20)), _rows);
            if (slot.Kind == SlotKind.Computer)
            {
                RowLabel(at + new Vector2I(164, 3), slot.Choice.Difficulty.ToString(), 70);
            }

            if (hostOther)
            {
                RowArea(at + new Vector2I(36, 0), new Vector2I(200, 20), () => NextKind(slot));
            }
        }

        if (slot.Kind is not (SlotKind.Human or SlotKind.Computer))
        {
            return;
        }

        // The kingdom's AI script under the name (SHELL-016, SHELL-017).
        if (row.Script >= 0 && row.Script < model.Scripts.Count)
        {
            RowLabel(at + new Vector2I(40, 20), "AI: " + model.Scripts[row.Script].Name, 104);
            if (row.CanChoose && model.Scripts.Count > 1)
            {
                RowArea(at + new Vector2I(36, 20), new Vector2I(108, 13), () => SetChoice(slot, model.WithScript(slot.Slot, (row.Script + 1) % model.Scripts.Count)));
            }
        }

        // The civilization between the arrows, and the team in the right square.
        int civ = Math.Max(0, Array.IndexOf(_civilizations, slot.Choice.Civilization));
        int civs = _civilizations.Length;
        Picture(at + new Vector2I(296, 6), CellOf(Art + "btn_civil.t2img", civ, new Vector2I(80, 20)), _rows);
        Picture(at + new Vector2I(437, 7), CellOf(Art + "btn_digits.t2img", Math.Clamp((int)slot.Choice.Team, 0, 8), Cell), _rows);
        if (row.CanChoose)
        {
            RowArea(at + new Vector2I(243, 3), new Vector2I(30, 27), () => SetChoice(slot, slot.Choice with { Civilization = _civilizations[(civ + civs - 1) % civs] }));
            RowArea(at + new Vector2I(398, 3), new Vector2I(30, 27), () => SetChoice(slot, slot.Choice with { Civilization = _civilizations[(civ + 1) % civs] }));
            RowArea(at + new Vector2I(432, 3), new Vector2I(29, 27), () => SetChoice(slot, slot.Choice with { Team = (byte)((slot.Choice.Team + 1) % 5) }));
        }
    }

    // The match settings in the right panel, in the model's order: a value between arrows, or a gem.
    private void AddSettings(LobbyView lobby, LobbyScreenModel model)
    {
        int y = 0;
        foreach (LobbySetting setting in model.Settings)
        {
            LobbySettingKey key = setting.Key;
            if (setting.IsCheck)
            {
                Check(setting.Caption, setting.Selected == 1, setting.Enabled, on => model.Choose(_match, key, on ? 1 : 0), ref y);
            }
            else if (setting.IsNumber)
            {
                Number(setting, i => model.Choose(_match, key, i), ref y);
            }
            else
            {
                Option(setting.Caption, setting.Items, setting.Selected, setting.Enabled, i => model.Choose(_match, key, i), ref y);
            }
        }

        Option("Dropped players", ["computer takes over", "neutral", "defeated"], (int)_drop, lobby.LocalIsHost, i => _drop = (DropRule)i, ref y);
        if (lobby.LocalIsHost)
        {
            MatchSettings s = lobby.Settings;
            PanelLabel(new Vector2(0, y), $"New map seed ({s.Seed % 100000})", 196);
            PanelArea(new Vector2(0, y - 2), new Vector2(196, 18), () => _match.SetSettings(s with { Seed = (ulong)Random.Shared.NextInt64() }));
            y += 22;
        }

        _settingsPanel.CustomMinimumSize = new Vector2(196, y);
    }

    private void Option(string caption, IReadOnlyList<string> items, int selected, bool enabled, Action<int> changed, ref int y)
    {
        PanelLabel(new Vector2(0, y), caption, 196);
        int index = Math.Clamp(selected, 0, Math.Max(0, items.Count - 1));
        PanelLabel(new Vector2(22, y + 16), items.Count > 0 ? items[index] : string.Empty, 150);
        if (enabled && items.Count > 1)
        {
            PanelPicture(new Vector2(0, y + 15), Ui.Texture(Art + "btn_dn.t2img"));
            PanelPicture(new Vector2(176, y + 15), Ui.Texture(Art + "btn_up.t2img"));
            PanelArea(new Vector2(0, y + 15), new Vector2(20, 20), () => changed((index + items.Count - 1) % items.Count));
            PanelArea(new Vector2(176, y + 15), new Vector2(20, 20), () => changed((index + 1) % items.Count));
        }

        y += 40;
    }

    // The original's value field (time or score limit) under its caption.
    private void Number(LobbySetting setting, Action<int> changed, ref int y)
    {
        PanelLabel(new Vector2(0, y), setting.Caption, 196);
        var number = new SpinBox { Position = new Vector2(22, y + 16), Size = new Vector2(150, 20), MinValue = setting.Min, MaxValue = setting.Max, Value = setting.Selected, Editable = setting.Enabled };
        number.ValueChanged += v => changed((int)v);
        _settingsPanel.AddChild(number);
        y += 40;
    }

    private void Check(string caption, bool on, bool enabled, Action<bool> changed, ref int y)
    {
        PanelPicture(new Vector2(0, y), CellOf(Art + "chk_check.t2img", on ? 1 : 0, Cell));
        PanelLabel(new Vector2(26, y + 3), caption, 170);
        if (enabled)
        {
            PanelArea(new Vector2(0, y), new Vector2(196, 20), () => changed(!on));
        }

        y += 24;
    }

    private void NextKind(LobbySlotView slot)
    {
        // Open → Computer easy → normal → hard → Disabled → Open.
        if (slot.Kind == SlotKind.Open)
        {
            _match.SetSlot(slot.Slot, SlotKind.Computer, slot.Choice with { Difficulty = Difficulty.Easy });
        }
        else if (slot.Kind == SlotKind.Computer && slot.Choice.Difficulty != Difficulty.Hard)
        {
            _match.SetSlot(slot.Slot, SlotKind.Computer, slot.Choice with { Difficulty = slot.Choice.Difficulty + 1 });
        }
        else if (slot.Kind == SlotKind.Computer)
        {
            _match.SetSlot(slot.Slot, SlotKind.Closed);
        }
        else
        {
            _match.SetSlot(slot.Slot, SlotKind.Open);
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

    private void RowLabel(Vector2I at, string text, int width) => Line(_rows, at, text, width);

    private void RowArea(Vector2I at, Vector2I size, Action pressed)
    {
        var button = new Button { Position = at, Size = size, Flat = true, Modulate = new Color(1, 1, 1, 0.02f) };
        button.Pressed += pressed;
        _rows.AddChild(button);
    }

    private void PanelLabel(Vector2 at, string text, int width) => Line(_settingsPanel, (Vector2I)at, text, width);

    private void PanelPicture(Vector2 at, Texture2D? texture)
        => _settingsPanel.AddChild(new TextureRect { Texture = texture, Position = at, MouseFilter = MouseFilterEnum.Ignore });

    private void PanelArea(Vector2 at, Vector2 size, Action pressed)
    {
        var button = new Button { Position = at, Size = size, Flat = true, Modulate = new Color(1, 1, 1, 0.02f) };
        button.Pressed += pressed;
        _settingsPanel.AddChild(button);
    }
}

/// <summary>
/// The replay browser (REQ-NET-09) on the connection screen's art (<c>S_MULTISETUP</c>, UI-A6): the recorded matches in
/// <c>user://replays</c>, newest first, in the upper box, the selected one's details in the lower, PLAY and BACK in the
/// slots.
/// </summary>
public partial class ReplayBrowserScreen : MultiplayerArtScreen
{
    private readonly ItemList _list;
    private readonly Control _info = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly string[] _files;

    /// <summary>Creates the browser over <paramref name="directory"/>.</summary>
    public ReplayBrowserScreen(UiAssets ui, Vector2I screen, string directory, Func<string, string, string, string>? text = null)
        : base(ui, screen, "art/screens/s_multisetup/back.t2img", text)
    {
        Text(new Vector2I(78, 76), "REPLAYS", UiAssets.BoldFont);
        _files = System.IO.Directory.Exists(directory)
            ? System.IO.Directory.GetFiles(directory, "*.t2rep").OrderByDescending(System.IO.File.GetLastWriteTimeUtc).ToArray()
            : [];
        _list = new ItemList { Position = Origin + new Vector2I(428, 80), Size = new Vector2(320, 182) };
        foreach (string f in _files)
        {
            _list.AddItem(string.Create(CultureInfo.InvariantCulture, $"{System.IO.Path.GetFileNameWithoutExtension(f)}   {System.IO.File.GetLastWriteTime(f):yyyy-MM-dd HH:mm}"));
        }

        _list.ItemActivated += i => Play?.Invoke(_files[(int)i]);
        _list.ItemSelected += i => ShowInfo((int)i);
        AddChild(_list);
        _info.Position = Origin + new Vector2I(430, 324);
        AddChild(_info);
        SetParagraph(_info, _files.Length == 0 ? "No replays yet: every match is recorded here." : string.Empty, 340);
        var size = new Vector2I(210, 27);
        Button(new Vector2I(72, 488), size, SlotButton, T("mp_mplayer", "play", "PLAY"), () =>
        {
            int selected = _list.GetSelectedItems().FirstOrDefault(-1);
            if (selected >= 0)
            {
                Play?.Invoke(_files[selected]);
            }
        });
        Button(new Vector2I(72, 552), size, SlotButton, T("mp_search", "back", "BACK"), () => Back?.Invoke());
    }

    /// <summary>PLAY of a replay file.</summary>
    public event Action<string>? Play;

    /// <summary>BACK.</summary>
    public event Action? Back;

    private void ShowInfo(int index)
    {
        if (index < 0 || index >= _files.Length)
        {
            return;
        }

        var file = new System.IO.FileInfo(_files[index]);
        SetParagraph(_info, string.Create(CultureInfo.InvariantCulture, $"{System.IO.Path.GetFileNameWithoutExtension(file.Name)}\nRecorded {file.LastWriteTime:yyyy-MM-dd HH:mm}\n{file.Length / 1024} KB"), 340);
    }
}
