using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Tzar2.Content.Compiled;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Tzar2.Sim.Threading;

namespace Tzar2.Sim.Kingdoms;

/// <summary>What a kingdom knows of a tile (GAME-12; the render snapshot's fog layer).</summary>
public enum TileVisibility : byte
{
    /// <summary>Never seen: drawn black.</summary>
    Unexplored = 0,

    /// <summary>Seen before: terrain, structures and resources as last seen; no units.</summary>
    Explored = 1,

    /// <summary>In sight now.</summary>
    Visible = 2,
}

/// <summary>A structure as a kingdom last saw it (GAME-12 "last seen" buildings and resources; ADR 0101).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Ghost
{
#pragma warning disable CA1051 // Plain data.
    /// <summary>The entity seen (index + generation); 0 = no record.</summary>
    public uint Entity;

    /// <summary>Its type then.</summary>
    public ushort TypeId;

    /// <summary>Its owner then.</summary>
    public byte Owner;

    /// <summary>1 if it was a construction site.</summary>
    public byte Site;

    /// <summary>Footprint left column.</summary>
    public ushort TileX;

    /// <summary>Footprint top row.</summary>
    public ushort TileY;

    /// <summary>Hit points then, per mille of the maximum.</summary>
    public ushort HealthPermille;

    /// <summary>Construction progress then, per mille (sites only).</summary>
    public ushort BuildPermille;
#pragma warning restore CA1051
}

/// <summary>
/// Deterministic per-kingdom visibility (GAME-12, ORIG-10, ADR 0101): a reference-counted sight grid per kingdom (one
/// count per tile and revealer covering it), a visible bitset per kingdom (count &gt; 0), the combined bitset each kingdom
/// sees (its own plus shared vision; No Fog: everything explored), the explored bitset, and the last-seen records of
/// other kingdoms' structures and of resources.
/// </summary>
/// <remarks>
/// <para><b>Incremental.</b> Each revealer (a kingdom's unit or building) keeps the disk it stamped (tile, radius,
/// owner); a tick re-stamps only revealers whose tile, radius or owner changed, and removes stamps of revealers that
/// are gone. Stamps are state (in snapshots), so a restored world has exactly the stamps of the uninterrupted one.</para>
/// <para><b>Parallel.</b> Stamp operations are collected per kingdom in entity order, then each kingdom's grid is
/// updated by its own job; combining and the last-seen refresh are per kingdom too, so results do not depend on the
/// worker count.</para>
/// </remarks>
public sealed class Visibility
{
    /// <summary>Largest sight radius in tiles.</summary>
    public const int MaxRadiusTiles = 64;

    private static readonly short[][] Disks = BuildDisks();

    private readonly int _width;
    private readonly int _height;
    private readonly int _tiles;
    private readonly int _words;
    private readonly int _players;
    private readonly int[][] _counts;
    private readonly ulong[][] _own;
    private readonly ulong[][] _visible;
    private readonly ulong[][] _explored;
    private readonly Ghost[][] _memory;
    private readonly List<TemporaryReveal> _temporary = [];
    private readonly StampOp[][] _ops;
    private readonly int[] _opCount;
    private readonly bool[] _noFog;
    private readonly ApplyJob _applyJob;
    private uint[] _stampEntity = new uint[1024];
    private int[] _stampTile = new int[1024];
    private byte[] _stampRadius = new byte[1024];
    private byte[] _stampOwner = new byte[1024];

    internal Visibility(int width, int height, int players)
    {
        _width = width;
        _height = height;
        _tiles = width * height;
        _words = (_tiles + 63) / 64;
        _players = players;
        _counts = new int[players][];
        _own = new ulong[players][];
        _visible = new ulong[players][];
        _explored = new ulong[players][];
        _memory = new Ghost[players][];
        _ops = new StampOp[players][];
        _opCount = new int[players];
        _noFog = new bool[players];
        for (int p = 0; p < players; p++)
        {
            _counts[p] = new int[_tiles];
            _own[p] = new ulong[_words];
            _visible[p] = new ulong[_words];
            _explored[p] = new ulong[_words];
            _memory[p] = new Ghost[1024];
            _ops[p] = new StampOp[256];
        }

        _stampTile.AsSpan().Fill(-1);
        _applyJob = new ApplyJob(this);
    }

