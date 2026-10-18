using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Rules;

/// <summary>Reads the <c>world:</c> section (constants of the world systems, ADR 0100) into <see cref="WorldSettings"/>.</summary>
internal static class WorldReader
{
    private static readonly string[] KnownFields =
    [
        "defaultSightTiles", "nightSightPercent", "memoryRefreshTicks", "diplomacyChangeIntervalMs", "tributeIntervalMs",
        "maxTributeAmount", "stockpileCap", "tradeProfitPerTileBasisPoints", "tradeMinDistanceTiles", "tradeMaxProfitPercent",
        "tradeDockMs", "convertRatio", "gambleOdds", "gambleHouseEdgePercent", "maxGambleStake", "loanStartAmount", "loanStepAmount",
        "loanGrowDelayMs", "loanInterestPercent", "claimReachPx", "claimIntervalMs", "guardRadiusTiles", "wanderRadiusTiles",
        "wanderIntervalMs", "dayLengthMs", "dayStartMinute", "nightStartMinute", "nightEndMinute", "weatherPeriodMs",
        "weatherChancePercent", "weather", "scoreGatherDivisor", "scoreKillDivisor", "scoreBuildDivisor", "scorePerTechnology",
        "victoryIntervalTicks", "deathmatchStartingResources", "cheatSmallResources", "cheatLargeResources",
    ];

    public static WorldSettings Build(RulesetCompiler.SectionSet settings, GameplaySettings gameplay)
    {
        if (settings.Mapping("world") is { } map)
        {
            foreach (KeyValuePair<string, YamlNode> f in map.Entries)
            {
                if (Array.IndexOf(KnownFields, f.Key) < 0)
                {
                    throw new RulesException(settings.FieldSource("world", f.Key), f.Value.Line, $"world.{f.Key}: unknown setting");
                }
            }
        }

        WorldSettings d = WorldSettings.Default;
        string deathmatch = settings.Scalar("world", "deathmatchStartingResources") ?? string.Empty;
        if (deathmatch.Length > 0 && !HasLevel(gameplay, deathmatch))
        {
            throw Error(settings, "deathmatchStartingResources", $"no starting-resource level '{deathmatch}'");
        }

        return d with
        {
            DefaultSightTiles = Range(settings, "defaultSightTiles", d.DefaultSightTiles, 0, 64),
            NightSightBasisPoints = Range(settings, "nightSightPercent", d.NightSightBasisPoints / 100, 0, 200) * 100,
            MemoryRefreshTicks = Range(settings, "memoryRefreshTicks", d.MemoryRefreshTicks, 1, 64),
            DiplomacyChangeIntervalMs = Range(settings, "diplomacyChangeIntervalMs", d.DiplomacyChangeIntervalMs, 0, 3_600_000),
            TributeIntervalMs = Range(settings, "tributeIntervalMs", d.TributeIntervalMs, 0, 3_600_000),
            MaxTributeAmount = Range(settings, "maxTributeAmount", d.MaxTributeAmount, 1, int.MaxValue),
            StockpileCap = Range(settings, "stockpileCap", d.StockpileCap, 1, int.MaxValue),
            TradeProfitPerTileBasisPoints = Range(settings, "tradeProfitPerTileBasisPoints", d.TradeProfitPerTileBasisPoints, 0, 100_000),
            TradeMinDistanceTiles = Range(settings, "tradeMinDistanceTiles", d.TradeMinDistanceTiles, 0, 1024),
            TradeMaxProfitBasisPoints = Range(settings, "tradeMaxProfitPercent", d.TradeMaxProfitBasisPoints / 100, 0, 10_000) * 100,
            TradeDockMs = Range(settings, "tradeDockMs", d.TradeDockMs, 0, 600_000),
            ConvertRatio = Range(settings, "convertRatio", d.ConvertRatio, 1, 1000),
            GambleOdds = Odds(settings) ?? d.GambleOdds,
            GambleHouseEdgeBasisPoints = Range(settings, "gambleHouseEdgePercent", d.GambleHouseEdgeBasisPoints / 100, 0, 100) * 100,
            MaxGambleStake = Range(settings, "maxGambleStake", d.MaxGambleStake, 1, 10_000_000),
            LoanStartAmount = Range(settings, "loanStartAmount", d.LoanStartAmount, 0, 10_000_000),
            LoanStepAmount = Range(settings, "loanStepAmount", d.LoanStepAmount, 0, 10_000_000),
            LoanGrowDelayMs = Range(settings, "loanGrowDelayMs", d.LoanGrowDelayMs, 0, int.MaxValue),
            LoanInterestBasisPoints = Range(settings, "loanInterestPercent", d.LoanInterestBasisPoints / 100, 0, 1000) * 100,
            ClaimReachUnits = Range(settings, "claimReachPx", d.ClaimReachUnits / 64, 0, 256) * 64,
            ClaimIntervalMs = Range(settings, "claimIntervalMs", d.ClaimIntervalMs, 1, 60_000),
            GuardRadiusTiles = Range(settings, "guardRadiusTiles", d.GuardRadiusTiles, 0, 128),
            WanderRadiusTiles = Range(settings, "wanderRadiusTiles", d.WanderRadiusTiles, 0, 64),
            WanderIntervalMs = Range(settings, "wanderIntervalMs", d.WanderIntervalMs, 1, int.MaxValue),
            DayLengthMs = Range(settings, "dayLengthMs", d.DayLengthMs, 0, 86_400_000),
            DayStartMinute = Range(settings, "dayStartMinute", d.DayStartMinute, 0, 1439),
            NightStartMinute = Range(settings, "nightStartMinute", d.NightStartMinute, 0, 1439),
            NightEndMinute = Range(settings, "nightEndMinute", d.NightEndMinute, 0, 1439),
            WeatherPeriodMs = Range(settings, "weatherPeriodMs", d.WeatherPeriodMs, 1000, int.MaxValue),
            WeatherChanceBasisPoints = Range(settings, "weatherChancePercent", d.WeatherChanceBasisPoints / 100, 0, 100) * 100,
            Weather = Weather(settings) ?? d.Weather,
            ScoreGatherDivisor = Range(settings, "scoreGatherDivisor", d.ScoreGatherDivisor, 1, 1_000_000),
            ScoreKillDivisor = Range(settings, "scoreKillDivisor", d.ScoreKillDivisor, 1, 1_000_000),
            ScoreBuildDivisor = Range(settings, "scoreBuildDivisor", d.ScoreBuildDivisor, 1, 1_000_000),
            ScorePerTechnology = Range(settings, "scorePerTechnology", d.ScorePerTechnology, 0, 1_000_000),
            VictoryIntervalTicks = Range(settings, "victoryIntervalTicks", d.VictoryIntervalTicks, 1, 1200),
            DeathmatchStartingResources = deathmatch,
            CheatSmallResources = Range(settings, "cheatSmallResources", d.CheatSmallResources, 0, 10_000_000),
            CheatLargeResources = Range(settings, "cheatLargeResources", d.CheatLargeResources, 0, 10_000_000),
        };
    }

