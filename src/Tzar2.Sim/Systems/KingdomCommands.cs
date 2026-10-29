using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>The <c>hm*</c> chat codes as sim commands (GAME-19, UNK-30). Values are wire format: append only.</summary>
public enum CheatCode
{
    /// <summary><c>hmreveal</c>: the whole map explored.</summary>
    Reveal = 1,

    /// <summary><c>hmnofog</c>: toggles No Fog for the kingdom.</summary>
    NoFog = 2,

    /// <summary><c>hmnotech</c>: toggles ignoring technology and building requirements.</summary>
    NoTech = 3,

    /// <summary><c>hmgod</c>: toggles invulnerability of the kingdom's units and buildings.</summary>
    God = 4,

    /// <summary><c>hmbuildozer</c>: toggles instant construction, training and research.</summary>
    Buildozer = 5,

    /// <summary><c>hmdvaleva</c>: <c>world.cheatSmallResources</c> of every resource.</summary>
    SmallResources = 6,

    /// <summary><c>hmpetleva</c>: <c>world.cheatLargeResources</c> of every resource.</summary>
    LargeResources = 7,

    /// <summary><c>hmnext</c>: the kingdom (and its victory team) wins.</summary>
    Next = 8,
}

/// <summary>
/// The world commands of pipeline step 1 (ADRs 0102, 0103, 0106, 0107): diplomacy, tribute, resignation, trade routes,
/// tavern conversion, gambling and loans, and cheats. Each is re-validated when it executes; every state change that
/// a client could spam is rate-limited, bounded and overflow-safe.
/// </summary>
internal static class KingdomCommands
{
    public static CommandRejection? SetDiplomacy(World world, in Command command)
    {
        int other = command.X;
        if (!world.HasPlayer(other) || other == command.Player || command.Arg0 is < 0 or > (int)Stance.Ally
            || (command.Arg1 & ~(int)(Sharing.Vision | Sharing.Resources | Sharing.UnitControl | Sharing.Victory)) != 0)
        {
            return CommandRejection.InvalidArgument;
        }

        var stance = (Stance)command.Arg0;
        var offers = (Sharing)command.Arg1;
        Diplomacy d = world.Diplomacy;
        if (d.StanceOf(command.Player, other) == stance && d.OffersOf(command.Player, other) == offers)
        {
            return CommandRejection.InvalidArgument; // no change
        }

        if ((long)world.Tick.Value - d.LastChangeTick(command.Player, other) < Ticks(world, world.Ruleset.World.DiplomacyChangeIntervalMs))
        {
            return CommandRejection.DiplomacyBusy;
        }

        d.Set(command.Player, other, stance, offers, world.Tick.Value);
        world.Events.Emit(new SimEvent(SimEventKind.DiplomacyChanged, command.Player, EntityId.None, other, (int)stance, (int)offers));
        return null;
    }

    public static CommandRejection? Tribute(World world, in Command command)
    {
        int to = command.X;
        int resource = command.Arg0;
        int amount = command.Arg1;
        WorldSettings w = world.Ruleset.World;
        if (!world.HasPlayer(to) || to == command.Player || (world.Players[to].Flags & PlayerFlags.Defeated) != 0
            || (uint)resource >= (uint)world.Ruleset.ResourceTypes.Count || amount < 1 || amount > w.MaxTributeAmount)
        {
            return CommandRejection.InvalidArgument;
        }

        // The tribute screen's SEND carries several resources to several kingdoms in one tick (HUD-013): the interval
        // only holds between ticks.
        ref KingdomState sender = ref world.Kingdoms[command.Player];
        if (sender.LastTributeTick != world.Tick.Value && (long)world.Tick.Value - sender.LastTributeTick < Ticks(world, w.TributeIntervalMs))
        {
            return CommandRejection.TributeBusy;
        }

        ref int stock = ref world.Players[command.Player].Resources[resource];
        if (stock < amount)
        {
            return CommandRejection.NotAffordable;
        }

        stock -= amount;
        world.Credit(to, resource, amount);
        sender.LastTributeTick = world.Tick.Value;
        world.Events.Emit(new SimEvent(SimEventKind.TributeSent, command.Player, EntityId.None, to, resource, amount));
        return null;
    }

