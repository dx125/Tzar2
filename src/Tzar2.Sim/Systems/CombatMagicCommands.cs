using System;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// The commands of GAME-07…11 (pipeline step 1; ADR 0096): board, unload, items (pick up, drop, use, give, default),
/// spells, stances, attack ground, conversions, bribes, crusades, crafting. Each is re-validated when it executes
/// (TDD §8.6); costs, mana and charges are paid when the effect happens, except the up-front prices of conversions,
/// crusades and crafting, so nothing can be spent twice or obtained for free.
/// </summary>
internal static class CombatMagicCommands
{
    public static CommandRejection? Board(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId holder = command.Target;
        CommandRejection? last = CommandRejection.NoValidEntities;
        int applied = 0;
        foreach (EntityId unit in entities)
        {
            if (!IsCommandedUnit(world, unit, command.Player) || unit == holder)
            {
                continue;
            }

            if (CanBoard(world, command.Player, unit, holder) is { } reason)
            {
                last = reason;
                continue;
            }

            StartErrand(world, unit, ErrandKind.Board, holder, 0);
            applied++;
        }

        if (applied > 0)
        {
            NavalCommands.MeetAtShore(world, holder, entities, DockPurpose.Board); // a ship sails to meet them (REQ-PATH-09)
        }

        return applied > 0 ? null : last;
    }

    /// <summary>
    /// Whether <paramref name="unit"/> may enter <paramref name="holder"/> now: the player's own holder (or the unit's
    /// kingdom's, or one of a kingdom that may command the unit: shared unit control, GAME-13, ADR 0135), completed,
    /// accepting, with room.
    /// </summary>
    public static CommandRejection? CanBoard(World world, byte player, EntityId unit, EntityId holder)
    {
        if (!world.Entities.IsAlive(holder) || world.Identities.IndexOf(holder) is not (>= 0 and var h) || !world.Identities.Has(unit)
            || (world.Identities.At(h).Owner is var keeper && keeper != player && keeper != world.Identities.Get(unit).Owner
                && !world.Diplomacy.MayCommand(keeper, world.Identities.Get(unit).Owner))
            || world.TypeOf(holder).Traits.Holder is not { } def || !world.Holders.Has(holder) || world.IsHeld(holder) || world.IsHeld(unit))
        {
            return CommandRejection.InvalidArgument;
        }

        if (!def.CanHold(world.Identities.Get(unit).TypeId))
        {
            return CommandRejection.NotAccepted;
        }

        return world.Holders.Get(holder).Count >= world.HolderCapacity(holder) ? CommandRejection.HolderFull : null;
    }

    public static CommandRejection? Unload(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        bool toPoint = world.Map.ContainsPoint(command.X, command.Y) && (command.X != 0 || command.Y != 0);
        int limit = command.Arg0 > 0 ? command.Arg0 : 0;
        EntityId chosen = command.Target;
        bool any = false;
        foreach (EntityId holder in entities)
        {
            if (!world.MayCommand(command.Player, holder) || world.Holders.IndexOf(holder) is not (>= 0 and var hs)
                || world.Holders.At(hs).Count == 0)
            {
                continue;
            }

            if (!chosen.IsNone)
            {
                // Emerge one picked unit (manual: pick a unit, Emerge, then the place); not from ships, which dock first.
                if (!world.IsShip(holder) && world.Entities.IsAlive(chosen) && world.Helds.IndexOf(chosen) is >= 0 and var hc
                    && world.Helds.At(hc).Holder == holder.Value && !world.Items.Has(chosen))
                {
                    Position from = world.Positions.Get(holder);
                    if (world.Unload(chosen, toPoint ? command.X : from.X, toPoint ? command.Y : from.Y))
                    {
                        any = true;
                        if (toPoint)
                        {
                            Interaction.MoveTo(world, chosen, command.X, command.Y);
                        }
                    }
                }

                continue;
            }

            if (world.IsShip(holder))
            {
                any |= NavalCommands.UnloadShip(world, holder, toPoint, command.X, command.Y, limit); // to the shore first
                continue;
            }

            Position at = world.Positions.Get(holder);
            any |= NavalSystem.UnloadNow(world, holder, toPoint ? command.X : at.X, toPoint ? command.Y : at.Y, limit, toPoint) > 0;
        }

        return any ? null : CommandRejection.NoValidEntities;
    }

    /// <summary>The listed own cloaked units become visible again (MAG-013: the ninja's uncloak).</summary>
    public static CommandRejection? Uncloak(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        bool any = false;
        foreach (EntityId unit in entities)
        {
            if (IsCommandedUnit(world, unit, command.Player) && world.IsCloaked(unit))
            {
                world.Statuses.At(world.Statuses.IndexOf(unit)).CloakUntil = 0;
                any = true;
            }
        }

        return any ? null : CommandRejection.NoValidEntities;
    }

