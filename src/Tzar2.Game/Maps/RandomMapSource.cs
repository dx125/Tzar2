using System;
using System.Collections.Immutable;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.MapGen;
using Tzar2.Game.Session;

namespace Tzar2.Game.Maps;

/// <summary>
/// A random map (GAME-18) from the map generator of stream C (ADR 0060) as an <see cref="IMapSource"/>: type, terrain
/// set, size, resources and features from the skirmish setup; seed, kingdom count and civilizations from the match.
/// </summary>
public sealed class RandomMapSource : IMapSource
{
    /// <summary>Creates the source.</summary>
    public RandomMapSource(RandomMapType type = RandomMapType.Continental, MapTerrainSet set = MapTerrainSet.Grass, RandomMapSize size = RandomMapSize.Small, MapResourceLevel resources = MapResourceLevel.Normal, MapFeatures features = MapFeatures.Default)
    {
        Type = type;
        Set = set;
        Size = size;
        Resources = resources;
        Features = features;
    }

    /// <summary>Map type.</summary>
    public RandomMapType Type { get; }

    /// <summary>Terrain set.</summary>
    public MapTerrainSet Set { get; }

    /// <summary>Map size.</summary>
    public RandomMapSize Size { get; }

    /// <summary>Resource level.</summary>
    public MapResourceLevel Resources { get; }

    /// <summary>Optional contents.</summary>
    public MapFeatures Features { get; }

    /// <inheritdoc />
    public string Name => $"{RandomMapNames.Name(Type)} ({RandomMapNames.Name(Size)}, {RandomMapNames.Name(Set)})";

    /// <summary>The generator options for <paramref name="setup"/>.</summary>
    public MapGenOptions Options(Ruleset ruleset, MatchSetup setup)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(setup);
        return new MapGenOptions
        {
            Type = Type,
            TerrainSet = Set,
            Size = Size,
            Seed = setup.Seed,
            Players = setup.Players.Count,
            Civilizations = [.. setup.Players.Select(p => p.Civilization)],
            Resources = Resources,
            Features = Features,
        }.BindTo(ruleset);
    }

    /// <inheritdoc />
    /// <exception cref="MapGenException">No attempt of the generator passed validation.</exception>
    public MapData Create(Ruleset ruleset, MatchSetup setup) => MapGenerator.Generate(Options(ruleset, setup)).Map;
}
