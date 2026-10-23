using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Tzar2.Core.Hashing;
using Tzar2.Game.Session;
using Tzar2.Net.Lockstep;
using Tzar2.Net.Protocol;

namespace Tzar2.Game.Multiplayer;

/// <summary>
/// One player's side of a multiplayer match (ADR 0162): the lobby (slots, choices, the host's settings, ready flags,
/// chat), the loading barrier with the match fingerprint, then a <see cref="GameSession"/> whose ticks are the relay's
/// closed ticks, plus rejoin (REQ-NET-06), the drop policy's control events (REQ-NET-07; ADR 0163), desync snapshots
/// (REQ-NET-04), consent-based saves (REQ-NET-10; ADR 0166) and pauses. The client UI and the headless soak harness
/// use it the same way; it hides every Net type (§7.2: the client sees only Game).
/// </summary>
/// <remarks>
/// <see cref="Poll"/> is called by the owner (the UI's main thread, or a headless loop). Before the match it handles
/// the relay's events itself; once <see cref="Session"/> exists the thread that runs its ticks handles them (the sim
/// thread after <see cref="GameSession.Start"/>, or <see cref="Poll"/> through <see cref="GameSession.PumpLockstep"/>).
/// </remarks>
public sealed partial class NetworkMatch : IDisposable
{
    private readonly NetworkMatchOptions _options;
    private readonly LockstepClient _client;
    private readonly LockstepDriver _driver;
    private readonly ConcurrentQueue<ChatLine> _lobbyChat = new();
    private readonly byte[] _simSlotOf = Enumerable.Repeat((byte)255, Limits.MaxSlots).ToArray();
    private byte[] _lobbySlotOf = [];
    private volatile GameSession? _session;
    private volatile LobbyView? _lobby;
    private volatile PlayerView[] _players = [];
    private volatile SaveRequestView? _pendingSave;
    private volatile string? _endReason;
    private volatile bool _paused;
    private volatile int _stage;
    private LobbyState? _lobbyState;
    private MatchSetup? _setup;
    private MatchDescriptor? _descriptor;
    private MatchParameters? _parameters;
    private PeerStatus[] _status = [];
    private string? _lastSavePath;

    private NetworkMatch(NetworkMatchOptions options, LockstepClient client)
    {
        _options = options;
        _client = client;
        _driver = new LockstepDriver(this, client);
    }

