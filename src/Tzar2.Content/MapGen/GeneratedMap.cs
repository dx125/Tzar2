using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.MapGen;

/// <summary>A kingdom's start: the castle's centre tile, team and civilization.</summary>
/// <param name="Player">Player slot.</param>
/// <param name="TileX">Centre tile X of the castle.</param>
/// <param name="TileY">Centre tile Y of the castle.</param>
/// <param name="Team">Team.</param>
/// <param name="Civilization">Civilization (resolved; never <c>random</c>).</param>
public readonly record struct StartPosition(int Player, int TileX, int TileY, int Team, string Civilization);

/// <summary>Counts of what a generated map holds.</summary>
/// <param name="LandTiles">Tiles whose four corners are land.</param>
/// <param name="WaterTiles">Tiles whose four corners are water.</param>
/// <param name="CliffTiles">Cliff tiles.</param>
/// <param name="ObjectsByRole">Placed objects per <see cref="MapObjectRole"/>.</param>
public sealed record MapGenStats(int LandTiles, int WaterTiles, int CliffTiles, ImmutableArray<int> ObjectsByRole)
{
    /// <summary>Objects placed for <paramref name="role"/>.</summary>
    public int Count(MapObjectRole role) => (int)role < ObjectsByRole.Length ? ObjectsByRole[(int)role] : 0;
}

/// <summary>Result of <see cref="MapGenerator.Generate(MapGenOptions)"/>.</summary>
public sealed class GeneratedMap
{
    internal GeneratedMap(MapData map, MapGenOptions options, RandomMapType resolvedType, ImmutableArray<StartPosition> starts, int attempts, MapGenStats stats, IReadOnlyList<string> notes)
    {
        Map = map;
        Options = options;
        ResolvedType = resolvedType;
        Starts = starts;
        Attempts = attempts;
        Stats = stats;
        Notes = notes;
    }

    /// <summary>The map (ruleset terrain ids, terrain-set corners, cliffs, objects).</summary>
    public MapData Map { get; }

    /// <summary>The options it was generated from.</summary>
    public MapGenOptions Options { get; }

    /// <summary>The type actually generated (<see cref="RandomMapType.Random"/> resolved).</summary>
    public RandomMapType ResolvedType { get; }

    /// <summary>One start per player, in player order.</summary>
    public ImmutableArray<StartPosition> Starts { get; }

    /// <summary>Attempts used (1 = the first attempt passed validation).</summary>
    public int Attempts { get; }

    /// <summary>Counts.</summary>
    public MapGenStats Stats { get; }

    /// <summary>Things the generator skipped or relaxed (a role the catalog lacks, no room for a feature).</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>Whether players meet over water rather than land (<see cref="RandomMapType.Archipelagos"/>).</summary>
    public bool IsNaval => ResolvedType == RandomMapType.Archipelagos;
}

/// <summary>The generator gave up: no attempt passed validation.</summary>
public sealed class MapGenException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MapGenException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public MapGenException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MapGenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
