using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.MapGen;
using Tzar2.Content.Terrain;
using Tzar2.Core.Rng;
using Xunit;

namespace Tzar2.Content.Tests;

/// <summary>The random map generator (GAME-18, ADR 0060): validity over many seeds, determinism, options.</summary>
public class MapGenTests
{
    private const ulong PlainMapHash = 0xF44F5662664EA940UL;

    private static readonly RandomMapType[] Types = [RandomMapType.Archipelagos, RandomMapType.Island, RandomMapType.Continental, RandomMapType.Lakes, RandomMapType.Dry];

    public static TheoryData<RandomMapType, RandomMapSize, int> Matrix()
    {
        var data = new TheoryData<RandomMapType, RandomMapSize, int>();
        foreach (RandomMapType type in Types)
        {
            data.Add(type, RandomMapSize.Tiny, 2);
            data.Add(type, RandomMapSize.Tiny, 4);
            data.Add(type, RandomMapSize.Small, 6);
            data.Add(type, RandomMapSize.Medium, 3);
            data.Add(type, RandomMapSize.Medium, 8);
            data.Add(type, RandomMapSize.Large, 5);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_seed_gives_a_valid_map(RandomMapType type, RandomMapSize size, int players)
    {
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var options = new MapGenOptions
            {
                Type = type,
                Size = size,
                Players = players,
                Seed = seed * 7919,
                TerrainSet = (MapTerrainSet)(seed % 4),
                Resources = (MapResourceLevel)(seed % 3),
                Features = seed % 2 == 0 ? MapFeatures.All : MapFeatures.Default,
            };
            GeneratedMap map = MapGenerator.Generate(options);
            Assert.Empty(MapGenValidator.Validate(map));
            Assert.Equal(type, map.ResolvedType);
            Assert.Equal(RandomMapNames.Side(size), map.Map.Width);
            Assert.Equal(players, map.Starts.Length);
            Assert.Equal(players, map.Stats.Count(MapObjectRole.Castle));
        }
    }

    [Theory]
    [InlineData(RandomMapType.Archipelagos)]
    [InlineData(RandomMapType.Island)]
    [InlineData(RandomMapType.Continental)]
    [InlineData(RandomMapType.Lakes)]
    [InlineData(RandomMapType.Dry)]
    public void Same_seed_gives_the_same_map_and_another_seed_another(RandomMapType type)
    {
        var options = new MapGenOptions { Type = type, Size = RandomMapSize.Large, Players = 4, Seed = 42, Features = MapFeatures.All };
        GeneratedMap a = MapGenerator.Generate(options);
        GeneratedMap b = MapGenerator.Generate(options);
        GeneratedMap c = MapGenerator.Generate(options with { Seed = 43 });
        Assert.Equal(a.Map.ContentHash, b.Map.ContentHash);
        Assert.True(a.Map.TerrainCorners.SequenceEqual(b.Map.TerrainCorners));
        Assert.True(a.Map.SetTerrainCorners.SequenceEqual(b.Map.SetTerrainCorners));
        Assert.True(a.Map.Objects.SequenceEqual(b.Map.Objects));
        Assert.NotEqual(a.Map.ContentHash, c.Map.ContentHash);
    }

    [Fact]
    public void The_match_stream_overload_draws_from_the_mapgen_stream()
    {
        var options = new MapGenOptions { Seed = 77 };
        var streams = new RngStreams(77, 2);
        GeneratedMap fromStream = MapGenerator.Generate(options, ref streams.MapGen);
        Assert.Equal(MapGenerator.Generate(options).Map.ContentHash, fromStream.Map.ContentHash);
        Assert.NotEqual(RngStreams.Derive(77, RngStreamKind.MapGen, 0).State, streams.MapGen.State);

        // The other streams are untouched.
        Assert.Equal(RngStreams.Derive(77, RngStreamKind.Combat, 0).State, streams.Combat.State);
    }

    [Theory]
    [InlineData(MapTerrainSet.Grass)]
    [InlineData(MapTerrainSet.Summer)]
    [InlineData(MapTerrainSet.Desert)]
    [InlineData(MapTerrainSet.Winter)]
    public void Every_tile_is_drawable_by_the_transition_rule(MapTerrainSet set)
    {
        TerrainProfile profile = TerrainProfile.For(set);
        foreach (RandomMapType type in Types)
        {
            MapData map = MapGenerator.Generate(new MapGenOptions { Type = type, TerrainSet = set, Size = RandomMapSize.Medium, Seed = 5 }).Map;
            Assert.Equal(profile.Id, map.TerrainSet);
            var used = new HashSet<int>();
            int stride = map.Width + 1;
            ReadOnlySpan<byte> c = map.SetTerrainCorners;
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    int i = (y * stride) + x;
                    Assert.True(TerrainComposer.IsDrawnByRule((c[i], c[i + 1], c[i + stride + 1], c[i + stride]), profile.HasTransition), $"{type} tile {x},{y}");
                    used.Add(c[i]);
                }
            }

            // The painted layers show up (Dry has no water).
            foreach (TerrainLayer layer in profile.Layers)
            {
                Assert.Contains(layer.Terrain, used);
            }
        }
    }

    [Fact]
    public void Profiles_are_consistent()
    {
        foreach (MapTerrainSet set in Enum.GetValues<MapTerrainSet>())
        {
            TerrainProfile p = TerrainProfile.For(set);
            Assert.True(p.HasTransition(0, 1) && p.HasTransition(1, 0));
            Assert.False(p.HasTransition(1, 1));
            Assert.Equal(-1, p.ParentOf(0));
            Assert.Equal(0, p.ParentOf(1));
            foreach (TerrainLayer layer in p.Layers)
            {
                Assert.True(p.HasTransition(layer.Parent, layer.Terrain));
                Assert.Equal(p.DepthOf(layer.Parent) + 1, p.DepthOf(layer.Terrain));
            }
        }
    }

    [Fact]
    public void Random_type_resolves_to_a_concrete_type_and_varies_with_the_seed()
    {
        var seen = new HashSet<RandomMapType>();
        for (ulong seed = 0; seed < 40; seed++)
        {
            GeneratedMap map = MapGenerator.Generate(new MapGenOptions { Type = RandomMapType.Random, Size = RandomMapSize.Tiny, Seed = seed });
            Assert.NotEqual(RandomMapType.Random, map.ResolvedType);
            seen.Add(map.ResolvedType);
        }

        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void Teammates_start_closer_to_each_other_than_to_enemies()
    {
        var options = new MapGenOptions { Size = RandomMapSize.Large, Players = 6, Teams = [0, 1, 0, 1, 0, 1], Seed = 9 };
        GeneratedMap map = MapGenerator.Generate(options);
        long friends = 0, enemies = 0;
        int friendPairs = 0, enemyPairs = 0;
        foreach (StartPosition a in map.Starts)
        {
            foreach (StartPosition b in map.Starts.Where(b => b.Player > a.Player))
            {
                int d = MapGenValidator.Distance(a.TileX, a.TileY, b.TileX, b.TileY);
                if (a.Team == b.Team)
                {
                    friends += d;
                    friendPairs++;
                }
                else
                {
                    enemies += d;
                    enemyPairs++;
                }
            }
        }

        Assert.True(friends * enemyPairs < enemies * friendPairs, $"teammates {friends / friendPairs}, enemies {enemies / enemyPairs} tiles apart on average");
    }

    [Fact]
    public void Civilizations_and_features_follow_the_options()
    {
        var options = new MapGenOptions
        {
            Players = 3,
            Civilizations = ["asia", "random", "arab"],
            Features = MapFeatures.None,
            Seed = 3,
        };
        GeneratedMap map = MapGenerator.Generate(options);
        Assert.Equal("asia", map.Starts[0].Civilization);
        Assert.Contains(map.Starts[1].Civilization, MapGenCatalog.Classic.Civilizations);
        Assert.Equal("arab", map.Starts[2].Civilization);
        Assert.Contains(map.Map.Objects, o => o.TypeKey == "asia.castle" && o.Owner == 0);
        Assert.Equal(0, map.Stats.Count(MapObjectRole.Cow));
        Assert.Equal(0, map.Stats.Count(MapObjectRole.GroundPatch));
        Assert.Equal(0, map.Stats.Count(MapObjectRole.Gateway));
        Assert.Equal(0, map.Map.CliffTiles.ToArray().Count(b => b != 0));

        GeneratedMap all = MapGenerator.Generate(options with { Features = MapFeatures.All, Size = RandomMapSize.Large });
        Assert.True(all.Stats.Count(MapObjectRole.Gateway) > 0);
        Assert.True(all.Stats.Count(MapObjectRole.Tower) > 0);
        Assert.True(all.Stats.Count(MapObjectRole.Item) > 0);
        Assert.True(all.Stats.CliffTiles > 0);
    }

    [Fact]
    public void More_map_resources_place_more()
    {
        var options = new MapGenOptions { Type = RandomMapType.Dry, Size = RandomMapSize.Large, Players = 2, Seed = 11 };
        GeneratedMap low = MapGenerator.Generate(options with { Resources = MapResourceLevel.Low });
        GeneratedMap high = MapGenerator.Generate(options with { Resources = MapResourceLevel.High });
        Assert.True(high.Stats.Count(MapObjectRole.Gold) > low.Stats.Count(MapObjectRole.Gold));
        Assert.True(high.Stats.Count(MapObjectRole.Tree) > low.Stats.Count(MapObjectRole.Tree));
    }

    [Fact]
    public void Archipelago_kingdoms_live_on_separate_islands_and_dry_maps_have_no_water()
    {
        GeneratedMap naval = MapGenerator.Generate(new MapGenOptions { Type = RandomMapType.Archipelagos, Size = RandomMapSize.Large, Players = 4, Seed = 2 });
        Assert.True(naval.IsNaval);
        Assert.True(naval.Stats.Count(MapObjectRole.FishPatch) >= 4);
        GeneratedMap dry = MapGenerator.Generate(new MapGenOptions { Type = RandomMapType.Dry, Size = RandomMapSize.Medium, Seed = 2 });
        Assert.Equal(0, dry.Stats.WaterTiles);
        Assert.DoesNotContain((byte)TerrainProfile.Water, dry.Map.SetTerrainCorners.ToArray());
    }

    [Fact]
    public void The_validator_catches_a_broken_map()
    {
        GeneratedMap map = MapGenerator.Generate(new MapGenOptions { Size = RandomMapSize.Tiny, Seed = 4 });
        MapData m = map.Map;
        List<MapObject> objects = [.. m.Objects.Where(o => o.TypeKey != "comm.res_gold")];
        var broken = new MapData(m.Name, m.Width, m.Height, m.TerrainCorners, m.HeightCorners, m.CliffTiles, objects, m.TerrainSet, m.SetTerrainCorners);
        IReadOnlyList<string> issues = MapGenValidator.Validate(broken, map.Starts, map.Options, map.ResolvedType);
        Assert.Contains(issues, i => i.Contains("gold", StringComparison.Ordinal));

        // Water in the middle of grass: the grass set has no water/grass transition.
        byte[] corners = m.SetTerrainCorners.ToArray();
        int stride = m.Width + 1;
        int hole = Enumerable.Range(stride + 1, corners.Length - (2 * stride) - 2)
            .First(i => new[] { -stride - 1, -stride, -stride + 1, -1, 0, 1, stride - 1, stride, stride + 1 }.All(d => corners[i + d] == 2));
        corners[hole] = 0;
        var badTerrain = new MapData(m.Name, m.Width, m.Height, m.TerrainCorners, m.HeightCorners, m.CliffTiles, m.Objects, m.TerrainSet, corners);
        Assert.Contains(MapGenValidator.Validate(badTerrain, map.Starts, map.Options, map.ResolvedType), i => i.Contains("drawable", StringComparison.Ordinal));
    }

    [Fact]
    public void Options_are_checked()
    {
        Assert.Throws<ArgumentException>(() => MapGenerator.Generate(new MapGenOptions { Players = 0 }));
        Assert.Throws<ArgumentException>(() => MapGenerator.Generate(new MapGenOptions { Size = RandomMapSize.Tiny, Players = 5 }));
        Assert.Throws<ArgumentException>(() => MapGenerator.Generate(new MapGenOptions { Players = 2, Teams = [0] }));
        Assert.Throws<ArgumentException>(() => MapGenerator.Generate(new MapGenOptions { Players = 1, Civilizations = ["elves"] }));
        Assert.Throws<ArgumentException>(() => MapGenerator.Generate(new MapGenOptions { MaxAttempts = 0 }));
    }

    [Fact]
    public void Names_parse_back()
    {
        foreach (RandomMapType t in Enum.GetValues<RandomMapType>())
        {
            Assert.True(RandomMapNames.TryParse(RandomMapNames.Name(t), out RandomMapType back) && back == t);
        }

        foreach (RandomMapSize s in Enum.GetValues<RandomMapSize>())
        {
            Assert.True(RandomMapNames.TryParse(RandomMapNames.Name(s), out RandomMapSize back) && back == s);
        }

        Assert.True(RandomMapNames.TryParse("512", out RandomMapSize rw) && rw == RandomMapSize.RealWorld);
        Assert.True(RandomMapNames.TryParse("snow", out MapTerrainSet snow) && snow == MapTerrainSet.Winter);
        Assert.False(RandomMapNames.TryParse("swamp", out MapTerrainSet _));
    }

    [Fact]
    public void A_map_without_a_terrain_set_hashes_as_before()
    {
        byte[] corners = new byte[9];
        var plain = new MapData("m", 2, 2, corners, [], []);
        Assert.Null(plain.TerrainSet);
        Assert.Equal(0, plain.SetTerrainCorners.Length);
        Assert.Equal(PlainMapHash, plain.ContentHash); // pinned: maps without a terrain set hash as before the change
        var withSet = new MapData("m", 2, 2, corners, [], [], null, "grass", corners);
        Assert.NotEqual(plain.ContentHash, withSet.ContentHash);
        Assert.Throws<ArgumentException>(() => new MapData("m", 2, 2, corners, [], [], null, "grass", []));
        Assert.Throws<ArgumentException>(() => new MapData("m", 2, 2, corners, [], [], null, null, corners));
    }

    [Fact]
    public void A_castle_is_no_passage_between_two_other_kingdoms()
    {
        // Review repro: the only land link between players 0 and 2 runs through player 1's castle.
        const int side = 64;
        byte[] land = new byte[side * side];
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                bool band = y is >= 20 and <= 44;
                bool on = x is >= 30 and <= 34 ? y is >= 30 and <= 34 : x is 29 or 35 ? y is >= 29 and <= 35 : band;
                land[(y * side) + x] = (byte)(on ? 1 : 0);
            }
        }

        int stride = side + 1;
        byte[] set = new byte[stride * stride];
        byte[] ids = new byte[stride * stride];
        for (int cy = 0; cy <= side; cy++)
        {
            for (int cx = 0; cx <= side; cx++)
            {
                bool water = true;
                for (int ty = cy - 1; ty <= cy; ty++)
                {
                    for (int tx = cx - 1; tx <= cx; tx++)
                    {
                        water &= !((uint)tx < side && (uint)ty < side && land[(ty * side) + tx] != 0);
                    }
                }

                set[(cy * stride) + cx] = (byte)(water ? 0 : 1);
                ids[(cy * stride) + cx] = (byte)(water ? 1 : 0);
            }
        }

        int[] centres = [10, 32, 54];
        var objects = centres.Select((c, p) => MapObject.AtTile("euro.castle", (byte)p, c - 2, 30)).ToList();
        var map = new MapData("repro", side, side, ids, [], [], objects, "grass", set);
        var options = new MapGenOptions { Type = RandomMapType.Dry, Size = RandomMapSize.Tiny, Players = 3, Resources = MapResourceLevel.Low, Features = MapFeatures.None };
        ImmutableArray<StartPosition> starts = [.. centres.Select((c, p) => new StartPosition(p, c, 32, p, "euro"))];
        IReadOnlyList<string> issues = MapGenValidator.Validate(map, starts, options, RandomMapType.Dry);
        Assert.Contains("player 2 cannot reach player 0 by land", issues);
        Assert.DoesNotContain("player 1 cannot reach player 0 by land", issues);
    }

    [Fact]
    public void Restricting_to_a_ruleset_keeps_the_footprint_envelope_of_all_civilizations()
    {
        TerrainTypeDefinition[] terrain = [new(0, "grass", LocomotorMask.Land | LocomotorMask.Air, false), new(1, "water", LocomotorMask.Ship | LocomotorMask.Air, true)];
        EntityTypeDefinition[] types =
        [
            new(0, "euro.castle", Locomotor.Foot, 0, 100, footprintWidth: 5, footprintHeight: 5),
            new(1, "asia.castle", Locomotor.Foot, 0, 100, footprintWidth: 4, footprintHeight: 6),
            new(2, "euro.peon", Locomotor.Foot, 100, 10),
            new(3, "asia.peon", Locomotor.Foot, 100, 10),
            new(4, "comm.tree", Locomotor.Foot, 0, 10, footprintWidth: 1, footprintHeight: 1),
        ];
        var ruleset = new Ruleset("two-civs", terrain, types);
        MapGenCatalog catalog = MapGenCatalog.Classic.RestrictTo(ruleset);
        Assert.Equal(["euro", "asia"], catalog.Civilizations.ToArray());
        Assert.Equal((5, 6), (catalog.Kind(MapObjectRole.Castle)!.Value.Width, catalog.Kind(MapObjectRole.Castle)!.Value.Height));
        Assert.Null(catalog.Kind(MapObjectRole.Gold));

        MapGenOptions options = new MapGenOptions { Players = 2, Civilizations = ["euro", "asia"], Seed = 8 }.BindTo(ruleset);
        Assert.Equal((0, 1), (options.LandTerrainId, options.WaterTerrainId));
        Assert.Empty(MapGenValidator.Validate(MapGenerator.Generate(options)));
    }

    [Fact]
    public void Archipelago_kingdoms_sharing_an_island_fail_validation()
    {
        GeneratedMap map = MapGenerator.Generate(new MapGenOptions { Type = RandomMapType.Archipelagos, Size = RandomMapSize.Medium, Players = 2, Seed = 6 });
        StartPosition a = map.Starts[0];
        ImmutableArray<StartPosition> same = [a, a with { Player = 1 }];
        Assert.Contains(MapGenValidator.Validate(map.Map, same, map.Options, map.ResolvedType), i => i.Contains("share an island", StringComparison.Ordinal));
    }

    [Fact]
    public void Crowded_maps_keep_each_kingdoms_starting_resources_nearest_its_own_castle()
    {
        for (ulong seed = 1; seed <= 6; seed++)
        {
            Assert.Empty(MapGenValidator.Validate(MapGenerator.Generate(new MapGenOptions { Size = RandomMapSize.Medium, Players = 8, Seed = seed, Type = RandomMapType.Dry })));
        }
    }

    [Fact]
    public void A_start_off_the_map_and_a_missing_catalog_are_reported()
    {
        GeneratedMap map = MapGenerator.Generate(new MapGenOptions { Size = RandomMapSize.Tiny, Seed = 4 });
        ImmutableArray<StartPosition> bad = [map.Starts[0], map.Starts[1] with { TileX = 500 }];
        Assert.Contains(MapGenValidator.Validate(map.Map, bad, map.Options, map.ResolvedType), i => i.Contains("off the map", StringComparison.Ordinal));
        Assert.Throws<ArgumentNullException>(() => MapGenerator.Generate(new MapGenOptions { Catalog = null! }));
    }
}