    /// <summary>The listed spies walk to the target unit of another kingdom and take its look on arrival (MAG-012).</summary>
    public static CommandRejection? Impersonate(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if (!CanBeImpersonated(world, command.Player, command.Target))
        {
            return CommandRejection.InvalidArgument;
        }

        CommandRejection? last = CommandRejection.NoValidEntities;
        int applied = 0;
        foreach (EntityId unit in entities)
        {
            if (!IsCommandedUnit(world, unit, command.Player) || (world.TypeOf(unit).Traits.Abilities & AbilityFlags.Impersonate) == 0)
            {
                continue;
            }

            if (world.TypeOf(unit).Traits.NotImpersonated.BinarySearch(world.Identities.Get(command.Target).TypeId) >= 0)
            {
                last = CommandRejection.NotAccepted; // a machine, a ship or a magic creature
                continue;
            }

            StartErrand(world, unit, ErrandKind.Impersonate, command.Target, 0);
            applied++;
        }

        return applied > 0 ? null : last;
    }

    /// <summary>The listed own taken-over units turn back into spies (order <c>reveal_self</c>).</summary>
    public static CommandRejection? RevealSelf(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        bool any = false;
        foreach (EntityId unit in entities)
        {
            if (IsCommandedUnit(world, unit, command.Player) && world.Disguises.Has(unit))
            {
                Unmask(world, unit);
                any = true;
            }
        }

        return any ? null : CommandRejection.NoValidEntities;
    }

    /// <summary>
    /// A taken-over unit shows its true self, by its order or unmasked: it becomes a spy of its player at its place, with
    /// its hit points in proportion, level, items and name (MAG-012; batch J: a spy, attack 20, after a reveal).
    /// </summary>
    public static void Unmask(World world, EntityId unit)
    {
        if (world.Disguises.IndexOf(unit) is not (>= 0 and var d))
        {
            return;
        }

        ushort spy = world.Disguises.At(d).TypeId;
        world.Disguises.Remove(unit);
        FinishConversion(world, unit, spy);
    }

    /// <summary>
    /// On arrival the spy takes the target over, if it still can (the owner, batch J; ADR 0184 amendment 6): the spy is
    /// gone, the target becomes its player's with its own type, stats and place, and seems to stay its old kingdom's. Its
    /// meter starts at 0 (MAG-021).
    /// </summary>
    public static void TryImpersonate(World world, EntityId spy, EntityId target)
    {
        byte owner = world.Identities.Get(spy).Owner;
        if (!CanBeImpersonated(world, owner, target) || world.TypeOf(spy).Traits.NotImpersonated.BinarySearch(world.Identities.Get(target).TypeId) >= 0)
        {
            return;
        }

        Identity look = world.Identities.Get(target);
        ushort spyType = world.Identities.Get(spy).TypeId;
        GameplayCommands.ClearTasks(world, target);
        world.StopCasting(target);
        Interaction.Halt(world, target);
        world.ChangeOwner(target, owner);
        world.Disguises.Set(target, new Disguise { TypeId = spyType, Owner = look.Owner, Mastery = 0 });
        world.Destroy(spy);
        world.Events.Emit(new SimEvent(SimEventKind.Impersonated, owner, target, spy.Index, look.Owner, look.TypeId));
    }

    // Another kingdom's unit on the map, walking or riding on land: not a ship, a flyer, a building or an animal kept for
    // food (the spy's own list leaves out machines and magic creatures).
    private static bool CanBeImpersonated(World world, byte player, EntityId target)
        => world.Entities.IsAlive(target) && world.Identities.IndexOf(target) is >= 0 and var t && !world.IsHeld(target)
            && world.Identities.At(t).Owner != player && world.HasPlayer(world.Identities.At(t).Owner)
            && world.TypeOf(target) is { IsMobile: true, Locomotor: Locomotor.Foot or Locomotor.Mounted or Locomotor.Climber } type && type.Traits.Category == EntityCategory.Unit
            && type.Traits.ResourceNode is null;

    public static CommandRejection? PickItem(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId item = command.Target;
        if (!world.Entities.IsAlive(item) || !world.Items.Has(item) || world.IsHeld(item))
        {
            return CommandRejection.InvalidArgument;
        }

        CommandRejection? last = CommandRejection.NoValidEntities;
        int applied = 0;
        foreach (EntityId unit in entities)
        {
            if (!IsCommandedUnit(world, unit, command.Player))
            {
                continue;
            }

            if (!world.TypeOf(item).Traits.Item!.CanBeHeldBy(world.Identities.Get(unit).TypeId) || !world.Inventories.Has(unit))
            {
                last = CommandRejection.NotAccepted;
                continue;
            }

            if (world.Inventories.Get(unit).Count >= world.TypeOf(unit).Traits.InventorySlots && world.TypeOf(item).Traits.Item!.Kind != ItemKind.Resource)
            {
                last = CommandRejection.InventoryFull;
                continue;
            }

            StartErrand(world, unit, ErrandKind.Pickup, item, 0);
            applied++;
        }

        return applied > 0 ? null : last;
    }

