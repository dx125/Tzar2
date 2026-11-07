using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Core.Rng;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 6 (activities, GAME-15, ADR 0104, ADR 0275): Nature's entities and the neutral ones. A magic gateway goes
/// to the kingdom whose fighters alone stand beside it, from whoever held it (NAT-001); the rescue kingdom's units join the
/// kingdom of a fighter beside them (NAT-008); a cow goes to a peasant beside it who knows Cow Poaching, unless its own
/// kingdom or an ally stands by it too (NAT-002); animals wander around their home; guarding units (bears,
/// guarded-town defenders) drop the chase and walk back when lured further than <c>guardRadiusTiles</c> from home. Cows
/// of every kingdom graze (NAT-007).
/// </summary>
public sealed class NatureSystem : ISimSystem
{
    /// <summary>NAT-007 (ReTzar 0x4e0a23): a cow walks to a cell at most this far from its home, in each axis.</summary>
    internal const int GrazeRadiusTiles = 2;

    /// <summary>NAT-007 (ReTzar 0x4ed120): the cycles of a cow's grazing before it walks.</summary>
    internal const int GrazeCycles = 30;

    // A cow that is milked grazes this many cycles more instead of walking (ReTzar 0x4ecb13).
    private const int MilkedCycles = 4;

    // A cell the cow cannot step into is drawn again this much later (ReTzar 0x4e0a23).
    private const int RedrawMs = 200;

    // How often cows without a home (trained, claimed, in an older save) are given one.
    private const int AdoptTicks = 20;

    // NatureHome.Graze's step that draws a cell to walk to.
    private const int StepDraw = 4;

    // The lengths of the grazing pictures (IMAGES.INI): cow_idle (800 + 9 x 200 ms), cow_down and cow_eat (1000 ms).
    private static readonly int[] PictureMs = [2600, 1000, 1000];

    // NAT-008 (ReTzar's reading of the original, op 0x7e at 0x502e18): a prisoner looks round every second.
    private const int RescueMs = 1000;

    // NAT-002 (ReTzar 0x4ec944, 0x4ecff8): Cow Poaching looks round a cow every two seconds.
    private const int PoachMs = 2000;

    private readonly List<(EntityId Entity, byte Owner)> _claims = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Activities;

    /// <inheritdoc />
    public string Name => "nature";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        int tick = world.Tick.Value;
        int every = Math.Max(1, world.Ruleset.World.ClaimIntervalMs / world.Config.TickLengthMilliseconds);
        if (tick % every == 0)
        {
            Claims(world);
        }

        int tickMs = world.Config.TickLengthMilliseconds;
        if (tick % Math.Max(1, RescueMs / tickMs) == 0)
        {
            Rescue(world);
        }

        if (tick % Math.Max(1, PoachMs / tickMs) == 0)
        {
            Poach(world);
        }

        if (tick % AdoptTicks == 0)
        {
            Adopt(world);
        }

