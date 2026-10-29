using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Components;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Map;
using Tzar2.Sim.Systems;

namespace Tzar2.Game.Session;

/// <summary>
/// Fills a <see cref="GameFrame"/> from the world right after a tick, on the sim thread (ADR 0070). It only reads the
/// world: the presentation never touches sim state (REQ-ARCH-03). The per-entity fields go beyond the sim's
/// <c>RenderSnapshot</c> (activity, loads, progress) so the client needs nothing else; when the sim's render snapshot
/// grows these fields, this class becomes a copy of it.
/// </summary>
internal static class FrameBuilder
{
    /// <summary>Fills <paramref name="frame"/>.</summary>
    /// <param name="frame">The frame to overwrite.</param>
    /// <param name="world">The world after the tick.</param>
    /// <param name="viewer">
    /// The kingdom whose view the frame shows (GAME-12: the sim's visibility; units only in sight, other kingdoms'
    /// structures and resources as last seen), or null for everything.
    /// </param>
    /// <param name="outcome">The local outcome.</param>
    /// <param name="player">The kingdom whose command card the frame serves (<see cref="GameFrame.Makeable"/>); the viewer when null.</param>
    public static void Fill(GameFrame frame, World world, byte? viewer, MatchOutcome outcome, byte? player = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(world);
        int tickMs = world.Config.TickLengthMilliseconds;
        frame.Tick = world.Tick;
        frame.TickMs = tickMs;
        frame.Outcome = outcome;

        byte? card = player ?? viewer; // whose holders show what is inside them
        frame.ClearRefunds();
        frame.ResourceCount = world.Ruleset.ResourceTypes.Count;
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        // Room for the remembered structures too (at most one per entity slot), so the buffer is not reallocated below.
        FrameEntity[] entities = frame.EntityBuffer(ids.Length + (viewer is { } room ? world.Visibility.Memory(room).Length : 0));
        int n = 0;
        int queued = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId id = ids[i];
            int p = world.Positions.IndexOf(id);
            if (p < 0 || world.IsHeld(id))
            {
                continue; // inside a holder or an inventory: not on the map (GAME-09/10)
            }

            Identity identity = identities[i];
            EntityTypeDefinition type = world.Ruleset.EntityTypes[identity.TypeId];
            Position position = world.Positions.At(p);
            if (viewer is { } v && !world.Visibility.CanSee(world, v, id))
            {
                // Out of sight: units are not shown; structures as the viewer last saw them (below).
                continue;
            }

            ref FrameEntity e = ref entities[n++];
            e = default;
            e.Id = id;
            e.X = position.X;
            e.Y = position.Y;
            e.TypeId = identity.TypeId;
            e.Owner = identity.Owner;
            e.Facing = identity.Facing;
            e.SwingMs = -1;
            e.ItemResource = -1;
            e.CarryResource = Worker.NoResource;
            e.ConstructionPermille = -1;
            e.DismantlePermille = -1;
            e.ResourceLeft = -1;
            e.TileX = -1;
            e.TileY = -1;
            int h = world.Healths.IndexOf(id);
            if (h >= 0)
            {
                Health health = world.Healths.At(h);
                e.Health = health.Current;
                e.MaxHealth = health.Max;
            }

            e.Burning = world.Burnings.Has(id);
            e.Cloaked = world.IsCloaked(id);
            e.SpyType = -1;
            e.SeemsOwner = -1;
            e.DisguiseMastery = -1;
            if (viewer is not { } watcher || watcher == identity.Owner)
            {
                // A spy in its own look shows a full meter (batch J: "Spy (100)").
                e.DisguiseMastery = (world.Ruleset.EntityTypes[identity.TypeId].Traits.Abilities & AbilityFlags.Impersonate) != 0 ? 100 : -1;
            }

            if (world.HypnotizedFrom(id) is >= 0 and var from)
            {
                e.SeemsOwner = from; // a hypnotized unit keeps its kingdom's colours (MAG-008)
            }

            if (world.Disguises.Count != 0 && world.Disguises.IndexOf(id) is >= 0 and var dg)
            {
                Disguise look = world.Disguises.At(dg);
                if (viewer is { } onlooker && onlooker != identity.Owner)
                {
                    e.Owner = look.Owner; // other kingdoms see one of its old kingdom's units (ADR 0184)
                }
                else
                {
                    e.SpyType = look.TypeId;
                    e.SeemsOwner = look.Owner;
                    e.DisguiseMastery = look.Mastery;
                }
            }
            if (world.Ruleset.EntityTypes[identity.TypeId].Traits.ManaPool is { } pool)
            {
                e.MaxMana = world.Manas.Has(id) ? world.MaxMana(id) : world.Stats.Apply(identity.Owner, ModifierStat.ManaMax, identity.TypeId, pool.Max);
                e.Mana = world.Manas.IndexOf(id) is >= 0 and var mana ? Math.Min(world.Manas.At(mana).MilliMana / 1000, e.MaxMana) : 0;
            }

            int m = world.Motions.IndexOf(id);
            bool moving = m >= 0 && (world.Motions.At(m).VelocityX != 0 || world.Motions.At(m).VelocityY != 0);
            e.Activity = moving ? UnitActivity.Walk : UnitActivity.Idle;

            e.CraftPermille = -1;
            if (world.Experiences.IndexOf(id) is >= 0 and var xp)
            {
                e.Level = world.Experiences.At(xp).Level;
                e.Hero = (world.Experiences.At(xp).Flags & ExperienceFlags.Hero) != 0;
            }

            e.Named = world.Names.Count > 0 && world.Names.Has(id);
            e.Name = e.Named ? world.Names.Get(id).Value : null;
            (e.Attack, e.Defence) = GameplayQueries.Combat(world, id);

            if (world.Holders.IndexOf(id) is >= 0 and var ho && (card is null || identity.Owner == card))
            {
                e.Inside = world.Holders.At(ho).Count;
                e.InsideMax = world.HolderCapacity(id);
            }

            if (world.Items.Count != 0 && world.Items.IndexOf(id) is >= 0 and var power)
            {
                e.ItemPower = world.Items.At(power).Power;
                if (world.Ruleset.EntityTypes[identity.TypeId].Traits.Item is { Kind: ItemKind.Resource } held)
                {
                    e.ItemResource = world.Items.At(power).Resource > 0 ? world.Items.At(power).Resource - 1 : held.Resource; // HUD-037
                }
            }

            if (world.Items.Count != 0 && world.Items.IndexOf(id) is >= 0 and var it && world.Items.At(it).Look is > 0 and var itemLook
                && world.Ruleset.EntityTypes[identity.TypeId].Traits.Item is { } itemType && itemLook <= itemType.Looks.Length)
            {
                e.ItemLook = itemType.Looks[itemLook - 1];
            }

            if (world.CraftJobs.IndexOf(id) is >= 0 and var cj)
            {
                CraftJob job = world.CraftJobs.At(cj);
                bool book = world.Ruleset.EntityTypes[job.ItemType].Traits.Item is { Kind: ItemKind.SpellBook };
                int total = Math.Max(1, world.Ruleset.Combat.CraftMs(book, job.Power));
                e.CraftPermille = (int)Math.Clamp((long)(total - job.LeftMs) * 1000 / total, 0, 1000);
            }

            int w = world.Workers.IndexOf(id);
            if (w >= 0)
            {
                Worker worker = world.Workers.At(w);
                e.CarryResource = worker.CarryResource;
                e.CarryAmount = worker.CarryAmount;
                EntityTypeDefinition? node = worker.Target != 0 && world.Identities.IndexOf(EntityId.FromValue(worker.Target)) is >= 0 and var ni
                    ? world.Ruleset.EntityTypes[world.Identities.At(ni).TypeId]
                    : null;
                if (worker.CarryAmount > 0)
                {
                    e.Carry = CarryOf(world.Ruleset, worker.CarryResource, node);
                }

                e.Job = JobOf(world.Ruleset, worker, node);
                if (!moving && worker.Phase == WorkerPhase.Gathering)
                {
                    e.Activity = node is { Traits.Category: EntityCategory.Unit } ? UnitActivity.Milk
                        : node is { Traits.LegacyClass: "meat" } ? UnitActivity.Meat // PRES-005
                        : GatherActivity(world.Ruleset, worker.NodeResource);
                }
                else if (!moving && worker.Phase is WorkerPhase.Building or WorkerPhase.Repairing)
                {
                    e.Activity = UnitActivity.Build; // repairing looks and sounds like building (the fourth play-test)
                }
                else if (!moving && worker.Phase == WorkerPhase.Capturing)
                {
                    e.Activity = UnitActivity.Capture; // PRES-005
                }
            }

            if (world.Explorers.Count > 0 && world.Explorers.Has(id))
            {
                e.Job = FrameJob.Explore;
            }
            else if (world.Errands.Count > 0 && world.Errands.IndexOf(id) is >= 0 and var errand)
            {
                e.Job = world.Errands.At(errand).Kind switch
                {
                    ErrandKind.Pickup => FrameJob.Collect,
                    ErrandKind.Board => FrameJob.Transport,
                    _ => e.Job,
                };
            }

            if (world.Inventories.Count > 0 && world.Inventories.IndexOf(id) is >= 0 and var inventory)
            {
                Inventory carried = world.Inventories.At(inventory);
                for (int slot = 0; slot < carried.Count && !e.CarriesBook; slot++)
                {
                    EntityId item = EntityId.FromValue(carried.Items[slot]);
                    e.CarriesBook = world.Entities.IsAlive(item) && world.TypeOf(item).Traits.Item is { Kind: ItemKind.SpellBook };
                }
            }

            if (world.Workers.IndexOf(id) is >= 0 and var wk)
            {
                e.WorkTarget = EntityId.FromValue(world.Workers.At(wk).Target);
            }

            if (world.Learned.Count > 0 && world.Learned.IndexOf(id) is >= 0 and var learned)
            {
                e.LearnedSpells = world.Learned.At(learned).Mask;
            }

            int c = world.Combats.IndexOf(id);
            if (c >= 0)
            {
                Combat combat = world.Combats.At(c);
                e.Target = EntityId.FromValue(combat.Target);
                if (combat.Target != 0 && world.Workers.Has(id) && e.Job == FrameJob.None)
                {
                    e.Job = FrameJob.Attack; // a peasant in a fight
                }
                ArmamentDefinition? arm = type.Traits.Armament;
                if (!moving && combat.Target != 0 && arm is not null && combat.CooldownMs > 0)
                {
                    // A swing started when the cooldown was re-armed: elapsed = cycle − remaining cooldown.
                    e.Activity = UnitActivity.Attack;
                    e.SwingMs = Math.Clamp(arm.CooldownMs - combat.CooldownMs, 0, arm.CooldownMs);
                }
            }

            if (!moving && world.Casters.Count > 0 && world.Casters.IndexOf(id) is >= 0 and var caster && world.Casters.At(caster).CastLeftMs > 0)
            {
                ushort casting = world.Casters.At(caster).Spell;
                e.Activity = casting < world.Ruleset.Spells.Count && world.Ruleset.Spells[casting].Effect == SpellEffect.Murder ? UnitActivity.Murder : UnitActivity.Cast; // PRES-005
            }

            int f = world.Footprints.IndexOf(id);
            if (f >= 0)
            {
                Footprint footprint = world.Footprints.At(f);
                e.TileX = footprint.TileX;
                e.TileY = footprint.TileY;
                e.Rotated = footprint.Rotated != 0;
                e.Mirrored = footprint.Mirrored != 0;
            }

            int k = world.Constructions.IndexOf(id);
            if (k >= 0)
            {
                Construction site = world.Constructions.At(k);
                e.ConstructionPermille = site.TotalMs > 0 ? (int)Math.Clamp(site.ProgressMs * 1000L / site.TotalMs, 0, 1000) : 0;
            }
            else if (world.Dismantlings.Count != 0 && world.Dismantlings.IndexOf(id) is >= 0 and var down)
            {
                Dismantling d = world.Dismantlings.At(down);
                e.DismantlePermille = (int)Math.Clamp(d.LeftMs * 1000L / Math.Max(1, d.TotalMs), 0, 1000);
            }
            else if (card is { } kingdom && identity.Owner == kingdom && world.TypeOf(id).Traits.Category == EntityCategory.Building)
            {
                GameplayQueries.DismantleRefund(world, id, frame.AddRefund(id.Value)); // BLD-027: the card's tooltip
            }

            int q = world.Productions.IndexOf(id);
            if (q >= 0)
            {
                Production production = world.Productions.At(q);
                e.QueueCount = QueueRuns.Items(production);
                e.HasRally = production.HasRally != 0;
                e.RallyX = production.RallyX;
                e.RallyY = production.RallyY;
                if (production.Count > 0)
                {
                    ushort head = production.Queue[0].Item;
                    int item = QueueItems.Id(head);
                    int total;
                    if (QueueItems.IsResearch(head))
                    {
                        e.QueueKind = FrameQueueKind.Research;
                        total = item < world.Ruleset.Technologies.Count ? world.Ruleset.Technologies[item].TimeMs : 0;
                    }
                    else if (QueueItems.IsUpgrade(head))
                    {
                        e.QueueKind = FrameQueueKind.Upgrade;
                        total = item < world.Ruleset.Upgrades.Count ? world.Ruleset.Upgrades[item].TimeMs : 0;
                    }
                    else
                    {
                        e.QueueKind = FrameQueueKind.Unit;
                        total = item < world.Ruleset.EntityTypes.Count ? world.Ruleset.EntityTypes[item].Traits.BuildTimeMs : 0;
                    }

                    e.QueueHead = (ushort)item;
                    e.QueuePermille = total > 0 ? (int)Math.Clamp(production.ProgressMs * 1000L / total, 0, 1000) : 0;
                }

                for (int item = 0; item < production.Count; item++)
                {
                    QueueRun run = production.Queue[item];
                    if (QueueItems.IsUpgrade(run.Item))
                    {
                        e.Upgrades |= 1u << QueueItems.Id(run.Item);
                    }

                    FrameQueueKind kind = QueueItems.IsResearch(run.Item) ? FrameQueueKind.Research : QueueItems.IsUpgrade(run.Item) ? FrameQueueKind.Upgrade : FrameQueueKind.Unit;
                    frame.SetQueueEntry(queued++, new FrameQueueEntry(id.Value, kind, (ushort)QueueItems.Id(run.Item), run.Count));
                }
            }

            if (world.UpgradeLevels.IndexOf(id) is >= 0 and var ul)
            {
                e.Upgrades |= world.UpgradeLevels.At(ul).Mask;
            }

            if (world.Stances.IndexOf(id) is >= 0 and var st)
            {
                e.Stance = (byte)world.Stances.At(st).Value;
            }

            if (world.Gates.IndexOf(id) is >= 0 and var g)
            {
                GateState gate = world.Gates.At(g);
                e.Gate = FrameGate.Present | (gate.Open != 0 ? FrameGate.Open : FrameGate.None) | (gate.Mode == GateMode.Automatic ? FrameGate.Automatic : FrameGate.None);
            }

            int s = world.ResourceStocks.IndexOf(id);
            if (s >= 0)
            {
                e.ResourceLeft = world.ResourceStocks.At(s).Amount;
            }
        }

