using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Ecs;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Tzar2.Sim.Systems;

namespace Tzar2.Sim;

/// <content>
/// GAME-07…11 state (ADRs 0090-0095): experience, holders and what they hold, inventories and items, projectiles, area
/// spells, status effects, casters, stances, crafting and crusades; the helpers that keep them consistent when entities
/// enter or leave holders, change owner, convert or die.
/// </content>
public sealed partial class World
{
    /// <summary>Experience of units (hot; GAME-08).</summary>
    public ComponentStore<Experience> Experiences { get; private set; } = null!;

    /// <summary>Units inside holders and items inside inventories (hot; GAME-09/10).</summary>
    public ComponentStore<Held> Helds { get; private set; } = null!;

    /// <summary>Holders' bookkeeping (hot; GAME-09).</summary>
    public ComponentStore<HolderState> Holders { get; private set; } = null!;

    /// <summary>Spies' disguises (MAG-012, ADR 0184).</summary>
    public ComponentStore<Disguise> Disguises { get; private set; } = null!;

    /// <summary>The spell of a book <paramref name="item"/>: its own (ITEM-016), else its type's; -1 when none.</summary>
    public int ItemSpell(EntityId item)
    {
        int own = Items.IndexOf(item) is >= 0 and var i ? Items.At(i).Spell : 0;
        return own > 0 ? own - 1 : TypeOf(item).Traits.Item?.Spell ?? -1;
    }

    /// <summary>The points units walk after their current move (ORD-005).</summary>
    public ComponentStore<WaypointQueue> WaypointQueues { get; private set; } = null!;

    /// <summary>The kingdom's market holding its items on offer (Jewelry, TECH-030).</summary>
    public ComponentStore<MarketStock> MarketStocks { get; private set; } = null!;

    /// <summary>The spells units have learned from books (ITEM-019, ADR 0266); only units that learned one have an entry.</summary>
    public ComponentStore<LearnedSpells> Learned { get; private set; } = null!;

    /// <summary>Whether <paramref name="unit"/> has learned <paramref name="spell"/> from a book (ITEM-019).</summary>
    public bool HasLearned(EntityId unit, int spell) => Learned.Count > 0 && Learned.IndexOf(unit) is >= 0 and var l && Learned.At(l).Has(spell);

    /// <summary>
    /// A caster's most mana: its pool's maximum with its owner's technologies, or the cost of a spell it was ordered to cast
    /// and could not afford when that is more (ReTzar 0x4e1c30, ADR 0267); 0 for a unit without a pool.
    /// </summary>
    public int MaxMana(EntityId unit)
    {
        if (Manas.IndexOf(unit) is not (>= 0 and var m) || TypeOf(unit).Traits.ManaPool is not { } pool)
        {
            return 0;
        }

        Identity identity = Identities.Get(unit);
        return Math.Max(Stats.Apply(identity.Owner, ModifierStat.ManaMax, identity.TypeId, pool.Max), Manas.At(m).RaisedMax);
    }

    /// <summary>Units' errands: boarding, pick-up, give, bribe, conversion (hot).</summary>
    public ComponentStore<Errand> Errands { get; private set; } = null!;

    /// <summary>Units' inventories (hot; GAME-10).</summary>
    public ComponentStore<Inventory> Inventories { get; private set; } = null!;

    /// <summary>Item entities' state (hot; GAME-10).</summary>
    public ComponentStore<ItemState> Items { get; private set; } = null!;

    /// <summary>Projectiles in flight (hot; GAME-07).</summary>
    public ComponentStore<Projectile> Projectiles { get; private set; } = null!;

    /// <summary>Area spells in progress (hot; GAME-11).</summary>
    public ComponentStore<AreaEffect> AreaEffects { get; private set; } = null!;

    /// <summary>Lasting spell effects (hot; GAME-11).</summary>
    public ComponentStore<StatusEffects> Statuses { get; private set; } = null!;

    /// <summary>Casters' spells in progress and cooldowns (hot; GAME-11).</summary>
    public ComponentStore<Caster> Casters { get; private set; } = null!;

    /// <summary>Non-default combat stances (hot; GAME-07).</summary>
    public ComponentStore<StanceState> Stances { get; private set; } = null!;

    /// <summary>Items being crafted (hot; GAME-10).</summary>
    public ComponentStore<CraftJob> CraftJobs { get; private set; } = null!;