    public static CommandRejection? Resign(World world, in Command command)
    {
        world.Players[command.Player].Flags |= PlayerFlags.Resigned;
        VictorySystem.Defeat(world, command.Player);
        return null;
    }

    /// <summary>Hands the kingdom to the computer (Arg0 = 1) or back to its human (Arg0 = 0); REQ-NET-07, ADR 0163.</summary>
    public static CommandRejection? SetController(World world, in Command command)
    {
        if (command.Arg0 is not (0 or 1) || command.Arg1 != 0)
        {
            return CommandRejection.InvalidArgument;
        }

        ref PlayerState player = ref world.Players[command.Player];
        player.Flags = command.Arg0 == 1 ? player.Flags | PlayerFlags.AiControlled : player.Flags & ~PlayerFlags.AiControlled;
        return null;
    }

    /// <summary>AI assistance (ADR 0183): Arg0 bit 1 = economy, bit 2 = war, 0 = none.</summary>
    public static CommandRejection? SetAiAssist(World world, in Command command)
    {
        if (command.Arg0 is < 0 or > 3 || command.Arg1 != 0)
        {
            return CommandRejection.InvalidArgument;
        }

        ref PlayerState player = ref world.Players[command.Player];
        player.Flags = (player.Flags & ~(PlayerFlags.AssistEconomy | PlayerFlags.AssistWar))
            | ((command.Arg0 & 1) != 0 ? PlayerFlags.AssistEconomy : PlayerFlags.None)
            | ((command.Arg0 & 2) != 0 ? PlayerFlags.AssistWar : PlayerFlags.None);
        return null;
    }

    /// <summary>Trade (GAME-14, ADR 0103): sets the route of each listed trader the player commands.</summary>
    public static CommandRejection? Trade(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        int goodsA = command.Arg0 & 0xFF;
        int goodsB = (command.Arg0 >> 8) & 0xFF;
        int resources = world.Ruleset.ResourceTypes.Count;
        if (goodsA >= resources || goodsB >= resources || (command.Arg0 & ~0xFFFF) != 0 || command.Arg1 < 0 || !world.Map.ContainsPoint(command.X, command.Y))
        {
            return CommandRejection.InvalidArgument;
        }

        EntityId dockA = command.Target;
        CommandRejection reason = CommandRejection.NoValidEntities;
        foreach (EntityId trader in entities)
        {
            if (!world.MayCommand(command.Player, trader) || world.TypeOf(trader).Traits.World.Trader is not { } def || !world.Motions.Has(trader))
            {
                continue;
            }

            byte owner = world.Identities.Get(trader).Owner;
            if (def.RequiredTechnology >= 0 && !world.Players[owner].HasTechnology(def.RequiredTechnology) && !world.IgnoresRequirements(owner))
            {
                reason = CommandRejection.RequirementNotMet;
                continue;
            }

            EntityId dockB = DockAt(world, command.X, command.Y, def.Access);
            if (!IsDock(world, dockA, def.Access) || dockB.IsNone || dockA == dockB)
            {
                reason = CommandRejection.InvalidArgument;
                continue;
            }

            if (!TradesWith(world, owner, dockA) || !TradesWith(world, owner, dockB))
            {
                reason = CommandRejection.NotAllied;
                continue;
            }

            GameplayCommands.ClearTasks(world, trader);
            Interaction.Halt(world, trader);
            Position a = world.Positions.Get(dockA);
            world.TradeRoutes.Set(trader, new TradeRoute
            {
                DockA = dockA.Value,
                DockB = dockB.Value,
                GoodsA = (byte)goodsA,
                GoodsB = (byte)goodsB,
                Amount = command.Arg1 == 0 ? def.Capacity : Math.Min(command.Arg1, def.Capacity),
                Phase = TradePhase.ToA,
                LoadX = a.X,
                LoadY = a.Y,
            });
            reason = 0;
        }

        return reason == 0 ? null : reason;
    }

