using System;
using System.Collections.Immutable;

namespace Tzar2.Content.Compiled;

/// <summary>Weather a terrain set can have (GAME-16; the <c>rain</c>/<c>snow</c> flags of <c>&lt;SET&gt;.TERRAIN.INI</c>).</summary>
public enum WeatherKind : byte
{
    /// <summary>No precipitation.</summary>
    Clear = 0,

    /// <summary>Rain.</summary>
    Rain = 1,

    /// <summary>Snow.</summary>
    Snow = 2,
}

/// <summary>The precipitation of one terrain set (e.g. <c>grass</c> → rain).</summary>
/// <param name="TerrainSet">Terrain-set id (case-insensitive match with <c>MapData.TerrainSet</c>).</param>
/// <param name="Kind">What falls when the weather is on.</param>
public readonly record struct TerrainWeather(string TerrainSet, WeatherKind Kind);

/// <summary>
/// The ruleset-wide constants of the world systems (M3 stream F, ADRs 0100-0106): fog and visibility (GAME-12),
/// diplomacy (GAME-13), trade and tavern (GAME-14), gateways and nature (GAME-15), day/night and weather (GAME-16),
/// victory and scoring (GAME-17) and cheats (GAME-19). The YAML section is <c>world:</c>. Every value without a source in
/// the original data is UNVERIFIED (docs/mechanics/m3-placeholders.md, stream F).
/// </summary>
public sealed record WorldSettings
{
    /// <summary>Most tavern offers per kingdom and period (a bit each in the kingdom state).</summary>
    public const int MaxTavernOffers = 8;

    /// <summary>Defaults (the classic YAML states every value explicitly).</summary>
    public static WorldSettings Default { get; } = new();

    /// <summary>Sight of a player-owned unit or building whose type has none, in tiles (GAME-12).</summary>
    public int DefaultSightTiles { get; init; } = 2;

    /// <summary>Sight at night as a share of the day sight, in basis points (UNK-09; 10,000 = no effect).</summary>
    public int NightSightBasisPoints { get; init; } = 10_000;

    /// <summary>Ticks between two refreshes of a structure's last-seen record (staggered by entity index).</summary>
    public int MemoryRefreshTicks { get; init; } = 4;

    /// <summary>Least time between two stance or sharing changes of one player towards another (anti flip-flop).</summary>
    public int DiplomacyChangeIntervalMs { get; init; } = 10_000;

    /// <summary>Least time between two tributes of one player.</summary>
    public int TributeIntervalMs { get; init; } = 1_000;

    /// <summary>Most of one resource a single tribute may carry.</summary>
    public int MaxTributeAmount { get; init; } = 100_000;

    /// <summary>Most of one resource a stockpile can hold; incomes above it are lost (overflow guard).</summary>
    public int StockpileCap { get; init; } = 1_000_000_000;

    /// <summary>Trade profit per tile of distance between the docks, in basis points of the carried amount (UNK-28).</summary>
    public int TradeProfitPerTileBasisPoints { get; init; } = 100;

    /// <summary>Docks closer than this (in tiles) give no profit (anti trade loop).</summary>
    public int TradeMinDistanceTiles { get; init; } = 8;

    /// <summary>Most profit of one delivery, in basis points of the carried amount.</summary>
    public int TradeMaxProfitBasisPoints { get; init; } = 30_000;

    /// <summary>Time a trader spends at a dock to unload and load.</summary>
    public int TradeDockMs { get; init; } = 2_000;

    /// <summary>Tavern conversion: give this many units for one unit of another resource (manual ch. 5 §7: 1/10).</summary>
    public int ConvertRatio { get; init; } = 10;

    /// <summary>Gambling odds a player may choose (pay N, win N × odds; UNK-28).</summary>
    public ImmutableArray<int> GambleOdds { get; init; } = [2, 3, 5, 10];

    /// <summary>House edge of gambling in basis points: the win chance is (1 − edge) / odds (UNK-28).</summary>
    public int GambleHouseEdgeBasisPoints { get; init; } = 1_000;

    /// <summary>Most one bet may stake.</summary>
    public int MaxGambleStake { get; init; } = 10_000;

    /// <summary>The first loan a player can take (manual ch. 5 §10: 1,000).</summary>
    public int LoanStartAmount { get; init; } = 1_000;

    /// <summary>How much the loan limit grows after a repayment (manual: 1,000 → 1,200).</summary>
    public int LoanStepAmount { get; init; } = 200;