    /// <summary>Crusade anchors (hot; GAME-11).</summary>
    public ComponentStore<CrusadeState> Crusades { get; private set; } = null!;

    /// <summary>Crusaders and their anchors (hot; GAME-11).</summary>
    public ComponentStore<CrusaderOf> CrusaderLinks { get; private set; } = null!;

    // Registration order fixes the snapshot and hash layout (ADR 0016): appended after the M3 stream A stores.
    private void RegisterCombatMagicStores()
    {
        Experiences = Components.RegisterHot<Experience>("game.experience");
        Helds = Components.RegisterHot<Held>("game.held");
        Holders = Components.RegisterHot<HolderState>("game.holder");
        Errands = Components.RegisterHot<Errand>("game.errand");
        Inventories = Components.RegisterHot<Inventory>("game.inventory");
        Items = Components.RegisterHot<ItemState>("game.item");
        Projectiles = Components.RegisterHot<Projectile>("game.projectile");
        AreaEffects = Components.RegisterHot<AreaEffect>("game.area");
        Statuses = Components.RegisterHot<StatusEffects>("game.status");
        Casters = Components.RegisterHot<Caster>("game.caster");
        Stances = Components.RegisterHot<StanceState>("game.stance");
        CraftJobs = Components.RegisterHot<CraftJob>("game.craft");
        Crusades = Components.RegisterHot<CrusadeState>("game.crusade");
        CrusaderLinks = Components.RegisterHot<CrusaderOf>("game.crusader");
        Disguises = Components.RegisterHot<Disguise>("game.disguise");
        WaypointQueues = Components.RegisterHot<WaypointQueue>("game.waypoints");
        MarketStocks = Components.RegisterHot<MarketStock>("game.market");
        Learned = Components.RegisterHot<LearnedSpells>("game.learned");
    }

    /// <summary>
    /// Whether <paramref name="entity"/> is a spy disguised as a unit of <paramref name="kingdom"/> or of one of its allies,
    /// so that kingdom's units leave it alone (ADR 0184).
    /// </summary>
    public bool LooksFriendlyTo(EntityId entity, int kingdom)
    {
        if (Disguises.Count == 0 || Disguises.IndexOf(entity) is not (>= 0 and var d) || (uint)kingdom >= (uint)Players.Length)
        {
            return false;
        }

        byte seeming = Disguises.At(d).Owner;
        return seeming == kingdom || Diplomacy.StanceOf(kingdom, seeming) == Tzar2.Sim.Kingdoms.Stance.Ally;
    }

    /// <summary>Whether an entity is inside a holder or an inventory (it is then not on the map: no movement, no targeting).</summary>
    public bool IsHeld(EntityId entity) => Helds.Count != 0 && Helds.Has(entity);

    /// <summary>Whether a unit is invisible (spell or ninja cloak; GAME-11). Visibility (GAME-12) consults this.</summary>
    public bool IsCloaked(EntityId entity)
        => Statuses.Count != 0 && Statuses.IndexOf(entity) is >= 0 and var s && Statuses.At(s).CloakUntil > Tick.Value;

    /// <summary>Whether a unit can act this tick: not frozen and not turned to stone.</summary>
    public bool CanAct(EntityId entity)
    {
        if (Statuses.Count == 0 || Statuses.IndexOf(entity) is not (>= 0 and var s))
        {
            return true;
        }

        ref StatusEffects st = ref Statuses.At(s);
        return st.FrozenUntil <= Tick.Value && st.StoneUntil <= Tick.Value;
    }

    /// <summary>Whether a unit is stone (takes no damage).</summary>
    public bool IsStone(EntityId entity)
        => Statuses.Count != 0 && Statuses.IndexOf(entity) is >= 0 and var s && Statuses.At(s).StoneUntil > Tick.Value;

    /// <summary>Whether a unit is confused (hostile to all).</summary>
    public bool IsConfused(EntityId entity)
        => Statuses.Count != 0 && Statuses.IndexOf(entity) is >= 0 and var s && Statuses.At(s).ConfuseUntil > Tick.Value;

    /// <summary>
    /// The kingdom a hypnotized unit belongs to and goes back to, or -1 (MAG-008): it keeps that kingdom's colours and
    /// counts in its population while it obeys the caster's.
    /// </summary>
    public int HypnotizedFrom(EntityId entity)
        => Statuses.Count != 0 && Statuses.IndexOf(entity) is >= 0 and var s && Statuses.At(s).HypnoUntil > Tick.Value ? Statuses.At(s).HypnoOwner : -1;

