using System;
using System.Collections.Generic;
using Tzar2.Core.Ids;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;

namespace Tzar2.Tools.Cli.Soak;

/// <summary>
/// A soak player (ADR 0167): every couple of seconds of game time it gives its kingdom an order the way a player would —
/// peasants to the nearest resource, a peasant trained at the castle, soldiers sent at the nearest enemy or somewhere on
/// the map — through <see cref="GameSession.Submit"/>, so every order travels the real lockstep path. Its choices use
/// its own seeded RNG (presentation-side randomness); the sim never sees it.
/// </summary>
internal sealed class SoakBot(int seed, int interval)
{
    private readonly int _interval = interval;
    private readonly Random _random = new(seed);
    private readonly CommandList _batch = new();
    private readonly List<EntityId> _peasants = [];
    private readonly List<EntityId> _soldiers = [];
    private int _nextTick = interval;

    /// <summary>Orders issued.</summary>
    public int Orders { get; private set; }

    /// <summary>Called between ticks on the thread that runs them.</summary>
    public void Act(GameSession session)
    {
        World w = session.Simulation.World;
        if (w.Tick.Value < _nextTick)
        {
            return;
        }

        _nextTick = w.Tick.Value + _interval + _random.Next(_interval);
        byte me = session.LocalPlayer;
        if ((w.Players[me].Flags & (PlayerFlags.Defeated | PlayerFlags.AiControlled)) != 0)
        {
            return;
        }

        _peasants.Clear();
        _soldiers.Clear();
        EntityId castle = EntityId.None;
        foreach (EntityId e in w.Identities.Entities)
        {
            Identity id = w.Identities.Get(e);
            if (id.Owner != me)
            {
                continue;
            }

            var type = w.Ruleset.EntityTypes[id.TypeId];
            if (type.Traits.Harvester is not null)
            {
                _peasants.Add(e);
            }
            else if (type.IsMobile)
            {
                _soldiers.Add(e);
            }
            else if (type.Key.EndsWith(".castle", StringComparison.Ordinal))
            {
                castle = e;
            }
        }

        _batch.Clear();
        int roll = _random.Next(100);
        if (roll < 45 && _peasants.Count > 0)
        {
            EntityId resource = Nearest(w, _peasants[0], e => w.Ruleset.EntityTypes[w.Identities.Get(e).TypeId].Key is "comm.tree" or "comm.res_gold" or "comm.res_stone");
            if (!resource.IsNone)
            {
                _batch.Add(CommandKind.Harvest, _peasants.ToArray(), target: resource);
            }
        }
        else if (roll < 65 && !castle.IsNone)
        {
            int peon = w.Ruleset.EntityTypeId(w.Ruleset.Civilizations[Math.Clamp(w.Players[me].Civilization, 0, w.Ruleset.Civilizations.Count - 1)] + ".peon");
            if (peon >= 0)
            {
                _batch.Add(CommandKind.Train, [castle], arg0: peon, arg1: 1);
            }
        }
        else if (_soldiers.Count > 0)
        {
            EntityId enemy = Nearest(w, _soldiers[0], e => w.Identities.Get(e).Owner != me && w.Identities.Get(e).Owner < w.Config.PlayerCount);
            if (roll < 85 && !enemy.IsNone)
            {
                _batch.Add(CommandKind.Attack, _soldiers.ToArray(), target: enemy);
            }
            else
            {
                int size = w.Map.Data.Width * Content.Compiled.MapData.UnitsPerTile;
                _batch.Add(CommandKind.Move, _soldiers.ToArray(), x: _random.Next(size), y: _random.Next(size));
            }
        }

        if (_batch.Count > 0)
        {
            session.Submit(_batch);
            Orders++;
        }
    }

    private static EntityId Nearest(World w, EntityId from, Func<EntityId, bool> match)
    {
        Position at = w.Positions.Get(from);
        EntityId best = EntityId.None;
        long bestDistance = long.MaxValue;
        foreach (EntityId e in w.Identities.Entities)
        {
            if (!w.Positions.Has(e) || !match(e))
            {
                continue;
            }

            Position p = w.Positions.Get(e);
            long dx = p.X - at.X;
            long dy = p.Y - at.Y;
            long d = (dx * dx) + (dy * dy);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = e;
            }
        }

        return best;
    }
}