    /// <summary>Time after a repayment until the higher limit is offered (manual: "after some time"; UNK-28).</summary>
    public int LoanGrowDelayMs { get; init; } = 120_000;

    /// <summary>Extra to pay back on top of the loan, in basis points (UNK-28).</summary>
    public int LoanInterestBasisPoints { get; init; } = 0;

    /// <summary>Reach of a touch that claims a gateway or a wild animal, edge to edge.</summary>
    public int ClaimReachUnits { get; init; } = 1024;

    /// <summary>Ticks between two claim scans (staggered).</summary>
    public int ClaimIntervalMs { get; init; } = 500;

    /// <summary>A Nature unit with a guard area returns home when it gets further than this (tiles).</summary>
    public int GuardRadiusTiles { get; init; } = 10;

    /// <summary>Wandering animals move at most this far from home (tiles).</summary>
    public int WanderRadiusTiles { get; init; } = 6;

    /// <summary>Mean time between two wander moves of an idle animal.</summary>
    public int WanderIntervalMs { get; init; } = 8_000;

    /// <summary>Length of a day-night cycle (UNK-32; 0 = no cycle).</summary>
    public int DayLengthMs { get; init; } = 720_000;

    /// <summary>Time of day at the start of a match, in minutes of a 24-hour clock.</summary>
    public int DayStartMinute { get; init; } = 600;

    /// <summary>Night begins at this minute of the 24-hour clock.</summary>
    public int NightStartMinute { get; init; } = 1_140;

    /// <summary>Night ends at this minute.</summary>
    public int NightEndMinute { get; init; } = 360;

    /// <summary>Length of a weather period: each period has weather or not (UNK-09).</summary>
    public int WeatherPeriodMs { get; init; } = 180_000;

    /// <summary>Chance that a period has weather, in basis points.</summary>
    public int WeatherChanceBasisPoints { get; init; } = 2_500;

    /// <summary>Precipitation per terrain set (unlisted sets: clear).</summary>
    public ImmutableArray<TerrainWeather> Weather { get; init; } = [];

    /// <summary>Score per this many units of resources gathered (UNK-29).</summary>
    public int ScoreGatherDivisor { get; init; } = 10;

    /// <summary>Score per this many resources of value of killed enemy units and destroyed buildings (UNK-29).</summary>
    public int ScoreKillDivisor { get; init; } = 10;

    /// <summary>Score per this many resources of value of completed buildings and trained units (UNK-29).</summary>
    public int ScoreBuildDivisor { get; init; } = 10;

    /// <summary>Score per researched technology (UNK-29).</summary>
    public int ScorePerTechnology { get; init; } = 100;

    /// <summary>Ticks between two victory evaluations.</summary>
    public int VictoryIntervalTicks { get; init; } = 20;

    /// <summary>The starting-resource level of a Deathmatch (a key of <c>gameplay.startingResources</c>; empty = the match's level).</summary>
    public string DeathmatchStartingResources { get; init; } = string.Empty;

    /// <summary>Resources the <c>hmdvaleva</c> cheat gives (each resource).</summary>
    public int CheatSmallResources { get; init; } = 2_000;

    /// <summary>Resources the <c>hmpetleva</c> cheat gives (each resource).</summary>
    public int CheatLargeResources { get; init; } = 5_000;

    /// <summary>The tavern's offers change every this long (one set per kingdom and period; UNK-28).</summary>
    public int TavernRefreshMs { get; init; } = 120_000;

    /// <summary>Mercenary groups a tavern offers per period (Mercenaries).</summary>
    public int MercenaryOffers { get; init; } = 3;

    /// <summary>Heroes a tavern offers per period (Heroes' Quest).</summary>
    public int HeroOffers { get; init; } = 1;

    /// <summary>Fewest soldiers in a mercenary group ("small groups of soldiers", manual ch. 6).</summary>
    public int MercenaryGroupMin { get; init; } = 3;

    /// <summary>Most soldiers in a mercenary group.</summary>
    public int MercenaryGroupMax { get; init; } = 5;

    /// <summary>A mercenary group costs its soldiers' training cost times this, in percent.</summary>
    public int MercenaryCostPercent { get; init; } = 150;

    /// <summary>A hero costs its type's training cost times this, in percent.</summary>
    public int HeroCostPercent { get; init; } = 600;

    /// <summary>The experience level hired mercenaries start with.</summary>
    public int MercenaryLevel { get; init; } = 1;