    /// <summary>Whether a unit is a named hero.</summary>
    public bool IsHero(EntityId entity)
        => Experiences.IndexOf(entity) is >= 0 and var x && (Experiences.At(x).Flags & ExperienceFlags.Hero) != 0;

    /// <summary>A holder's capacity after its owner's technologies (Landing Tactics).</summary>
    public int HolderCapacity(EntityId holder)
    {
        EntityTypeDefinition type = TypeOf(holder);
        int base_ = type.Traits.Holder?.Capacity ?? 0;
        return Math.Clamp(Stats.Apply(Identities.Get(holder).Owner, ModifierStat.HolderCapacity, type.Id, base_), 0, HolderDefinition.MaxCapacity);
    }

    // Spawn/placement hook: the components the stream E traits call for.
    private void AddCombatMagicComponents(EntityId entity, EntityTypeDefinition type, bool underConstruction)
    {
        EntityTraits t = type.Traits;
        byte owner = Identities.Get(entity).Owner;
        if (t.Item is { } item)
        {
            Items.Set(entity, new ItemState { Power = item.Power, Charges = item.Charges });
        }

        if (underConstruction)
        {
            return;
        }

        if (t.GainsExperience)
        {
            int start = Math.Max(0, Stats.Apply(owner, ModifierStat.StartingLevel, type.Id, 0));
            var xp = new Experience { Level = (ushort)Math.Min(start, ushort.MaxValue) };
            if (start > 0)
            {
                xp.Xp = CombatStats.XpForLevel(Ruleset.Combat, start, CombatStats.ExperienceSpeed(this, owner));
            }

            Experiences.Set(entity, xp);
            if (start > 0)
            {
                RefreshMaxHealth(entity);
                ref Health h = ref Healths.Get(entity);
                h.Current = h.Max;
            }
        }

        if (t.InventorySlots > 0)
        {
            Inventories.Set(entity, new Inventory { Default = Inventory.NoDefault });
        }

        if (t.Holder is not null)
        {
            Holders.Set(entity, default);
        }

        if (!t.Spells.IsDefaultOrEmpty)
        {
            Casters.Set(entity, new Caster { Spell = Projectile.NoSpell, MoveTick = -1 });
        }
    }

    // Extra maximum hit points of an entity from its level (Body Building).
    private int LevelHealth(EntityId entity, int owner, EntityTypeDefinition type)
    {
        if (Experiences.Count == 0 || Experiences.IndexOf(entity) is not (>= 0 and var x))
        {
            return 0;
        }

        int perLevel = Stats.Apply(owner, ModifierStat.HealthPerLevel, type.Id, 0);
        return (int)Math.Clamp((long)perLevel * Experiences.At(x).Level, 0, 1_000_000);
    }

    /// <summary>
    /// Puts a unit into a holder (GAME-09): it leaves the map (no movement, occupancy, fight or task) and moves with the
    /// holder. The caller checked ownership, acceptance and room.
    /// </summary>
    internal void Board(EntityId unit, EntityId holder)
    {
        GameplayCommands.ClearTasks(this, unit);
        Errands.Remove(unit);
        StopCasting(unit);
        int serial = 0;
        if (Motions.IndexOf(unit) is >= 0 and var m)
        {
            MovementOrders.Stop(this, unit, ref Motions.At(m));
            serial = Motions.At(m).RequestSerial;
            Motions.Remove(unit);
        }

        if (OccupancyCells.IndexOf(unit) is >= 0 and var c)
        {
            if (OccupancyCells.At(c).Cell >= 0)
            {
                Map.RemoveOccupant(OccupancyCells.At(c).Cell);
            }

            OccupancyCells.Remove(unit);
        }

        Paths.Clear(unit.Index);
        Positions.Get(unit) = Positions.Get(holder);
        Helds.Set(unit, new Held { Holder = holder.Value, RequestSerial = serial });
        Holders.Get(holder).Count++;
        Events.Emit(new SimEvent(SimEventKind.Boarded, Identities.Get(unit).Owner, unit, holder.Index, 0, Identities.Get(unit).TypeId));
    }

