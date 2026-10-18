using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Scenarios;

/// <content>
/// GAME-07…11 in the gameplay scenario (ADR 0096), so the cross-OS golden, the hash-stream CI and the benchmark cover
/// them: every kingdom also has a mage, a priest and a catapult; items lie around the center. The bot garrisons
/// soldiers in its castle and lets them out again, the mage casts lightning at the nearest enemy, the priest heals,
/// the catapult bombards the center (attack ground, splash), soldiers pick items up; archers already shoot arrows.
/// </content>
public sealed partial class GameplayScenario
{
    private readonly List<EntityId> _scratch = [];
    private long _spells;
    private long _projectiles;
    private long _boarded;
    private long _picked;
    private long _levels;

    /// <summary>Spells cast, projectiles fired, units boarded, items picked up and level-ups so far (GAME-07…11).</summary>
    public (long Spells, long Projectiles, long Boarded, long Picked, long Levels) CombatMagicEvents => (_spells, _projectiles, _boarded, _picked, _levels);

    private void CountCombatMagic(SimEventKind kind)
    {
        switch (kind)
        {
            case SimEventKind.SpellCast: _spells++; break;
            case SimEventKind.ProjectileLaunched: _projectiles++; break;
            case SimEventKind.Boarded: _boarded++; break;
            case SimEventKind.ItemPicked: _picked++; break;
            case SimEventKind.LevelUp: _levels++; break;
        }
    }

    // Per base: a mage, a priest and a catapult beside it; once: weapons and gold lying around the center.
    private static void AddCombatMagicObjects(List<MapObject> objects, bool[] used, int size, Ruleset ruleset, int player, int bx, int by)
    {
        if (ruleset.EntityTypeId("euro.mage") >= 0)
        {
            objects.Add(new MapObject("euro.mage", (byte)player, Center(bx + 6, size), Center(by + 10, size)));
        }

        if (ruleset.EntityTypeId("euro.priest") >= 0)
        {
            objects.Add(new MapObject("euro.priest", (byte)player, Center(bx + 7, size), Center(by + 10, size)));
        }

        if (ruleset.EntityTypeId("comm.catapult") >= 0)
        {
            objects.Add(new MapObject("comm.catapult", (byte)player, Center(bx + 8, size), Center(by + 11, size)));
        }

        int c = size / 2;
        if (player == 0)
        {
            for (int i = 0; i < 8; i++)
            {
                string key = i % 3 == 2 ? "comm.resitem" : "comm.item";
                int x = c - 6 + ((i % 4) * 4);
                int y = c - 3 + ((i / 4) * 6);
                if (ruleset.EntityTypeId(key) >= 0 && !used[(y * size) + x])
                {
                    objects.Add(new MapObject(key, MapObject.Nature, Center(x, size), Center(y, size)));
                }
            }
        }

        static int Center(int tile, int size) => (Math.Clamp(tile, 0, size - 1) * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2);
    }