    /// <summary>Connects to the relay and creates a lobby; this player hosts it.</summary>
    public static NetworkMatch Create(NetworkMatchOptions options, string lobbyName, int slots, bool isPublic)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slots, Limits.MaxSlots);
        return new NetworkMatch(options, LockstepClient.Create(ClientOptions(options), new LobbyCreate(lobbyName, (byte)slots, isPublic)));
    }

    /// <summary>Connects to the relay and joins the lobby with <paramref name="code"/>.</summary>
    public static NetworkMatch Join(NetworkMatchOptions options, string code)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new NetworkMatch(options, LockstepClient.Join(ClientOptions(options), code));
    }

    /// <summary>Reconnects to a running match (REQ-NET-06): the match resumes from a peer's snapshot and catches up.</summary>
    public static NetworkMatch Rejoin(NetworkMatchOptions options, ReconnectTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ticket);
        options.Validate();
        return new NetworkMatch(options, LockstepClient.Rejoin(ClientOptions(options) , ticket.Code, ticket.Slot, ticket.Token));
    }

    private static LockstepClientOptions ClientOptions(NetworkMatchOptions o) =>
        new() { Host = o.Host, Port = o.Port, Name = o.PlayerName, GameVersion = o.GameVersion };

    /// <summary>The stage.</summary>
    public LobbyStage Stage => (LobbyStage)_stage;

    /// <summary>Why the match ended (set with <see cref="LobbyStage.Ended"/>).</summary>
    public string? EndReason => _endReason;

    /// <summary>The lobby, or null before the welcome.</summary>
    public LobbyView? Lobby => _lobby;

    /// <summary>The running match, or null before loading.</summary>
    public GameSession? Session => _session;

    /// <summary>The kingdoms with their controllers and pings (in-game player list).</summary>
    public IReadOnlyList<PlayerView> Players => _players;

    /// <summary>A save proposal waiting for this player's answer, or null.</summary>
    public SaveRequestView? PendingSave => _pendingSave;

    /// <summary>Whether this machine hosts the lobby.</summary>
    public bool IsHost => _lobby?.LocalIsHost ?? false;

    /// <summary>The join code, or null before the welcome.</summary>
    public string? Code => _client.Welcome?.Code;

    /// <summary>What reconnects this player to the match after a crash, or null before the welcome.</summary>
    public ReconnectTicket? Ticket => _client.Welcome is { } w ? new ReconnectTicket(_options.Host, _options.Port, w.Code, w.Slot, w.Token) : null;

    /// <summary>The adaptive input delay in ticks.</summary>
    public int InputDelay => _client.InputDelay;

    /// <summary>The RTT to the relay in milliseconds.</summary>
    public int RttMs => _client.RttMs;

    /// <summary>Traffic counters: batches, late batches, closures, UDP bytes.</summary>
    public (long Batches, long Late, long Closures, long BytesSent, long BytesReceived) Traffic
    {
        get
        {
            ClientStatistics s = _client.Statistics;
            return (s.BatchesSent, s.LateBatches, s.ClosuresReceived, s.BytesSent, s.BytesReceived);
        }
    }

    /// <summary>The path of the last multiplayer save this machine wrote, or null.</summary>
    public string? LastSavePath => Volatile.Read(ref _lastSavePath);

    /// <summary>Whether the relay reported a desync.</summary>
    public bool Desynced { get; private set; }

    /// <summary>Drives the lobby and loading (and, without a sim thread, the match). Call every frame or loop iteration.</summary>
    public void Poll()
    {
        GameSession? session = _session;
        if (session is null)
        {
            _client.Poll();
            ProcessEvents();
        }
        else if (!session.IsRunning)
        {
            session.PumpLockstep(256);
        }
    }

    /// <summary>Moves the lobby's chat and system lines into <paramref name="into"/> (in the match: <see cref="GameSession.DrainChat"/>).</summary>
    public void DrainChat(List<ChatLine> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        while (_lobbyChat.TryDequeue(out ChatLine? line))
        {
            into.Add(line);
        }
    }

    // ---- lobby ----

    /// <summary>Sets the match settings (host).</summary>
    public void SetSettings(MatchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _client.SetSettings(settings.ToBytes());
    }

    /// <summary>Sets this player's civilization and team (not while ready).</summary>
    public void SetChoice(SlotChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (_client.Welcome is { } w)
        {
            _client.SetChoice(w.Slot, choice.ToBytes());
        }
    }

    /// <summary>Opens, closes, fills with a computer or empties slot <paramref name="slot"/> (host); a player in it is removed.</summary>
    public void SetSlot(byte slot, SlotKind kind, SlotChoice? computer = null)
    {
        if (kind == SlotKind.Human)
        {
            throw new ArgumentException("Players join by themselves.", nameof(kind));
        }

        _client.SetSlot(slot, (SlotState)kind);
        if (kind == SlotKind.Computer)
        {
            _client.SetChoice(slot, (computer ?? SlotChoice.Default).ToBytes());
        }
    }

    /// <summary>Sets a computer slot's civilization, team and difficulty (host).</summary>
    public void SetComputerChoice(byte slot, SlotChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);

        // The relay refuses anything else (ADR 0160); asking only for what it allows keeps the lobby quiet.
        if (_lobby is not { LocalIsHost: true } lobby || slot >= lobby.Slots.Count || lobby.Slots[slot].Kind != SlotKind.Computer)
        {
            return;
        }

        _client.SetChoice(slot, choice.ToBytes());
    }

    /// <summary>Sets the ready flag (settings are locked while ready).</summary>
    public void SetReady(bool ready) => _client.SetReady(ready);

    /// <summary>Starts loading (host; every player must be ready).</summary>
    public void Start(MatchRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        MatchSettings settings = _lobby?.Settings ?? new MatchSettings();
        int tickUs = (int)(50_000L * 100 / Math.Clamp(settings.SpeedPercent, 25, 400));
        _client.RequestStart(new MatchParameters(0, tickUs, 10, Math.Clamp(rules.DropTimeoutSeconds, 0, 600) * 1000, (DropPolicy)rules.Drop, (byte)Math.Clamp(rules.PausesPerPlayer, 0, 20)));
    }

    /// <summary>
    /// Prepares the lobby to resume <paramref name="save"/> (host, REQ-NET-10): its settings, and computers in the
    /// slots of its computer kingdoms. Players who are missing at start are played by the computer.
    /// </summary>
    public void PrepareResume(MultiplayerSave save)
    {
        ArgumentNullException.ThrowIfNull(save);
        SetSettings(save.Descriptor.Settings with { ResumeSnapshotHash = save.SnapshotHash });
        foreach (MatchPlayer p in save.Descriptor.Players)
        {
            if (p.Controller == ControllerKind.Computer && p.Slot != 0)
            {
                SetSlot(p.Slot, SlotKind.Computer, new SlotChoice(p.Civilization, p.Team, p.Difficulty));
            }
        }
    }

    /// <summary>Starts a resumed match: open slots of the save's players become computers first (missing humans → AI).</summary>
    public void StartResume(MultiplayerSave save, MatchRules rules)
    {
        ArgumentNullException.ThrowIfNull(save);
        LobbyView? lobby = _lobby;
        foreach (MatchPlayer p in save.Descriptor.Players)
        {
            if (lobby is not null && p.Slot < lobby.Slots.Count && lobby.Slots[p.Slot].Kind == SlotKind.Open)
            {
                SetSlot(p.Slot, SlotKind.Computer, new SlotChoice(p.Civilization, p.Team, p.Difficulty));
            }
        }

        Start(rules);
    }

    /// <summary>Sends a chat line (the lobby: to everyone; in the match use <see cref="GameSession.SubmitChat(string, ChatTarget, byte, int, int)"/>).</summary>
    public void Chat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _client.Chat(ChatRecipients.All, text);
    }

    /// <summary>Proposes a save (host); every player must consent.</summary>
    public void RequestSave(string label) => _client.RequestSave(label);

    /// <summary>Answers the pending save proposal.</summary>
    public void VoteSave(bool accept)
    {
        if (_pendingSave is { } pending)
        {
            _client.VoteSave(pending.Id, accept);
            _pendingSave = null;
        }
    }

    /// <summary>Votes to wait longer for dropped kingdom <paramref name="slot"/> (REQ-NET-07).</summary>
    public void VoteWait(byte slot) => _client.VoteWait(LobbySlot(slot));

    /// <summary>Leaves the lobby or match (a running match applies the drop policy to this kingdom).</summary>
    public void Leave() => _client.Leave();

    /// <inheritdoc />
    public void Dispose()
    {
        _session?.Dispose();
        _client.Dispose();
    }

    // ---- events (owner thread before the match, tick thread during it) ----

    internal bool PausedByRelay => _paused;

    internal int SpeedPercent => (_lobby?.Settings ?? new MatchSettings()).SpeedPercent;

    internal int HashInterval => _parameters?.HashInterval ?? 10;

    internal byte SimSlot(byte lobbySlot) => lobbySlot < _simSlotOf.Length ? _simSlotOf[lobbySlot] : (byte)255;

    internal byte LobbySlot(byte simSlot) => simSlot < _lobbySlotOf.Length ? _lobbySlotOf[simSlot] : (byte)255;

    internal MatchDescriptor? Descriptor => _descriptor;

    internal bool AutoAcceptSaves => _options.AutoAcceptSaves;

    internal void ProcessEvents()
    {
        while (_client.TryDequeue(out ClientEvent? e))
        {
            Handle(e!);
        }
    }

    private void Handle(ClientEvent e)
    {
        switch (e)
        {
            case WelcomeEvent:
                SetStage(LobbyStage.Lobby);
                break;
            case RefusedEvent r:
                End("The relay refused: " + r.Refusal.Detail);
                break;
            case LobbyEvent l:
                OnLobby(l.Lobby);
                break;
            case ChatEvent c:
                Post(new ChatLine(SimSlotOrRaw(c.From), NameOf(c.From), c.Text, ChatLineKind.Player, c.Recipients));
                break;
            case NoticeEvent n:
                System(n.Text);
                break;
            case LoadEvent l:
                OnLoad(l.Parameters);
                break;
            case StartEvent:
                SetStage(LobbyStage.Running);
                break;
            case ResumeEvent r:
                OnResume(r);
                break;
            case SnapshotRequestEvent s:
                OnSnapshotRequest(s);
                break;
            case DesyncEvent d:
                Desynced = true;
                _driver.LastDesyncTick = d.Notice.Tick;
                System(string.Create(CultureInfo.InvariantCulture, $"Desync: the game states differ at tick {d.Notice.Tick} (kingdom {string.Join(", ", d.Notice.OddSlots.Select(s => NameOf(s)))}). The match ends; a report is kept."));
                break;
            case SaveProposalEvent p:
                OnSaveProposal(p.Proposal);
                break;
            case SaveOutcomeEvent o:
                _pendingSave = null;
                System(o.Outcome.Accepted ? "Every player agreed: the game is saved at the next tick." : $"Save refused{(o.Outcome.RefusedBy == 255 ? " (no answer in time)" : " by " + NameOf(o.Outcome.RefusedBy))}.");
                break;
            case PauseEvent p:
                _paused = p.Paused;
                System(p.Paused ? $"{NameOf(p.By)} paused the game ({p.PausesLeft} pauses left)." : $"{NameOf(p.By)} resumed the game.");
                break;
            case StatusEvent s:
                OnStatus(s.Slots);
                break;
            case EndEvent end:
                End(EndText(end.End));
                break;
            case DisconnectedEvent d:
                End("Connection to the relay lost: " + d.Reason);
                break;
        }
    }

    private void OnLobby(LobbyState lobby)
    {
        _lobbyState = lobby;
        MatchSettings settings = new();
        if (lobby.Settings.Length > 0)
        {
            try
            {
                settings = MatchSettings.Parse(lobby.Settings);
            }
            catch (InvalidDataException)
            {
                System("The host's match settings are unreadable (another game version?).");
            }
        }

        byte local = _client.Welcome?.Slot ?? 255;
        LobbyView? before = _lobby;
        _lobby = new LobbyView(lobby.Code, lobby.Name, settings, lobby.Slots.Select((s, i) => new LobbySlotView(
            (byte)i, (SlotKind)s.State, s.Name, s.Ready, s.Connected, s.PingMs, SlotChoice.Parse(s.Choice), i == lobby.HostSlot, i == local)).ToArray(), lobby.HostSlot == local);
        AnnounceScripts(before, _lobby);
    }

    // SHELL-017: every player is told when the host changes a computer's strategy (a player chooses his own).
    private void AnnounceScripts(LobbyView? before, LobbyView after)
    {
        if (before is null)
        {
            return;
        }

        for (int i = 0; i < after.Slots.Count && i < before.Slots.Count; i++)
        {
            LobbySlotView was = before.Slots[i];
            LobbySlotView now = after.Slots[i];
            if (was.Kind == SlotKind.Computer && now.Kind == SlotKind.Computer && was.Choice.AiScript != now.Choice.AiScript)
            {
                System($"The host set Computer {i + 1}'s AI script to {ScriptName(now.Choice.AiScript)}.");
            }
        }
    }

    /// <summary>The strategies a kingdom's computer player can follow (AI-001; the first is the default).</summary>
    public IReadOnlyList<AiScriptOption> AiScripts => _options.Ai.Scripts;

    /// <summary>The name of <paramref name="script"/> as the lobby shows it.</summary>
    public string ScriptName(string script) => AiScripts.FirstOrDefault(o => o.Script == script)?.Name ?? script;

    private void OnLoad(MatchParameters parameters)
    {
        _parameters = parameters;
        if (_lobbyState is not { } lobby)
        {
            return;
        }

        SetStage(LobbyStage.Loading);
        MultiplayerSave? save = null;
        try
        {
            MatchSettings settings = _lobby!.Settings;
            if (settings.ResumeSnapshotHash != 0)
            {
                save = MultiplayerSave.Find(_options.SaveDirectory, settings.ResumeSnapshotHash)
                    ?? throw new InvalidDataException("This machine does not have the saved game the host resumes.");
            }

            _descriptor = Describe(lobby, settings, save);
            byte local = SimSlot(_client.Welcome!.Slot);
            _setup = _descriptor.ToSetup(_options.Ai, multiplayer: true, localSlot: local == 255 ? null : local);
            if (_client.Phase == ClientPhase.Resuming)
            {
                return; // a rejoin: the match comes from a peer's snapshot (OnResume)
            }

            var session = new GameSession(_setup, _options.Ruleset, _driver, save?.Snapshot, _options.Fog);
            _driver.Begin(tickOffset: save is null ? 0 : session.Simulation.World.Tick.Value, resumed: save is not null);
            _session = session;
            MatchFingerprint f = session.Fingerprint;
            ulong ai = new StateHasher().Add(f.AiPrograms.Length).GetCurrentHash();
            foreach (Replay.ReplayAiProgram p in f.AiPrograms)
            {
                ai = new StateHasher().Add(ai).Add(p.Slot).Add(p.ProgramHash).GetCurrentHash();
            }

            _loadReport = ([f.RulesetHash, f.MapHash, f.SettingsHash, ai, save?.SnapshotHash ?? 0], session.Simulation.ComputeStateHash());
            if (!_options.DeferLoadReport)
            {
                ReportLoaded();
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or Content.MapGen.MapGenException or InvalidOperationException)
        {
            System("Cannot load the match: " + ex.Message);
            _client.Loaded([(ulong)Random.Shared.NextInt64()], 0); // never equal to another peer: the barrier stops the match
        }
    }

    private (ulong[] Parts, ulong Initial)? _loadReport;

    /// <summary>Tells the relay this machine finished loading (with <see cref="NetworkMatchOptions.DeferLoadReport"/>).</summary>
    public void ReportLoaded()
    {
        if (Interlocked.Exchange(ref _loadReportSent, 1) == 0 && _loadReport is { } report)
        {
            _client.Loaded(report.Parts, report.Initial);
        }
    }

    private int _loadReportSent;

    // The kingdoms are the lobby's occupied slots in slot order; a resumed match keeps the save's civilizations and teams.
    private MatchDescriptor Describe(LobbyState lobby, MatchSettings settings, MultiplayerSave? save)
    {
        (MatchDescriptor descriptor, byte[] simOf, byte[] lobbyOf) = LobbyDescription.Build(
            settings, lobby.Slots.Select(s => new LobbySlotData(s.State, s.Name, s.Choice)).ToArray(), _options.Ruleset, save);
        simOf.CopyTo(_simSlotOf, 0);
        _lobbySlotOf = lobbyOf;
        return descriptor;
    }

    /// <summary>Test hook: changes this peer's command set of a closed tick (forced desync).</summary>
    internal void TestTickHook(Action<int, Sim.Commands.CommandList> hook) => _driver.TickHook = hook;

    private void OnResume(ResumeEvent resume)
    {
        if (_setup is null)
        {
            End("Cannot rejoin: the match settings did not arrive.");
            return;
        }

        try
        {
            var session = new GameSession(_setup, _options.Ruleset, _driver, resume.Snapshot, _options.Fog);
            if (session.Simulation.ComputeStateHash() != resume.Hash)
            {
                session.Dispose();
                End("Cannot rejoin: the snapshot does not match its hash.");
                return;
            }

            _driver.Begin(tickOffset: session.Simulation.World.Tick.Value - (resume.Tick + 1), resumed: false);
            _session = session;
            SetStage(LobbyStage.Running);
            System(string.Create(CultureInfo.InvariantCulture, $"Reconnected: catching up {resume.BacklogTicks} ticks."));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            End("Cannot rejoin: " + ex.Message);
        }
    }

    // On the tick thread, between two ticks.
    private void OnSnapshotRequest(SnapshotRequestEvent request)
    {
        if (_session is not { } session)
        {
            return;
        }

        int offset = _driver.TickOffset;
        Sim.Simulation sim = session.Simulation;
        int executed = sim.World.Tick.Value - 1 - offset;
        byte[] data = sim.CaptureSnapshot();
        ulong hash = sim.ComputeStateHash();
        if (request.Purpose == SnapshotPurpose.Rejoin)
        {
            _client.SendSnapshot(new SnapshotMessage(SnapshotPurpose.Rejoin, request.RequestId, executed, hash, data, -1, []));
            return;
        }

        // Desync: the last keyframe every peer agreed on is one taken before the first differing checkpoint.
        int desyncTick = _driver.LastDesyncTick ?? executed;
        (int Tick, byte[] Snapshot)? before = session.KeyframeBefore(desyncTick + offset - HashInterval + 1);
        _client.SendSnapshot(new SnapshotMessage(
            SnapshotPurpose.Desync, request.RequestId, executed, hash, data, before is { } k ? k.Tick - 1 - offset : -1, before?.Snapshot ?? []));
    }

    private void OnSaveProposal(SaveProposal proposal)
    {
        if (_options.AutoAcceptSaves || proposal.By == _client.Welcome?.Slot)
        {
            _client.VoteSave(proposal.Id, accept: true);
        }
        else
        {
            _pendingSave = new SaveRequestView(proposal.Id, NameOf(proposal.By), proposal.Label);
        }

        System($"{NameOf(proposal.By)} wants to save the game{(proposal.Label.Length > 0 ? " (" + proposal.Label + ")" : string.Empty)}.");
    }

    private void OnStatus(PeerStatus[] slots)
    {
        for (int i = 0; i < slots.Length && i < _status.Length; i++)
        {
            if (slots[i].Control != _status[i].Control)
            {
                string? text = slots[i].Control switch
                {
                    SlotControl.Dropped => $"{NameOf((byte)i)} dropped; the drop timeout runs.",
                    SlotControl.Computer when _status[i].Control is SlotControl.Dropped => $"The computer took over {NameOf((byte)i)}'s kingdom.",
                    SlotControl.Neutral => $"{NameOf((byte)i)}'s kingdom is neutral now.",
                    SlotControl.Defeated => $"{NameOf((byte)i)}'s kingdom is defeated.",
                    SlotControl.Human when _status[i].Control is not SlotControl.Absent => $"{NameOf((byte)i)} is back.",
                    _ => null,
                };
                if (text is not null)
                {
                    System(text);
                }
            }
        }

        _status = slots;
        _players = slots.Select((s, i) => new PlayerView(SimSlotOrRaw((byte)i), NameOf((byte)i), (PlayerControl)s.Control, s.Connected, s.RttMs, s.DroppedSeconds))
            .Where(p => p.Control != PlayerControl.Absent).ToArray();
    }

    internal void Saved(GameSession session, uint id, int closureTick)
    {
        if (_descriptor is null)
        {
            return;
        }

        byte[] snapshot = session.Simulation.CaptureSnapshot();
        var save = new MultiplayerSave(
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), _parameters?.MatchId ?? 0, _descriptor, session.Simulation.World.Tick.Value, snapshot);
        if (_options.SaveDirectory is { } dir)
        {
            try
            {
                Volatile.Write(ref _lastSavePath, save.Write(dir));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                System("The game could not be saved: " + e.Message);
                return;
            }
        }

        LastSave = save;
        System(string.Create(CultureInfo.InvariantCulture, $"Game saved (save {id}, tick {closureTick})."));
    }

    /// <summary>The last multiplayer save made in this match, or null.</summary>
    public MultiplayerSave? LastSave { get; private set; }

    private void End(string reason)
    {
        _endReason ??= reason;
        SetStage(LobbyStage.Ended);
        System(reason);
    }

    private static string EndText(EndMessage end) => end.Reason switch
    {
        Net.Protocol.EndReason.LoadMismatch => $"The match did not start: a player's game differs ({end.Detail}). Every player needs the same version, ruleset, map and AI scripts.",
        Net.Protocol.EndReason.Desync => "The match ended: " + end.Detail,
        Net.Protocol.EndReason.LoadTimeout => "The match did not start: loading took too long.",
        Net.Protocol.EndReason.Shutdown => "The relay shut down.",
        _ => end.Detail.Length > 0 ? end.Detail : "The lobby was closed.",
    };

    private void SetStage(LobbyStage stage)
    {
        if (Stage != LobbyStage.Ended)
        {
            _stage = (int)stage;
        }
    }

    internal void System(string text) => Post(new ChatLine(255, string.Empty, text, ChatLineKind.System));

    internal void Post(ChatLine line)
    {
        if (_session is { } session)
        {
            session.PostChat(line, session.Simulation.World.Tick.Value);
        }
        else if (_lobbyChat.Count < 1024)
        {
            _lobbyChat.Enqueue(line);
        }
    }

    private byte SimSlotOrRaw(byte lobbySlot) => SimSlot(lobbySlot) is var s and not 255 ? s : lobbySlot;

    private string NameOf(byte lobbySlot)
    {
        LobbyView? lobby = _lobby;
        if (lobby is null || lobbySlot >= lobby.Slots.Count)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Player {lobbySlot + 1}");
        }

        LobbySlotView s = lobby.Slots[lobbySlot];
        return s.Kind == SlotKind.Computer ? string.Create(CultureInfo.InvariantCulture, $"Computer {lobbySlot + 1}") : s.Name;
    }
}