    /// <summary>
    /// Lets a unit out of its holder at the free tile nearest to (<paramref name="towardX"/>, <paramref name="towardY"/>)
    /// within <c>combat.exitRadiusTiles</c>.
    /// </summary>
    /// <returns><c>false</c> (the unit stays inside) when there is no room for it.</returns>
    internal bool Unload(EntityId unit, int towardX, int towardY)
    {
        Held held = Helds.Get(unit);
        EntityId holder = EntityId.FromValue(held.Holder);
        EntityTypeDefinition type = TypeOf(unit);
        if (!ExitPoint(holder, type.Id, towardX, towardY, out int x, out int y))
        {
            return false;
        }

        Helds.Remove(unit);
        ref HolderState state = ref Holders.Get(holder);
        state.Count--;
        Positions.Get(unit) = new Position(x, y);
        Motions.Set(unit, new Motion
        {
            TargetX = x,
            TargetY = y,
            SteerX = x,
            SteerY = y,
            OrderX = x,
            OrderY = y,
            SpeedPerTick = SpeedPerTick(type.Id),
            Radius = type.Radius,
            GoalTile = -1,
            Layer = Navigation.LayerOfType(type.Id),
            State = MotionState.Idle,
            RequestSerial = held.RequestSerial + 1,
        });
        int cell = Map.CellIndexAt(x, y);
        OccupancyCells.Set(unit, new OccupancyCell { Cell = cell });
        Map.AddOccupant(cell);
        Events.Emit(new SimEvent(SimEventKind.Unloaded, Identities.Get(unit).Owner, unit, holder.Index, 0, type.Id));
        return true;
    }

    /// <summary>
    /// Where a unit of <paramref name="unitType"/> leaving a holder stands: next to a building's footprint, or the
    /// nearest tile around a ship or carpet that its navigation layer can stand on (ties: row-major order).
    /// </summary>
    internal bool ExitPoint(EntityId holder, ushort unitType, int towardX, int towardY, out int x, out int y)
    {
        Locomotor locomotor = Ruleset.EntityTypes[unitType].Locomotor;
        if (Footprints.Has(holder))
        {
            return Interaction.ApproachPoint(this, holder, towardX, towardY, locomotor, out x, out y);
        }

        Navigation.NavLayer? layer = Navigation.ForType(unitType);

        Position at = Positions.Get(holder);
        int cx = at.X / MapState.UnitsPerTile;
        int cy = at.Y / MapState.UnitsPerTile;
        int radius = Ruleset.Combat.ExitRadiusTiles;
        long best = long.MaxValue;
        x = 0;
        y = 0;
        for (int ty = cy - radius; ty <= cy + radius; ty++)
        {
            for (int tx = cx - radius; tx <= cx + radius; tx++)
            {
                if (!Map.ContainsTile(tx, ty) || !Map.IsPassable(locomotor, tx, ty))
                {
                    continue;
                }

                int px = (tx * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2);
                int py = (ty * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2);
                if (layer is not null && !layer.IsWalkablePoint(px, py))
                {
                    continue; // too narrow for the unit's clearance (REQ-PATH-09)
                }
                long d = Core.Numerics.IntMath.DistanceSquared(px, py, towardX, towardY) + Core.Numerics.IntMath.DistanceSquared(px, py, at.X, at.Y);
                if (d < best)
                {
                    best = d;
                    x = px;
                    y = py;
                }
            }
        }

        return best != long.MaxValue;
    }

    /// <summary>
    /// Ends a unit's errand and spell in progress (a new order replaces them); a conversion in progress is refunded what
    /// it cost.
    /// </summary>
    internal void CancelErrandAndCast(EntityId unit)
    {
        if (Errands.Count != 0 && Errands.IndexOf(unit) is >= 0 and var e)
        {
            Errand errand = Errands.At(e);
            if (errand.Kind == ErrandKind.Convert && HasPlayer(Identities.Get(unit).Owner))
            {
                // The current cost modifier: technologies only lower costs, so this never returns more than was paid.
                byte owner = Identities.Get(unit).Owner;
                (int add, int bp) = Stats.Modifier(owner, ModifierStat.Cost, (ushort)errand.Arg);
                ResearchRules.RefundFor(this, owner, Ruleset.EntityTypes[errand.Arg], new PaidModifier { Add = add, BasisPoints = bp }, 10_000);
            }

            Errands.Remove(unit);
        }

        StopCasting(unit);
    }

