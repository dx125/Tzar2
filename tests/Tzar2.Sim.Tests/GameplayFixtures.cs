using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Sim.Map;
using Tzar2.Sim.Scenarios;

namespace Tzar2.Sim.Tests;

/// <summary>
/// The classic ruleset compiled from the synthetic legacy tables (invented numbers, REQ-LEGAL-03) and helpers for
/// gameplay test maps. Synthetic numbers used by the tests: peon 45f / 9 s / 44 HP / attack 3 / defence 1 / swing 3 s;
/// carries 10 and gathers 1 unit per s (UNVERIFIED YAML placeholders); footman 66f / 11 s / 70 HP / attack 9 /
/// defence 2 / swing 1 s; archer range 7, attack 7, swing 2 s; maceman speed 2; castle 5×5 (cut corners), 5,500 HP,
/// population 24; barracks 3×3, 160 stone + 140 food, 42 s, 1,250 HP; house 2×2, 55 wood, 16 s, 420 HP, resistance 40,
/// population 12; tree 100 wood; starting resources (medium) 500 of each.
/// </summary>
internal static class GameplayFixtures
{
    public const int Tile = MapState.UnitsPerTile;

    public static Ruleset Classic { get; } = SyntheticLegacyContent.CompileRuleset();

    public static ushort Id(string key) => Id(Classic, key);

    public static ushort Id(Ruleset ruleset, string key)
        => ruleset.EntityTypeId(key) is >= 0 and var id ? (ushort)id : throw new ArgumentException($"no type {key}");

    public static int Resource(string key) => Classic.ResourceId(key);

    /// <summary>Compiles classic with an extra YAML layer.</summary>
    public static Ruleset ClassicWith(string yaml)
        => SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions { OverrideTexts = [new("test.yaml", yaml)] });

    /// <summary>A grass map (ASCII rows like <see cref="ScenarioMaps.FromAscii"/>, or flat W×H) with objects.</summary>
    public static MapData TestMap(int width, int height, params MapObject[] objects)
        => ScenarioMaps.WithObjects(ScenarioMaps.Build("test", width, height, new byte[width * height], []), objects);

    public static MapData AsciiMap(string[] rows, params MapObject[] objects)
        => ScenarioMaps.WithObjects(ScenarioMaps.FromAscii("test", rows), objects);

    /// <summary>A unit at the center of a tile.</summary>
    public static MapObject Unit(string key, int owner, int tileX, int tileY)
        => new(key, (byte)owner, (tileX * Tile) + (Tile / 2), (tileY * Tile) + (Tile / 2));

    /// <summary>A structure with its footprint's top-left at a tile.</summary>
    public static MapObject Structure(string key, int owner, int tileX, int tileY) => MapObject.AtTile(key, (byte)owner, tileX, tileY);

    public static MapObject Tree(int tileX, int tileY) => MapObject.AtTile("comm.tree", MapObject.Nature, tileX, tileY);

    public static IEnumerable<Core.Ids.EntityId> OfType(World world, string key, int owner = -1)
    {
        ushort type = Id(world.Ruleset, key);
        var list = new List<Core.Ids.EntityId>();
        ReadOnlySpan<Core.Ids.EntityId> ids = world.Identities.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Components.Identity identity = world.Identities.Components[i];
            if (identity.TypeId == type && (owner < 0 || identity.Owner == owner))
            {
                list.Add(ids[i]);
            }
        }

        return list;
    }
}
