using System.Text;
using Tzar2.Core.Hashing;

namespace Tzar2.Content.Compiled;

public sealed partial class Ruleset
{
    /// <summary>World-system constants: fog, diplomacy, trade, nature, day/night, victory, cheats (ADR 0100).</summary>
    public WorldSettings World { get; }

    private void AddWorld(StateHasher hasher)
    {
        WorldSettings w = World;
        hasher.Add(w.DefaultSightTiles).Add(w.NightSightBasisPoints).Add(w.MemoryRefreshTicks).Add(w.DiplomacyChangeIntervalMs)
            .Add(w.TributeIntervalMs).Add(w.MaxTributeAmount).Add(w.StockpileCap).Add(w.TradeProfitPercent)
            .Add(w.TradeMinDistanceTiles).Add(w.TradeDockMs).Add(w.ConvertRatio).Add(w.ExchangeUnitMs)
            .AddSpan<int>(w.GambleOdds.AsSpan()).Add(w.GambleHouseEdgeBasisPoints).Add(w.MaxGambleStake).Add(w.LoanStartAmount)
            .Add(w.LoanStepAmount).Add(w.LoanGrowDelayMs).Add(w.LoanInterestBasisPoints).Add(w.ClaimReachUnits).Add(w.ClaimIntervalMs)
            .Add(w.GuardRadiusTiles).Add(w.WanderRadiusTiles).Add(w.WanderIntervalMs).Add(w.DayLengthMs).Add(w.DayStartMinute)
            .Add(w.NightStartMinute).Add(w.NightEndMinute).Add(w.WeatherPeriodMs).Add(w.WeatherChanceBasisPoints)
            .Add(w.ScoreGatherDivisor).Add(w.ScoreKillDivisor).Add(w.ScoreBuildDivisor).Add(w.ScorePerTechnology)
            .Add(w.VictoryIntervalTicks).Add(Encoding.UTF8.GetBytes(w.DeathmatchStartingResources)).Add(w.CheatSmallResources)
            .Add(w.CheatLargeResources).Add(w.TavernRefreshMs).Add(w.MercenaryOffers).Add(w.HeroOffers).Add(w.MercenaryGroupMin)
            .Add(w.MercenaryGroupMax).Add(w.MercenaryCostPercent).Add(w.HeroCostPercent).Add(w.MercenaryLevel).Add(w.FameGroupBonus)
            .Add(w.FameLevelBonus).Add(w.FameCostPercent).Add(Encoding.UTF8.GetBytes(w.MercenariesTechnology))
            .Add(Encoding.UTF8.GetBytes(w.HeroesTechnology)).Add(Encoding.UTF8.GetBytes(w.FameTechnology))
            .Add(Encoding.UTF8.GetBytes(w.GamblingTechnology)).Add(Encoding.UTF8.GetBytes(w.LoanTechnology)).Add(w.Weather.Length);
        foreach (TerrainWeather t in w.Weather)
        {
            hasher.Add(Encoding.UTF8.GetBytes(t.TerrainSet)).Add((byte)t.Kind);
        }
    }

    private void ValidateWorldTraits(EntityTypeDefinition e, string where)
    {
        WorldTraits w = e.Traits.World;
        if (w.Trader is { } trader)
        {
            System.ArgumentOutOfRangeException.ThrowIfLessThan(trader.Capacity, 1, where);
            System.ArgumentOutOfRangeException.ThrowIfGreaterThan(trader.Capacity, 1_000_000, where);
            if (!e.IsMobile || trader.RequiredTechnology >= _technologies.Length || trader.RequiredTechnology < -1
                || trader.Access is not (DropOffAccess.Land or DropOffAccess.Water))
            {
                throw new System.ArgumentException($"{where}: a trader must be mobile and trade by land or by water.");
            }
        }

        if ((w.TradeDock != DropOffAccess.None || w.IsTavern) && !e.HasFootprint)
        {
            throw new System.ArgumentException($"{where}: only buildings can be docks or taverns.");
        }

        if ((byte)w.TradeDock > 3 || (w.Claim is { } claim && claim > ClaimBy.Military))
        {
            throw new System.ArgumentException($"{where}: invalid dock or claim.");
        }

        if ((w.Wanders || w.Grazes) && !e.IsMobile)
        {
            throw new System.ArgumentException($"{where}: only units wander.");
        }

        System.ArgumentOutOfRangeException.ThrowIfNegative(w.DetectUnits, where);
        System.ArgumentOutOfRangeException.ThrowIfGreaterThan(w.DetectUnits, 64 * 2048, where);
    }

    private static void AddWorldTraits(StateHasher hasher, WorldTraits w)
    {
        hasher.Add(w.IsMilitary).Add((byte)w.TradeDock).Add(w.IsTavern).Add(w.Claim.HasValue).Add((byte)(w.Claim ?? 0))
            .Add(w.Wanders).Add(w.Guards).Add(w.IsAmbient).Add(w.DetectUnits).Add(w.Trader is not null).Add(w.Grazes);
        if (w.Trader is { } t)
        {
            hasher.Add(t.Capacity).Add((byte)t.Access).Add(t.RequiredTechnology);
        }
    }
}