    /// <summary>Whether <paramref name="dock"/> is a completed dock for traders of <paramref name="access"/>.</summary>
    public static bool IsDock(World world, EntityId dock, DropOffAccess access)
        => world.Entities.IsAlive(dock) && world.Identities.Has(dock) && (world.TypeOf(dock).Traits.World.TradeDock & access) != 0
            && !world.Constructions.Has(dock);

    /// <summary>
    /// Whether a kingdom's traders may use the dock: any kingdom's, enemies' too (TRADE-003; ReTzar's reading of the
    /// original's trade dialog, 0x4a2f36, names both markets of any owner). The profit goes to the trader's kingdom.
    /// </summary>
    public static bool TradesWith(World world, int owner, EntityId dock)
        => owner >= 0 && world.Identities.Has(dock);

    public static CommandRejection? Convert(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        int give = command.Arg0 & 0xFF;
        int receive = (command.Arg0 >> 8) & 0xFF;
        int amount = command.Arg1;
        WorldSettings w = world.Ruleset.World;
        int resources = world.Ruleset.ResourceTypes.Count;
        if (give >= resources || receive >= resources || give == receive || (command.Arg0 & ~0xFFFF) != 0 || amount < w.ConvertRatio
            || amount > w.MaxGambleStake * w.ConvertRatio)
        {
            return CommandRejection.InvalidArgument;
        }

        if (Tavern(world, command.Player, entities) is not { IsNone: false } tavern)
        {
            return CommandRejection.NoValidEntities;
        }

        // TRADE-011 (ReTzar's reading of the original, 0x4d3104): one exchange at a time for a kingdom.
        ref KingdomState k = ref world.Kingdoms[command.Player];
        if (k.ExchangeLeft > 0)
        {
            return CommandRejection.ExchangeUnderWay;
        }

        int received = amount / w.ConvertRatio;
        int paid = received * w.ConvertRatio; // only whole units are converted
        Span<int> cost = stackalloc int[resources];
        cost[give] = paid;
        if (!SharedStockpile.TryDeduct(world, command.Player, cost))
        {
            return CommandRejection.NotAffordable;
        }

        if (w.ExchangeUnitMs <= 0)
        {
            world.Credit(command.Player, receive, received);
            world.Events.Emit(new SimEvent(SimEventKind.Converted, command.Player, EntityId.None, give, receive, received));
            return null;
        }

        // Paid now, received a unit at a time.
        k.ExchangeGive = (byte)give;
        k.ExchangeReceive = (byte)receive;
        k.ExchangeLeft = received;
        k.ExchangeTotal = received;
        k.ExchangeNextTick = world.Tick.Value + Ticks(world, w.ExchangeUnitMs);
        k.ExchangeTavern = tavern.Value;
        return null;
    }

    /// <summary>
    /// Gives each kingdom the units of its tavern exchange whose time has come, one every <c>world.exchangeUnitMs</c>
    /// (TRADE-011); an exchange whose tavern is lost ends there. Each tick, before the trade.
    /// </summary>
    public static void DeliverExchanges(World world)
    {
        Span<KingdomState> kingdoms = world.Kingdoms;
        for (int p = 0; p < kingdoms.Length; p++)
        {
            ref KingdomState k = ref kingdoms[p];
            if (k.ExchangeLeft <= 0)
            {
                continue;
            }

            EntityId tavern = EntityId.FromValue(k.ExchangeTavern);
            if (!world.Entities.IsAlive(tavern) || world.Identities.Get(tavern).Owner != p)
            {
                (k.ExchangeLeft, k.ExchangeTotal) = (0, 0);
                continue;
            }

            int unit = Ticks(world, world.Ruleset.World.ExchangeUnitMs);
            while (k.ExchangeLeft > 0 && world.Tick.Value >= k.ExchangeNextTick)
            {
                world.Credit((byte)p, k.ExchangeReceive, 1);
                k.ExchangeLeft--;
                k.ExchangeNextTick += unit;
            }

            if (k.ExchangeLeft == 0)
            {
                world.Events.Emit(new SimEvent(SimEventKind.Converted, (byte)p, tavern, k.ExchangeGive, k.ExchangeReceive, k.ExchangeTotal));
                k.ExchangeTotal = 0;
            }
        }
    }

