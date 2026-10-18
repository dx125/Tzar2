using System;

namespace Tzar2.Sim.Kingdoms;

/// <summary>
/// Paying with shared resources (GAME-13, ADR 0102): a cost is paid from the payer's own stockpile first; what it does
/// not cover comes from the allies that share resources with it, in ascending slot order. All or nothing.
/// </summary>
internal static class SharedStockpile
{
    /// <summary>Deducts <paramref name="amounts"/> (one per resource type id); false and nothing paid if the pool cannot afford it.</summary>
    public static bool TryDeduct(World world, int player, ReadOnlySpan<int> amounts)
    {
        Span<PlayerState> players = world.Players;
        uint partners = 0;
        for (int q = 0; q < players.Length; q++)
        {
            if (q != player && world.Diplomacy.Shares(player, q, Sharing.Resources))
            {
                partners |= 1u << q;
            }
        }

        for (int r = 0; r < amounts.Length; r++)
        {
            long available = players[player].Resources[r];
            for (int q = 0; q < players.Length && available < amounts[r]; q++)
            {
                available += (partners & (1u << q)) != 0 ? players[q].Resources[r] : 0;
            }

            if (available < amounts[r])
            {
                return false;
            }
        }

        for (int r = 0; r < amounts.Length; r++)
        {
            int due = amounts[r];
            int own = Math.Min(due, players[player].Resources[r]);
            players[player].Resources[r] -= own;
            due -= own;
            for (int q = 0; q < players.Length && due > 0; q++)
            {
                if ((partners & (1u << q)) != 0)
                {
                    int take = Math.Min(due, players[q].Resources[r]);
                    players[q].Resources[r] -= take;
                    due -= take;
                }
            }
        }

        return true;
    }
}
