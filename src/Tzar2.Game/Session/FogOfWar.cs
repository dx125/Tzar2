using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Components;

namespace Tzar2.Game.Session;

/// <summary>
/// ⚠ PLACEHOLDER (ADR 0070): the sim has no visibility system yet (TDD §8.5 visibility step, GAME stream A). Until it
/// has, the session derives the local player's fog from its own entities' sight radius
/// (<see cref="EntityTraits.SightUnits"/>) every <see cref="IntervalTicks"/> ticks: tiles within sight are visible, tiles
/// seen once stay explored. Presentation only: it never feeds back into the sim, and enemy-building "ghosts" (§13.6) are
/// not kept. Replace this class with a copy of the sim's per-player visibility when it exists.
/// </summary>
public sealed class FogOfWar
{
    /// <summary>Ticks between two updates (sight changes slowly; the renderer softens the edges).</summary>
    public const int IntervalTicks = 4;

    /// <summary>Sight of entities whose type declares none (one tile = 2,048 units).</summary>
    public const int DefaultSightUnits = 4 * MapData.UnitsPerTile;

    private readonly byte[] _state;

    /// <summary>Creates an all-unexplored layer.</summary>
    public FogOfWar(int width, int height)
    {
        Width = width;
        Height = height;
        _state = new byte[width * height];
    }

    /// <summary>Width in tiles.</summary>
    public int Width { get; }

    /// <summary>Height in tiles.</summary>
    public int Height { get; }

    /// <summary>0 unexplored, 1 explored, 2 visible, per tile.</summary>
    public ReadOnlySpan<byte> State => _state;

    /// <summary>Recomputes what <paramref name="player"/> sees.</summary>
    public void Update(World world, byte player)
    {
        ArgumentNullException.ThrowIfNull(world);
        for (int i = 0; i < _state.Length; i++)
        {
            if (_state[i] == 2)
            {
                _state[i] = 1;
            }
        }

        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            if (identities[i].Owner != player)
            {
                continue;
            }

            int p = world.Positions.IndexOf(ids[i]);
            if (p < 0)
            {
                continue;
            }

            EntityTypeDefinition type = world.Ruleset.EntityTypes[identities[i].TypeId];
            Position position = world.Positions.At(p);
            int cx = position.X, cy = position.Y;
            int f = world.Footprints.IndexOf(ids[i]);
            if (f >= 0)
            {
                // Structures see from their footprint's centre.
                Footprint footprint = world.Footprints.At(f);
                cx = (footprint.TileX * MapData.UnitsPerTile) + (type.FootprintWidth * MapData.UnitsPerTile / 2);
                cy = (footprint.TileY * MapData.UnitsPerTile) + (type.FootprintHeight * MapData.UnitsPerTile / 2);
            }

            int sight = type.Traits.SightUnits > 0 ? type.Traits.SightUnits : DefaultSightUnits;
            Reveal(cx, cy, sight + (Math.Max(type.FootprintWidth, type.FootprintHeight) * MapData.UnitsPerTile / 2));
        }
    }

    /// <summary>Marks a disc as visible (world units).</summary>
    public void Reveal(int x, int y, int radius)
    {
        int tile = MapData.UnitsPerTile;
        int r = (radius / tile) + 1;
        int tx = x / tile, ty = y / tile;
        long r2 = (long)radius * radius;
        for (int yy = Math.Max(0, ty - r); yy <= Math.Min(Height - 1, ty + r); yy++)
        {
            long dy = ((long)yy * tile) + (tile / 2) - y;
            for (int xx = Math.Max(0, tx - r); xx <= Math.Min(Width - 1, tx + r); xx++)
            {
                long dx = ((long)xx * tile) + (tile / 2) - x;
                if ((dx * dx) + (dy * dy) <= r2)
                {
                    _state[(yy * Width) + xx] = 2;
                }
            }
        }
    }

    /// <summary>Marks everything explored and visible (fog off, observers).</summary>
    public void RevealAll() => _state.AsSpan().Fill(2);
}
