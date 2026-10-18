using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 5 (economy, GAME-14, ADR 0103): traders cycle between two docks. At a dock a trader spends
/// <c>tradeDockMs</c>, sells what it carries (when it was loaded at the other dock) for the load plus a profit of
/// <c>tradeProfitPerTileBasisPoints</c> per tile between the two docks (none below <c>tradeMinDistanceTiles</c>, at most
/// <c>tradeMaxProfitPercent</c>; Trade Taxes raise it), then loads the goods of this dock (paid from the stockpile; an
/// unaffordable load leaves empty) and heads for the other dock. A route ends when a dock is gone, unfinished, or no
/// longer an allowed partner; carried goods are refunded without profit.
/// </summary>
public sealed class TradeSystem : ISimSystem
{
    /// <summary>Ticks between two path requests of a trader that is not moving.</summary>
    public const int RepathIntervalTicks = 20;

    private readonly List<EntityId> _ended = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Economy;

    /// <inheritdoc />
    public string Name => "trade";

    /// <summary>The profit of selling <paramref name="carry"/> loaded at (loadX, loadY) at <paramref name="dock"/>.</summary>
    public static int ProfitOf(World world, byte owner, int carry, int loadX, int loadY, EntityId dock)
    {
        ArgumentNullException.ThrowIfNull(world);
        WorldSettings w = world.Ruleset.World;
        Position at = world.Positions.Get(dock);
        long tiles = IntMath.ISqrt(IntMath.DistanceSquared(loadX, loadY, at.X, at.Y)) / MapState.UnitsPerTile;
        if (tiles < w.TradeMinDistanceTiles)
        {
            return 0;
        }

        long bp = Math.Min(tiles * w.TradeProfitPerTileBasisPoints, w.TradeMaxProfitBasisPoints);
        int profit = (int)Math.Min((long)carry * bp / 10_000, int.MaxValue);
        return Math.Max(0, world.Stats.Apply(owner, ModifierStat.Income, world.Identities.Get(dock).TypeId, profit));
    }

    /// <summary>Ends a trader's route (another order, or a dock lost): carried goods go back to the stockpile without profit.</summary>
    internal static void Cancel(World world, EntityId trader)
    {
        int i = world.TradeRoutes.IndexOf(trader);
        if (i < 0)
        {
            return;
        }

        TradeRoute r = world.TradeRoutes.At(i);
        byte owner = world.Identities.Get(trader).Owner;
        if (r.Carry > 0 && world.HasPlayer(owner))
        {
            world.Credit(owner, r.CarryResource, r.Carry);
        }

        world.TradeRoutes.Remove(trader);
    }

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        if (world.TradeRoutes.Count == 0)
        {
            return;
        }

        int tick = world.Tick.Value;
        int tickMs = world.Config.TickLengthMilliseconds;
        _ended.Clear();
        ReadOnlySpan<EntityId> traders = world.TradeRoutes.Entities;
        for (int i = 0; i < traders.Length; i++)
        {
            EntityId trader = traders[i];
            ref TradeRoute r = ref world.TradeRoutes.At(i);
            byte owner = world.Identities.Get(trader).Owner;
            TraderDefinition def = world.TypeOf(trader).Traits.World.Trader!;
            EntityId a = EntityId.FromValue(r.DockA);
            EntityId b = EntityId.FromValue(r.DockB);
            if (!world.HasPlayer(owner) || !KingdomCommands.IsDock(world, a, def.Access) || !KingdomCommands.IsDock(world, b, def.Access)
                || !KingdomCommands.TradesWith(world, owner, a) || !KingdomCommands.TradesWith(world, owner, b))
            {
                _ended.Add(trader);
                continue;
            }

            EntityId dock = r.Phase == TradePhase.ToA ? a : b;
            if (r.TimerMs > 0)
            {
                r.TimerMs -= tickMs;
                if (r.TimerMs <= 0)
                {
                    r.TimerMs = 0;
                    Dock(world, trader, ref r, owner, dock);
                }

                continue;
            }

            if (Interaction.WorkerReaches(world, trader, dock))
            {
                Interaction.Halt(world, trader);
                r.TimerMs = Math.Max(1, world.Ruleset.World.TradeDockMs);
                continue;
            }

            if (!Interaction.IsMoving(world, trader) && (trader.Index + tick) % RepathIntervalTicks == 0)
            {
                Position p = world.Positions.Get(trader);
                if (!Interaction.ApproachPoint(world, dock, p.X, p.Y, world.TypeOf(trader).Locomotor, out int x, out int y))
                {
                    (x, y) = (world.Positions.Get(dock).X, world.Positions.Get(dock).Y);
                }

                Interaction.MoveTo(world, trader, x, y);
            }
        }

        foreach (EntityId trader in _ended)
        {
            Cancel(world, trader);
        }
    }

    // At the dock: sell the load brought from the other dock, then load this dock's goods and turn around.
    private static void Dock(World world, EntityId trader, ref TradeRoute r, byte owner, EntityId dock)
    {
        int here = r.Phase == TradePhase.ToA ? 1 : 2;
        if (r.Carry > 0 && r.LoadedAt != here)
        {
            int profit = ProfitOf(world, owner, r.Carry, r.LoadX, r.LoadY, dock);
            int total = (int)Math.Min((long)r.Carry + profit, int.MaxValue);
            world.Credit(owner, r.CarryResource, total);
            world.Events.Emit(new SimEvent(SimEventKind.TradeDelivered, owner, trader, r.CarryResource, profit, total));
            r.Carry = 0;
            r.LoadedAt = 0;
        }

        if (r.Carry == 0)
        {
            int goods = here == 1 ? r.GoodsA : r.GoodsB;
            Span<int> cost = stackalloc int[world.Ruleset.ResourceTypes.Count];
            cost[goods] = r.Amount;
            if (SharedStockpile.TryDeduct(world, owner, cost))
            {
                r.Carry = r.Amount;
                r.CarryResource = (byte)goods;
                r.LoadedAt = (byte)here;
            }
        }

        Position at = world.Positions.Get(dock);
        r.LoadX = at.X;
        r.LoadY = at.Y;
        r.Phase = here == 1 ? TradePhase.ToB : TradePhase.ToA;
    }
}
