using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using LiteNetLib;
using Tzar2.Core.Serialization;
using Tzar2.Net.Protocol;

namespace Tzar2.Net.Relay;

/// <summary>
/// The relay and lobby service of DEC-08 / REQ-NET-01 (ADR 0160), shared by the internet server (<c>Tzar2.Server</c>)
/// and the LAN host (one client embeds it): lobbies by code with slot ownership through session tokens, chat, the
/// loading barrier, and per match the deadline-closing tick authority of ADR 0009. It orders and forwards opaque command
/// payloads, compares state hashes, serves rejoins from a peer's snapshot, applies the drop policy, coordinates saves
/// and pauses — it never simulates. All input is hostile: every message is size- and rate-limited and decoded with
/// bounds (REQ-NET-11); a member that sends a malformed message or floods is disconnected.
/// </summary>
/// <remarks>Single-threaded: <see cref="Update"/> (or <see cref="Run"/>) on one thread. <see cref="Metrics"/> and
/// <see cref="PublicLobbies"/> may be read from any thread.</remarks>
public sealed partial class RelayServer : IDisposable
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const byte GameChannel = 0;
    private const byte ChatChannel = 1;
    private const int MaxTrackedAddresses = 4096;

    private readonly RelayOptions _options;
    private readonly NetManager _net;
    private readonly Dictionary<string, Lobby> _lobbies = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Member> _members = [];
    private readonly Dictionary<IPAddress, TokenBucket> _connectLimits = [];
    private readonly BinaryBufferWriter _w = new(4096);
    private readonly BinaryBufferWriter _payload = new(4096);
    private readonly List<Lobby> _removed = [];
    private volatile LobbySummary[] _listing = [];
    private long _nextHousekeepingUs;
    private bool _started;

    /// <summary>Creates a relay; call <see cref="Start"/>.</summary>
    public RelayServer(RelayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        var listener = new EventBasedNetListener();
        listener.ConnectionRequestEvent += OnConnectionRequest;
        listener.PeerDisconnectedEvent += OnPeerDisconnected;
        listener.NetworkReceiveEvent += OnReceive;
        listener.NetworkReceiveUnconnectedEvent += OnUnconnected;
        _net = new NetManager(listener)
        {
            AutoRecycle = true,
            DisconnectTimeout = options.DisconnectTimeoutMs,
            UpdateTime = 1,
            EnableStatistics = true,
            UnconnectedMessagesEnabled = true,
            ChannelsCount = 2,
        };
    }

    /// <summary>Counters for monitoring.</summary>
    public RelayMetrics Metrics { get; } = new();

    /// <summary>The bound UDP port.</summary>
    public int Port => _net.LocalPort;

    /// <summary>The public lobbies (refreshed every 250 ms; any thread).</summary>
    public IReadOnlyList<LobbySummary> PublicLobbies => _listing;

    /// <summary>Starts listening on <see cref="RelayOptions.Port"/>.</summary>
    /// <exception cref="InvalidOperationException">The port cannot be bound.</exception>
    public void Start()
    {
        if (!_net.Start(_options.Port))
        {
            throw new InvalidOperationException($"Cannot bind UDP port {_options.Port}.");
        }

        _started = true;
        Log("relay.start", string.Empty, -1, FormattableString.Invariant($"port {_net.LocalPort}"));
    }

    /// <summary>Processes network events and closes due ticks. Call in a loop (about every millisecond).</summary>
    public void Update()
    {
        if (!_started)
        {
            throw new InvalidOperationException("Start the relay first.");
        }

        _net.PollEvents();
        long now = MonotonicClock.NowMicroseconds();
        foreach (Lobby lobby in _lobbies.Values)
        {
            UpdateLobby(lobby, now);
            if (lobby.RemoveAtUs > 0 && now >= lobby.RemoveAtUs)
            {
                _removed.Add(lobby);
            }
        }

        foreach (Lobby lobby in _removed)
        {
            RemoveLobby(lobby);
        }

        _removed.Clear();
        if (now >= _nextHousekeepingUs)
        {
            _nextHousekeepingUs = now + 250_000;
            Housekeeping();
        }
    }

    /// <summary>Runs <see cref="Update"/> until <paramref name="cancellation"/> fires, then ends every match and stops.</summary>
    public void Run(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            Update();
            Thread.Sleep(1);
        }

        Shutdown();
    }

    /// <summary>Tells every member the relay shuts down and flushes for a moment.</summary>
    public void Shutdown()
    {
        if (!_started)
        {
            return;
        }

        foreach (Lobby lobby in _lobbies.Values)
        {
            if (lobby.Phase != LobbyPhase.Ended)
            {
                EndLobby(lobby, EndReason.Shutdown, 255, "The relay shuts down.");
            }
        }

        for (int i = 0; i < 20; i++)
        {
            _net.PollEvents();
            Thread.Sleep(5);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _net.Stop();
        _started = false;
    }

    // ---- connections ----

    private void OnConnectionRequest(ConnectionRequest request)
    {
        IPAddress address = request.RemoteEndPoint.Address;
        if (!AllowRequest(address))
        {
            Metrics.Limited();
            Refuse(request, RefusalReason.RateLimited, "Too many connection attempts.");
            return;
        }

        if (_members.Count >= _options.MaxPeers)
        {
            Refuse(request, RefusalReason.RateLimited, "The relay is full.");
            return;
        }

        Hello hello;
        try
        {
            hello = Wire.ReadHello(request.Data.GetRemainingBytesSpan());
        }
        catch (InvalidDataException)
        {
            Refuse(request, RefusalReason.Malformed, "Malformed hello.");
            return;
        }

        if (hello.Protocol != Wire.ProtocolVersion)
        {
            Refuse(request, RefusalReason.ProtocolVersion, FormattableString.Invariant($"Protocol version {hello.Protocol}, the relay speaks {Wire.ProtocolVersion}."));
            return;
        }

        switch (hello.Kind)
        {
            case HelloKind.Create:
                Create(request, hello, address);
                break;
            case HelloKind.Join:
                Join(request, hello);
                break;
            default:
                Rejoin(request, hello);
                break;
        }
    }

    private void Create(ConnectionRequest request, Hello hello, IPAddress address)
    {
        if (hello.Create is not { MaxPlayers: >= 1 and <= Limits.MaxSlots } create)
        {
            Refuse(request, RefusalReason.Malformed, "A lobby needs 1 to 8 slots.");
            return;
        }

        if (_lobbies.Count >= _options.MaxLobbies || _lobbies.Values.Count(l => l.CreatorAddress.Equals(address)) >= _options.MaxLobbiesPerAddress)
        {
            Metrics.Limited();
            Refuse(request, RefusalReason.RateLimited, "Too many lobbies.");
            return;
        }

        var lobby = new Lobby(NewCode(), Sanitize(create.LobbyName, "Lobby"), create.MaxPlayers, create.Public, hello.GameVersion, address);
        _lobbies.Add(lobby.Code, lobby);
        Metrics.LobbyCreated();
        Log("lobby.create", lobby.Code, 0, FormattableString.Invariant($"{create.MaxPlayers} slots, public={create.Public}"));
        Admit(request, lobby, lobby.Slots[0], hello.Name);
    }

    private void Join(ConnectionRequest request, Hello hello)
    {
        if (!_lobbies.TryGetValue(NormalizeCode(hello.Code), out Lobby? lobby) || lobby.Phase == LobbyPhase.Ended)
        {
            Refuse(request, RefusalReason.UnknownLobby, "No lobby with this code.");
            return;
        }

        if (lobby.Phase != LobbyPhase.Lobby)
        {
            Refuse(request, RefusalReason.InMatch, "The match has started.");
            return;
        }

        if (!string.Equals(lobby.GameVersion, hello.GameVersion, StringComparison.Ordinal))
        {
            Refuse(request, RefusalReason.GameVersion, $"The lobby runs version {lobby.GameVersion}, you have {hello.GameVersion}.");
            return;
        }

        Slot? open = Array.Find(lobby.Slots, s => s.State == SlotState.Open);
        if (open is null)
        {
            Refuse(request, RefusalReason.LobbyFull, "The lobby is full.");
            return;
        }

        Admit(request, lobby, open, hello.Name);
    }

    private void Rejoin(ConnectionRequest request, Hello hello)
    {
        if (!_lobbies.TryGetValue(NormalizeCode(hello.Code), out Lobby? lobby) || lobby.Phase == LobbyPhase.Ended)
        {
            Refuse(request, RefusalReason.UnknownLobby, "No lobby with this code.");
            return;
        }

        if (hello.Slot >= lobby.Slots.Length || lobby.Slots[hello.Slot] is not { State: SlotState.Human } slot || !TokenEquals(slot.Token, hello.Token))
        {
            Refuse(request, RefusalReason.BadToken, "Wrong slot or session token.");
            return;
        }

        if (slot.Peer is { } stale)
        {
            // A stale connection of the same player (it reconnected before the old one timed out).
            _members.Remove(stale.Id);
            slot.Peer = null;
            _net.DisconnectPeerForce(stale);
        }

        NetPeer peer = request.Accept();
        Attach(lobby, slot, peer);
        SendWelcome(lobby, slot);
        Log("member.rejoin", lobby.Code, slot.Index, lobby.Phase.ToString());

        // Settings first, then the match parameters: a rejoiner rebuilds the match from both before it resumes.
        _w.Clear();
        Wire.Write(_w, lobby.State());
        peer.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
        if (lobby.Parameters is not null)
        {
            slot.Loaded = false;
            _w.Clear();
            Wire.Write(_w, lobby.Parameters);
            peer.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
        }

        if (lobby.Phase == LobbyPhase.Running)
        {
            BeginResume(lobby, slot, MonotonicClock.NowMicroseconds());
        }

        BroadcastLobby(lobby);
    }

    private void Admit(ConnectionRequest request, Lobby lobby, Slot slot, string name)
    {
        NetPeer peer = request.Accept();
        slot.State = SlotState.Human;
        slot.Token = NewToken();
        slot.Name = Sanitize(name, FormattableString.Invariant($"Player {slot.Index + 1}"));
        slot.Ready = false;
        slot.Choice = [];
        Attach(lobby, slot, peer);
        SendWelcome(lobby, slot);
        Log("member.join", lobby.Code, slot.Index, slot.Name);
        BroadcastLobby(lobby);
    }

    private void Attach(Lobby lobby, Slot slot, NetPeer peer)
    {
        long now = MonotonicClock.NowMicroseconds();
        slot.Peer = peer;
        _members[peer.Id] = new Member(lobby, slot, peer)
        {
            Messages = new TokenBucket(_options.MessagesPerSecond, _options.MessageBurst, now),
            Bytes = new TokenBucket(_options.BytesPerSecond, _options.ByteBurst, now),
            Chat = new SlidingWindowLimit(_options.ChatLines, _options.ChatWindowMs * 1000L),
        };
    }

    private void SendWelcome(Lobby lobby, Slot slot)
    {
        _w.Clear();
        Wire.Write(_w, new Welcome(lobby.Code, slot.Index, slot.Token, (byte)lobby.Slots.Length));
        slot.Peer!.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!_members.Remove(peer.Id, out Member? member) || !ReferenceEquals(member.Slot.Peer, peer))
        {
            return;
        }

        Lobby lobby = member.Lobby;
        Slot slot = member.Slot;
        slot.Peer = null;
        Log("member.drop", lobby.Code, slot.Index, info.Reason.ToString());
        if (lobby.Phase == LobbyPhase.Lobby)
        {
            // Leaving a lobby frees the slot; the host role passes to the next connected human.
            slot.Clear();
            if (lobby.HostSlot == slot.Index)
            {
                Slot? next = Array.Find(lobby.Slots, s => s.State == SlotState.Human && s.Peer is not null);
                if (next is null)
                {
                    EndLobby(lobby, EndReason.Closed, slot.Index, "The host left.");
                    return;
                }

                lobby.HostSlot = next.Index;
                next.Ready = false;
            }

            BroadcastLobby(lobby);
            return;
        }

        if (lobby.Phase is LobbyPhase.Loading or LobbyPhase.Running && slot.Control == SlotControl.Human)
        {
            slot.Control = SlotControl.Dropped;
            slot.DroppedAtUs = MonotonicClock.NowMicroseconds();
            slot.WaitVotes = 0;
        }

        slot.Resuming = false;
        if (lobby.Phase == LobbyPhase.Loading)
        {
            TryStart(lobby);
        }

        BroadcastLobby(lobby);
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (!_members.TryGetValue(peer.Id, out Member? member))
        {
            return;
        }

        ReadOnlySpan<byte> data = reader.GetRemainingBytesSpan();
        long now = MonotonicClock.NowMicroseconds();

        // A snapshot the relay asked for is exempt from the byte budget (it can be megabytes); anything else counts.
        bool requestedSnapshot = data.Length > 0 && data[0] == (byte)MessageType.Snapshot && member.Slot.SnapshotRequested;
        if (!member.Messages.TryTake(1, now) || (!requestedSnapshot && !member.Bytes.TryTake(data.Length, now)))
        {
            Metrics.Limited();
            Kick(member, "rate limit");
            return;
        }

        try
        {
            var r = new BinaryBufferReader(data);
            Handle(member, ref r, now);
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            Metrics.MalformedMessage();
            Kick(member, "malformed message: " + e.Message);
        }
    }

    private void Handle(Member member, ref BinaryBufferReader r, long now)
    {
        Lobby lobby = member.Lobby;
        Slot slot = member.Slot;
        switch (Wire.ReadType(ref r))
        {
            case MessageType.Ping:
                _w.Clear();
                Wire.WriteValue(_w, MessageType.Pong, Wire.ReadValue(ref r));
                member.Peer.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.Unreliable);
                break;
            case MessageType.Chat:
                (byte recipients, string text) = Wire.ReadChat(ref r);
                OnChat(member, recipients, text, now);
                break;
            case MessageType.SetChoice:
                (byte target, byte[] choice) = Wire.ReadSetChoice(ref r);
                OnSetChoice(lobby, slot, target, choice);
                break;
            case MessageType.SetSettings:
                OnSetSettings(lobby, slot, Wire.ReadSetSettings(ref r));
                break;
            case MessageType.SetSlot:
                (byte which, SlotState state) = Wire.ReadSetSlot(ref r);
                OnSetSlot(lobby, slot, which, state);
                break;
            case MessageType.SetReady:
                OnSetReady(lobby, slot, r.ReadBoolean());
                break;
            case MessageType.StartRequest:
                OnStartRequest(lobby, slot, Wire.ReadStartRequest(ref r), now);
                break;
            case MessageType.Loaded:
                (ulong[] fingerprint, ulong initial) = Wire.ReadLoaded(ref r);
                OnLoaded(lobby, slot, fingerprint, initial);
                break;
            case MessageType.Batches:
                OnBatches(lobby, member, Wire.ReadBatches(ref r));
                break;
            case MessageType.Snapshot:
                OnSnapshot(lobby, slot, Wire.ReadSnapshot(ref r), now);
                break;
            case MessageType.SaveRequest:
                OnSaveRequest(lobby, slot, Wire.ReadSaveRequest(ref r), now);
                break;
            case MessageType.SaveVote:
                (uint id, bool accept) = Wire.ReadSaveVote(ref r);
                OnSaveVote(lobby, slot, id, accept);
                break;
            case MessageType.Pause:
                OnPause(lobby, slot, r.ReadBoolean(), now);
                break;
            case MessageType.WaitVote:
                OnWaitVote(lobby, slot, Wire.ReadValue(ref r));
                break;
            default:
                throw new InvalidDataException("Unexpected message type.");
        }
    }

    private void OnUnconnected(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
    {
        if (type != UnconnectedMessageType.BasicMessage || !AllowRequest(remote.Address))
        {
            return;
        }

        BrowseQuery query;
        try
        {
            query = Wire.ReadBrowseQuery(reader.GetRemainingBytesSpan());
        }
        catch (InvalidDataException)
        {
            return;
        }

        LobbySummary[] all = _listing;
        LobbySummary[] page = all.Skip(query.Page * Limits.BrowsePageSize).Take(Limits.BrowsePageSize).ToArray();
        _w.Clear();
        Wire.Write(_w, new BrowseReply(query.Nonce, all.Length, page));
        _net.SendUnconnectedMessage(_w.WrittenSpan, remote);
    }

    private void Refuse(ConnectionRequest request, RefusalReason reason, string detail)
    {
        Metrics.Refused();
        _w.Clear();
        Wire.Write(_w, new Refusal(reason, detail));
        request.Reject(_w.ToArray());
        Log("hello.refused", string.Empty, -1, $"{request.RemoteEndPoint.Address}: {reason} {detail}");
    }

    private void Kick(Member member, string reason)
    {
        Log("member.kick", member.Lobby.Code, member.Slot.Index, reason);
        _net.DisconnectPeer(member.Peer);
    }

    // ---- lobby operations ----

    private void OnChat(Member member, byte recipients, string text, long now)
    {
        Lobby lobby = member.Lobby;
        if (!member.Chat.TryRecord(now))
        {
            _w.Clear();
            Wire.WriteNotice(_w, NoticeKind.ChatThrottled, "At most 5 chat lines in 10 seconds.");
            member.Peer.Send(_w.WrittenSpan, ChatChannel, DeliveryMethod.ReliableOrdered);
            return;
        }

        string line = SanitizeChat(text);
        if (line.Length == 0)
        {
            return;
        }

        byte from = member.Slot.Index;
        _w.Clear();
        Wire.WriteChatDelivery(_w, from, recipients, line);
        foreach (Slot s in lobby.Slots)
        {
            if (s.Peer is not null && (s.Index == from || SlotMask.Contains(recipients, s.Index)))
            {
                s.Peer.Send(_w.WrittenSpan, ChatChannel, DeliveryMethod.ReliableOrdered);
            }
        }

        Metrics.Chat();
    }

    private void OnSetChoice(Lobby lobby, Slot slot, byte target, byte[] choice)
    {
        if (lobby.Phase != LobbyPhase.Lobby || target >= lobby.Slots.Length)
        {
            Notice(slot, NoticeKind.Refused, "Not now.");
            return;
        }

        Slot t = lobby.Slots[target];
        bool own = t == slot && !slot.Ready;
        bool hostForComputer = slot.Index == lobby.HostSlot && t.State == SlotState.Computer;
        if (!own && !hostForComputer)
        {
            Notice(slot, NoticeKind.Refused, slot.Ready ? "Settings are locked while you are ready." : "Not your slot.");
            return;
        }

        t.Choice = choice;
        BroadcastLobby(lobby);
    }

    private void OnSetSettings(Lobby lobby, Slot slot, byte[] settings)
    {
        if (lobby.Phase != LobbyPhase.Lobby || slot.Index != lobby.HostSlot)
        {
            Notice(slot, NoticeKind.Refused, "Only the host changes the match settings.");
            return;
        }

        lobby.Settings = settings;
        lobby.SettingsRevision++;

        // The original's semantics: the others confirm the new settings again.
        foreach (Slot s in lobby.Slots)
        {
            s.Ready = false;
        }

        BroadcastLobby(lobby);
    }

    private void OnSetSlot(Lobby lobby, Slot slot, byte which, SlotState state)
    {
        if (lobby.Phase != LobbyPhase.Lobby || slot.Index != lobby.HostSlot || which >= lobby.Slots.Length || which == lobby.HostSlot
            || state == SlotState.Human)
        {
            Notice(slot, NoticeKind.Refused, "Only the host opens, closes or fills other slots.");
            return;
        }

        Slot t = lobby.Slots[which];
        if (t.Peer is { } peer)
        {
            _members.Remove(peer.Id);
            t.Peer = null;
            _w.Clear();
            Wire.Write(_w, new EndMessage(-1, EndReason.Closed, which, "The host removed you from the lobby."));
            peer.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
            _net.DisconnectPeer(peer);
        }

        t.Clear();
        t.State = state;
        BroadcastLobby(lobby);
    }

    private void OnSetReady(Lobby lobby, Slot slot, bool ready)
    {
        if (lobby.Phase != LobbyPhase.Lobby)
        {
            return;
        }

        slot.Ready = ready;
        BroadcastLobby(lobby);
    }

    private void Notice(Slot slot, NoticeKind kind, string text)
    {
        if (slot.Peer is null)
        {
            return;
        }

        _w.Clear();
        Wire.WriteNotice(_w, kind, text);
        slot.Peer.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastLobby(Lobby lobby)
    {
        _w.Clear();
        Wire.Write(_w, lobby.State());
        foreach (Slot s in lobby.Slots)
        {
            s.Peer?.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
        }
    }

    private void EndLobby(Lobby lobby, EndReason reason, byte slot, string detail)
    {
        if (lobby.Phase == LobbyPhase.Ended)
        {
            return;
        }

        lobby.Phase = LobbyPhase.Ended;
        long now = MonotonicClock.NowMicroseconds();
        lobby.EndedAtUs = now;

        // Keep the lobby a little so the redundant tail and the end message reach everyone.
        lobby.RemoveAtUs = now + 10_000_000;
        _w.Clear();
        Wire.Write(_w, new EndMessage(lobby.LastClosed, reason, slot, detail));
        foreach (Slot s in lobby.Slots)
        {
            s.Peer?.Send(_w.WrittenSpan, GameChannel, DeliveryMethod.ReliableOrdered);
        }

        Log("lobby.end", lobby.Code, slot == 255 ? -1 : slot, FormattableString.Invariant($"{reason} at tick {lobby.LastClosed}: {detail}"));
    }

    private void RemoveLobby(Lobby lobby)
    {
        _lobbies.Remove(lobby.Code);
        foreach (Slot s in lobby.Slots)
        {
            if (s.Peer is { } peer)
            {
                _members.Remove(peer.Id);
                s.Peer = null;
                _net.DisconnectPeer(peer);
            }
        }
    }

    private void Housekeeping()
    {
        if (_connectLimits.Count > MaxTrackedAddresses)
        {
            _connectLimits.Clear();
        }

        LobbySummary[] listing = _lobbies.Values
            .Where(l => l.Public && l.Phase != LobbyPhase.Ended)
            .OrderBy(l => l.Phase).ThenBy(l => l.Code, StringComparer.Ordinal)
            .Select(l => new LobbySummary(l.Code, l.Name, (byte)l.Slots.Count(s => s.State is SlotState.Human or SlotState.Computer), (byte)l.Slots.Length, l.Phase, l.GameVersion))
            .ToArray();
        _listing = listing;
        Metrics.Lobbies = _lobbies.Count;
        Metrics.Matches = _lobbies.Values.Count(l => l.Phase == LobbyPhase.Running);
        Metrics.Peers = _members.Count;
        Metrics.BytesReceived = _net.Statistics.BytesReceived;
        Metrics.BytesSent = _net.Statistics.BytesSent;
    }

    private bool AllowRequest(IPAddress address)
    {
        long now = MonotonicClock.NowMicroseconds();
        if (!_connectLimits.TryGetValue(address, out TokenBucket? bucket))
        {
            bucket = new TokenBucket(_options.ConnectsPerSecond, _options.ConnectBurst, now);
            _connectLimits[address] = bucket;
        }

        return bucket.TryTake(1, now);
    }

    private string NewCode()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            string code = _options.CodeGenerator?.Invoke() ?? RandomCode();
            if (!_lobbies.ContainsKey(code))
            {
                return code;
            }
        }

        throw new InvalidOperationException("No free lobby code.");
    }

    private static string RandomCode()
    {
        Span<char> code = stackalloc char[6];
        for (int i = 0; i < code.Length; i++)
        {
            code[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(code);
    }

    private static ulong NewToken()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes) | 1; // never 0 (0 = no token)
    }

    private static bool TokenEquals(ulong a, ulong b)
    {
        Span<byte> x = stackalloc byte[8];
        Span<byte> y = stackalloc byte[8];
        BitConverter.TryWriteBytes(x, a);
        BitConverter.TryWriteBytes(y, b);
        return a != 0 && CryptographicOperations.FixedTimeEquals(x, y);
    }

    /// <summary>Upper-case code without separators (players type codes by hand).</summary>
    public static string NormalizeCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        Span<char> buffer = stackalloc char[Limits.MaxCodeBytes];
        int n = 0;
        foreach (char c in code)
        {
            if (char.IsAsciiLetterOrDigit(c) && n < buffer.Length)
            {
                buffer[n++] = char.ToUpperInvariant(c);
            }
        }

        return new string(buffer[..n]);
    }

    // Names and chat must not carry control characters into other players' UIs.
    private static string Sanitize(string text, string fallback)
    {
        string clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 0 ? clean : fallback;
    }

    private static string SanitizeChat(string text) => new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();

    private void Log(string name, string lobby, int slot, string detail) => _options.Log?.Invoke(new RelayLogEntry(name, lobby, slot, detail));

    private sealed class Member(Lobby lobby, Slot slot, NetPeer peer)
    {
        public Lobby Lobby { get; } = lobby;

        public Slot Slot { get; } = slot;

        public NetPeer Peer { get; } = peer;

        public required TokenBucket Messages { get; init; }

        public required TokenBucket Bytes { get; init; }

        public required SlidingWindowLimit Chat { get; init; }
    }
}
