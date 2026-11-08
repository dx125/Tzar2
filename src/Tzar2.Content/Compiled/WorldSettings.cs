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
    public const int MaxTavernOffers = 8; // each of a tavern's two lists (TRADE-009: 0x4d2030)

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

    /// <summary>
    /// The trade profit across the whole map, in percent of the load (TRADE-001; ReTzar's reading of the original,
    /// 0x4e7c98): a delivery earns load x tiles between the docks x this / (map side x 100).
    /// </summary>
    public int TradeProfitPercent { get; init; } = 40;

    /// <summary>Docks closer than this (in tiles, the larger of the two axes) give no profit.</summary>
    public int TradeMinDistanceTiles { get; init; } = 12;

    /// <summary>Time a trader spends at a dock to unload and load.</summary>
    public int TradeDockMs { get; init; } = 2_000;

    /// <summary>Tavern conversion: give this many units for one unit of another resource (manual ch. 5 §7: 1/10).</summary>
    public int ConvertRatio { get; init; } = 10;

    /// <summary>
    /// Tavern conversion: the exchange is paid at once and received a unit every this many milliseconds (TRADE-011;
    /// ReTzar's reading of the original, 0x4d3104: 300); 0 gives it all at once.
    /// </summary>
    public int ExchangeUnitMs { get; init; }

    /// <summary>
    /// TRADE-012 (ReTzar's reading of the original, D_GAMBLE 0x4a3fee): what a win pays, in stakes, for each choice of the
    /// dialog (1:1, 2:1, 5:1, 10:1); the stake is kept on a win and lost otherwise.
    /// </summary>
    public ImmutableArray<int> GambleOdds { get; init; } = [1, 2, 5, 10];

    /// <summary>TRADE-012: the chance of a win for each choice, in percent (a roll of 1 to 100 at most this).</summary>
    public ImmutableArray<int> GambleChances { get; init; } = [50, 33, 20, 10];

    /// <summary>TRADE-012: the least stake, and the step stakes go by.</summary>
    public int GambleStakeStep { get; init; } = 50;

    /// <summary>Most one bet may stake.</summary>
    public int MaxGambleStake { get; init; } = 10_000;

    /// <summary>The first loan a player can take (manual ch. 5 §10: 1,000).</summary>
    public int LoanStartAmount { get; init; } = 1_000;

    /// <summary>How much the loan limit grows after a repayment (manual: 1,000 → 1,200).</summary>
    public int LoanStepAmount { get; init; } = 200;

    /// <summary>TRADE-007 (ReTzar's reading of the original, 0x4d24e9): time after a repayment until the higher limit is offered.</summary>
    public int LoanGrowDelayMs { get; init; } = 60_000;

    /// <summary>TRADE-007 (0x4d2d6a): what is paid back on top of the loan.</summary>
    public int LoanInterestAmount { get; init; } = 200;

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

    /// <summary>DIP-011 (ReTzar's reading of the original, 0x504cbe): the raw score a kingdom makes for each building it destroys.</summary>
    public int ScoreForBuilding { get; init; } = 500;

    /// <summary>DIP-011: the raw score for anything else it kills.</summary>
    public int ScoreForKill { get; init; } = 200;

    /// <summary>
    /// DIP-011 (0x443430 type 4, 0x4b0076): the resources (keys) whose mines score what each load gives as it is taken - gold
    /// and stone.
    /// </summary>
    public ImmutableArray<string> ScoreMinedResources { get; init; } = [];

    /// <summary>Ticks between two victory evaluations.</summary>
    public int VictoryIntervalTicks { get; init; } = 20;

    /// <summary>The starting-resource level of a Deathmatch (a key of <c>gameplay.startingResources</c>; empty = the match's level).</summary>
    public string DeathmatchStartingResources { get; init; } = string.Empty;

    /// <summary>Resources the <c>hmdvaleva</c> cheat gives (each resource; the original's 10,000, HUD-036).</summary>
    public int CheatSmallResources { get; init; } = 10_000;

    /// <summary>Resources the <c>hmpetleva</c> cheat gives (each resource; the original sends hmdvaleva five times).</summary>
    public int CheatLargeResources { get; init; } = 50_000;

    /// <summary>TRADE-009 (ReTzar's reading of the original, 0x4d1e5f): a tavern's first offers come this long after it stands.</summary>
    public int TavernFirstMs { get; init; } = 1_000;

    /// <summary>TRADE-009 (0x4d24c0): then each list gains an offer every this long.</summary>
    public int TavernEveryMs { get; init; } = 60_000;

    /// <summary>TRADE-009 (0x4d2567): the same in a Deathmatch.</summary>
    public int TavernDeathmatchEveryMs { get; init; } = 360_000;

    /// <summary>TRADE-009 (0x4d2030): fewest soldiers in a mercenary offer.</summary>
    public int MercenaryGroupMin { get; init; } = 8;

    /// <summary>TRADE-009: most soldiers in a mercenary offer.</summary>
    public int MercenaryGroupMax { get; init; } = 12;

    /// <summary>TRADE-009: the experience level of mercenaries, before Fame.</summary>
    public int MercenaryLevel { get; init; } = 0;

    /// <summary>TRADE-009: the lowest level of a hero on offer, before Fame.</summary>
    public int HeroLevelMin { get; init; } = 10;

    /// <summary>TRADE-009: the highest.</summary>
    public int HeroLevelMax { get; init; } = 30;

    /// <summary>TRADE-009 (0x4d22b6): what Fame adds to the level of every offer.</summary>
    public int FameLevelBonus { get; init; } = 4;

    /// <summary>TRADE-009 (0x4d23b0): an offer costs its type's cost for the kingdom times its count times this, in percent.</summary>
    public int HirePricePercent { get; init; } = 160;

    /// <summary>TRADE-009: and this much gold more a level.</summary>
    public int HireGoldPerLevel { get; init; } = 30;

    /// <summary>TRADE-009: and this much more a level of each other resource the type costs.</summary>
    public int HireOtherPerLevel { get; init; } = 15;

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

    /// <summary>
    /// Whether a map's ground objects and ruins close the cells under their picture (WORLD-003, ADR 0281). The original's
    /// do; it waits for the original's diagonal step past corners, without which a campaign mission cannot be finished.
    /// </summary>
    public bool DecorationsObstruct { get; init; }

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
        ArgumentOutOfRangeException.ThrowIfNegative(TradeProfitPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TradeProfitPercent, 100_000);
        ArgumentOutOfRangeException.ThrowIfNegative(TradeMinDistanceTiles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TradeMinDistanceTiles, 1024);
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
            ArgumentOutOfRangeException.ThrowIfLessThan(odds, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(odds, 100);
        }

        if (GambleChances.IsDefault || GambleChances.Length != GambleOdds.Length)
        {
            throw new ArgumentException("Every gamble odds needs its chance.", nameof(GambleChances));
        }

        foreach (int chance in GambleChances)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(chance, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(chance, 100);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(GambleStakeStep, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(ExchangeUnitMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ExchangeUnitMs, 60_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxGambleStake, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxGambleStake, 10_000_000);
        ArgumentOutOfRangeException.ThrowIfNegative(LoanStartAmount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(LoanStartAmount, 10_000_000);
        ArgumentOutOfRangeException.ThrowIfNegative(LoanStepAmount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(LoanStepAmount, 10_000_000);
        ArgumentOutOfRangeException.ThrowIfNegative(LoanGrowDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(LoanInterestAmount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(LoanInterestAmount, 10_000_000);
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

        ArgumentOutOfRangeException.ThrowIfNegative(ScoreForBuilding);
        ArgumentOutOfRangeException.ThrowIfNegative(ScoreForKill);
        if (ScoreMinedResources.IsDefault)
        {
            throw new ArgumentException("world: the mined resources must not be default.", nameof(ScoreMinedResources));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(VictoryIntervalTicks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(VictoryIntervalTicks, 1_200);
        ArgumentNullException.ThrowIfNull(DeathmatchStartingResources);
        ArgumentOutOfRangeException.ThrowIfNegative(CheatSmallResources);
        ArgumentOutOfRangeException.ThrowIfNegative(CheatLargeResources);
        ArgumentOutOfRangeException.ThrowIfLessThan(TavernFirstMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(TavernEveryMs, 1_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(TavernDeathmatchEveryMs, 1_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MercenaryGroupMin, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MercenaryGroupMax, MercenaryGroupMin);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MercenaryGroupMax, 255);
        ArgumentOutOfRangeException.ThrowIfNegative(MercenaryLevel);
        ArgumentOutOfRangeException.ThrowIfLessThan(HeroLevelMin, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(HeroLevelMax, HeroLevelMin);
        ArgumentOutOfRangeException.ThrowIfNegative(FameLevelBonus);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Math.Max(HeroLevelMax, MercenaryLevel) + FameLevelBonus, 255);
        ArgumentOutOfRangeException.ThrowIfNegative(HirePricePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HirePricePercent, 100_000);
        ArgumentOutOfRangeException.ThrowIfNegative(HireGoldPerLevel);
        ArgumentOutOfRangeException.ThrowIfNegative(HireOtherPerLevel);
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

    /// <summary>
    /// Whether a unit of this type grazes, whoever owns it (cows, NAT-007): it stands and eats a while, then walks to a
    /// cell near where it was last sent, unless it is milked.
    /// </summary>
    public bool Grazes { get; init; }

    /// <summary>Whether a Nature-owned unit of this type guards its home (returns when lured away).</summary>
    public bool Guards { get; init; }

    /// <summary>
    /// NAT-003 (ReTzar's reading of the original, Bear Idle 0x42fea0, 0x4eeed0, 0x4eeea0): a bear - at each idle tick of
    /// 1.5 s it sets off, one time in five, to a cell within 6 of where it was placed, and it looks for a target in only
    /// 30 % of its looks; lured away, it is not called back.
    /// </summary>
    public bool Prowls { get; init; }

    /// <summary>Whether the type is ambient life (birds): never a combat target, never counted for victory.</summary>
    public bool IsAmbient { get; init; }

    /// <summary>
    /// Whether nothing hurts it and its owner cannot delete it (<c>Indestructible</c>; the magic gateway, NAT-001; ReTzar's
    /// reading of the original, 0x4b6740, 0x421190).
    /// </summary>
    public bool Indestructible { get; init; }

    /// <summary>Detection radius against cloaked units (spies, ninjas), in world units (0 = no detector).</summary>
    public int DetectUnits { get; init; }
}
