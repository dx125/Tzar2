using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Navigation;

/// <summary>
/// All navigation layers of a match (TDD §9.2, ADR 0021): one <see cref="NavLayer"/> per movement class, i.e. per
/// (locomotor passability, clearance) pair used by a mobile ground or water type. Locomotors whose base passability is
/// identical on this map share a layer (blockers affect every non-air locomotor alike). Air units fly straight
/// (REQ-PATH-10) and have no layer.
/// </summary>
/// <remarks>
/// Navigation is <b>derived state</b>: a pure function of the map's passability. It is built on first use and kept
/// up to date incrementally from <see cref="MapState"/>'s changed-tile log (REQ-PATH-01/02); it is neither hashed nor
/// snapshotted, and a restored match rebuilds it identically.
/// </remarks>
public sealed class NavigationGrid
{
    /// <summary>Layer index of types that do not path (air, immobile).</summary>
    public const byte NoLayer = 0xFF;

    private readonly MapState _map;
    private readonly byte[] _typeLayer;
    private readonly List<(LocomotorMask Mask, Locomotor Representative, int Clearance)> _layerKeys = [];
    private NavLayer[] _layers = [];

    internal NavigationGrid(MapState map, Ruleset ruleset)
    {
        _map = map;
        _typeLayer = new byte[ruleset.EntityTypes.Count];

        // Locomotor equivalence classes on this map: equal base passability bits on every tile. The climber keeps its own:
        // walls built later open to it alone (WORLD-018).
        Span<int> classOf = stackalloc int[LocomotorExtensions.Count];
        ReadOnlySpan<byte> passability = map.BasePassability;
        for (int l = 0; l < LocomotorExtensions.Count; l++)
        {
            classOf[l] = l;
            for (int k = 0; k < l && l != (int)Locomotor.Climber; k++)
            {
                if (classOf[k] == k && SameColumn(passability, (Locomotor)k, (Locomotor)l))
                {
                    classOf[l] = k;
                    break;
                }
            }
        }

        for (int t = 0; t < _typeLayer.Length; t++)
        {
            EntityTypeDefinition type = ruleset.EntityTypes[t];
            _typeLayer[t] = NoLayer;
            if (type.SpeedUnitsPerSecond == 0 || type.Locomotor == Locomotor.Air)
            {
                continue;
            }

            var representative = (Locomotor)classOf[(int)type.Locomotor];
            int index = _layerKeys.FindIndex(k => k.Representative == representative && k.Clearance == type.ClearanceTiles);
            if (index < 0)
            {
                index = _layerKeys.Count;
                _layerKeys.Add((LocomotorMask.None, representative, type.ClearanceTiles));
            }

            (LocomotorMask mask, Locomotor rep, int clearance) = _layerKeys[index];
            _layerKeys[index] = (mask | type.Locomotor.ToMask(), rep, clearance);
            _typeLayer[t] = (byte)index;
        }
    }

    /// <summary>Whether the layers have been built.</summary>
    public bool IsBuilt { get; private set; }

    /// <summary>The layers (build first with <see cref="EnsureUpToDate"/>).</summary>
    public ReadOnlySpan<NavLayer> Layers => _layers;

    /// <summary>Number of layers the ruleset needs on this map.</summary>
    public int LayerCount => _layerKeys.Count;

    /// <summary>Clusters rebuilt by incremental updates so far, over every layer (a cost counter; derived, not state).</summary>
    public long ClustersRebuilt
    {
        get
        {
            long n = 0;
            foreach (NavLayer layer in _layers)
            {
                n += layer.ClustersRebuilt;
            }

            return n;
        }
    }

    /// <summary>The map's <see cref="MapState.PassabilityVersion"/> the layers reflect (state, so plans can be validated against it after a restore).</summary>
    public int Version { get; private set; } = -1;

    /// <summary>Layer index of an entity type, or <see cref="NoLayer"/>.</summary>
    public byte LayerOfType(ushort typeId) => _typeLayer[typeId];

    /// <summary>The layer of an entity type, or <c>null</c> (air/immobile).</summary>
    public NavLayer? ForType(ushort typeId)
    {
        EnsureUpToDate();
        byte layer = _typeLayer[typeId];
        return layer == NoLayer ? null : _layers[layer];
    }

    /// <summary>Builds the layers on first use and applies pending passability changes (deterministic, in tile order).</summary>
    public void EnsureUpToDate()
    {
        if (!IsBuilt)
        {
            _layers = new NavLayer[_layerKeys.Count];
            for (int i = 0; i < _layers.Length; i++)
            {
                (LocomotorMask mask, Locomotor rep, int clearance) = _layerKeys[i];
                _layers[i] = new NavLayer(i, _map, mask, rep, clearance);
            }

            IsBuilt = true;
            _map.ClearChangedTiles();
            Version = _map.PassabilityVersion;
            return;
        }

        if (Version == _map.PassabilityVersion && !_map.BlockersReplaced)
        {
            return;
        }

        if (_map.BlockersReplaced)
        {
            foreach (NavLayer layer in _layers)
            {
                layer.RebuildAll();
            }
        }
        else
        {
            ReadOnlySpan<int> changed = _map.ChangedTiles;
            foreach (NavLayer layer in _layers)
            {
                foreach (int tile in changed)
                {
                    layer.TileChanged(tile % _map.Width, tile / _map.Width);
                }

                layer.ApplyChanges();
            }
        }

        _map.ClearChangedTiles();
        Version = _map.PassabilityVersion;
    }

    private static bool SameColumn(ReadOnlySpan<byte> passability, Locomotor a, Locomotor b)
    {
        int ba = 1 << (int)a;
        int bb = 1 << (int)b;
        foreach (byte p in passability)
        {
            if (((p & ba) != 0) != ((p & bb) != 0))
            {
                return false;
            }
        }

        return true;
    }
}
