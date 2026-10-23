using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Game.Session;

namespace Tzar2.Game.Multiplayer;

/// <summary>What a lobby slot holds (the client-facing mirror of the relay's slot states).</summary>
public enum SlotKind
{
    /// <summary>Free for a player to join.</summary>
    Open = 0,

    /// <summary>Closed by the host.</summary>
    Closed = 1,

    /// <summary>A player.</summary>
    Human = 2,

    /// <summary>A computer kingdom.</summary>
    Computer = 3,
}

/// <summary>Where a <see cref="NetworkMatch"/> is.</summary>
public enum LobbyStage
{
    /// <summary>Connecting to the relay.</summary>
    Connecting = 0,

    /// <summary>In the lobby.</summary>
    Lobby = 1,

    /// <summary>Loading (the loading barrier).</summary>
    Loading = 2,

    /// <summary>The match runs (<see cref="NetworkMatch.Session"/>).</summary>
    Running = 3,

    /// <summary>Ended: see <see cref="NetworkMatch.EndReason"/>.</summary>
    Ended = 4,
}

/// <summary>Who controls a kingdom during the match (player list).</summary>
public enum PlayerControl
{
    /// <summary>Nobody (free or closed slot).</summary>
    Absent = 0,

    /// <summary>Its player.</summary>
    Human = 1,

    /// <summary>The computer (a computer kingdom, or a dropped player's kingdom taken over).</summary>
    Computer = 2,

    /// <summary>A dropped player's kingdom set neutral.</summary>
    Neutral = 3,

    /// <summary>A dropped player's kingdom defeated.</summary>
    Defeated = 4,

    /// <summary>Its player dropped; the drop timeout runs.</summary>
    Dropped = 5,
}

/// <summary>What happens to a dropped player's kingdom after the timeout (REQ-NET-07 lobby option).</summary>
public enum DropRule
{
    /// <summary>The computer takes it over.</summary>
    AiTakeover = 0,

    /// <summary>Nobody commands it; it turns neutral towards everyone.</summary>
    Neutral = 1,

    /// <summary>It is defeated.</summary>
    Defeat = 2,
}

/// <summary>One lobby slot as the lobby screen shows it.</summary>
/// <param name="Slot">Lobby slot (also the colour).</param>
/// <param name="Kind">Open, closed, player or computer.</param>
/// <param name="Name">The player's name.</param>
/// <param name="Ready">Ready flag (the host is always ready).</param>
/// <param name="Connected">Whether the player is connected.</param>
/// <param name="PingMs">RTT to the relay.</param>
/// <param name="Choice">Civilization, team, difficulty.</param>
/// <param name="IsHost">The host's slot.</param>
/// <param name="IsLocal">This machine's slot.</param>
public sealed record LobbySlotView(byte Slot, SlotKind Kind, string Name, bool Ready, bool Connected, int PingMs, SlotChoice Choice, bool IsHost, bool IsLocal);

/// <summary>The lobby as the lobby screen shows it.</summary>
/// <param name="Code">The join code.</param>
/// <param name="Name">The lobby name.</param>
/// <param name="Settings">The host's match settings (defaults until the host sets them).</param>
/// <param name="Slots">Every slot.</param>
/// <param name="LocalIsHost">Whether this machine hosts.</param>
public sealed record LobbyView(string Code, string Name, MatchSettings Settings, IReadOnlyList<LobbySlotView> Slots, bool LocalIsHost);

/// <summary>A kingdom in the in-game player list.</summary>
public sealed record PlayerView(byte Slot, string Name, PlayerControl Control, bool Connected, int PingMs, int DroppedSeconds);

/// <summary>A save the host proposed; every player must consent (REQ-NET-10).</summary>
public sealed record SaveRequestView(uint Id, string By, string Label);

/// <summary>Match rules the host chooses at start (sent to the relay).</summary>
/// <param name="Drop">Drop policy.</param>
/// <param name="DropTimeoutSeconds">Drop timeout (REQ-NET-07: 30 s).</param>
/// <param name="PausesPerPlayer">Pauses each player may use.</param>
public sealed record MatchRules(DropRule Drop = DropRule.AiTakeover, int DropTimeoutSeconds = 30, int PausesPerPlayer = 3);

/// <summary>What a player needs to reconnect to a running match (REQ-NET-06), e.g. after the game crashed.</summary>
public sealed record ReconnectTicket(string Host, int Port, string Code, byte Slot, ulong Token);

/// <summary>Where and how a <see cref="NetworkMatch"/> plays.</summary>
public sealed class NetworkMatchOptions
{
    /// <summary>Relay host name or address.</summary>
    public required string Host { get; init; }

    /// <summary>Relay UDP port.</summary>
    public required int Port { get; init; }

    /// <summary>Player name.</summary>
    public string PlayerName { get; init; } = "Player";

    /// <summary>The compiled ruleset every peer must share (fingerprinted).</summary>
    public required Ruleset Ruleset { get; init; }

    /// <summary>The computer kingdoms' script (fingerprinted).</summary>
    public SkirmishAi Ai { get; init; } = SkirmishAi.EngineDefault;

    /// <summary>Engine version (the lobby admits only the host's).</summary>
    public string GameVersion { get; init; } = GameInfo.Version;

    /// <summary>Where multiplayer saves are written and found (null: saves are kept in memory only).</summary>
    public string? SaveDirectory { get; init; }

    /// <summary>The custom maps this machine has (SHELL-018; ADR 0233): a lobby on one of them needs it on every peer.</summary>
    public IReadOnlyList<Campaigns.CustomMapEntry> CustomMaps { get; init; } = [];

    /// <summary>Consent to every save proposal automatically (headless peers).</summary>
    public bool AutoAcceptSaves { get; init; }

    /// <summary>Whether the frames show the local player's fog.</summary>
    public bool Fog { get; init; } = true;

    /// <summary>
    /// The UI reports "loaded" itself with <see cref="NetworkMatch.ReportLoaded"/> once its screen is ready (sprites,
    /// sounds), so the match starts when every machine can show it (REQ-NET-05, ORIG-06); otherwise right after the
    /// session is built (headless peers).
    /// </summary>
    public bool DeferLoadReport { get; init; }

    /// <summary>Checks the values.</summary>
    /// <exception cref="ArgumentException">A value is invalid.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Host);
        ArgumentNullException.ThrowIfNull(Ruleset);
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65535);
    }
}