        if (viewer is { } who)
        {
            n = AddRemembered(frame, world, who, n);
        }

        frame.EntityCount = n;
        frame.QueueEntryCount = queued;
        FillHelds(frame, world, card);
        FillWaypoints(frame, world, card);
        frame.DayMinute = world.DayMinute;
        frame.IsNight = world.IsNight;
        frame.Weather = world.Weather;

        // Corpses: the decal ring holds expiry ticks; age = TTL − remaining.
        DecalIndex decals = world.Decals;
        FrameCorpse[] corpses = frame.CorpseBuffer(decals.Count);
        int ttl = world.CorpseTtlTicks;
        int nc = 0;
        for (int i = 0; i < decals.Count; i++)
        {
            Decal d = decals[i];
            if (d.Kind != DecalKind.Corpse)
            {
                continue;
            }

            int age = Math.Max(0, ttl - (d.ExpiresTick - world.Tick.Value));
            corpses[nc++] = new FrameCorpse(d.X, d.Y, d.TypeId, d.Owner, age * tickMs);
        }

        frame.CorpseCount = nc;

        // Projectiles where the viewer sees them.
        ReadOnlySpan<EntityId> shots = world.Projectiles.Entities;
        Span<Projectile> flights = world.Projectiles.Components;
        FrameProjectile[] projectiles = frame.ProjectileBuffer(shots.Length);
        int np = 0;
        for (int i = 0; i < shots.Length; i++)
        {
            Projectile s = flights[i];
            if (viewer is { } sees && !world.Visibility.IsVisibleAt(sees, s.X, s.Y))
            {
                continue;
            }

            projectiles[np++] = new FrameProjectile(shots[i].Value, s.X, s.Y, s.TargetX, s.TargetY, s.Kind, s.Spell, s.AttackerType);
        }