    public static CommandRejection? Gamble(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        int resource = command.Arg0 & 0xFF;
        int oddsIndex = (command.Arg0 >> 8) & 0xFF;
        int stake = command.Arg1;
        WorldSettings w = world.Ruleset.World;
        int resources = world.Ruleset.ResourceTypes.Count;
        if (resource >= resources || oddsIndex >= w.GambleOdds.Length || (command.Arg0 & ~0xFFFF) != 0 || stake < 1 || stake > w.MaxGambleStake)
        {
            return CommandRejection.InvalidArgument;
        }

        if (!HasTechnology(world, command.Player, world.Ruleset.World.GamblingTechnology))
        {
            return CommandRejection.RequirementNotMet;
        }

        if (!HasTavern(world, command.Player, entities))
        {
            return CommandRejection.NoValidEntities;
        }

        ref int stock = ref world.Players[command.Player].Resources[resource];
        if (stock < stake)
        {
            return CommandRejection.NotAffordable;
        }

        stock -= stake;
        int odds = w.GambleOdds[oddsIndex];
        int chance = (10_000 - w.GambleHouseEdgeBasisPoints) / odds;
        bool won = world.Rng.Misc.NextInt(10_000) < chance;
        int payout = won ? stake * odds : 0;
        if (won)
        {
            world.Credit(command.Player, resource, payout);
        }

        world.Events.Emit(new SimEvent(SimEventKind.Gambled, command.Player, EntityId.None, resource, won ? 1 : 0, payout));
        return null;
    }

    public static CommandRejection? Loan(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if (command.Arg1 is < 0 or > 1 || (command.Arg1 == 0 && (uint)command.Arg0 >= (uint)world.Ruleset.ResourceTypes.Count))
        {
            return CommandRejection.InvalidArgument;
        }

        if (!HasTechnology(world, command.Player, world.Ruleset.World.LoanTechnology))
        {
            return CommandRejection.RequirementNotMet;
        }

        if (!HasTavern(world, command.Player, entities))
        {
            return CommandRejection.NoValidEntities;
        }

        WorldSettings w = world.Ruleset.World;
        ref KingdomState k = ref world.Kingdoms[command.Player];
        RaiseLoanLimit(world, ref k);
        if (command.Arg1 == 0)
        {
            if (k.LoanResource != KingdomState.NoLoan)
            {
                return CommandRejection.LoanOutstanding;
            }

            if (k.LoanLimit <= 0)
            {
                return CommandRejection.NotAffordable;
            }

            k.LoanResource = command.Arg0;
            k.LoanDue = (int)Math.Min((long)k.LoanLimit * (10_000 + w.LoanInterestBasisPoints) / 10_000, int.MaxValue);
            world.Credit(command.Player, command.Arg0, k.LoanLimit);
            world.Events.Emit(new SimEvent(SimEventKind.LoanTaken, command.Player, EntityId.None, command.Arg0, 0, k.LoanLimit));
            return null;
        }

        if (k.LoanResource == KingdomState.NoLoan)
        {
            return CommandRejection.NoLoan;
        }

        Span<int> cost = stackalloc int[world.Ruleset.ResourceTypes.Count];
        cost[k.LoanResource] = k.LoanDue;
        if (!SharedStockpile.TryDeduct(world, command.Player, cost))
        {
            return CommandRejection.NotAffordable;
        }

        world.Events.Emit(new SimEvent(SimEventKind.LoanRepaid, command.Player, EntityId.None, k.LoanResource, 0, k.LoanDue));
        k.LoanResource = KingdomState.NoLoan;
        k.LoanDue = 0;
        k.LoanRaiseTick = world.Tick.Value + Ticks(world, w.LoanGrowDelayMs);
        return null;
    }

