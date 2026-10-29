using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 16 (GAME-17, ADR 0106): match statistics from the tick's events (the F4 scores and the statistics file),
/// then, every <c>victoryIntervalTicks</c>, defeat and victory per the match's victory condition and allied victory.
/// </summary>
/// <remarks>
/// <para><b>Defeat</b> (DIP-010, ReTzar's reading of the original, 0x442d10): Standard, Score Limit and Time Limit — a
/// kingdom with no unit and no building left that keeps it in the game (all but the <c>Minor</c> types, walls, gates,
/// bridges and fields; its cows count); Destroy Castles — a kingdom without a castle. Resigning defeats at once. A
/// defeated kingdom's commands are refused; what it has left stays on the map.</para>
/// <para><b>Victory</b>: kingdoms win together when they are mutual allies sharing victory (allied victory, manual ch. 5
/// §8). Standard and Destroy Castles: when the kingdoms still in the game form one such team. Score Limit: the first team
/// whose summed score reaches the limit. Time Limit: at the limit, the team with the highest score (ties: the team with
/// the lowest slot). Not Specified: never automatically (scenario triggers decide). A one-kingdom match never ends by
/// itself.</para>
/// </remarks>
public sealed class VictorySystem : ISimSystem
{
    private int[] _team = [];
    private long[] _teamScore = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Victory;

    /// <inheritdoc />
    public string Name => "victory";