        frame.ProjectileCount = np;

        int players = world.Players.Length;
        if (frame.Players.Length != players)
        {
            frame.Players = new FramePlayer[players];
        }

        int resources = Math.Min(world.Ruleset.ResourceTypes.Count, 8);
        for (int p = 0; p < players; p++)
        {
            ref PlayerState state = ref world.Players[p];
            ref FramePlayer fp = ref frame.Players[p];
            for (int r = 0; r < resources; r++)
            {
                fp.Resources[r] = state.Resources[r];
            }

            fp.Population = state.Population;
            fp.PopulationCap = state.PopulationCap;
            fp.Defeated = (state.Flags & PlayerFlags.Defeated) != 0;
            fp.Victorious = (state.Flags & PlayerFlags.Victorious) != 0;
            fp.AiAssist = (byte)(((state.Flags & PlayerFlags.AssistEconomy) != 0 ? 1 : 0) | ((state.Flags & PlayerFlags.AssistWar) != 0 ? 2 : 0));
            fp.Civilization = world.PlayerCivilization(p);
            fp.Statistics = KingdomStatistics.From(world.Kingdoms[p]);
            fp.Score = VictorySystem.Score(world.Ruleset.World, world.Kingdoms[p]);
            Sim.Kingdoms.KingdomState k = world.Kingdoms[p];
            fp.LoanLimit = k.LoanRaiseTick >= 0 && world.Tick.Value >= k.LoanRaiseTick ? (int)Math.Min((long)k.LoanLimit + world.Ruleset.World.LoanStepAmount, 100_000_000) : k.LoanLimit;
            fp.LoanDue = k.LoanDue;
            fp.LoanResource = k.LoanResource;
            fp.ExchangeLeft = k.ExchangeLeft;
            fp.ExchangeTotal = k.ExchangeTotal;
            fp.ExchangeReceive = k.ExchangeReceive;
            for (int w = 0; w < TechnologyRow.Capacity / 64; w++)
            {
                fp.Technologies[w] = state.Technologies[w];
                fp.Researching[w] = 0;
            }
        }