    /// <summary>Applies a pending loan-limit raise whose time has come (the manual: 1,000, then 1,200 "after some time").</summary>
    public static void RaiseLoanLimit(World world, ref KingdomState k)
    {
        if (k.LoanRaiseTick >= 0 && world.Tick.Value >= k.LoanRaiseTick)
        {
            k.LoanLimit = (int)Math.Min((long)k.LoanLimit + world.Ruleset.World.LoanStepAmount, 100_000_000);
            k.LoanRaiseTick = -1;
        }
    }

    public static CommandRejection? Cheat(World world, in Command command)
    {
        if (!world.Config.CheatsAllowed || world.Config.Ranked)
        {
            return CommandRejection.CheatsDisabled;
        }

        ref KingdomState k = ref world.Kingdoms[command.Player];
        WorldSettings w = world.Ruleset.World;
        switch ((CheatCode)command.Arg0)
        {
            case CheatCode.Reveal:
                world.Visibility.ExploreAll(command.Player);
                world.Visibility.RememberAll(world, command.Player);
                break;
            case CheatCode.NoFog:
                k.Cheats ^= CheatFlags.NoFog;
                break;
            case CheatCode.NoTech:
                k.Cheats ^= CheatFlags.NoTech;
                break;
            case CheatCode.God:
                k.Cheats ^= CheatFlags.God;
                break;
            case CheatCode.Buildozer:
                k.Cheats ^= CheatFlags.Buildozer;
                break;
            case CheatCode.SmallResources:
            case CheatCode.LargeResources:
                int amount = (CheatCode)command.Arg0 == CheatCode.SmallResources ? w.CheatSmallResources : w.CheatLargeResources;
                for (int r = 0; r < world.Ruleset.ResourceTypes.Count; r++)
                {
                    world.Credit(command.Player, r, amount);
                }

                break;
            case CheatCode.Next:
                VictorySystem.Win(world, command.Player);
                break;
            default:
                return CommandRejection.InvalidArgument;
        }

        CheatFlags on = (CheatCode)command.Arg0 switch
        {
            CheatCode.NoFog => CheatFlags.NoFog,
            CheatCode.NoTech => CheatFlags.NoTech,
            CheatCode.God => CheatFlags.God,
            CheatCode.Buildozer => CheatFlags.Buildozer,
            _ => CheatFlags.None,
        };
        world.Events.Emit(new SimEvent(SimEventKind.CheatUsed, command.Player, EntityId.None, (k.Cheats & on) != 0 ? 1 : 0, 0, command.Arg0));
        return null;
    }

    private static bool HasTavern(World world, byte player, ReadOnlySpan<EntityId> entities) => !Tavern(world, player, entities).IsNone;

    // The first finished tavern of the player among the listed entities, or none.
    private static EntityId Tavern(World world, byte player, ReadOnlySpan<EntityId> entities)
    {
        foreach (EntityId e in entities)
        {
            if (world.Entities.IsAlive(e) && world.Identities.IndexOf(e) is >= 0 and var i && world.Identities.At(i).Owner == player
                && world.TypeOf(e).Traits.World.IsTavern && !world.Constructions.Has(e))
            {
                return e;
            }
        }

        return EntityId.None;
    }

    // A technology the ruleset does not have (a mod without it) is no requirement.
    internal static bool HasTechnology(World world, byte player, string key)
    {
        int id = world.Ruleset.TechnologyId(key);
        return id < 0 || world.Players[player].HasTechnology(id) || world.IgnoresRequirements(player);
    }

    private static EntityId DockAt(World world, int x, int y, DropOffAccess access)
    {
        int tx = x / MapState.UnitsPerTile;
        int ty = y / MapState.UnitsPerTile;
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            if ((world.TypeOf(structures[i]).Traits.World.TradeDock & access) == 0)
            {
                continue;
            }

            (int fx, int fy, int w, int h) = world.FootprintRect(structures[i]);
            if (tx >= fx && tx < fx + w && ty >= fy && ty < fy + h)
            {
                return IsDock(world, structures[i], access) ? structures[i] : EntityId.None;
            }
        }

        return EntityId.None;
    }

    private static int Ticks(World world, int ms) => (ms + world.Config.TickLengthMilliseconds - 1) / world.Config.TickLengthMilliseconds;
}