    /// <summary>The score of a kingdom (UNK-29 placeholder formula: gathered, killed and built value, technologies).</summary>
    public static long Score(WorldSettings settings, in KingdomState k)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return (k.GatheredTotal / settings.ScoreGatherDivisor) + (k.KillValue / settings.ScoreKillDivisor)
            + (k.BuildValue / settings.ScoreBuildDivisor) + ((long)k.TechnologiesResearched * settings.ScorePerTechnology);
    }

    /// <summary>Whether the match is over (some kingdom won).</summary>
    public static bool IsOver(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        foreach (PlayerState p in world.Players)
        {
            if ((p.Flags & PlayerFlags.Victorious) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Defeats a kingdom now (resignation, losing the last castle).</summary>
    internal static void Defeat(World world, int player)
    {
        ref PlayerState state = ref world.Players[player];
        if ((state.Flags & (PlayerFlags.Defeated | PlayerFlags.Victorious)) != 0)
        {
            return;
        }

        state.Flags |= PlayerFlags.Defeated;
        world.Kingdoms[player].OutcomeTick = world.Tick.Value;
        world.Events.Emit(new SimEvent(SimEventKind.PlayerDefeated, (byte)player, EntityId.None, 0, 0, 0));
    }

    /// <summary>Makes <paramref name="player"/>'s victory team win and everyone else lose (also <c>hmnext</c>).</summary>
    internal static void Win(World world, int player)
    {
        if (IsOver(world))
        {
            return;
        }

        for (int p = 0; p < world.Players.Length; p++)
        {
            if (p == player || SharesVictory(world, player, p))
            {
                ref PlayerState state = ref world.Players[p];
                state.Flags = (state.Flags & ~PlayerFlags.Defeated) | PlayerFlags.Victorious;
                world.Kingdoms[p].OutcomeTick = world.Tick.Value;
                world.Events.Emit(new SimEvent(SimEventKind.PlayerVictorious, (byte)p, EntityId.None, 0, 0, 0));
            }
        }

        for (int p = 0; p < world.Players.Length; p++)
        {
            Defeat(world, p);
        }
    }

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        CountEvents(world);
        if (IsOver(world) || world.Config.Victory == VictoryCondition.NotSpecified || world.Tick.Value % world.Ruleset.World.VictoryIntervalTicks != 0)
        {
            return;
        }

        Evaluate(world);
    }

    private static bool SharesVictory(World world, int a, int b) => world.Diplomacy.Shares(a, b, Sharing.Victory);

    private static long CostOf(World world, int typeId)
    {
        long sum = 0;
        if ((uint)typeId < (uint)world.Ruleset.EntityTypes.Count)
        {
            foreach (int c in world.Ruleset.EntityTypes[typeId].Traits.Cost)
            {
                sum += c;
            }
        }

        return sum;
    }

    private static void CountEvents(World world)
    {
        Span<KingdomState> k = world.Kingdoms;
        int players = k.Length;
        int resources = world.Ruleset.ResourceTypes.Count;
        foreach (SimEvent e in world.Events.Events)
        {
            int p = e.Player;
            bool own = p < players;
            switch (e.Kind)
            {
                case SimEventKind.ResourceDelivered when own && (uint)e.X < (uint)resources && e.Arg > 0:
                    k[p].Gathered[e.X] = (int)Math.Min((long)k[p].Gathered[e.X] + e.Arg, int.MaxValue);
                    k[p].GatheredTotal += e.Arg;
                    break;
                case SimEventKind.UnitTrained when own:
                    k[p].UnitsTrained++;
                    k[p].BuildValue += CostOf(world, e.Arg);
                    break;
                case SimEventKind.ConstructionCompleted when own:
                    k[p].BuildingsBuilt++;
                    k[p].BuildValue += CostOf(world, e.Arg);
                    break;
                case SimEventKind.ResearchCompleted when own:
                    k[p].TechnologiesResearched++;
                    break;
                case SimEventKind.Captured when own:
                    k[p].BuildingsCaptured++;
                    break;
                case SimEventKind.TradeDelivered when own && e.Y > 0:
                    k[p].TradeIncome += e.Y;
                    break;
                case SimEventKind.TributeSent when own && (uint)e.X < (uint)players && e.Arg > 0:
                    k[p].TributeSent += e.Arg;
                    k[e.X].TributeReceived += e.Arg;
                    break;
                case SimEventKind.Killed:
                case SimEventKind.BurnedDown:
                    CountLoss(world, e);
                    break;
            }
        }
    }

    // Killed: Player = victim's owner, Arg = victim type, Y = killer's owner; BurnedDown has no killer.
    private static void CountLoss(World world, SimEvent e)
    {
        Span<KingdomState> k = world.Kingdoms;
        if ((uint)e.Arg >= (uint)world.Ruleset.EntityTypes.Count)
        {
            return;
        }

        EntityCategory category = world.Ruleset.EntityTypes[e.Arg].Traits.Category;
        if (category is not (EntityCategory.Unit or EntityCategory.Building))
        {
            return;
        }

        bool building = category == EntityCategory.Building;
        if (e.Player < k.Length)
        {
            if (building)
            {
                k[e.Player].BuildingsLost++;
            }
            else
            {
                k[e.Player].UnitsLost++;
            }
        }

        int killer = e.Kind == SimEventKind.Killed ? e.Y : -1;
        if ((uint)killer < (uint)k.Length && killer != e.Player)
        {
            if (building)
            {
                k[killer].BuildingsDestroyed++;
            }
            else
            {
                k[killer].UnitsKilled++;
            }

            k[killer].KillValue += CostOf(world, e.Arg);
        }
    }

    private void Evaluate(World world)
    {
        int players = world.Players.Length;
        VictoryCondition condition = world.Config.Victory;

        // Defeats.
        Span<bool> alive = stackalloc bool[players];
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            int owner = identities[i].Owner;
            if (owner >= players || alive[owner])
            {
                continue;
            }

            EntityTraits t = world.Ruleset.EntityTypes[identities[i].TypeId].Traits;
            alive[owner] = condition == VictoryCondition.DestroyCastles
                ? t.Category == EntityCategory.Building && t.IsSeat
                : !t.IsMinor && ((t.Category == EntityCategory.Unit && !t.World.IsAmbient)
                    || (t.Category == EntityCategory.Building && !t.IsWall && t.Gate is null && !t.IsBridge && t.Decay is null));
        }

        for (int p = 0; p < players; p++)
        {
            if (!alive[p] && (world.Players[p].Flags & PlayerFlags.Active) != 0 && world.Scenario?.CannotLose(p) != true)
            {
                Defeat(world, p);
            }
        }

        if (players < 2)
        {
            return;
        }

        // Victory teams: a kingdom's team is the lowest slot it shares victory directly with (not transitive:
        // A↔B and B↔C does not make A and C a team unless A↔C also holds, ADR 0107 review finding).
        if (_team.Length != players)
        {
            _team = new int[players];
            _teamScore = new long[players];
        }

        Array.Clear(_teamScore);
        for (int p = 0; p < players; p++)
        {
            _team[p] = p;
            for (int q = 0; q < p; q++)
            {
                if (!SharesVictory(world, p, q))
                {
                    continue;
                }

                int leader = _team[q];
                // Only join the team if P directly shares with its leader (no transitive chains: A↔B and B↔C
                // does not make A and C one team unless A↔C also holds; review finding on PR #23).
                if (leader == q || SharesVictory(world, p, leader))
                {
                    _team[p] = leader;
                    break;
                }
            }

            _teamScore[_team[p]] += Score(world.Ruleset.World, world.Kingdoms[p]);
        }

        switch (condition)
        {
            case VictoryCondition.Standard:
            case VictoryCondition.DestroyCastles:
            {
                int team = -1;
                for (int p = 0; p < players; p++)
                {
                    if ((world.Players[p].Flags & PlayerFlags.Defeated) != 0 || (world.Players[p].Flags & PlayerFlags.Active) == 0)
                    {
                        continue;
                    }

                    if (team >= 0 && _team[p] != team)
                    {
                        return; // two teams still fight
                    }

                    team = _team[p];
                }

                if (team >= 0 && MayWin(world, team))
                {
                    Win(world, team);
                }

                return;
            }

            case VictoryCondition.ScoreLimit:
            {
                int best = BestTeam(world);
                if (best >= 0 && _teamScore[best] >= world.Config.ScoreLimit && MayWin(world, best))
                {
                    Win(world, best);
                }

                return;
            }

            case VictoryCondition.TimeLimit:
            {
                long elapsedMs = (long)(world.Tick.Value + 1) * world.Config.TickLengthMilliseconds;
                if (elapsedMs >= (long)world.Config.TimeLimitMinutes * 60_000 && BestTeam(world) is >= 0 and var best && MayWin(world, best))
                {
                    Win(world, best);
                }

                return;
            }
        }
    }

    // Change Player Status "cannot win" (ADR 0130): a team with such a kingdom does not win automatically.
    private bool MayWin(World world, int team)
    {
        if (world.Scenario is not { } scenario)
        {
            return true;
        }

        for (int p = 0; p < _team.Length; p++)
        {
            if (_team[p] == team && scenario.CannotWin(p))
            {
                return false;
            }
        }

        return true;
    }

    // The team (lowest slot) with the highest summed score among teams with a kingdom still in the game.
    private int BestTeam(World world)
    {
        int best = -1;
        for (int p = 0; p < _team.Length; p++)
        {
            if (_team[p] != p || !TeamPlays(world, p))
            {
                continue;
            }

            if (best < 0 || _teamScore[p] > _teamScore[best])
            {
                best = p;
            }
        }

        return best;
    }

    private bool TeamPlays(World world, int team)
    {
        for (int p = 0; p < _team.Length; p++)
        {
            if (_team[p] == team && (world.Players[p].Flags & PlayerFlags.Defeated) == 0 && (world.Players[p].Flags & PlayerFlags.Active) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