        // Diplomacy between the kingdoms (the diplomacy screen, HUD-012).
        if (frame.Stances.Length != players * players)
        {
            frame.Stances = new byte[players * players];
            frame.Offers = new byte[players * players];
        }

        for (int a = 0; a < players; a++)
        {
            for (int b = 0; b < players; b++)
            {
                frame.Stances[(a * players) + b] = (byte)world.Diplomacy.StanceOf(a, b);
                frame.Offers[(a * players) + b] = (byte)world.Diplomacy.OffersOf(a, b);
            }
        }

        // Research queued anywhere (the card does not offer it twice).
        ReadOnlySpan<EntityId> producers = world.Productions.Entities;
        for (int i = 0; i < producers.Length; i++)
        {
            ref Production production = ref world.Productions.At(i);
            byte owner = world.Identities.IndexOf(producers[i]) is >= 0 and var o ? world.Identities.At(o).Owner : (byte)0xFF;
            if (owner >= players)
            {
                continue;
            }

            for (int k = 0; k < production.Count; k++)
            {
                if (QueueItems.IsResearch(production.Queue[k].Item) && QueueItems.Id(production.Queue[k].Item) is var t && t < TechnologyRow.Capacity)
                {
                    frame.Players[owner].Researching[t >> 6] |= 1UL << (t & 63);
                }
            }
        }