    // A peasant takes a resource item as its load and carries it home (NAT-004; ReTzar's reading of the original, 0x464f60,
    // program 0x1e); any other unit, or a peasant with another load, brings it to the stockpile at once.
    private static bool TakeAsLoad(World world, EntityId unit, EntityId item, ItemDefinition def, int amount)
    {
        if (world.Workers.IndexOf(unit) is not (>= 0 and var w) || world.TypeOf(unit).Traits.Harvester is not { } harvester || !harvester.CanGather(def.Resource))
        {
            return false;
        }

        ref Worker worker = ref world.Workers.At(w);
        if (worker.CarryAmount > 0 && worker.CarryResource != def.Resource)
        {
            return false;
        }

        Position at = world.Positions.Get(item);
        worker.CarryAmount = (int)Math.Min((long)worker.CarryAmount + amount, int.MaxValue);
        worker.CarryResource = (byte)def.Resource;
        worker.NodeResource = (byte)def.Resource;
        worker.NodeX = at.X;
        worker.NodeY = at.Y;
        worker.Task = WorkerTask.Harvest;
        worker.Phase = WorkerPhase.ToDropOff;
        worker.Target = 0;
        worker.DropTarget = 0;
        worker.Retries = 0;
        return true;
    }

    /// <summary>Whether <paramref name="unit"/> can take <paramref name="item"/> into its inventory now.</summary>
    public static bool CanCarry(World world, EntityId unit, EntityId item)
        => world.Entities.IsAlive(unit) && world.Inventories.IndexOf(unit) is >= 0 and var i && world.Items.Has(item)
            && world.Inventories.At(i).Count < world.TypeOf(unit).Traits.InventorySlots
            && world.TypeOf(item).Traits.Item!.CanBeHeldBy(world.Identities.Get(unit).TypeId);

    /// <summary>
    /// Picks an item up (on arrival): resource items go to the stockpile at once; others into the inventory. The item
    /// must still lie on the ground, so two units can never both get it.
    /// </summary>
    public static void TryPickUp(World world, EntityId unit, EntityId item)
    {
        if (!world.Entities.IsAlive(item) || world.Items.IndexOf(item) is not (>= 0 and var s) || world.IsHeld(item) || world.IsHeld(unit))
        {
            return;
        }

        ItemDefinition def = world.TypeOf(item).Traits.Item!;
        byte owner = world.Identities.Get(unit).Owner;
        if (!def.CanBeHeldBy(world.Identities.Get(unit).TypeId) || !world.HasPlayer(owner))
        {
            return;
        }

        ushort type = world.Identities.Get(item).TypeId;
        if (def.Kind == ItemKind.Resource && TakeAsLoad(world, unit, item, def, world.Items.At(s).Power))
        {
            world.Events.Emit(new SimEvent(SimEventKind.ItemPicked, owner, item, unit.Index, 0, type));
            world.Destroy(item);
            return;
        }

        if (def.Kind == ItemKind.Resource)
        {
            ref PlayerState player = ref world.Players[owner];
            player.Resources[def.Resource] = (int)Math.Min((long)player.Resources[def.Resource] + world.Items.At(s).Power, int.MaxValue);
            world.Events.Emit(new SimEvent(SimEventKind.ItemPicked, owner, item, unit.Index, 0, type));
            world.Destroy(item);
            return;
        }

        if (!CanCarry(world, unit, item))
        {
            return;
        }

        world.PutInInventory(unit, item);
        world.Events.Emit(new SimEvent(SimEventKind.ItemPicked, owner, item, unit.Index, 0, type));
        if (def.Kind == ItemKind.Scroll)
        {
            world.Events.Emit(new SimEvent(SimEventKind.ItemUsed, owner, item, unit.Index, 0, type)); // the story text shows
        }
    }

