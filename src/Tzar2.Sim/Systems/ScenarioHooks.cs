using System;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;

namespace Tzar2.Sim.Systems;

/// <summary>
/// The in-sim scenario controller (the legacy trigger engine of Tzar2.Scripting, REQ-SCR-01; ADR 0130) as the sim's
/// systems see it. Tzar2.Sim cannot reference Tzar2.Scripting (§7.2), so the few places where scenario state changes
/// what a system does ask this interface. It holds no state of its own outside the world: the implementation keeps its
/// state in a <see cref="Snapshots.IWorldStateExtension"/>, and the hook is set again when the controller re-attaches
/// after a restore.
/// </summary>
public interface IScenarioController
{
    /// <summary>Whether a Lock Units action locked the entity: the players' commands skip it (step 1).</summary>
    bool IsLocked(EntityId entity);

    /// <summary>Whether Change Player Status made the kingdom unable to win (the automatic evaluation of step 16).</summary>
    bool CannotWin(int player);

    /// <summary>Whether Change Player Status made the kingdom unable to lose (the automatic evaluation of step 16).</summary>
    bool CannotLose(int player);

    /// <summary>
    /// A <see cref="CommandKind.ScenarioReply"/> of a player (a conversation choice): validates it and records it for the
    /// controller's phase of the same tick.
    /// </summary>
    /// <returns>Why it is refused, or null.</returns>
    CommandRejection? Reply(World world, in Command command);
}

/// <summary>
/// The world changes that scenario actions need beyond the public world mutators (ADR 0130): forced victory and
/// defeat, diplomacy set by a script, and the map-object table. Only callable during a tick.
/// </summary>
public static class ScenarioOperations
{
    /// <summary>
    /// Player Win (Appendix C.7 #18): the kingdoms of <paramref name="mask"/> (bit = slot) win at once and every other
    /// kingdom that has not won is defeated, which ends the match (UNVERIFIED: whether the others lose, UNK-50). Kingdoms
    /// already defeated stay defeated.
    /// Ignores <see cref="IScenarioController.CannotWin"/> (the action is explicit).
    /// </summary>
    public static void ForceVictory(World world, uint mask)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireTick(world);
        if (VictorySystem.IsOver(world))
        {
            return;
        }

        bool any = false;
        for (int p = 0; p < world.Players.Length; p++)
        {
            if ((mask & (1u << p)) == 0)
            {
                continue;
            }

            ref PlayerState state = ref world.Players[p];
            if ((state.Flags & PlayerFlags.Active) == 0 || (state.Flags & PlayerFlags.Defeated) != 0)
            {
                continue; // a defeated kingdom stays defeated
            }

            state.Flags = (state.Flags & ~PlayerFlags.Defeated) | PlayerFlags.Victorious;
            world.Kingdoms[p].OutcomeTick = world.Tick.Value;
            world.Events.Emit(new SimEvent(SimEventKind.PlayerVictorious, (byte)p, EntityId.None, 0, 0, 0));
            any = true;
        }

        if (!any)
        {
            return;
        }

        for (int p = 0; p < world.Players.Length; p++)
        {
            VictorySystem.Defeat(world, p);
        }
    }

    /// <summary>Player Lose (Appendix C.7 #17): the kingdoms of <paramref name="mask"/> are defeated at once.</summary>
    public static void ForceDefeat(World world, uint mask)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireTick(world);
        for (int p = 0; p < world.Players.Length; p++)
        {
            if ((mask & (1u << p)) != 0 && (world.Players[p].Flags & PlayerFlags.Active) != 0)
            {
                VictorySystem.Defeat(world, p);
            }
        }
    }

    /// <summary>
    /// Change Diplomacy Settings (Appendix C.7 #4): kingdom <paramref name="from"/> takes <paramref name="stance"/>
    /// towards <paramref name="to"/> and offers <paramref name="offers"/>, without the players' rate limit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A kingdom, the stance or the offers are invalid.</exception>
    public static void SetDiplomacy(World world, int from, int to, Stance stance, Sharing offers)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireTick(world);
        if (!world.HasPlayer(from) || !world.HasPlayer(to) || from == to || stance > Stance.Ally
            || (offers & ~(Sharing.Vision | Sharing.Resources | Sharing.UnitControl | Sharing.Victory)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stance));
        }

        world.Diplomacy.Set(from, to, stance, offers, world.Tick.Value);
        world.Events.Emit(new SimEvent(SimEventKind.DiplomacyChanged, (byte)from, EntityId.None, to, (int)stance, (int)offers));
    }

    private static void RequireTick(World world)
    {
        if (world.IsLocked)
        {
            throw new InvalidOperationException("Scenario operations run only inside a tick.");
        }
    }
}
