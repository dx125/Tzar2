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
public static class FrameBuilder
{
    /// <summary>Fills <paramref name="frame"/>.</summary>
    /// <param name="frame">The frame to overwrite.</param>
    /// <param name="world">The world after the tick.</param>
    /// <param name="viewer">
    /// The kingdom whose view the frame shows (GAME-12: the sim's visibility; units only in sight, other kingdoms'
    /// structures and resources as last seen), or null for everything.
    /// </param>
    /// <param name="outcome">The local outcome.</param>
    public static void Fill(GameFrame frame, World world, byte? viewer, MatchOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(world);
        int tickMs = world.Config.TickLengthMilliseconds;
        frame.Tick = world.Tick;
        frame.TickMs = tickMs;
        frame.Outcome = outcome;

        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        // Room for the remembered structures too (at most one per entity slot), so the buffer is not reallocated below.
        FrameEntity[] entities = frame.EntityBuffer(ids.Length + (viewer is { } room ? world.Visibility.Memory(room).Length : 0));
        int n = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId id = ids[i];
            int p = world.Positions.IndexOf(id);
            if (p < 0)
            {
                continue;
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
            e.CarryResource = Worker.NoResource;
            e.ConstructionPermille = -1;
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

            int m = world.Motions.IndexOf(id);
            bool moving = m >= 0 && (world.Motions.At(m).VelocityX != 0 || world.Motions.At(m).VelocityY != 0);
            e.Activity = moving ? UnitActivity.Walk : UnitActivity.Idle;

            int w = world.Workers.IndexOf(id);
            if (w >= 0)
            {
                Worker worker = world.Workers.At(w);
                e.CarryResource = worker.CarryResource;
                e.CarryAmount = worker.CarryAmount;
                if (!moving && worker.Phase == WorkerPhase.Gathering)
                {
                    e.Activity = GatherActivity(world.Ruleset, worker.NodeResource);
                }
                else if (!moving && worker.Phase == WorkerPhase.Building)
                {
                    e.Activity = UnitActivity.Build;
                }
            }

            int c = world.Combats.IndexOf(id);
            if (c >= 0)
            {
                Combat combat = world.Combats.At(c);
                e.Target = EntityId.FromValue(combat.Target);
                ArmamentDefinition? arm = type.Traits.Armament;
                if (!moving && combat.Target != 0 && arm is not null && combat.CooldownMs > 0)
                {
                    // A swing started when the cooldown was re-armed: elapsed = cycle − remaining cooldown.
                    e.Activity = UnitActivity.Attack;
                    e.SwingMs = Math.Clamp(arm.CooldownMs - combat.CooldownMs, 0, arm.CooldownMs);
                }
            }

            int f = world.Footprints.IndexOf(id);
            if (f >= 0)
            {
                Footprint footprint = world.Footprints.At(f);
                e.TileX = footprint.TileX;
                e.TileY = footprint.TileY;
            }

            int k = world.Constructions.IndexOf(id);
            if (k >= 0)
            {
                Construction site = world.Constructions.At(k);
                e.ConstructionPermille = site.TotalMs > 0 ? (int)Math.Clamp(site.ProgressMs * 1000L / site.TotalMs, 0, 1000) : 0;
            }

            int q = world.Productions.IndexOf(id);
            if (q >= 0)
            {
                Production production = world.Productions.At(q);
                e.QueueCount = production.Count;
                e.HasRally = production.HasRally != 0;
                e.RallyX = production.RallyX;
                e.RallyY = production.RallyY;
                if (production.Count > 0)
                {
                    ushort head = production.Queue[0];
                    e.QueueHead = head;
                    int total = head < world.Ruleset.EntityTypes.Count ? world.Ruleset.EntityTypes[head].Traits.BuildTimeMs : 0;
                    e.QueuePermille = total > 0 ? (int)Math.Clamp(production.ProgressMs * 1000L / total, 0, 1000) : 0;
                }
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
            fp.Statistics = world.Kingdoms[p];
            fp.Score = VictorySystem.Score(world.Ruleset.World, world.Kingdoms[p]);
        }

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
            frame.BuildableVersion = -1;
        }

        if (frame.BuildableVersion == world.Map.PassabilityVersion)
        {
            return;
        }

        frame.BuildableVersion = world.Map.PassabilityVersion;

        byte[] buildable = frame.Buildable;
        int width = world.Map.Width;
        for (int ty = 0; ty < world.Map.Height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                buildable[(ty * width) + tx] = PlacementQueries.IsBuildableTile(world, tx, ty) ? (byte)1 : (byte)0;
            }
        }
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
            e.CarryResource = Worker.NoResource;
            e.ConstructionPermille = g.Site != 0 ? g.BuildPermille : -1;
            e.ResourceLeft = -1;
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