    /// <summary>Map width in tiles.</summary>
    public int Width => _width;

    /// <summary>Map height in tiles.</summary>
    public int Height => _height;

    /// <summary>Temporary reveals (magic vision, fog revealers, scripted "explore area"), in creation order.</summary>
    public IReadOnlyList<TemporaryReveal> TemporaryReveals => _temporary;

    /// <summary>Whether <paramref name="player"/> sees tile (<paramref name="tx"/>, <paramref name="ty"/>) now.</summary>
    public bool IsVisible(int player, int tx, int ty)
        => (uint)player < (uint)_players && (uint)tx < (uint)_width && (uint)ty < (uint)_height && Bit(_visible[player], (ty * _width) + tx);

    /// <summary>Whether <paramref name="player"/> has explored the tile.</summary>
    public bool IsExplored(int player, int tx, int ty)
        => (uint)player < (uint)_players && (uint)tx < (uint)_width && (uint)ty < (uint)_height && Bit(_explored[player], (ty * _width) + tx);

    /// <summary>Whether <paramref name="player"/> sees the world point.</summary>
    public bool IsVisibleAt(int player, int x, int y) => IsVisible(player, x / MapState.UnitsPerTile, y / MapState.UnitsPerTile);

    /// <summary>The state of tile (<paramref name="tx"/>, <paramref name="ty"/>) for <paramref name="player"/>.</summary>
    public TileVisibility StateOf(int player, int tx, int ty)
        => IsVisible(player, tx, ty) ? TileVisibility.Visible : IsExplored(player, tx, ty) ? TileVisibility.Explored : TileVisibility.Unexplored;