    /// <summary>Fame: more soldiers per group.</summary>
    public int FameGroupBonus { get; init; } = 1;

    /// <summary>Fame: more experienced mercenaries (levels).</summary>
    public int FameLevelBonus { get; init; } = 2;

    /// <summary>Fame: cheaper mercenaries (percent of the price kept).</summary>
    public int FameCostPercent { get; init; } = 75;

    /// <summary>The technology that opens the tavern's mercenaries (empty: none needed).</summary>
    public string MercenariesTechnology { get; init; } = "te_mercenaries";

    /// <summary>The technology that opens the tavern's heroes (empty: none needed).</summary>
    public string HeroesTechnology { get; init; } = "te_heroesquest";

    /// <summary>The technology that gives more, better and cheaper mercenaries (empty: none).</summary>
    public string FameTechnology { get; init; } = "te_fame";

    /// <summary>The technology gambling needs (empty: none needed).</summary>
    public string GamblingTechnology { get; init; } = "te_gambling";

    /// <summary>The technology loans need (empty: none needed).</summary>
    public string LoanTechnology { get; init; } = "te_usury";

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(DefaultSightTiles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DefaultSightTiles, 64);
        ArgumentOutOfRangeException.ThrowIfNegative(NightSightBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(NightSightBasisPoints, 20_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MemoryRefreshTicks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MemoryRefreshTicks, 64);
        ArgumentOutOfRangeException.ThrowIfNegative(DiplomacyChangeIntervalMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DiplomacyChangeIntervalMs, 3_600_000);
        ArgumentOutOfRangeException.ThrowIfNegative(TributeIntervalMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TributeIntervalMs, 3_600_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTributeAmount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(StockpileCap, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(TradeProfitPerTileBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TradeProfitPerTileBasisPoints, 100_000);
        ArgumentOutOfRangeException.ThrowIfNegative(TradeMinDistanceTiles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TradeMinDistanceTiles, 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(TradeMaxProfitBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TradeMaxProfitBasisPoints, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfNegative(TradeDockMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TradeDockMs, 600_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(ConvertRatio, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ConvertRatio, 1_000);
        if (GambleOdds.IsDefault)
        {
            throw new ArgumentException("Gamble odds must not be default.", nameof(GambleOdds));
        }

        foreach (int odds in GambleOdds)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(odds, 2);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(odds, 100);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(GambleHouseEdgeBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(GambleHouseEdgeBasisPoints, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxGambleStake, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxGambleStake, 10_000_000);
        ArgumentOutOfRangeException.ThrowIfNegative(LoanStartAmount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(LoanStartAmount, 10_000_000);
        ArgumentOutOfRangeException.ThrowIfNegative(LoanStepAmount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(LoanStepAmount, 10_000_000);
        ArgumentOutOfRangeException.ThrowIfNegative(LoanGrowDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(LoanInterestBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(LoanInterestBasisPoints, 100_000);
        ArgumentOutOfRangeException.ThrowIfNegative(ClaimReachUnits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ClaimReachUnits, 8 * 2048);
        ArgumentOutOfRangeException.ThrowIfLessThan(ClaimIntervalMs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ClaimIntervalMs, 60_000);
        ArgumentOutOfRangeException.ThrowIfNegative(GuardRadiusTiles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(GuardRadiusTiles, 128);
        ArgumentOutOfRangeException.ThrowIfNegative(WanderRadiusTiles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(WanderRadiusTiles, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(WanderIntervalMs, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(DayLengthMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DayLengthMs, 86_400_000);
        foreach (int minute in new[] { DayStartMinute, NightStartMinute, NightEndMinute })
        {
            ArgumentOutOfRangeException.ThrowIfNegative(minute);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minute, 1_440);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(WeatherPeriodMs, 1_000);
        ArgumentOutOfRangeException.ThrowIfNegative(WeatherChanceBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(WeatherChanceBasisPoints, 10_000);
        if (Weather.IsDefault)
        {
            throw new ArgumentException("Weather must not be default.", nameof(Weather));
        }

        foreach (TerrainWeather w in Weather)
        {
            ArgumentException.ThrowIfNullOrEmpty(w.TerrainSet);
            if (w.Kind > WeatherKind.Snow)
            {
                throw new ArgumentOutOfRangeException(nameof(Weather));
            }
        }

        foreach (int divisor in new[] { ScoreGatherDivisor, ScoreKillDivisor, ScoreBuildDivisor })
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(divisor, 1);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(ScorePerTechnology);
        ArgumentOutOfRangeException.ThrowIfLessThan(VictoryIntervalTicks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(VictoryIntervalTicks, 1_200);
        ArgumentNullException.ThrowIfNull(DeathmatchStartingResources);
        ArgumentOutOfRangeException.ThrowIfNegative(CheatSmallResources);
        ArgumentOutOfRangeException.ThrowIfNegative(CheatLargeResources);
        ArgumentOutOfRangeException.ThrowIfLessThan(TavernRefreshMs, 1_000);
        ArgumentOutOfRangeException.ThrowIfNegative(MercenaryOffers);
        ArgumentOutOfRangeException.ThrowIfNegative(HeroOffers);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MercenaryOffers + HeroOffers, MaxTavernOffers);
        ArgumentOutOfRangeException.ThrowIfLessThan(MercenaryGroupMin, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MercenaryGroupMax, MercenaryGroupMin);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MercenaryGroupMax + FameGroupBonus, 32);
        ArgumentOutOfRangeException.ThrowIfNegative(FameGroupBonus);
        ArgumentOutOfRangeException.ThrowIfNegative(MercenaryCostPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MercenaryCostPercent, 100_000);
        ArgumentOutOfRangeException.ThrowIfNegative(HeroCostPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HeroCostPercent, 100_000);
        ArgumentOutOfRangeException.ThrowIfNegative(MercenaryLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MercenaryLevel, 1_000);
        ArgumentOutOfRangeException.ThrowIfNegative(FameLevelBonus);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FameLevelBonus, 1_000);
        ArgumentOutOfRangeException.ThrowIfNegative(FameCostPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FameCostPercent, 1_000);
    }

    /// <summary>The precipitation of a terrain set (clear when unlisted or null).</summary>
    public WeatherKind WeatherOf(string? terrainSet)
    {
        foreach (TerrainWeather w in Weather)
        {
            if (string.Equals(w.TerrainSet, terrainSet ?? "grass", StringComparison.OrdinalIgnoreCase))
            {
                return w.Kind;
            }
        }

        return WeatherKind.Clear;
    }
}

/// <summary>A unit that trades between two docks (GAME-14: caravans, trading ships).</summary>
/// <param name="Capacity">Most goods carried per trip.</param>
/// <param name="Access">The docks it uses: land (markets) or water (harbors).</param>
/// <param name="RequiredTechnology">Technology id needed to trade (-1 = none; Trade Routes).</param>
public sealed record TraderDefinition(int Capacity, DropOffAccess Access, int RequiredTechnology);

/// <summary>Who claims a Nature-owned entity by touching it (GAME-15).</summary>
public enum ClaimBy : byte
{
    /// <summary>Any unit of a kingdom (wild cows).</summary>
    AnyUnit = 0,

    /// <summary>A military unit (magic gateways: manual ch. 5 §4.4.2).</summary>
    Military = 1,
}

/// <summary>The world traits of an entity type (ADRs 0101-0104); <see cref="None"/> for most types.</summary>
public sealed record WorldTraits
{
    /// <summary>No world traits.</summary>
    public static WorldTraits None { get; } = new();

    /// <summary>Whether the type is a military unit: a unit with a weapon that is neither a worker nor a trader (GAME-13 neutral stance, GAME-15 gateways).</summary>
    public bool IsMilitary { get; init; }

    /// <summary>Trading unit, or null.</summary>
    public TraderDefinition? Trader { get; init; }

    /// <summary>Which traders this building docks (markets: land, harbors: water; None = not a dock).</summary>
    public DropOffAccess TradeDock { get; init; }

    /// <summary>Whether the building is a tavern (conversion, gambling, loans).</summary>
    public bool IsTavern { get; init; }

    /// <summary>Whether a Nature-owned entity of this type is claimed by touching it, and by whom (null = not claimable).</summary>
    public ClaimBy? Claim { get; init; }

    /// <summary>Whether a Nature-owned unit of this type wanders around its home.</summary>
    public bool Wanders { get; init; }

    /// <summary>Whether a Nature-owned unit of this type guards its home (returns when lured away).</summary>
    public bool Guards { get; init; }

    /// <summary>Whether the type is ambient life (birds): never a combat target, never counted for victory.</summary>
    public bool IsAmbient { get; init; }

    /// <summary>Detection radius against cloaked units (spies, ninjas), in world units (0 = no detector).</summary>
    public int DetectUnits { get; init; }
}