    private static bool HasLevel(GameplaySettings gameplay, string key)
    {
        foreach (StartingResources level in gameplay.StartingResources)
        {
            if (string.Equals(level.Key, key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ImmutableArray<int>? Odds(RulesetCompiler.SectionSet settings)
    {
        if (settings.Field("world", "gambleOdds") is not { } node)
        {
            return null;
        }

        string source = settings.FieldSource("world", "gambleOdds");
        var odds = ImmutableArray.CreateBuilder<int>();
        foreach (YamlNode item in RulesetCompiler.Seq(node, source, "world.gambleOdds").Items)
        {
            int value = RulesetCompiler.Int(item, source, "world.gambleOdds");
            odds.Add(value is >= 2 and <= 100 ? value : throw new RulesException(source, item.Line, "world.gambleOdds: each odds must be 2..100"));
        }

        return odds.ToImmutable();
    }

    // weather: { grass: rain, snow: snow, desert: clear }
    private static ImmutableArray<TerrainWeather>? Weather(RulesetCompiler.SectionSet settings)
    {
        if (settings.Field("world", "weather") is not { } node)
        {
            return null;
        }

        string source = settings.FieldSource("world", "weather");
        var list = ImmutableArray.CreateBuilder<TerrainWeather>();
        foreach (KeyValuePair<string, YamlNode> e in RulesetCompiler.Map(node, source, "world.weather").Entries)
        {
            WeatherKind kind = RulesetCompiler.Str(e.Value, source, $"world.weather.{e.Key}") switch
            {
                "clear" => WeatherKind.Clear,
                "rain" => WeatherKind.Rain,
                "snow" => WeatherKind.Snow,
                { } other => throw new RulesException(source, e.Value.Line, $"world.weather.{e.Key}: '{other}' is not clear/rain/snow"),
            };
            list.Add(new TerrainWeather(e.Key, kind));
        }

        return list.ToImmutable();
    }

    private static int Range(RulesetCompiler.SectionSet settings, string field, int fallback, int min, int max)
    {
        int value = settings.Int("world", field) ?? fallback;
        return value < min || value > max ? throw Error(settings, field, $"{value} is outside {min}..{max}") : value;
    }

    private static RulesException Error(RulesetCompiler.SectionSet settings, string field, string message)
        => new(settings.FieldSource("world", field), settings.Field("world", field)?.Line ?? 0, $"world.{field}: {message}");
}