    /// <summary>Writes one <see cref="TileVisibility"/> byte per tile (row-major) for <paramref name="player"/> into <paramref name="into"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="into"/> is smaller than the map.</exception>
    public void WriteStates(int player, Span<byte> into)
    {
        if (into.Length < _tiles)
        {
            throw new ArgumentException("The span is smaller than the map.", nameof(into));
        }

        if ((uint)player >= (uint)_players)
        {
            into[.._tiles].Fill((byte)TileVisibility.Visible);
            return;
        }

        ulong[] visible = _visible[player];
        ulong[] explored = _explored[player];
        for (int w = 0; w < _words; w++)
        {
            ulong v = visible[w];
            ulong e = explored[w];
            int baseTile = w * 64;
            int n = Math.Min(64, _tiles - baseTile);
            if (v == 0 && e == 0)
            {
                into.Slice(baseTile, n).Clear();
                continue;
            }

            for (int b = 0; b < n; b++)
            {
                ulong m = 1UL << b;
                into[baseTile + b] = (v & m) != 0 ? (byte)TileVisibility.Visible : (e & m) != 0 ? (byte)TileVisibility.Explored : (byte)TileVisibility.Unexplored;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="player"/> can see <paramref name="entity"/> now: its own and shared-control entities always;
    /// others when a tile of it is in sight and it is not concealed (cloaked and not detected).
    /// </summary>
    public bool CanSee(World world, int player, EntityId entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        int i = world.Identities.IndexOf(entity);
        int p = world.Positions.IndexOf(entity);
        if (!world.Entities.IsAlive(entity) || i < 0 || p < 0)
        {
            return false;
        }

        byte owner = world.Identities.At(i).Owner;
        if (owner == player || (world.HasPlayer(owner) && (world.Diplomacy.VisionSources(player) & (1u << owner)) != 0))
        {
            return true;
        }

        Position at = world.Positions.At(p);
        if (world.Footprints.Has(entity))
        {
            (int tx, int ty, int w, int h) = world.FootprintRect(entity);
            for (int y = ty; y < ty + h; y++)
            {
                for (int x = tx; x < tx + w; x++)
                {
                    if (IsVisible(player, x, y))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        return IsVisibleAt(player, at.X, at.Y) && !IsConcealedFrom(world, player, entity, at);
    }

    /// <summary>
    /// Whether a cloaked entity is hidden from <paramref name="player"/>: cloaked (GAME-11 status, <c>World.IsCloaked</c>)
    /// and no detector of the player (spies, ninjas: <c>Detector</c> trait) within its radius.
    /// </summary>
    public bool IsConcealedFrom(World world, int player, EntityId entity, Position at)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.IsConcealed(entity) && !IsDetectedBy(world, player, at);
    }

    /// <summary>Whether a detector of <paramref name="player"/> is within its detection radius of the point (spies and ninjas see each other).</summary>
    public static bool IsDetectedBy(World world, int player, Position at)
    {
        ArgumentNullException.ThrowIfNull(world);
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            if (identities[i].Owner != player)
            {
                continue;
            }

            int detect = world.Ruleset.EntityTypes[identities[i].TypeId].Traits.World.DetectUnits;
            if (detect > 0 && world.Positions.IndexOf(ids[i]) is >= 0 and var q)
            {
                Position d = world.Positions.At(q);
                if (Core.Numerics.IntMath.DistanceSquared(d.X, d.Y, at.X, at.Y) <= (long)detect * detect)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The last-seen record of <paramref name="player"/> for entity slot <paramref name="index"/> (Entity 0 = none).</summary>
    public Ghost MemoryOf(int player, int index)
        => (uint)player < (uint)_players && (uint)index < (uint)_memory[player].Length ? _memory[player][index] : default;

    /// <summary>The last-seen records of <paramref name="player"/>, by entity slot (Entity 0 = none).</summary>
    public ReadOnlySpan<Ghost> Memory(int player) => (uint)player < (uint)_players ? _memory[player] : [];

    // Test and tooling access to a record (the slot is created when missing).
    internal ref Ghost MemoryRef(int player, int index)
    {
        EnsureCapacity(index + 1);
        return ref _memory[player][index];
    }

    // Test and tooling access to the explored bitset.
    internal Span<ulong> ExploredBits(int player) => _explored[player];

    /// <summary>Marks the whole map explored for <paramref name="player"/> (Explore setting, <c>hmreveal</c>).</summary>
    internal void ExploreAll(int player)
    {
        Span<ulong> explored = _explored[player];
        explored.Fill(ulong.MaxValue);
        int tail = _tiles % 64;
        if (tail != 0)
        {
            explored[^1] = (1UL << tail) - 1;
        }
    }

    /// <summary>
    /// Reveals a disk for <paramref name="player"/> for a while (Magic Vision, fog revealers, scripted "explore area"):
    /// it is stamped now and removed by the update of tick <paramref name="expiresTick"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public void Reveal(World world, int player, int tileX, int tileY, int radiusTiles, int expiresTick)
    {
        ArgumentNullException.ThrowIfNull(world);
        if ((uint)player >= (uint)_players || (uint)tileX >= (uint)_width || (uint)tileY >= (uint)_height || radiusTiles is < 0 or > MaxRadiusTiles)
        {
            throw new ArgumentOutOfRangeException(nameof(player));
        }

        if (world.IsLocked)
        {
            throw new InvalidOperationException("The world is locked between ticks.");
        }

        var reveal = new TemporaryReveal((byte)player, (tileY * _width) + tileX, (byte)radiusTiles, Math.Max(expiresTick, world.Tick.Value + 1));
        _temporary.Add(reveal);
        Apply(player, reveal.Tile, reveal.Radius, +1); // in sight from this tick's visibility step on
    }

    // ----- the tick update ---------------------------------------------------------------------------------------

    /// <summary>Pipeline step 14: re-stamps changed revealers, expires temporary reveals, combines and refreshes memory.</summary>
    internal void Update(World world, DeterministicWorkerPool workers)
    {
        int tick = world.Tick.Value;
        EnsureCapacity(world.Entities.HighWater);
        Array.Clear(_opCount);
        for (int p = 0; p < _players; p++)
        {
            _noFog[p] = world.Config.NoFog || (world.Kingdoms[p].Cheats & CheatFlags.NoFog) != 0;
        }

        // Stamps of revealers that are gone.
        for (int i = 0; i < _stampEntity.Length; i++)
        {
            if (_stampTile[i] >= 0 && !world.Entities.IsAlive(EntityId.FromValue(_stampEntity[i])))
            {
                Queue(_stampOwner[i], _stampTile[i], _stampRadius[i], -1);
                _stampTile[i] = -1;
                _stampEntity[i] = 0;
                _stampRadius[i] = 0;
                _stampOwner[i] = 0;
            }
        }

        // Revealers in ascending dense order of the identity store (deterministic).
        int nightBp = world.IsNight ? world.Ruleset.World.NightSightBasisPoints : 10_000;
        int defaultSight = world.Ruleset.World.DefaultSightTiles * MapState.UnitsPerTile;
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        for (int k = 0; k < ids.Length; k++)
        {
            EntityId e = ids[k];
            Identity identity = identities[k];
            int index = e.Index;
            int tile = -1;
            int radius = 0;
            if (identity.Owner < _players && world.Positions.IndexOf(e) is >= 0 and var q && !world.IsHeld(e))
            {
                EntityTypeDefinition type = world.Ruleset.EntityTypes[identity.TypeId];
                if (type.Traits.Category is EntityCategory.Unit or EntityCategory.Building)
                {
                    int sight = type.Traits.SightUnits > 0 ? world.Stats.Apply(identity.Owner, ModifierStat.Sight, type.Id, type.Traits.SightUnits) : defaultSight;
                    long reach = ((long)Math.Max(0, sight) * nightBp / 10_000) + (Math.Max(type.FootprintWidth, type.FootprintHeight) * MapState.UnitsPerTile / 2);
                    radius = (int)Math.Clamp((reach + (MapState.UnitsPerTile / 2)) / MapState.UnitsPerTile, 0, MaxRadiusTiles);
                    Position at = world.Positions.At(q);
                    tile = (Math.Clamp(at.Y / MapState.UnitsPerTile, 0, _height - 1) * _width) + Math.Clamp(at.X / MapState.UnitsPerTile, 0, _width - 1);
                }
            }

            bool stamped = _stampTile[index] >= 0;
            if (stamped && _stampEntity[index] == e.Value && _stampTile[index] == tile && _stampRadius[index] == radius && _stampOwner[index] == identity.Owner)
            {
                continue;
            }

            if (stamped)
            {
                Queue(_stampOwner[index], _stampTile[index], _stampRadius[index], -1);
                _stampTile[index] = -1;
                _stampEntity[index] = 0;
                _stampRadius[index] = 0;
                _stampOwner[index] = 0;
            }

            if (tile >= 0)
            {
                Queue(identity.Owner, tile, radius, +1);
                _stampTile[index] = tile;
                _stampEntity[index] = e.Value;
                _stampRadius[index] = (byte)radius;
                _stampOwner[index] = identity.Owner;
            }
        }

        // Temporary reveals that end now.
        for (int i = _temporary.Count - 1; i >= 0; i--)
        {
            if (_temporary[i].ExpiresTick <= tick)
            {
                Queue(_temporary[i].Player, _temporary[i].Tile, _temporary[i].Radius, -1);
                _temporary.RemoveAt(i);
            }
        }

        _applyJob.World = world;
        _applyJob.Tick = tick;
        _applyJob.Combining = false;
        workers.Run(_applyJob, _players);
        _applyJob.Combining = true;
        workers.Run(_applyJob, _players);
    }

    // Recomputes after a restore: counts from the stored stamps and temporary reveals, then the combined bitsets.
    internal void Rebuild(World world)
    {
        for (int p = 0; p < _players; p++)
        {
            Array.Clear(_counts[p]);
            Array.Clear(_own[p]);
            _noFog[p] = world.Config.NoFog || (world.Kingdoms[p].Cheats & CheatFlags.NoFog) != 0;
        }

        for (int i = 0; i < _stampTile.Length; i++)
        {
            if (_stampTile[i] >= 0)
            {
                Apply(_stampOwner[i], _stampTile[i], _stampRadius[i], +1);
            }
        }

        foreach (TemporaryReveal r in _temporary)
        {
            Apply(r.Player, r.Tile, r.Radius, +1);
        }

        for (int p = 0; p < _players; p++)
        {
            Combine(world, p, explore: false);
        }
    }

    // Match start: Explore setting, and the first sight of every kingdom.
    internal void Start(World world, DeterministicWorkerPool? workers)
    {
        if (world.Config.Explored)
        {
            for (int p = 0; p < _players; p++)
            {
                ExploreAll(p);
                RememberAll(world, p);
            }
        }

        using var inline = workers is null ? new DeterministicWorkerPool(1) : null;
        Update(world, workers ?? inline!);
    }

    private void Queue(int player, int tile, int radius, int delta)
    {
        ref StampOp[] ops = ref _ops[player];
        if (_opCount[player] == ops.Length)
        {
            Array.Resize(ref ops, ops.Length * 2);
        }

        ops[_opCount[player]++] = new StampOp(tile, (byte)radius, (sbyte)delta);
    }

    // Adds delta to every tile of the disk around `tile`, keeping the own bitset in step.
    private void Apply(int player, int tile, int radius, int delta)
    {
        int cx = tile % _width;
        int cy = tile / _width;
        short[] disk = Disks[radius];
        int[] counts = _counts[player];
        ulong[] own = _own[player];
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = cy + dy;
            if ((uint)y >= (uint)_height)
            {
                continue;
            }

            int half = disk[dy + radius];
            int x0 = Math.Max(0, cx - half);
            int x1 = Math.Min(_width - 1, cx + half);
            int row = y * _width;
            for (int x = x0; x <= x1; x++)
            {
                int t = row + x;
                int before = counts[t];
                int after = before + delta;
                counts[t] = after;
                if (before == 0 && after > 0)
                {
                    own[t >> 6] |= 1UL << (t & 63);
                }
                else if (before > 0 && after == 0)
                {
                    own[t >> 6] &= ~(1UL << (t & 63));
                }
            }
        }
    }

    // combined = OR of the vision sources' own bitsets (No Fog: explored too); explored |= combined.
    private void Combine(World world, int player, bool explore)
    {
        ulong[] visible = _visible[player];
        ulong[] explored = _explored[player];
        Array.Copy(_own[player], visible, _words);
        uint sources = world.Diplomacy.VisionSources(player) & ~(1u << player);
        while (sources != 0)
        {
            int s = BitOperations.TrailingZeroCount(sources);
            sources &= sources - 1;
            if (s >= _players)
            {
                continue;
            }

            ulong[] other = _own[s];
            for (int w = 0; w < _words; w++)
            {
                visible[w] |= other[w];
            }
        }

        if (explore)
        {
            for (int w = 0; w < _words; w++)
            {
                explored[w] |= visible[w];
            }
        }

        if (_noFog[player])
        {
            Array.Copy(explored, visible, _words);
        }
    }

    // A kingdom's last-seen records: every MemoryRefreshTicks ticks a slot is checked (staggered by slot index). A
    // visible structure of another kingdom, or a resource, is recorded as it is; a record whose tile is in sight but whose
    // entity is gone (or changed) is dropped: the kingdom sees that it is no longer there.
    private void RefreshMemory(World world, int player, int tick)
    {
        int stride = world.Ruleset.World.MemoryRefreshTicks;
        Ghost[] memory = _memory[player];
        ulong[] visible = _visible[player];
        int high = Math.Min(world.Entities.HighWater, memory.Length);
        for (int index = tick % stride; index < high; index += stride)
        {
            ref Ghost g = ref memory[index];
            EntityId e = world.Entities.AtIndex(index);
            int f = e.IsNone ? -1 : world.Footprints.IndexOf(e);
            if (f >= 0)
            {
                Identity identity = world.Identities.Get(e);
                bool remembered = identity.Owner != player && !world.Diplomacy.AreMutualAllies(player, identity.Owner);
                if (remembered && AnyVisible(world, visible, e))
                {
                    g = Record(world, e, identity, world.Footprints.At(f));
                    continue;
                }

                if (!remembered)
                {
                    g = default;
                    continue;
                }
            }

            if (g.Entity != 0 && g.Entity != e.Value && Bit(visible, (g.TileY * _width) + g.TileX))
            {
                g = default;
            }
        }
    }

    // Explore setting and hmreveal: every structure of others is known as it is now (the whole map is explored).
    internal void RememberAll(World world, int player)
    {
        EnsureCapacity(world.Entities.HighWater);
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            Identity identity = world.Identities.Get(structures[i]);
            if (identity.Owner != player && !world.Diplomacy.AreMutualAllies(player, identity.Owner))
            {
                _memory[player][structures[i].Index] = Record(world, structures[i], identity, world.Footprints.Components[i]);
            }
        }
    }

    private static Ghost Record(World world, EntityId e, Identity identity, Footprint footprint)
    {
        Health health = world.Healths.Get(e);
        int site = world.Constructions.IndexOf(e);
        return new Ghost
        {
            Entity = e.Value,
            TypeId = identity.TypeId,
            Owner = identity.Owner,
            Site = site >= 0 ? (byte)1 : (byte)0,
            TileX = (ushort)footprint.TileX,
            TileY = (ushort)footprint.TileY,
            HealthPermille = (ushort)(health.Max > 0 ? Math.Clamp(health.Current * 1000L / health.Max, 0, 1000) : 0),
            BuildPermille = (ushort)(site >= 0 && world.Constructions.At(site).TotalMs > 0
                ? Math.Clamp(world.Constructions.At(site).ProgressMs * 1000L / world.Constructions.At(site).TotalMs, 0, 1000) : 0),
        };
    }

    private bool AnyVisible(World world, ulong[] visible, EntityId structure)
    {
        (int tx, int ty, int w, int h) = world.FootprintRect(structure);
        for (int y = ty; y < ty + h; y++)
        {
            for (int x = tx; x < tx + w; x++)
            {
                if (Bit(visible, (y * _width) + x))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void EnsureCapacity(int highWater)
    {
        if (highWater <= _stampTile.Length)
        {
            return;
        }

        int size = Math.Max(highWater, _stampTile.Length * 2);
        int old = _stampTile.Length;
        Array.Resize(ref _stampEntity, size);
        Array.Resize(ref _stampTile, size);
        Array.Resize(ref _stampRadius, size);
        Array.Resize(ref _stampOwner, size);
        _stampTile.AsSpan(old).Fill(-1);
        for (int p = 0; p < _players; p++)
        {
            Array.Resize(ref _memory[p], size);
        }
    }

    private static bool Bit(ulong[] bits, int tile) => (bits[tile >> 6] & (1UL << (tile & 63))) != 0;

    // Half-widths per row of a tile disk: dx² + dy² ≤ r² + r (a rounder disk than r²).
    private static short[][] BuildDisks()
    {
        var disks = new short[MaxRadiusTiles + 1][];
        for (int r = 0; r <= MaxRadiusTiles; r++)
        {
            var rows = new short[(2 * r) + 1];
            int limit = (r * r) + r;
            for (int dy = -r; dy <= r; dy++)
            {
                int half = 0;
                while ((half + 1) * (half + 1) + (dy * dy) <= limit)
                {
                    half++;
                }

                rows[dy + r] = (short)half;
            }

            disks[r] = rows;
        }

        return disks;
    }

    // ----- hashing and snapshots ---------------------------------------------------------------------------------

    // Hashes what snapshots store (capacity-independent: only non-empty stamps and records) plus the visible bitsets.
    internal void AppendTo(StateHasher hasher)
    {
        hasher.Add(_players);
        for (int p = 0; p < _players; p++)
        {
            hasher.AddSpan<ulong>(_explored[p]).AddSpan<ulong>(_visible[p]);
            Ghost[] memory = _memory[p];
            for (int i = 0; i < memory.Length; i++)
            {
                if (memory[i].Entity != 0)
                {
                    hasher.Add(i).AddSpan<Ghost>(memory.AsSpan(i, 1));
                }
            }
        }

        for (int i = 0; i < _stampTile.Length; i++)
        {
            if (_stampTile[i] >= 0)
            {
                hasher.Add(i).Add(_stampEntity[i]).Add(_stampTile[i]).Add(_stampRadius[i]).Add(_stampOwner[i]);
            }
        }

        hasher.Add(_temporary.Count);
        foreach (TemporaryReveal r in _temporary)
        {
            hasher.Add(r.Player).Add(r.Tile).Add(r.Radius).Add(r.ExpiresTick);
        }
    }

    internal void WriteTo(BinaryBufferWriter writer)
    {
        for (int p = 0; p < _players; p++)
        {
            WriteRuns(writer, _explored[p]);
        }

        int stamps = 0;
        foreach (int t in _stampTile)
        {
            stamps += t >= 0 ? 1 : 0;
        }

        writer.WriteVarUInt64((ulong)stamps);
        for (int i = 0; i < _stampTile.Length; i++)
        {
            if (_stampTile[i] >= 0)
            {
                writer.WriteVarUInt64((ulong)i);
                writer.WriteUInt32(_stampEntity[i]);
                writer.WriteVarUInt64((ulong)_stampTile[i]);
                writer.WriteByte(_stampRadius[i]);
                writer.WriteByte(_stampOwner[i]);
            }
        }

        writer.WriteVarUInt64((ulong)_temporary.Count);
        foreach (TemporaryReveal r in _temporary)
        {
            writer.WriteByte(r.Player);
            writer.WriteVarUInt64((ulong)r.Tile);
            writer.WriteByte(r.Radius);
            writer.WriteVarUInt64((ulong)r.ExpiresTick);
        }

        for (int p = 0; p < _players; p++)
        {
            int n = 0;
            foreach (Ghost g in _memory[p])
            {
                n += g.Entity != 0 ? 1 : 0;
            }

            writer.WriteVarUInt64((ulong)n);
            for (int i = 0; i < _memory[p].Length; i++)
            {
                if (_memory[p][i].Entity != 0)
                {
                    writer.WriteVarUInt64((ulong)i);
                    writer.WriteBytes(MemoryMarshal.AsBytes(_memory[p].AsSpan(i, 1)));
                }
            }
        }
    }

    internal void ReadFrom(ref BinaryBufferReader reader, World world)
    {
        int tick = world.Tick.Value;
        for (int p = 0; p < _players; p++)
        {
            ReadRuns(ref reader, _explored[p]);
            int tail = _tiles % 64;
            if (tail != 0 && (_explored[p][^1] >> tail) != 0)
            {
                throw Invalid("explored bits beyond the map");
            }
        }

        // Every stamp and record names a slot below the registry's high-water mark (the entity existed).
        ulong capacity = (ulong)world.Entities.HighWater;
        EnsureCapacity(world.Entities.HighWater);
        ulong stamps = reader.ReadVarUInt64();
        if (stamps > capacity)
        {
            throw Invalid("too many stamps");
        }

        long previous = -1;
        for (ulong k = 0; k < stamps; k++)
        {
            ulong index = reader.ReadVarUInt64();
            uint entity = reader.ReadUInt32();
            ulong tile = reader.ReadVarUInt64();
            byte radius = reader.ReadByte();
            byte owner = reader.ReadByte();
            if (index >= capacity || (long)index <= previous || entity == 0 || EntityId.FromValue(entity).Index != (int)index
                || tile >= (ulong)_tiles || radius > MaxRadiusTiles || owner >= _players)
            {
                throw Invalid("invalid sight stamp");
            }

            previous = (long)index;
            _stampEntity[index] = entity;
            _stampTile[index] = (int)tile;
            _stampRadius[index] = radius;
            _stampOwner[index] = owner;
        }

        ulong temporary = reader.ReadVarUInt64();
        if (temporary > 65_536)
        {
            throw Invalid("too many temporary reveals");
        }

        _temporary.Clear();
        for (ulong k = 0; k < temporary; k++)
        {
            byte player = reader.ReadByte();
            ulong tile = reader.ReadVarUInt64();
            byte radius = reader.ReadByte();
            ulong expires = reader.ReadVarUInt64();
            if (player >= _players || tile >= (ulong)_tiles || radius > MaxRadiusTiles || expires <= (ulong)tick || expires > int.MaxValue)
            {
                throw Invalid("invalid temporary reveal");
            }

            _temporary.Add(new TemporaryReveal(player, (int)tile, radius, (int)expires));
        }

        int types = world.Ruleset.EntityTypes.Count;
        for (int p = 0; p < _players; p++)
        {
            ulong n = reader.ReadVarUInt64();
            if (n > capacity)
            {
                throw Invalid("too many last-seen records");
            }

            previous = -1;
            for (ulong k = 0; k < n; k++)
            {
                ulong index = reader.ReadVarUInt64();
                if (index >= capacity || (long)index <= previous)
                {
                    throw Invalid("invalid last-seen record");
                }

                previous = (long)index;
                Ghost g = MemoryMarshal.Read<Ghost>(reader.ReadBytes(Marshal.SizeOf<Ghost>()));
                if (g.Entity == 0 || EntityId.FromValue(g.Entity).Index != (int)index || g.TypeId >= types || !world.IsValidOwner(g.Owner)
                    || g.Owner == p || g.Site > 1 || g.TileX >= _width || g.TileY >= _height || g.HealthPermille > 1000 || g.BuildPermille > 1000
                    || !world.Ruleset.EntityTypes[g.TypeId].HasFootprint)
                {
                    throw Invalid("invalid last-seen record");
                }

                _memory[p][index] = g;
            }
        }

        Rebuild(world);

        static InvalidDataException Invalid(string what) => new($"Snapshot violates a world invariant: {what}.");
    }

    // Explored bitsets are mostly all-clear or all-set words: (run length, word) pairs.
    private static void WriteRuns(BinaryBufferWriter writer, ulong[] words)
    {
        int i = 0;
        while (i < words.Length)
        {
            int j = i + 1;
            while (j < words.Length && words[j] == words[i])
            {
                j++;
            }

            writer.WriteVarUInt64((ulong)(j - i));
            writer.WriteUInt64(words[i]);
            i = j;
        }
    }

    private static void ReadRuns(ref BinaryBufferReader reader, ulong[] words)
    {
        int i = 0;
        while (i < words.Length)
        {
            ulong run = reader.ReadVarUInt64();
            ulong word = reader.ReadUInt64();
            if (run == 0 || run > (ulong)(words.Length - i))
            {
                throw new InvalidDataException("Snapshot violates a world invariant: invalid explored runs.");
            }

            words.AsSpan(i, (int)run).Fill(word);
            i += (int)run;
        }
    }

    private readonly record struct StampOp(int Tile, byte Radius, sbyte Delta);

    // One job per kingdom: apply its stamp operations in order, combine, then refresh its last-seen records.
    private sealed class ApplyJob(Visibility owner) : IParallelJob
    {
        public World World { get; set; } = null!;

        public int Tick { get; set; }

        public bool Combining { get; set; }

        public void Execute(int jobIndex)
        {
            if (Combining)
            {
                // Reads every kingdom's own bitset (done by the first pass); writes only this kingdom's state.
                owner.Combine(World, jobIndex, explore: true);
                owner.RefreshMemory(World, jobIndex, Tick);
                return;
            }

            StampOp[] ops = owner._ops[jobIndex];
            for (int i = 0; i < owner._opCount[jobIndex]; i++)
            {
                owner.Apply(jobIndex, ops[i].Tile, ops[i].Radius, ops[i].Delta);
            }
        }
    }
}

/// <summary>A temporary reveal (Magic Vision, fog revealers, scripted "explore area"; ADR 0101).</summary>
/// <param name="Player">The kingdom that sees.</param>
/// <param name="Tile">Center tile index (row-major).</param>
/// <param name="Radius">Radius in tiles.</param>
/// <param name="ExpiresTick">The update of this tick removes it.</param>
public readonly record struct TemporaryReveal(byte Player, int Tile, byte Radius, int ExpiresTick);