    /// <summary>Ends a caster's spell in progress (it keeps its cooldown).</summary>
    internal void StopCasting(EntityId entity)
    {
        if (Casters.Count != 0 && Casters.IndexOf(entity) is >= 0 and var c)
        {
            ref Caster caster = ref Casters.At(c);
            caster.Phase = CastPhase.None;
            caster.Spell = Projectile.NoSpell;
            caster.Target = 0;
            caster.Book = 0;
            caster.CastLeftMs = 0;
            caster.MoveTick = -1;
            caster.X = 0;
            caster.Y = 0;
        }
    }

    /// <summary>Puts an item on the ground at a point (dropped, or left by a dead holder).</summary>
    internal void DropItemAt(EntityId item, int x, int y)
    {
        if (Helds.IndexOf(item) is >= 0 and var h)
        {
            EntityId owner = EntityId.FromValue(Helds.At(h).Holder);
            Helds.Remove(item);
            RemoveFromInventory(owner, item);
        }

        Positions.Get(item) = new Position(Math.Clamp(x, 0, Map.WorldWidth - 1), Math.Clamp(y, 0, Map.WorldHeight - 1));
        Events.Emit(new SimEvent(SimEventKind.ItemDropped, MapObject.Nature, item, x, y, Identities.Get(item).TypeId));
    }

    /// <summary>Moves an item into a unit's inventory (the caller checked room and who may carry it).</summary>
    internal void PutInInventory(EntityId unit, EntityId item)
    {
        if (Helds.IndexOf(item) is >= 0 and var h)
        {
            EntityId previous = EntityId.FromValue(Helds.At(h).Holder);
            RemoveFromInventory(previous, item);
        }

        ref Inventory inventory = ref Inventories.Get(unit);
        inventory.Items[inventory.Count++] = item.Value;
        Helds.Set(item, new Held { Holder = unit.Value });
        Positions.Get(item) = Positions.Get(unit);
    }

    private void RemoveFromInventory(EntityId unit, EntityId item)
    {
        if (Inventories.IndexOf(unit) is not (>= 0 and var i))
        {
            return;
        }

        ref Inventory inventory = ref Inventories.At(i);
        for (int k = 0; k < inventory.Count; k++)
        {
            if (inventory.Items[k] != item.Value)
            {
                continue;
            }

            for (int j = k; j < inventory.Count - 1; j++)
            {
                inventory.Items[j] = inventory.Items[j + 1];
            }

            inventory.Count--;
            inventory.Items[inventory.Count] = 0;
            if (inventory.Default == k)
            {
                inventory.Default = Inventory.NoDefault;
            }
            else if (inventory.Default != Inventory.NoDefault && inventory.Default > k)
            {
                inventory.Default--;
            }

            return;
        }
    }

    // Destroy hook (every removal: death, deletion, conversion, decay): what the entity holds leaves it first, and it
    // leaves whatever holds it. Units inside a destroyed holder take the nearest free tiles; where there are none
    // (a ship at sea) they go down with it.
    private void BeforeDestroyCombatMagic(EntityId entity)
    {
        if (MarketStocks.Count != 0 && MarketStocks.Has(entity))
        {
            Systems.MarketItems.Clear(this, entity); // the offers go with their market (TECH-030)
        }

        // The soul items bound to a unit go with it (ITEM-010; ReTzar's reading of the original, 0x4e219f).
        if (Items.Count != 0 && !Items.Has(entity))
        {
            Span<uint> gems = stackalloc uint[16];
            int found = 0;
            ReadOnlySpan<EntityId> items = Items.Entities;
            for (int i = 0; i < items.Length && found < gems.Length; i++)
            {
                if (Items.Components[i].Bound == entity.Value)
                {
                    gems[found++] = items[i].Value;
                }
            }

            for (int k = 0; k < found; k++)
            {
                Destroy(EntityId.FromValue(gems[k]));
            }
        }

        if (Inventories.Count != 0 && Inventories.IndexOf(entity) is >= 0 and var inv && Inventories.At(inv).Count > 0)
        {
            Inventory items = Inventories.At(inv);
            Position at = Positions.Get(entity);
            for (int k = items.Count - 1; k >= 0; k--)
            {
                EntityId item = EntityId.FromValue(items.Items[k]);
                if (Entities.IsAlive(item))
                {
                    DropItemAt(item, at.X, at.Y);
                }
            }
        }

        if (Holders.Count != 0 && Holders.IndexOf(entity) is >= 0 and var hs && Holders.At(hs).Count > 0)
        {
            Span<uint> inside = stackalloc uint[HolderDefinition.MaxCapacity];
            int n = 0;
            ReadOnlySpan<EntityId> held = Helds.Entities;
            Span<Held> holds = Helds.Components;
            for (int i = 0; i < held.Length && n < inside.Length; i++)
            {
                if (holds[i].Holder == entity.Value && Items.IndexOf(held[i]) < 0)
                {
                    inside[n++] = held[i].Value;
                }
            }

            Position at = Positions.Get(entity);
            for (int k = 0; k < n; k++)
            {
                EntityId unit = EntityId.FromValue(inside[k]);
                if (Entities.IsAlive(unit) && !Unload(unit, at.X, at.Y))
                {
                    Events.Emit(new SimEvent(SimEventKind.Killed, Identities.Get(unit).Owner, unit, entity.Index, Identities.Get(entity).Owner, Identities.Get(unit).TypeId));
                    Kill(unit);
                }
            }
        }

        if (Helds.Count != 0 && Helds.IndexOf(entity) is >= 0 and var me)
        {
            EntityId holder = EntityId.FromValue(Helds.At(me).Holder);
            Helds.Remove(entity);
            if (Items.IndexOf(entity) >= 0)
            {
                RemoveFromInventory(holder, entity);
            }
            else if (Holders.IndexOf(holder) is >= 0 and var hh)
            {
                Holders.At(hh).Count--;
            }
        }
    }