        FillMakeable(frame, world, player ?? viewer);
        FillTavern(frame, world, player ?? viewer);

        int tiles = world.Map.Width * world.Map.Height;
        if (viewer is { } fogOf)
        {
            if (frame.Visibility.Length != tiles)
            {
                frame.Visibility = new byte[tiles];
            }

            world.Visibility.WriteStates(fogOf, frame.Visibility);
        }
        else if (frame.Visibility.Length != 0)
        {
            frame.Visibility = [];
        }

        if (frame.Buildable.Length != tiles)
        {
            frame.Buildable = new byte[tiles];
            frame.Terrain = new byte[tiles];
            frame.BuildableVersion = -1;
        }

        if (frame.BuildableVersion == world.Map.PassabilityVersion && frame.BuildableFieldsVersion == world.WalkableFootprintsVersion)
        {
            return;
        }

        frame.BuildableVersion = world.Map.PassabilityVersion;
        frame.BuildableFieldsVersion = world.WalkableFootprintsVersion;

        byte[] buildable = frame.Buildable;
        byte[] terrain = frame.Terrain;
        MapState map = world.Map;
        int width = map.Width;
        for (int ty = 0; ty < map.Height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int i = (ty * width) + tx;
                buildable[i] = PlacementQueries.IsBuildableTile(world, tx, ty) ? (byte)1 : (byte)0;
                terrain[i] = (byte)((map.IsPassable(Locomotor.Ship, tx, ty) ? Input.Placement.Water : 0)
                    | (map.IsPassable(Locomotor.Foot, tx, ty) ? Input.Placement.Land : 0)
                    | (map.Data.CliffTiles[i] == 0 && map.Blockers[i] == 0 && map.Bridges[i] == 0 ? Input.Placement.Free : 0));
            }
        }

        // The card kingdom's finished walls, which its gates may replace (BLD-018).
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int s = 0; s < structures.Length && card is { } kingdom; s++)
        {
            EntityId e = structures[s];
            Footprint piece = world.Footprints.Components[s];
            if (world.TypeOf(e).Traits.IsWall && world.Identities.Get(e).Owner == kingdom && !world.Constructions.Has(e) && map.ContainsTile(piece.TileX, piece.TileY))
            {
                terrain[(piece.TileY * width) + piece.TileX] |= Input.Placement.OwnWall;
            }
        }
    }

    /// <summary>A worker's job by its task and the node it works (STRINGS.INI [peons]; HUD-032).</summary>
    public static FrameJob JobOf(Ruleset rules, in Worker worker, EntityTypeDefinition? node)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return worker.Task switch
        {
            WorkerTask.Build => FrameJob.Build,
            WorkerTask.Repair => FrameJob.Repair,
            WorkerTask.Capture => FrameJob.Capture,
            WorkerTask.Harvest when node is { Traits.Category: EntityCategory.Unit } => FrameJob.TendCows,
            WorkerTask.Harvest when node is { Traits.WalkableFootprint: true } => FrameJob.Dig,
            WorkerTask.Harvest when node is { HasFootprint: false } => FrameJob.Collect, // meat on the ground
            WorkerTask.Harvest when worker.NodeResource < rules.ResourceTypes.Count && rules.ResourceTypes[worker.NodeResource] == "wood" => FrameJob.Chop,
            WorkerTask.Harvest when worker.NodeResource < rules.ResourceTypes.Count && rules.ResourceTypes[worker.NodeResource] == "food" => FrameJob.Dig,
            WorkerTask.Harvest => FrameJob.Mine,
            _ => FrameJob.None,
        };
    }

    // The orders of the card's kingdom's units: what each does now, then what it has queued (CTRL-025).
    private static void FillWaypoints(GameFrame frame, World world, byte? card)
    {
        int n = 0;
        if (card is { } kingdom)
        {
            ReadOnlySpan<EntityId> movers = world.Motions.Entities;
            for (int i = 0; i < movers.Length; i++)
            {
                EntityId unit = movers[i];
                if (world.IsHeld(unit) || world.Identities.Get(unit).Owner != kingdom)
                {
                    continue;
                }

                Motion m = world.Motions.Components[i];
                bool queued = world.WaypointQueues.Count > 0 && world.WaypointQueues.Has(unit);
                if (queued && world.WaypointQueues.Get(unit).Points[0].Kind == WaypointKind.Patrol)
                {
                    WaypointQueue path = world.WaypointQueues.Get(unit);
                    for (int k = 0; k < path.Count; k++)
                    {
                        Waypoint p = path.Points[(path.Cursor + k) % path.Count];
                        frame.SetWaypoint(n++, new FrameWaypoint(unit.Value, FrameOrderKind.Patrol, p.X, p.Y, 0));
                    }

                    continue;
                }

                if (world.Combats.IndexOf(unit) is >= 0 and var c && world.Combats.At(c).Mode == CombatMode.Explicit && world.Combats.At(c).Target != 0
                    && world.Positions.IndexOf(EntityId.FromValue(world.Combats.At(c).Target)) is >= 0 and var t)
                {
                    Position at = world.Positions.At(t);
                    frame.SetWaypoint(n++, new FrameWaypoint(unit.Value, FrameOrderKind.Attack, at.X, at.Y, world.Combats.At(c).Target));
                }
                else if (m.Order == MotionOrder.Follow && m.FollowTarget != 0)
                {
                    frame.SetWaypoint(n++, new FrameWaypoint(unit.Value, FrameOrderKind.Guard, m.OrderX, m.OrderY, m.FollowTarget));
                }
                else if (m.Order == MotionOrder.Patrol)
                {
                    frame.SetWaypoint(n++, new FrameWaypoint(unit.Value, FrameOrderKind.Patrol, m.OrderX, m.OrderY, 0));
                    frame.SetWaypoint(n++, new FrameWaypoint(unit.Value, FrameOrderKind.Patrol, m.PatrolX, m.PatrolY, 0));
                }
                else if (m.Order == MotionOrder.Move)
                {
                    frame.SetWaypoint(n++, new FrameWaypoint(unit.Value, FrameOrderKind.Move, m.OrderX, m.OrderY, 0));
                }

                if (queued)
                {
                    WaypointQueue q = world.WaypointQueues.Get(unit);
                    for (int k = 0; k < q.Count; k++)
                    {
                        Waypoint p = q.Points[k];
                        frame.SetWaypoint(n++, new FrameWaypoint(unit.Value, (FrameOrderKind)p.Kind, p.X, p.Y, p.Target));
                    }
                }
            }
        }

        frame.WaypointCount = n;
    }

    // What the player may build or train now, by the sim's own requirement check (ADR 0040).
    // The units inside the holders of the card's kingdom and the items its units carry (of everyone without one),
    // grouped by holder.
    private static void FillHelds(GameFrame frame, World world, byte? card)
    {
        int n = 0;
        for (int i = 0; i < world.Helds.Count; i++)
        {
            EntityId unit = world.Helds.Entities[i];
            EntityId holder = EntityId.FromValue(world.Helds.Components[i].Holder);
            bool item = world.Items.Has(unit);
            if ((!item && !world.Holders.Has(holder)) || (card is { } k && world.Identities.Get(holder).Owner != k))
            {
                continue;
            }

            Health health = world.Healths.IndexOf(unit) is >= 0 and var h ? world.Healths.At(h) : default;
            int slot = -1;
            bool isDefault = false;
            int spell = -1;
            string? look = null;
            int value = 0;
            if (item && world.Inventories.IndexOf(holder) is >= 0 and var inv)
            {
                Inventory inventory = world.Inventories.At(inv);
                for (int s2 = 0; s2 < inventory.Count; s2++)
                {
                    if (inventory.Items[s2] == unit.Value)
                    {
                        slot = s2;
                    }
                }

                isDefault = slot >= 0 && inventory.Default == slot;
            }
            else if (item)
            {
                slot = MarketItems.IndexInStock(world, holder, unit); // a market's offer (TECH-030)
            }

            if (item)
            {
                spell = world.ItemSpell(unit);
                ItemDefinition def = world.TypeOf(unit).Traits.Item!;
                look = world.Items.Get(unit).Look is > 0 and var l && l <= def.Looks.Length ? def.Looks[l - 1] : null;
                value = MarketItems.Value(world, unit);
            }

            bool hero = false;
            int level = 0;
            if (!item && world.Experiences.IndexOf(unit) is >= 0 and var xp)
            {
                hero = (world.Experiences.At(xp).Flags & ExperienceFlags.Hero) != 0; // the kingdom's list of heroes names it (HUD-033)
                level = world.Experiences.At(xp).Level;
            }

            hero |= !item && world.Names.Count > 0 && world.Names.Has(unit);

            string? name = !item && world.Names.Count > 0 && world.Names.Has(unit) ? world.Names.Get(unit).Value : null;
            frame.SetHeld(n++, new FrameHeld(holder.Value, unit, world.Identities.Get(unit).TypeId, health.Current, health.Max, item, slot, isDefault, spell, look, value, hero, level, name));
        }

        frame.HeldCount = n;
        frame.GroupHelds();
    }

    private static void FillMakeable(GameFrame frame, World world, byte? player)
    {
        if (player is not { } who || !world.HasPlayer(who))
        {
            if (frame.Makeable.Length != 0)
            {
                frame.Makeable = [];
                frame.Researchable = [];
            }

            return;
        }

        int types = world.Ruleset.EntityTypes.Count;
        if (frame.Makeable.Length != types)
        {
            frame.Makeable = new byte[types];
        }

        byte[] makeable = frame.Makeable;
        for (int t = 0; t < types; t++)
        {
            // Every type that is made: units, buildings and resources with a footprint (a field is a building and a food
            // source; the fifth play-test found its button greyed out as "Requires: requirements").
            EntityTypeDefinition type = world.Ruleset.EntityTypes[t];
            bool made = type.Traits.Category is EntityCategory.Unit or EntityCategory.Building
                || (type.Traits.Category == EntityCategory.Resource && type.HasFootprint);
            makeable[t] = made && PlacementQueries.RequirementsMet(world, who, (ushort)t) ? (byte)1 : (byte)0;
        }

        int techs = world.Ruleset.Technologies.Count;
        if (frame.Researchable.Length != techs)
        {
            frame.Researchable = new byte[techs];
        }

        for (int t = 0; t < techs; t++)
        {
            frame.Researchable[t] = PlacementQueries.ResearchRequirementsMet(world, who, t) ? (byte)1 : (byte)0;
        }
    }

    // The tavern's offers of the card's kingdom (GAME-14): what the hire buttons show.
    private static void FillTavern(GameFrame frame, World world, byte? player)
    {
        frame.TavernOfferCount = 0;
        if (player is not { } who || !world.HasPlayer(who))
        {
            return;
        }

        int slots = TavernOffers.SlotCount(world);
        int resources = world.Ruleset.ResourceTypes.Count;
        FrameTavernOffer[] offers = frame.OfferBuffer(slots);
        if (frame.TavernCosts.Length < slots * resources)
        {
            frame.TavernCosts = new int[slots * resources];
        }

        frame.TavernCostStride = resources;
        KingdomState k = world.Kingdoms[who];
        int period = TavernOffers.PeriodAt(world, world.Tick.Value);
        int taken = k.TavernPeriod == period ? k.TavernTaken : 0;
        Span<int> cost = stackalloc int[Ruleset.MaxResourceTypes];
        int n = 0;
        for (int slot = 0; slot < slots; slot++)
        {
            if (TavernOffers.Offer(world, who, slot) is not { } offer)
            {
                continue;
            }

            TavernOffers.CostOf(world, who, offer, cost);
            cost[..resources].CopyTo(frame.TavernCosts.AsSpan(n * resources, resources));
            offers[n++] = new FrameTavernOffer(slot, offer.TypeId, offer.Count, offer.Level, offer.IsHero, (taken & (1 << slot)) != 0);
        }

        frame.TavernOfferCount = n;
    }

    /// <summary>The look of a load of <paramref name="resource"/> taken from a node of type <paramref name="node"/> (null when gone).</summary>
    public static FrameCarry CarryOf(Ruleset ruleset, int resource, EntityTypeDefinition? node)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        string key = resource < ruleset.ResourceTypes.Count ? ruleset.ResourceTypes[resource] : string.Empty;
        return key switch
        {
            "wood" => FrameCarry.Wood,
            "gold" => FrameCarry.Gold,
            "stone" or "iron" => FrameCarry.Stone,
            "food" when node is { Traits.Category: EntityCategory.Unit } => FrameCarry.Milk,
            "food" when node is { Traits.LegacyClass: "meat" } => FrameCarry.Meat,
            "food" => FrameCarry.Food,
            _ => FrameCarry.None,
        };
    }

    /// <summary>The gathering animation for a resource type id: wood → chop, food → farm, others → mine.</summary>
    // Structures the viewer remembers but does not see now: the real entity's last-seen state, or a ghost of one that is
    // gone (destroyed or recycled) without the viewer having seen it go.
    private static int AddRemembered(GameFrame frame, World world, byte viewer, int n)
    {
        ReadOnlySpan<Ghost> memory = world.Visibility.Memory(viewer);
        int high = Math.Min(memory.Length, world.Entities.HighWater);
        for (int index = 0; index < high; index++)
        {
            Ghost g = memory[index];
            if (g.Entity == 0)
            {
                continue;
            }

            EntityId id = EntityId.FromValue(g.Entity);
            if (world.Entities.IsAlive(id) && world.Visibility.CanSee(world, viewer, id))
            {
                continue; // shown as it is
            }

            EntityTypeDefinition type = world.Ruleset.EntityTypes[g.TypeId];
            FrameEntity[] entities = frame.EntityBuffer(n + 1);
            ref FrameEntity e = ref entities[n++];
            e = default;
            e.Id = id;
            e.TypeId = g.TypeId;
            e.Owner = g.Owner;
            e.TileX = g.TileX;
            e.TileY = g.TileY;
            e.X = (g.TileX * MapState.UnitsPerTile) + (type.FootprintWidth * MapState.UnitsPerTile / 2);
            e.Y = (g.TileY * MapState.UnitsPerTile) + (type.FootprintHeight * MapState.UnitsPerTile / 2);
            e.MaxHealth = type.MaxHealth;
            e.Health = (int)((long)type.MaxHealth * g.HealthPermille / 1000);
            e.SwingMs = -1;
            e.ItemResource = -1;
            e.CarryResource = Worker.NoResource;
            e.ConstructionPermille = g.Site != 0 ? g.BuildPermille : -1;
            e.DismantlePermille = -1;
            e.ResourceLeft = -1;
            e.SeemsOwner = -1; // in its kingdom's colours, as last seen (the eighth play-test: 0 drew the player's flags)
            e.Remembered = true;
        }

        return n;
    }

    public static UnitActivity GatherActivity(Ruleset ruleset, int resource)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        string key = (uint)resource < (uint)ruleset.ResourceTypes.Count ? ruleset.ResourceTypes[resource] : string.Empty;
        return key switch
        {
            "wood" => UnitActivity.Chop,
            "food" => UnitActivity.Farm,
            _ => UnitActivity.Mine,
        };
    }
}