    public static CommandRejection? DropItem(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        int applied = 0;
        foreach (EntityId unit in entities)
        {
            if (IsCommandedUnit(world, unit, command.Player) && ItemInSlot(world, unit, command.Arg0) is { IsNone: false } item)
            {
                Position at = world.Positions.Get(unit);
                world.DropItemAt(item, at.X, at.Y);
                applied++;
            }
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
    }

    public static CommandRejection? SetDefaultItem(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        int applied = 0;
        foreach (EntityId unit in entities)
        {
            if (!IsOwnedUnit(world, unit, command.Player) || world.Inventories.IndexOf(unit) is not (>= 0 and var i))
            {
                continue;
            }

            ref Inventory inventory = ref world.Inventories.At(i);
            if (command.Arg0 == -1)
            {
                inventory.Default = Inventory.NoDefault;
                applied++;
            }
            else if ((uint)command.Arg0 < inventory.Count)
            {
                inventory.Default = (byte)command.Arg0;
                applied++;
            }
        }

        return applied > 0 ? null : CommandRejection.InvalidArgument;
    }

    public static CommandRejection? GiveItem(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId receiver = command.Target;
        if (!IsOwnedUnit(world, receiver, command.Player))
        {
            return CommandRejection.InvalidArgument;
        }

        foreach (EntityId unit in entities)
        {
            if (!IsOwnedUnit(world, unit, command.Player) || unit == receiver || ItemInSlot(world, unit, command.Arg0) is not { IsNone: false } item)
            {
                continue;
            }

            if (!CanCarry(world, receiver, item))
            {
                return CommandRejection.InventoryFull;
            }

            StartErrand(world, unit, ErrandKind.Give, receiver, (int)item.Value);
            return null;
        }

        return CommandRejection.NoValidEntities;
    }

    public static CommandRejection? UseItem(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        foreach (EntityId unit in entities)
        {
            if (!IsOwnedUnit(world, unit, command.Player) || world.Inventories.IndexOf(unit) is not (>= 0 and var i))
            {
                continue;
            }

            int slot = command.Arg0 == -1 ? world.Inventories.At(i).Default : command.Arg0;
            if (ItemInSlot(world, unit, slot) is not { IsNone: false } item)
            {
                return CommandRejection.InvalidArgument;
            }

            ItemDefinition def = world.TypeOf(item).Traits.Item!;
            if (def.Kind is ItemKind.Potion or ItemKind.PlusPlus or ItemKind.Scroll && world.Statuses.IndexOf(unit) is >= 0 and var st)
            {
                world.Statuses.At(st).CloakUntil = 0; // using an item reveals an invisible unit (1.09, FOG-005)
            }

            switch (def.Kind)
            {
                case ItemKind.Potion:
                case ItemKind.PlusPlus:
                    Drink(world, unit, item, def);
                    return null;
                case ItemKind.Scroll:
                    world.Events.Emit(new SimEvent(SimEventKind.ItemUsed, command.Player, item, unit.Index, 0, world.Identities.Get(item).TypeId));
                    return null;
                case ItemKind.Soul:
                    // ITEM-010: Arg1 0 = item_kill, 1 = item_berserk, on the bound unit; an unbound soul does nothing.
                    return MagicSystem.SoulOrder(world, unit, item, command.Arg1 == 1) ? null : CommandRejection.InvalidArgument;
                case ItemKind.SpellBook:
                    if (world.ItemSpell(item) is not (>= 0 and var spell))
                    {
                        return CommandRejection.InvalidArgument;
                    }

                    // Arg1 1: used with no target (ITEM-019, ReTzar 0x4640f0): a unit with a mana pool learns the book's spell.
                    return command.Arg1 == 1 ? Learn(world, unit, item, spell) : StartCast(world, unit, (ushort)spell, item, command.Target, command.X, command.Y);
                default:
                    return CommandRejection.InvalidArgument; // weapons, armor, flags work while carried
            }
        }

        return CommandRejection.NoValidEntities;
    }

    // ITEM-019 (ReTzar 0x4640f0, ADR 0266): a book used with no target teaches its spell to a holder with a mana pool that
    // does not know it yet, and is used up whatever its charges. The spell then joins the unit's card and is cast with
    // the unit's own mana, behind the same research as for those who know it by nature.
    private static CommandRejection? Learn(World world, EntityId unit, EntityId book, int spell)
    {
        if (world.Manas.IndexOf(unit) < 0 || world.TypeOf(unit).Traits.ManaPool is null || spell >= LearnedSpells.Capacity
            || world.TypeOf(unit).Traits.CanCast((ushort)spell) || world.HasLearned(unit, spell))
        {
            return CommandRejection.InvalidArgument;
        }

        if (world.Learned.IndexOf(unit) is >= 0 and var l)
        {
            world.Learned.At(l).Mask |= 1UL << spell;
        }
        else
        {
            world.Learned.Set(unit, new LearnedSpells { Mask = 1UL << spell });
        }

        world.Events.Emit(new SimEvent(SimEventKind.ItemUsed, world.Identities.Get(unit).Owner, book, unit.Index, spell, world.Identities.Get(book).TypeId));
        world.Destroy(book);
        return null;
    }

    // A potion (or "plusplus"): levels, attack, defence, health, mana for good; the potion is used up. A potion with its
    // own effect (ITEM-016) adds its power to that one only.
    private static void Drink(World world, EntityId unit, EntityId item, ItemDefinition def)
    {
        ItemState state = world.Items.Get(item);
        int Of(ItemEffect effect, int typeValue) => state.Effect == ItemEffect.Default ? typeValue : state.Effect == effect ? state.Power : 0;
        int levels = Of(ItemEffect.Levels, def.PotionLevel), attack = Of(ItemEffect.Attack, def.PotionAttack), defence = Of(ItemEffect.Defence, def.PotionDefence);
        int health = Of(ItemEffect.Health, def.PotionHealth), mana = Of(ItemEffect.Mana, def.PotionMana);
        if (world.Experiences.IndexOf(unit) is >= 0 and var x)
        {
            ref Experience e = ref world.Experiences.At(x);
            if (levels > 0)
            {
                int wanted = CombatStats.XpForLevel(world, unit, e.Level + Math.Min(levels, 1000));
                e.PendingXp += Math.Max(0, wanted - e.Xp);
            }

            e.BonusAttack = Math.Min(e.BonusAttack + attack, 100_000);
            e.BonusDefence = Math.Min(e.BonusDefence + defence, 100_000);
        }

        ref Health h = ref world.Healths.Get(unit);
        h.Current = (int)Math.Min((long)h.Current + health, h.Max);
        if (mana > 0 && world.Manas.IndexOf(unit) is >= 0 and var m && world.TypeOf(unit).Traits.ManaPool is { } pool)
        {
            long max = (long)world.Stats.Apply(world.Identities.Get(unit).Owner, ModifierStat.ManaMax, world.Identities.Get(unit).TypeId, pool.Max) * 1000;
            world.Manas.At(m).MilliMana = (int)Math.Min(world.Manas.At(m).MilliMana + ((long)mana * 1000), max);
        }

        world.Events.Emit(new SimEvent(SimEventKind.ItemUsed, world.Identities.Get(unit).Owner, item, unit.Index, 0, world.Identities.Get(item).TypeId));
        world.Destroy(item);
    }

    public static CommandRejection? CastSpell(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if ((uint)command.Arg0 >= (uint)world.Ruleset.Spells.Count)
        {
            return CommandRejection.InvalidArgument;
        }

        CommandRejection? last = CommandRejection.NoValidEntities;
        int applied = 0;
        foreach (EntityId caster in entities)
        {
            if (!IsOwnedUnit(world, caster, command.Player))
            {
                continue;
            }

            CommandRejection? r = StartCast(world, caster, (ushort)command.Arg0, EntityId.None, command.Target, command.X, command.Y);
            if (r is null)
            {
                applied++;
            }
            else
            {
                last = r;
            }
        }

        return applied > 0 ? null : last;
    }

    private static CommandRejection? StartCast(World world, EntityId caster, ushort spell, EntityId book, EntityId target, int x, int y)
    {
        switch (MagicSystem.CanCast(world, caster, spell, book))
        {
            case MagicSystem.CommandRejectionReason.Unknown:
                return CommandRejection.SpellUnknown;
            case MagicSystem.CommandRejectionReason.NotReady:
                return CommandRejection.SpellNotReady;
            case MagicSystem.CommandRejectionReason.NoMana:
                return CommandRejection.NotEnoughMana;
        }

        SpellDefinition s = world.Ruleset.Spells[spell];
        byte owner = world.Identities.Get(caster).Owner;
        if (s.Target == SpellTarget.Unit && !MagicSystem.Affects(world, s, owner, target))
        {
            return CommandRejection.InvalidTarget;
        }

        if (s.Target == SpellTarget.Ground && !world.Map.ContainsPoint(x, y))
        {
            return CommandRejection.OutOfMap;
        }

        // A wandering area (the whirlpool) forms only on water, its whole square (ReTzar 0x4733c4).
        if (s.MoveMs > 0 && !MagicSystem.SquareOnWater(world, x / MapState.UnitsPerTile, y / MapState.UnitsPerTile, s.RadiusUnits / MapState.UnitsPerTile))
        {
            return CommandRejection.InvalidTarget;
        }

        if (!world.Casters.Has(caster))
        {
            world.Casters.Set(caster, new Caster { Spell = Projectile.NoSpell, MoveTick = -1 });
        }

        GameplayCommands.ClearTasks(world, caster);
        world.Errands.Remove(caster);
        ref Caster c = ref world.Casters.Get(caster);
        c.Spell = spell;
        c.Phase = CastPhase.Approach;
        c.Target = s.Target == SpellTarget.Unit ? target.Value : 0;
        c.X = s.Target == SpellTarget.Ground ? x : 0;
        c.Y = s.Target == SpellTarget.Ground ? y : 0;
        c.Book = book.Value;
        c.CastLeftMs = 0;
        c.MoveTick = -1;
        return null;
    }

    public static CommandRejection? SetStance(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if (command.Arg0 is < 0 or > (int)CombatStance.HoldFire)
        {
            return CommandRejection.InvalidArgument;
        }

        int applied = 0;
        foreach (EntityId entity in entities)
        {
            if (!world.Entities.IsAlive(entity) || world.Identities.Get(entity).Owner != command.Player || !world.Combats.Has(entity))
            {
                continue;
            }

            if (command.Arg0 == 0)
            {
                world.Stances.Remove(entity);
            }
            else
            {
                world.Stances.Set(entity, new StanceState { Value = (CombatStance)command.Arg0 });
            }

            applied++;
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
    }

    public static CommandRejection? AttackGround(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if (!world.Map.ContainsPoint(command.X, command.Y))
        {
            return CommandRejection.OutOfMap;
        }

        int applied = 0;
        foreach (EntityId entity in entities)
        {
            if (!world.Entities.IsAlive(entity) || world.Identities.Get(entity).Owner != command.Player || world.Combats.IndexOf(entity) is not (>= 0 and var c)
                || world.TypeOf(entity).Traits.Armament is not { RangeUnits: > 0 })
            {
                continue;
            }

            GameplayCommands.ClearTasks(world, entity, CombatMode.Ground);
            Interaction.Halt(world, entity);
            ref Combat combat = ref world.Combats.At(c);
            combat.ChaseX = command.X;
            combat.ChaseY = command.Y;
            applied++;
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
    }

    public static CommandRejection? Convert(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if ((uint)command.Arg0 >= (uint)world.Ruleset.EntityTypes.Count || world.Ruleset.EntityTypes[command.Arg0].Traits.ConvertsFrom is not { } conversion)
        {
            return CommandRejection.InvalidArgument;
        }

        EntityTypeDefinition into = world.Ruleset.EntityTypes[command.Arg0];
        CommandRejection? last = CommandRejection.NoValidEntities;
        int applied = 0;
        foreach (EntityId unit in entities)
        {
            if (!IsOwnedUnit(world, unit, command.Player) || conversion.From.BinarySearch(world.Identities.Get(unit).TypeId) < 0 || world.Errands.Has(unit))
            {
                continue;
            }

            string civ = world.CivilizationOf(unit);
            int gate = world.Ruleset.OrderTechnology(conversion.Order, civ);
            if ((gate >= 0 && !world.Players[command.Player].HasTechnology(gate))
                || !ResearchRules.RequirementsMet(world, command.Player, civ, into.Traits.Requirements, into.Traits.RequiredTypes))
            {
                last = CommandRejection.RequirementNotMet;
                continue;
            }

            if (!ResearchRules.TryPayFor(world, command.Player, into, out _))
            {
                last = CommandRejection.NotAffordable;
                continue;
            }

            GameplayCommands.ClearTasks(world, unit);
            world.StopCasting(unit);
            Interaction.Halt(world, unit);
            world.Errands.Set(unit, new Errand { Kind = ErrandKind.Convert, Arg = into.Id, TimerMs = Math.Min(Math.Max(into.Traits.BuildTimeMs, 1), 3_600_000), MoveTick = -1 });
            applied++;
        }

        return applied > 0 ? null : last;
    }

    /// <summary>
    /// Replaces a converting unit by the new type at its place: level, experience, items and name carry over, hit points
    /// in proportion (UNVERIFIED).
    /// </summary>
    public static void FinishConversion(World world, EntityId unit, ushort type)
    {
        if (!world.Entities.IsAlive(unit) || world.Entities.RemainingCapacity < 1)
        {
            return;
        }

        Identity identity = world.Identities.Get(unit);
        Position at = world.Positions.Get(unit);
        Health health = world.Healths.Get(unit);
        EntityId next = world.Spawn(type, identity.Owner, at.X, at.Y);
        world.Identities.Get(next).Facing = identity.Facing;
        if (world.Experiences.IndexOf(unit) is >= 0 and var x && world.Experiences.IndexOf(next) is >= 0 and var y)
        {
            Experience old = world.Experiences.At(x);
            ref Experience e = ref world.Experiences.At(y);
            e.Xp = Math.Max(e.Xp, old.Xp);
            e.Level = Math.Max(e.Level, old.Level);
            e.BonusAttack = old.BonusAttack;
            e.BonusDefence = old.BonusDefence;
            e.Flags = old.Flags;
            world.RefreshMaxHealth(next);
        }

        if (world.Names.Has(unit))
        {
            world.Names.SetValue(next, world.Names.Get(unit));
        }

        if (world.Inventories.IndexOf(unit) is >= 0 and var inv)
        {
            Inventory items = world.Inventories.At(inv);
            for (int k = items.Count - 1; k >= 0; k--)
            {
                EntityId item = EntityId.FromValue(items.Items[k]);
                if (CanCarry(world, next, item))
                {
                    world.PutInInventory(next, item);
                }
            }
        }

        ref Health h = ref world.Healths.Get(next);
        h.Current = (int)Math.Clamp((long)h.Max * health.Current / Math.Max(1, health.Max), 1, h.Max);
        world.Destroy(unit);
        world.Events.Emit(new SimEvent(SimEventKind.UnitConverted, identity.Owner, next, identity.TypeId, 0, type));
    }

    public static CommandRejection? Bribe(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId target = command.Target;
        if (BribeTargetError(world, command.Player, target) is { } error)
        {
            return error;
        }

        CommandRejection? last = CommandRejection.NoValidEntities;
        int applied = 0;
        foreach (EntityId unit in entities)
        {
            if (!IsOwnedUnit(world, unit, command.Player) || (world.TypeOf(unit).Traits.Abilities & AbilityFlags.Bribe) == 0)
            {
                continue;
            }

            int gate = world.Ruleset.OrderTechnology("bribe", world.CivilizationOf(unit));
            if (gate >= 0 && !world.Players[command.Player].HasTechnology(gate))
            {
                last = CommandRejection.RequirementNotMet;
                continue;
            }

            if (world.Players[command.Player].Resources[GoldId(world)] < BribeCost(world, target))
            {
                last = CommandRejection.NotAffordable;
                continue;
            }

            StartErrand(world, unit, ErrandKind.Bribe, target, 0);
            applied++;
        }

        return applied > 0 ? null : last;
    }

    private static CommandRejection? BribeTargetError(World world, byte player, EntityId target)
    {
        if (!world.Entities.IsAlive(target) || world.Identities.IndexOf(target) is not (>= 0 and var t) || world.IsHeld(target)
            || !world.HasPlayer(world.Identities.At(t).Owner) || world.Identities.At(t).Owner == player
            || world.TypeOf(target).Traits.Category != EntityCategory.Unit || GoldId(world) < 0)
        {
            return CommandRejection.InvalidArgument;
        }

        return (world.TypeOf(target).Traits.Abilities & AbilityFlags.BribeImmune) != 0 || world.IsHero(target) || world.CrusaderLinks.Has(target)
            ? CommandRejection.InvalidTarget
            : null;
    }

    /// <summary>Bribes on arrival: re-validated, paid in gold, the unit changes sides.</summary>
    public static void TryBribe(World world, EntityId unit, EntityId target)
    {
        byte player = world.Identities.Get(unit).Owner;
        int gold = GoldId(world);
        if (!world.HasPlayer(player) || BribeTargetError(world, player, target) is not null || world.Players[player].Resources[gold] < BribeCost(world, target))
        {
            return;
        }

        world.Players[player].Resources[gold] -= BribeCost(world, target);
        byte old = world.Identities.Get(target).Owner;
        GameplayCommands.ClearTasks(world, target);
        world.ChangeOwner(target, player);
        world.Events.Emit(new SimEvent(SimEventKind.Bribed, player, target, old, 0, world.Identities.Get(target).TypeId));
    }

    // The gold a bribe costs: the unit's cost (every resource, counted as gold) times combat.bribeCostPercent.
    private static int BribeCost(World world, EntityId target)
    {
        long sum = 0;
        foreach (int c in world.TypeOf(target).Traits.Cost)
        {
            sum += c;
        }

        return (int)Math.Min(sum * world.Ruleset.Combat.BribeCostPercent / 100, int.MaxValue);
    }

    private static int GoldId(World world) => world.Ruleset.ResourceId("gold");

    public static CommandRejection? PreachCrusade(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        CombatSettings settings = world.Ruleset.Combat;
        if (settings.CrusadeAnchor < 0 || settings.Crusader < 0)
        {
            return CommandRejection.InvalidArgument;
        }

        if (!world.Map.ContainsPoint(command.X, command.Y))
        {
            return CommandRejection.OutOfMap;
        }

        foreach (EntityId cathedral in entities)
        {
            if (!world.Entities.IsAlive(cathedral) || world.Identities.Get(cathedral).Owner != command.Player
                || (world.TypeOf(cathedral).Traits.Abilities & AbilityFlags.PreachCrusade) == 0 || world.Constructions.Has(cathedral))
            {
                continue;
            }

            int gate = world.Ruleset.OrderTechnology("preach_crusade", world.CivilizationOf(cathedral));
            if (gate >= 0 && !world.Players[command.Player].HasTechnology(gate))
            {
                return CommandRejection.RequirementNotMet;
            }

            foreach (EntityId anchor in world.Crusades.Entities)
            {
                if (world.Identities.Get(anchor).Owner == command.Player)
                {
                    return CommandRejection.CrusadeActive; // one crusade at a time, until its crusaders have all died
                }
            }

            // The crusade takes every crusader the kingdom's cathedrals have gathered (MAG-017: early, fewer for the same
            // price); a full one raises the ceiling.
            ref PlayerState kingdom = ref world.Players[command.Player];
            int size = kingdom.Crusaders;
            if (size <= 0)
            {
                return CommandRejection.CrusadeActive;
            }

            long d = Interaction.DistanceSquaredTo(world, command.X, command.Y, cathedral);
            if (d > (long)settings.CrusadeAnchorRadiusUnits * settings.CrusadeAnchorRadiusUnits
                || !world.Map.IsPassableAt(world.Ruleset.EntityTypes[settings.Crusader].Locomotor, command.X, command.Y))
            {
                return CommandRejection.IllegalPlacement;
            }

            if (world.Entities.RemainingCapacity < 1 + size)
            {
                return CommandRejection.EntityLimit;
            }

            EntityTypeDefinition anchorType = world.Ruleset.EntityTypes[settings.CrusadeAnchor];
            if (!ResearchRules.TryPay(world, command.Player, anchorType.Traits.Cost))
            {
                return CommandRejection.NotAffordable;
            }

            if (size >= MagicSystem.CrusadeCeiling(world, command.Player) && kingdom.FullCrusades < byte.MaxValue)
            {
                kingdom.FullCrusades++;
            }

            kingdom.Crusaders = 0;
            EntityId a = world.Spawn(anchorType.Id, command.Player, command.X, command.Y);
            world.Crusades.Set(a, new CrusadeState { ToRaise = size, NextTick = world.Tick.Value });
            world.Events.Emit(new SimEvent(SimEventKind.CrusadePreached, command.Player, a, cathedral.Index, 0, size));
            return null;
        }

        return CommandRejection.NoValidEntities;
    }

    public static CommandRejection? CraftItem(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        CombatSettings settings = world.Ruleset.Combat;
        int gold = GoldId(world);
        if ((uint)command.Arg0 >= (uint)world.Ruleset.EntityTypes.Count || gold < 0 || command.Arg1 < settings.CraftGoldPerPower
            || world.Ruleset.EntityTypes[command.Arg0].Traits.Item is not { } made || (uint)command.X >= (uint)made.CraftVariantCount)
        {
            return CommandRejection.InvalidArgument;
        }

        // 1.05: magic books cost four times more and take twice as long (craftBook*Percent).
        bool book = world.Ruleset.EntityTypes[command.Arg0].Traits.Item is { Kind: ItemKind.SpellBook };
        int unitPrice = book ? settings.CraftGoldPerPower * settings.CraftBookCostPercent / 100 : settings.CraftGoldPerPower;
        if (command.Arg1 < unitPrice)
        {
            return CommandRejection.InvalidArgument;
        }

        int power = Math.Min(command.Arg1 / unitPrice, settings.CraftMaxPower);
        int price = power * unitPrice;
        foreach (EntityId workshop in entities)
        {
            if (!world.Entities.IsAlive(workshop) || world.Identities.Get(workshop).Owner != command.Player || world.TypeOf(workshop).Traits.Crafter is not { } crafter
                || world.Constructions.Has(workshop) || world.CraftJobs.Has(workshop) || crafter.Items.BinarySearch((ushort)command.Arg0) < 0)
            {
                continue;
            }

            if (world.Players[command.Player].Resources[gold] < price)
            {
                return CommandRejection.NotAffordable;
            }

            world.Players[command.Player].Resources[gold] -= price;
            world.CraftJobs.Set(workshop, new CraftJob { ItemType = command.Arg0, Power = power, LeftMs = Math.Max(1, settings.CraftMs(book, power)), Variant = command.X });
            return null;
        }

        return CommandRejection.NoValidEntities;
    }

    private static void StartErrand(World world, EntityId unit, ErrandKind kind, EntityId target, int arg)
    {
        GameplayCommands.ClearTasks(world, unit);
        world.StopCasting(unit);
        world.Errands.Set(unit, new Errand { Kind = kind, Target = target.Value, Arg = arg, MoveTick = -1 });
    }

    private static EntityId ItemInSlot(World world, EntityId unit, int slot)
    {
        if (world.Inventories.IndexOf(unit) is not (>= 0 and var i) || (uint)slot >= world.Inventories.At(i).Count)
        {
            return EntityId.None;
        }

        return EntityId.FromValue(world.Inventories.At(i).Items[slot]);
    }

    // A free unit of the player, or of a kingdom that shares unit control with it (GAME-13): boarding, unloading and
    // carrying items are orders such a player gives too (ADR 0135).
    private static bool IsCommandedUnit(World world, EntityId unit, byte player)
        => world.MayCommand(player, unit) && world.Motions.Has(unit) && !world.IsHeld(unit);

    private static bool IsOwnedUnit(World world, EntityId unit, byte player)
        => world.Entities.IsAlive(unit) && world.Identities.IndexOf(unit) is >= 0 and var i && world.Identities.At(i).Owner == player
            && world.Motions.Has(unit) && !world.IsHeld(unit);
}