    private void CombatMagicOrders(World world, byte player, int tick, EntityId castle)
    {
        int window = _options.BotIntervalTicks;

        // Garrison: four idle soldiers enter the castle, and come out again 300 ticks later.
        if (tick % 600 < window && world.Holders.Has(castle) && _idleSoldiers.Count > 4)
        {
            _selection.Clear();
            for (int i = 0; i < 4; i++)
            {
                _selection.Add(_idleSoldiers[^1]);
                _idleSoldiers.RemoveAt(_idleSoldiers.Count - 1);
            }

            _batch.Add(CommandKind.Board, Span(_selection), target: castle);
        }
        else if (tick % 600 >= 300 && tick % 600 < 300 + window && world.Holders.IndexOf(castle) is >= 0 and var h && world.Holders.At(h).Count > 0)
        {
            _selection.Clear();
            _selection.Add(castle);
            int c = world.Map.WorldWidth / 2;
            _batch.Add(CommandKind.Unload, Span(_selection), c, c);
        }

        int lightning = world.Ruleset.SpellId("spell_lightning");
        int heal = world.Ruleset.SpellId("spell_heal");
        ReadOnlySpan<EntityId> casters = world.Casters.Entities;
        for (int i = 0; i < casters.Length; i++)
        {
            EntityId caster = casters[i];
            if (world.Identities.Get(caster).Owner != player || world.Casters.At(i).Phase != CastPhase.None || world.IsHeld(caster))
            {
                continue;
            }

            EntityTraits traits = world.TypeOf(caster).Traits;
            Position at = world.Positions.Get(caster);
            if (lightning >= 0 && traits.CanCast((ushort)lightning) && NearestUnit(world, player, at, enemy: true, 10 * MapState.UnitsPerTile) is { IsNone: false } foe)
            {
                _selection.Clear();
                _selection.Add(caster);
                _batch.Add(CommandKind.CastSpell, Span(_selection), target: foe, arg0: lightning);
            }
            else if (heal >= 0 && traits.CanCast((ushort)heal) && NearestUnit(world, player, at, enemy: false, 8 * MapState.UnitsPerTile) is { IsNone: false } friend)
            {
                _selection.Clear();
                _selection.Add(caster);
                _batch.Add(CommandKind.CastSpell, Span(_selection), target: friend, arg0: heal);
            }
        }

        // The catapult bombards the center now and then (attack ground; stones splash).
        if (tick % 800 < window)
        {
            _scratch.Clear();
            ReadOnlySpan<EntityId> ids = world.Combats.Entities;
            for (int i = 0; i < ids.Length; i++)
            {
                if (world.Identities.Get(ids[i]).Owner == player && world.TypeOf(ids[i]).Traits.Armament is { Projectile: >= 0, MinRangeUnits: > 0 } && world.Motions.Has(ids[i]))
                {
                    _scratch.Add(ids[i]);
                }
            }

            if (_scratch.Count > 0)
            {
                int c = world.Map.WorldWidth / 2;
                _batch.Add(CommandKind.AttackGround, Span(_scratch), c + _bot.NextInt(-2048, 2048), c + _bot.NextInt(-2048, 2048));
            }
        }

        // The idle soldier nearest to an item on the ground (within 16 tiles) goes to pick it up.
        if (tick % 200 < window && _idleSoldiers.Count > 0 && world.Items.Count > 0)
        {
            int bestSoldier = -1;
            EntityId best = EntityId.None;
            long bestDistance = (long)(16 * MapState.UnitsPerTile) * (16 * MapState.UnitsPerTile);
            ReadOnlySpan<EntityId> items = world.Items.Entities;
            for (int s = 0; s < _idleSoldiers.Count; s++)
            {
                if (!world.Inventories.Has(_idleSoldiers[s]))
                {
                    continue;
                }

                Position at = world.Positions.Get(_idleSoldiers[s]);
                for (int i = 0; i < items.Length; i++)
                {
                    if (world.IsHeld(items[i]))
                    {
                        continue;
                    }

                    Position p = world.Positions.Get(items[i]);
                    long d = IntMath.DistanceSquared(at.X, at.Y, p.X, p.Y);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = items[i];
                        bestSoldier = s;
                    }
                }
            }

            if (bestSoldier >= 0)
            {
                _selection.Clear();
                _selection.Add(_idleSoldiers[bestSoldier]);
                _batch.Add(CommandKind.PickItem, Span(_selection), target: best);
                _idleSoldiers.RemoveAt(bestSoldier);
            }
        }

        // Learning first (GAME-08: experience needs it), so the soldiers' levels are covered too.
        int learning = world.Ruleset.TechnologyId("te_learning");
        if (tick < window && learning >= 0 && world.TypeOf(castle).Traits.Producer is { } producer && producer.CanResearch((ushort)learning))
        {
            _selection.Clear();
            _selection.Add(castle);
            _batch.Add(CommandKind.Research, Span(_selection), arg0: learning);
        }
    }

    // The nearest live enemy unit (or the most hurt own unit) within the radius; ties: lower entity index.
    private static EntityId NearestUnit(World world, byte player, Position at, bool enemy, int radius)
    {
        EntityId best = EntityId.None;
        long bestKey = long.MaxValue;
        long r2 = (long)radius * radius;
        ReadOnlySpan<EntityId> ids = world.Combats.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Identity identity = world.Identities.Get(ids[i]);
            if ((identity.Owner != player) != enemy || !world.HasPlayer(identity.Owner) || !world.TypeOf(ids[i]).IsMobile || world.IsHeld(ids[i])
                || world.IsCloaked(ids[i]) || world.IsStone(ids[i]))
            {
                continue;
            }

            Position p = world.Positions.Get(ids[i]);
            long d = IntMath.DistanceSquared(at.X, at.Y, p.X, p.Y);
            Health h = world.Healths.Get(ids[i]);
            if (d > r2 || (!enemy && h.Current >= h.Max))
            {
                continue;
            }

            long key = enemy ? d : h.Current - (long)h.Max;
            if (key < bestKey)
            {
                bestKey = key;
                best = ids[i];
            }
        }

        return best;
    }
}