    // ChangeOwner hook: a captured or bribed entity ends its errand and spell; a captured holder lets the previous
    // owner's units out; a bribed unit leaves the holder of its previous owner.
    private void AfterChangeOwnerCombatMagic(EntityId entity)
    {
        if (MarketStocks.Count != 0 && MarketStocks.Has(entity))
        {
            Systems.MarketItems.Clear(this, entity); // the offers were its old kingdom's (TECH-030)
        }

        Errands.Remove(entity);
        StopCasting(entity);
        CrusaderLinks.Remove(entity);
        if (Holders.IndexOf(entity) is >= 0 and var h && Holders.At(h).Count > 0)
        {
            Position at = Positions.Get(entity);
            Span<uint> inside = stackalloc uint[HolderDefinition.MaxCapacity];
            int n = 0;
            for (int i = 0; i < Helds.Count && n < inside.Length; i++)
            {
                if (Helds.Components[i].Holder == entity.Value && Items.IndexOf(Helds.Entities[i]) < 0)
                {
                    inside[n++] = Helds.Entities[i].Value;
                }
            }

            for (int k = 0; k < n; k++)
            {
                EntityId unit = EntityId.FromValue(inside[k]);
                if (!Unload(unit, at.X, at.Y))
                {
                    Kill(unit);
                }
            }
        }

        if (Helds.IndexOf(entity) is >= 0 and var held && Items.IndexOf(entity) < 0)
        {
            Position at = Positions.Get(entity);
            if (!Unload(entity, at.X, at.Y))
            {
                Kill(entity);
            }
        }
    }

    // ConvertType hook: stores the new type cannot hold go (a holder's units leave first).
    private void AfterConvertTypeCombatMagic(EntityId entity, EntityTypeDefinition next)
    {
        if (next.Traits.Holder is null && Holders.IndexOf(entity) is >= 0)
        {
            AfterChangeOwnerCombatMagic(entity);
            Holders.Remove(entity);
        }
        else if (next.Traits.Holder is not null && !Holders.Has(entity) && !Constructions.Has(entity))
        {
            Holders.Set(entity, default);
        }

        if (next.Traits.Spells.IsDefaultOrEmpty && !(Inventories.IndexOf(entity) >= 0))
        {
            Casters.Remove(entity);
        }
        else if (!next.Traits.Spells.IsDefaultOrEmpty && !Casters.Has(entity))
        {
            Casters.Set(entity, new Caster { Spell = Projectile.NoSpell, MoveTick = -1 });
        }
    }

    // CompleteConstruction hook.
    private void AfterCompleteCombatMagic(EntityId entity, EntityTypeDefinition type)
    {
        if (type.Traits.Holder is not null && !Holders.Has(entity))
        {
            Holders.Set(entity, default);
        }
    }
}
