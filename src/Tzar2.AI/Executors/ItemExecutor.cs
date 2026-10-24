using System;
using System.Collections.Generic;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;

namespace Tzar2.AI.Executors;

/// <summary>
/// The computer's items, as ReTzar reads the original's AI (AI-005, ADR 0269):
/// <list type="bullet">
/// <item>beyond the easy level, an idle soldier picks up the nearest power item, spell book or potion lying within 16
/// tiles (0x413e00 with 0x413ee0);</item>
/// <item>a unit of the computer that holds a potion drinks it; a book of one charge whose spell the unit can learn teaches
/// it; a summoning book of more charges is cast on its holder when it is ready (the taken items, 0x4155c0; 0x464280).</item>
/// </list>
/// </summary>
public sealed class ItemExecutor : IAiExecutor
{
    /// <summary>How near an item a soldier goes for it (tiles; 0x414219).</summary>
    public const int ReachTiles = 16;

    private readonly List<EntityId> _ground = [];
    private readonly HashSet<uint> _sought = [];

    /// <inheritdoc />
    public string Name => "items";

    /// <inheritdoc />
    public void Run(AiExecutorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.WarAi || !context.Settings.TakesItems)
        {
            return;
        }

        World world = context.World;
        foreach (EntityId unit in context.Player.Army)
        {
            UseHeld(context, world, unit);
        }

        if (context.Settings.Difficulty != AiDifficulty.Easy)
        {
            Seek(context, world);
        }
    }

    private static void UseHeld(AiExecutorContext context, World world, EntityId unit)
    {
        if (context.IsClaimed(unit) || world.Inventories.IndexOf(unit) is not (>= 0 and var i))
        {
            return;
        }

        Inventory carried = world.Inventories.At(i);
        for (int slot = 0; slot < carried.Count; slot++)
        {
            EntityId item = EntityId.FromValue(carried.Items[slot]);
            if (!world.Entities.IsAlive(item) || world.TypeOf(item).Traits.Item is not { } type)
            {
                continue;
            }

            if (type.Kind == ItemKind.Potion)
            {
                context.Issue(CommandKind.UseItem, [unit], target: unit, arg0: slot);
                return;
            }

            if (type.Kind != ItemKind.SpellBook || world.ItemSpell(item) is not (>= 0 and var spell))
            {
                continue;
            }

            ItemState state = world.Items.Get(item);
            bool limited = state.LimitedCharges(type);
            EntityTraits traits = world.TypeOf(unit).Traits;
            if (limited && state.Charges == 1 && traits.ManaPool is not null && spell < LearnedSpells.Capacity
                && !traits.CanCast((ushort)spell) && !world.HasLearned(unit, spell))
            {
                context.Issue(CommandKind.UseItem, [unit], target: unit, arg0: slot, arg1: 1); // learns its spell
                return;
            }

            // An endless book (ADR 0265) would summon for ever: the computer uses only books whose charges run out.
            if (limited && state.Charges > 1 && world.Ruleset.Spells[spell].Effect == SpellEffect.Summon && state.ReadyTick <= world.Tick.Value)
            {
                Position at = world.Positions.Get(unit);
                context.Issue(CommandKind.UseItem, [unit], at.X, at.Y, unit, arg0: slot); // summons at its holder
                return;
            }
        }
    }

    private void Seek(AiExecutorContext context, World world)
    {
        _ground.Clear();
        _sought.Clear();
        ReadOnlySpan<EntityId> items = world.Items.Entities;
        foreach (EntityId item in items)
        {
            if (!world.IsHeld(item) && world.Positions.Has(item) && world.TypeOf(item).Traits.Item is { Kind: ItemKind.PlusPlus or ItemKind.SpellBook or ItemKind.Potion })
            {
                _ground.Add(item);
            }
        }

        if (_ground.Count == 0)
        {
            return;
        }

        long reach = (long)ReachTiles * MapState.UnitsPerTile;
        foreach (EntityId unit in context.Player.Army)
        {
            if (context.IsClaimed(unit) || !IsIdle(world, unit) || world.Inventories.IndexOf(unit) is not (>= 0 and var i)
                || world.Inventories.At(i).Count >= world.TypeOf(unit).Traits.InventorySlots)
            {
                continue;
            }

            Position at = world.Positions.Get(unit);
            EntityId best = EntityId.None;
            long bestD = long.MaxValue;
            foreach (EntityId item in _ground)
            {
                if (_sought.Contains(item.Value))
                {
                    continue;
                }

                Position p = world.Positions.Get(item);
                long d = Math.Max(Math.Abs((long)p.X - at.X), Math.Abs((long)p.Y - at.Y));
                if (d <= reach && d < bestD)
                {
                    bestD = d;
                    best = item;
                }
            }

            if (!best.IsNone && context.Issue(CommandKind.PickItem, [unit], target: best))
            {
                _sought.Add(best.Value);
            }
        }
    }

    private static bool IsIdle(World world, EntityId unit)
    {
        int c = world.Combats.IndexOf(unit);
        int m = world.Motions.IndexOf(unit);
        return c >= 0 && m >= 0 && world.Combats.At(c).Target == 0 && world.Motions.At(m).State != MotionState.Moving
            && !world.Errands.Has(unit) && !world.IsHeld(unit) && !(world.Casters.IndexOf(unit) is >= 0 and var k && world.Casters.At(k).Phase != CastPhase.None);
    }
}
