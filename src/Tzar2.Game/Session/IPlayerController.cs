using Tzar2.Sim;
using Tzar2.Sim.Commands;

namespace Tzar2.Game.Session;

/// <summary>
/// A computer player (the AI-player hook, ADR 0070). The session calls <see cref="Think"/> on the sim thread before
/// every tick with read access to the world; the controller appends its orders to <paramref name="batch"/> as a client
/// would, and the session stamps them with the slot like the relay does (ADR 0009). The legacy AI (stream B) implements
/// this interface; <see cref="PlaceholderAi"/> stands in until it is merged.
/// </summary>
public interface IPlayerController
{
    /// <summary>Plans this tick's orders of <paramref name="player"/>. Must not change the world.</summary>
    void Think(World world, byte player, CommandList batch);
}