        Roam(world, tick);
    }

    // Every cow has a home where it was last sent (NAT-007): one trained, claimed or loaded from an older save gets it here.
    private static void Adopt(World world)
    {
        ReadOnlySpan<EntityId> ids = world.Motions.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId e = ids[i];
            if (world.TypeOf(e).Traits.World.Grazes && !world.NatureHomes.Has(e) && world.Positions.IndexOf(e) is >= 0 and var p)
            {
                Position at = world.Positions.At(p);
                world.NatureHomes.Set(e, new NatureHome { X = at.X, Y = at.Y, Pasture = 2 });
            }
        }
    }

    // The tile a unit stands on.
    private static (int X, int Y) TileOf(World world, EntityId e)
    {
        Position p = world.Positions.Get(e);
        return (p.X / MapState.UnitsPerTile, p.Y / MapState.UnitsPerTile);
    }

    // A unit that takes gateways and frees prisoners (ReTzar 0x5028a0, flag 0x10000): a military unit of a kingdom that may
    // rescue, on the map and alive.
    private static bool Fighter(World world, EntityId e, out byte owner)
    {
        owner = world.Identities.Get(e).Owner;
        EntityTraits t = world.TypeOf(e).Traits;
        return t.Category == EntityCategory.Unit && t.World.IsMilitary && world.MayRescue(owner) && !world.IsHeld(e) && world.Positions.Has(e)
            && world.Healths.IndexOf(e) is >= 0 and var h && world.Healths.At(h).Current > 0;
    }

    // NAT-001 (ReTzar's reading of the original, 0x420fd0 over 0x41fd70): the fighters standing in a gateway's footprint
    // grown by one take it when they all belong to one kingdom, from whoever held it.
    private static void TakenByPresence(World world, EntityId gateway, byte holder, List<(EntityId, byte)> claims)
    {
        if (world.Footprints.IndexOf(gateway) is not (>= 0 and var f))
        {
            return;
        }

        Footprint fp = world.Footprints.At(f);
        EntityTypeDefinition type = world.TypeOf(gateway);
        int x0 = fp.TileX - 1, y0 = fp.TileY - 1, x1 = fp.TileX + fp.Width(type), y1 = fp.TileY + fp.Height(type);
        int taker = -1;
        ReadOnlySpan<EntityId> movers = world.Motions.Entities;
        for (int m = 0; m < movers.Length; m++)
        {
            if (!Fighter(world, movers[m], out byte owner))
            {
                continue;
            }

            (int tx, int ty) = TileOf(world, movers[m]);
            if (tx < x0 || tx > x1 || ty < y0 || ty > y1)
            {
                continue;
            }

            if (taker >= 0 && taker != owner)
            {
                return; // fighters of two kingdoms: nobody takes it
            }

            taker = owner;
        }

        if (taker >= 0 && taker != holder)
        {
            claims.Add((gateway, (byte)taker));
        }
    }

    // NAT-008 (ReTzar's reading of the original, 0x44092d, 0x502e18): a unit of the rescue kingdom joins the kingdom of
    // the first fighter standing on the tiles round it; what it holds goes with it.
    private void Rescue(World world)
    {
        _claims.Clear();
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId prisoner = ids[i];
            if (!world.IsRescue(world.Identities.Get(prisoner).Owner) || world.TypeOf(prisoner).Traits.Category != EntityCategory.Unit
                || world.IsHeld(prisoner) || !world.Positions.Has(prisoner))
            {
                continue;
            }

            (int px, int py) = TileOf(world, prisoner);
            for (int k = 0; k < ids.Length; k++)
            {
                if (k != i && world.Motions.Has(ids[k]) && Fighter(world, ids[k], out byte owner) && TileOf(world, ids[k]) is var t
                    && Math.Abs(t.X - px) <= 1 && Math.Abs(t.Y - py) <= 1)
                {
                    _claims.Add((prisoner, owner));
                    break;
                }
            }
        }

        foreach ((EntityId prisoner, byte owner) in _claims)
        {
            Take(world, prisoner, owner);
            ReadOnlySpan<EntityId> held = world.Helds.Entities;
            for (int h = held.Length - 1; h >= 0; h--)
            {
                if (world.Helds.Components[h].Holder == prisoner.Value && world.Identities.Has(held[h]))
                {
                    world.ChangeOwner(held[h], owner);
                }
            }
        }
    }

    // NAT-002 (ReTzar's reading of the original, poach_cows 0x4ec944): every two seconds, the peasants on the tiles round a
    // cow take it to their kingdom when none of them is of its kingdom or an ally of it and one of them knows Cow Poaching
    // (the last of them); that one loses its invisibility.
    private void Poach(World world)
    {
        _claims.Clear();
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId cow = ids[i];
            if (world.TypeOf(cow).Traits.Capturable is not { Instant: true, RequiredTechnology: >= 0 and var tech } || world.IsHeld(cow) || !world.Positions.Has(cow))
            {
                continue;
            }

            byte holder = world.Identities.Get(cow).Owner;
            (int cx, int cy) = TileOf(world, cow);
            EntityId thief = EntityId.None;
            bool guarded = false;
            ReadOnlySpan<EntityId> movers = world.Motions.Entities;
            for (int m = 0; m < movers.Length && !guarded; m++)
            {
                EntityId p = movers[m];
                if (p == cow || !world.TypeOf(p).Traits.IsCapturer || world.IsHeld(p) || !world.Positions.Has(p) || TileOf(world, p) is var t && (Math.Abs(t.X - cx) > 1 || Math.Abs(t.Y - cy) > 1))
                {
                    continue;
                }

                byte owner = world.Identities.Get(p).Owner;
                guarded = owner == holder || (world.HasPlayer(holder) && world.HasPlayer(owner) && world.Diplomacy.StanceOf(holder, owner) == Kingdoms.Stance.Ally);
                if (!guarded && world.HasPlayer(owner) && world.Players[owner].HasTechnology(tech))
                {
                    thief = p;
                }
            }

            if (!guarded && !thief.IsNone)
            {
                _claims.Add((cow, world.Identities.Get(thief).Owner));
                if (world.Statuses.IndexOf(thief) is >= 0 and var s)
                {
                    world.Statuses.At(s).CloakUntil = 0;
                }
            }
        }

        foreach ((EntityId cow, byte owner) in _claims)
        {
            Take(world, cow, owner);
        }
    }

    // An entity changes hands: its tasks end, it stops, and the new owner hears of it.
    private static void Take(World world, EntityId target, byte owner)
    {
        if (!world.TypeOf(target).Traits.World.Grazes)
        {
            world.NatureHomes.Remove(target);
        }

        GameplayCommands.ClearTasks(world, target);
        Interaction.Halt(world, target);
        world.ChangeOwner(target, owner);
        world.Events.Emit(new SimEvent(SimEventKind.Claimed, owner, target, 0, 0, world.Identities.Get(target).TypeId));
    }

    private void Claims(World world)
    {
        _claims.Clear();
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        long reach = world.Ruleset.World.ClaimReachUnits;
        for (int i = 0; i < ids.Length; i++)
        {
            if (world.Ruleset.EntityTypes[identities[i].TypeId].Traits.World.Claim is not { } by || world.Constructions.Has(ids[i]))
            {
                continue;
            }

            if (by == ClaimBy.Military)
            {
                TakenByPresence(world, ids[i], identities[i].Owner, _claims);
                continue;
            }

            if (identities[i].Owner != MapObject.Nature)
            {
                continue;
            }

            EntityId target = ids[i];
            EntityId best = EntityId.None;
            long bestDistance = long.MaxValue;
            ReadOnlySpan<EntityId> movers = world.Motions.Entities;
            for (int m = 0; m < movers.Length; m++)
            {
                Identity u = world.Identities.Get(movers[m]);
                if (!world.HasPlayer(u.Owner) || (world.Players[u.Owner].Flags & PlayerFlags.Defeated) != 0 || movers[m] == target)
                {
                    continue;
                }

                EntityTraits t = world.Ruleset.EntityTypes[u.TypeId].Traits;
                if (t.Category != EntityCategory.Unit || (by == ClaimBy.Military && !t.World.IsMilitary))
                {
                    continue;
                }

                long d = Interaction.EdgeDistance(world, world.Positions.Get(movers[m]), Interaction.RadiusOf(world, movers[m]), target);
                if (d <= reach && d < bestDistance)
                {
                    bestDistance = d;
                    best = movers[m];
                }
            }

            if (!best.IsNone)
            {
                _claims.Add((target, world.Identities.Get(best).Owner));
            }
        }

        foreach ((EntityId target, byte owner) in _claims)
        {
            Take(world, target, owner);
        }
    }

    private static void Roam(World world, int tick)
    {
        if (world.NatureHomes.Count == 0)
        {
            return;
        }

        WorldSettings w = world.Ruleset.World;
        long guard = (long)w.GuardRadiusTiles * MapState.UnitsPerTile;
        int intervalTicks = Math.Max(1, w.WanderIntervalMs / world.Config.TickLengthMilliseconds);
        ReadOnlySpan<EntityId> ids = world.NatureHomes.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId e = ids[i];
            ref NatureHome home = ref world.NatureHomes.At(i);
            WorldTraits traits = world.TypeOf(e).Traits.World;
            if (traits.Grazes)
            {
                Graze(world, e, ref home, tick);
                continue;
            }

            Position p = world.Positions.Get(e);
            long away = IntMath.DistanceSquared(p.X, p.Y, home.X, home.Y);
            if (traits.Guards && home.Returning == 0 && away > guard * guard)
            {
                // Lured away: give up the fight and walk home.
                GameplayCommands.ClearTasks(world, e);
                Interaction.MoveTo(world, e, home.X, home.Y);
                home.Returning = 1;
                continue;
            }

            bool moving = Interaction.IsMoving(world, e);
            if (home.Returning != 0)
            {
                if (!moving)
                {
                    home.Returning = 0;
                    home.NextMoveTick = tick + intervalTicks;
                }

                continue;
            }

            bool fighting = world.Combats.IndexOf(e) is >= 0 and var c && world.Combats.At(c).Target != 0;
            if (traits.Prowls)
            {
                // NAT-003 (0x4eeed0, Wander 0x4e0a23): at each idle tick of 1.5 s, one time in five (R(0, 100) <= 20), a cell
                // drawn within the radius of where the bear was placed, x then y.
                int every = Math.Max(1, ProwlTickMs / world.Config.TickLengthMilliseconds);
                if (moving || fighting || !world.Motions.Has(e) || (tick + e.Index) % every != 0 || world.Rng.Misc.NextInt(101) > ProwlChance)
                {
                    continue;
                }

                int span = w.WanderRadiusTiles * MapState.UnitsPerTile;
                int px = Math.Clamp(home.X - span + world.Rng.Misc.NextInt((2 * span) + 1), 0, world.Map.WorldWidth - 1);
                int py = Math.Clamp(home.Y - span + world.Rng.Misc.NextInt((2 * span) + 1), 0, world.Map.WorldHeight - 1);
                Interaction.MoveTo(world, e, px, py);
                continue;
            }

            if (!traits.Wanders || moving || fighting || tick < home.NextMoveTick || !world.Motions.Has(e))
            {
                continue;
            }

            // A wander target hashed from the match seed, the entity and the tick (no RNG stream is consumed).
            ulong roll = SplitMix64.Mix(world.Config.MatchSeed ^ ((ulong)e.Value << 20) ^ (ulong)tick);
            int r = (w.WanderRadiusTiles * MapState.UnitsPerTile) + 1;
            int x = Math.Clamp(home.X + (int)(roll % (ulong)(2 * r)) - r, 0, world.Map.WorldWidth - 1);
            int y = Math.Clamp(home.Y + (int)((roll >> 32) % (ulong)(2 * r)) - r, 0, world.Map.WorldHeight - 1);
            Interaction.MoveTo(world, e, x, y);
            home.NextMoveTick = tick + (intervalTicks / 2) + (int)((roll >> 48) % (ulong)intervalTicks);
        }
    }

    // NAT-003: a bear's idle tick and its chance to set off in 100 (0x4eeed0).
    private const int ProwlTickMs = 1500;
    private const int ProwlChance = 20;

    // NAT-007 (ReTzar's reading of the original: Cow Idle 0x42f450, its grazing 0x4ed120 and 0x4ecb13, Wander 0x4e0a23).
    // The cow stands and grazes 30 cycles: each draws R(0, 2), and on 0 a standing cow lowers its head and eats, an
    // eating one raises it. Then, unless it is milked, it walks to a cell drawn within two of its home, and grazes again.
    // A milked cow raises its head and grazes 4 cycles more. Its home is where an order left it; its own walks keep it.
    private static void Graze(World world, EntityId e, ref NatureHome home, int tick)
    {
        if (world.IsHeld(e) || world.Motions.IndexOf(e) is not (>= 0 and var m))
        {
            return;
        }

        if (world.Motions.At(m).State == MotionState.Moving)
        {
            if (home.Pasture != 1)
            {
                home.Pasture = 2; // sent: it grazes where it stops
            }

            return;
        }

        if (Interaction.IsMoving(world, e))
        {
            return; // a step to finish, or stepping aside for a friend
        }

        if (home.Pasture != 0)
        {
            if (home.Pasture == 2)
            {
                Position p = world.Positions.Get(e);
                home.X = p.X;
                home.Y = p.Y;
            }

            home.Pasture = 0;
            home.Cycles = GrazeCycles;
            Show(world, ref home, 0, tick);
            return;
        }

        if (tick < home.NextMoveTick)
        {
            return;
        }

        ulong roll = SplitMix64.Mix(world.Config.MatchSeed ^ ((ulong)e.Value << 20) ^ (uint)tick ^ 0x6C6F_5752UL);
        int step = home.Graze & 7;
        int picture = home.Graze >> 3;
        bool walk = false;
        switch (step)
        {
            case StepDraw:
                walk = true;
                break;
            case 0 when home.Cycles == 0:
                if (picture == 0 && !Milked(world, e))
                {
                    walk = true;
                }
                else
                {
                    Show(world, ref home, 1, tick, step: 3); // raises its head
                }

                break;
            case 0:
                home.Cycles--;
                if (roll % 3 != 0)
                {
                    home.NextMoveTick = tick + Ticks(world, PictureMs[picture]); // the same picture again
                }
                else
                {
                    Show(world, ref home, 1, tick, step: picture == 0 ? 1 : 2); // lowers or raises its head
                }

                break;
            case 1:
                Show(world, ref home, 2, tick); // eats
                break;
            case 2:
                Show(world, ref home, 0, tick); // stands
                break;
            default:
                if (Milked(world, e))
                {
                    home.Cycles = MilkedCycles;
                    Show(world, ref home, 0, tick);
                }
                else
                {
                    walk = true;
                }

                break;
        }

        if (walk)
        {
            Walk(world, e, ref home, tick, roll >> 8);
        }
    }

    // The cell of a cow's walk, within two of its home and on the map; one it cannot step into now is drawn again later.
    private static void Walk(World world, EntityId e, ref NatureHome home, int tick, ulong roll)
    {
        int hx = home.X / MapState.UnitsPerTile;
        int hy = home.Y / MapState.UnitsPerTile;
        int x0 = Math.Max(0, hx - GrazeRadiusTiles);
        int y0 = Math.Max(0, hy - GrazeRadiusTiles);
        int x1 = Math.Min(world.Map.Width - 1, hx + GrazeRadiusTiles);
        int y1 = Math.Min(world.Map.Height - 1, hy + GrazeRadiusTiles);
        int tx = x0 + (int)(roll % (ulong)(x1 - x0 + 1));
        int ty = y0 + (int)((roll >> 16) % (ulong)(y1 - y0 + 1));
        Position p = world.Positions.Get(e);
        bool here = tx == p.X / MapState.UnitsPerTile && ty == p.Y / MapState.UnitsPerTile;
        if (!here && !world.CanStepInto(e, tx, ty))
        {
            home.Graze = StepDraw;
            home.NextMoveTick = tick + Ticks(world, RedrawMs);
            return;
        }

        home.Graze = 0;
        home.Cycles = GrazeCycles;
        home.Pasture = 1;
        home.NextMoveTick = tick;
        Interaction.MoveTo(world, e, (tx * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2), (ty * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2));
    }

    private static void Show(World world, ref NatureHome home, int picture, int tick, int step = 0)
    {
        home.Graze = (byte)(step | (picture << 3));
        home.NextMoveTick = tick + Ticks(world, PictureMs[picture]);
    }

    // A peasant has the cow as its harvest (ReTzar 0x4ecb13, `milked`).
    private static bool Milked(World world, EntityId cow)
    {
        ReadOnlySpan<Worker> workers = world.Workers.Components;
        for (int i = 0; i < workers.Length; i++)
        {
            if (workers[i].Task == WorkerTask.Harvest && workers[i].Target == cow.Value)
            {
                return true;
            }
        }

        return false;
    }

    private static int Ticks(World world, int ms) => Math.Max(1, (ms + world.Config.TickLengthMilliseconds - 1) / world.Config.TickLengthMilliseconds);
}
