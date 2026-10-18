using System;
using Tzar2.Content.Compiled;

namespace Tzar2.Sim;

/// <summary>
/// The effect of every player's researched technologies on every entity type (GAME-06; ADR 0040): per player, stat and
/// type, the sum of the flat adds and of the basis points of the matching modifiers. Derived state — a pure function of
/// the ruleset and <see cref="PlayerState.Technologies"/> — rebuilt for a player when one of its researches completes
/// and for everyone after a restore; never hashed or snapshotted.
/// </summary>
/// <remarks>
/// <c>value = (base + add) × (10,000 + bp) / 10,000</c>, truncated toward zero, at least 0. Nature and other non-player
/// owners always get the base value. Lookups are two array reads, cheap enough for every hit and every gathered unit.
/// </remarks>
public sealed class PlayerStats
{
    private readonly Ruleset _ruleset;
    private readonly int _players;
    private readonly int _types;
    private readonly int[] _add;
    private readonly int[] _bp;
    private readonly bool[] _anyByPlayer;

    internal PlayerStats(Ruleset ruleset, int players)
    {
        _ruleset = ruleset;
        _players = players;
        _types = ruleset.EntityTypes.Count;
        _add = new int[players * ModifierStats.Count * _types];
        _bp = new int[_add.Length];
        _anyByPlayer = new bool[players * ModifierStats.Count];
    }

    /// <summary>The effective value of <paramref name="stat"/> for an entity of <paramref name="typeId"/> owned by <paramref name="owner"/>.</summary>
    public int Apply(int owner, ModifierStat stat, ushort typeId, int baseValue)
    {
        if ((uint)owner >= (uint)_players || !_anyByPlayer[(owner * ModifierStats.Count) + (int)stat] || typeId >= _types)
        {
            return baseValue;
        }

        int i = Index(owner, stat, typeId);
        return Combine(baseValue, _add[i], _bp[i]);
    }

    /// <summary>The summed flat add and basis points of <paramref name="stat"/> for (<paramref name="owner"/>, <paramref name="typeId"/>).</summary>
    public (int Add, int BasisPoints) Modifier(int owner, ModifierStat stat, ushort typeId)
    {
        if ((uint)owner >= (uint)_players || typeId >= _types)
        {
            return (0, 0);
        }

        int i = Index(owner, stat, typeId);
        return (_add[i], _bp[i]);
    }

    /// <summary><c>(base + add) × (10,000 + bp) / 10,000</c>, truncated toward zero, at least 0, saturated to <see cref="int"/>.</summary>
    public static int Combine(int baseValue, int add, int basisPoints)
    {
        long v = ((long)baseValue + add) * (10_000 + basisPoints) / 10_000;
        return (int)Math.Clamp(v, 0, int.MaxValue);
    }

    /// <summary>Recomputes the tables of <paramref name="player"/> from its researched technologies.</summary>
    internal void Rebuild(int player, in PlayerState state)
    {
        int start = player * ModifierStats.Count * _types;
        Array.Clear(_add, start, ModifierStats.Count * _types);
        Array.Clear(_bp, start, ModifierStats.Count * _types);
        Array.Clear(_anyByPlayer, player * ModifierStats.Count, ModifierStats.Count);
        for (int t = 0; t < _ruleset.Technologies.Count; t++)
        {
            if (!state.HasTechnology(t))
            {
                continue;
            }

            foreach (ModifierDefinition m in _ruleset.Technologies[t].Effects)
            {
                _anyByPlayer[(player * ModifierStats.Count) + (int)m.Stat] = true;
                foreach (ushort type in m.Types)
                {
                    int i = Index(player, m.Stat, type);
                    _add[i] = (int)Math.Clamp((long)_add[i] + m.Add, int.MinValue / 2, int.MaxValue / 2);
                    _bp[i] = (int)Math.Clamp((long)_bp[i] + m.BasisPoints, -10_000, int.MaxValue / 2);
                }
            }
        }
    }

    private int Index(int player, ModifierStat stat, ushort type) => (((player * ModifierStats.Count) + (int)stat) * _types) + type;
}
