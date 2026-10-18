using Tzar2.Sim;
using Tzar2.Sim.Commands;

namespace Tzar2.Game.Session;

/// <summary>Who a chat line goes to (REQ-NET-03: all, allies, enemies or a custom set of kingdoms, as in the original).</summary>
public enum ChatTarget
{
    /// <summary>Every player.</summary>
    All = 0,

    /// <summary>The own team and allied kingdoms.</summary>
    Allies = 1,

    /// <summary>Every other kingdom.</summary>
    Enemies = 2,

    /// <summary>The kingdoms of a custom mask.</summary>
    Custom = 3,
}

/// <summary>What a <see cref="ChatLine"/> is.</summary>
public enum ChatLineKind
{
    /// <summary>A player's line.</summary>
    Player = 0,

    /// <summary>A message of the game itself (a player dropped, the match was saved, ...).</summary>
    System = 1,
}

/// <summary>A chat or system line for the in-game chat window.</summary>
/// <param name="From">The sender's kingdom (system lines: 255).</param>
/// <param name="Sender">The sender's name (system lines: empty).</param>
/// <param name="Text">The text.</param>
/// <param name="Kind">Player or system line.</param>
/// <param name="Recipients">Who received it (bit = kingdom).</param>
public sealed record ChatLine(byte From, string Sender, string Text, ChatLineKind Kind, byte Recipients = 0xFF);

/// <summary>
/// The network side of a multiplayer <see cref="GameSession"/> (ADR 0162): it supplies each closed tick's commands,
/// takes the local player's commands, and hears about every executed tick. Called on the thread that runs the ticks.
/// </summary>
internal interface ILockstepDriver
{
    /// <summary>Game speed of the match in percent (fixed by the lobby).</summary>
    int SpeedPercent { get; }

    /// <summary>Whether the relay paused the match.</summary>
    bool Paused { get; }

    /// <summary>Network events, local commands and chat (between two ticks).</summary>
    void Pump(GameSession session);

    /// <summary>Fills <paramref name="into"/> with the closed command set of sim tick <paramref name="tick"/>, if it arrived.</summary>
    bool TryTakeTick(int tick, CommandList into);

    /// <summary>After each executed tick: hash reports, saves.</summary>
    void AfterTick(GameSession session, TickResult result);

    /// <summary>Asks the relay to pause or resume.</summary>
    void RequestPause(bool paused);

    /// <summary>Sends a chat line.</summary>
    void SendChat(ChatTarget target, byte customMask, string text);
}
