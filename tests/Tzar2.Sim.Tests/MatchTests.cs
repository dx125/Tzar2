using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>GAME-16 day clock and weather (ADR 0105), GAME-17 victory and scores (ADR 0106), GAME-19 cheats (ADR 0107).</summary>
public class MatchTests
{
    private static readonly Ruleset ShortDays = ClassicWith("world: { dayLengthMs: 24000, nightSightPercent: 50 }");

    [Fact]
    public void The_day_clock_turns_to_night_and_back_and_night_can_shorten_sight()
    {
        // 24 s days (test rules): 16.7 ms per clock minute; 10:00 → 19:00 is 9,000 ms = 180 ticks.
        using var d = new SimDriver(TestMap(48, 16, Unit("euro.peon", 0, 5, 8)), ruleset: ShortDays);
        World w = d.World;
        Assert.Equal(600, w.MinuteAt(0));
        Assert.False(w.IsNightAt(w.MinuteAt(179)));
        Assert.True(w.IsNightAt(w.MinuteAt(180)));
        Assert.True(w.IsNightAt(w.MinuteAt(359))); // 01:00
        Assert.False(w.IsNightAt(w.MinuteAt(420))); // 06:00 + a bit

        d.Step();
        Assert.True(w.Visibility.IsVisible(0, 11, 8)); // sight 7 by day
        d.RunUntil(() => d.Count(SimEventKind.DayPhaseChanged) == 1, 400);
        Assert.Equal(1, d.Events.First(e => e.Event.Kind == SimEventKind.DayPhaseChanged).Event.Arg);
        d.Step();
        Assert.True(w.IsNight);
        Assert.False(w.Visibility.IsVisible(0, 11, 8)); // 50% at night (test rules; classic: 100%, UNK-09)
        Assert.True(w.Visibility.IsVisible(0, 9, 8));

        using var off = new SimDriver(TestMap(16, 16), ruleset: ShortDays, configure: c => c with { DayNight = false });
        Assert.Equal(720, off.World.MinuteAt(180));
        Assert.False(off.World.IsNightAt(off.World.MinuteAt(180)));
    }

    [Fact]
    public void Weather_follows_the_terrain_set_and_the_seed()
    {
        static ulong RainyPeriods(string? set, ulong seed, bool weather = true)
        {
            MapData map = TestMap(16, 16);
            map = new MapData(map.Name, map.Width, map.Height, map.TerrainCorners, map.HeightCorners, map.CliffTiles, map.Objects, set, set is null ? [] : new byte[17 * 17]);
            using var d = new SimDriver(map, ruleset: Classic, configure: c => c with { MatchSeed = seed, Weather = weather });
            int periodTicks = d.World.Ruleset.World.WeatherPeriodMs / d.World.Config.TickLengthMilliseconds;
            ulong rainy = 0; // bit p: period p had weather
            for (int p = 0; p < 64; p++)
            {
                WeatherKind kind = d.World.WeatherAt(p * periodTicks);
                Assert.True(kind == WeatherKind.Clear || kind == d.World.Ruleset.World.WeatherOf(set));
                rainy |= kind != WeatherKind.Clear ? 1UL << p : 0;
            }

            return rainy;
        }

        ulong grass = RainyPeriods("grass", 1);
        Assert.InRange(System.Numerics.BitOperations.PopCount(grass), 6, 30); // 25% of the periods (UNVERIFIED)
        Assert.Equal(grass, RainyPeriods("grass", 1));
        Assert.NotEqual(grass, RainyPeriods("grass", 2));
        Assert.Equal(0UL, RainyPeriods("desert", 1)); // DESERT.TERRAIN.INI has no rain
        Assert.Equal(0UL, RainyPeriods("grass", 1, weather: false));
        Assert.Equal(WeatherKind.Snow, Classic.World.WeatherOf("snow"));
    }

    [Fact]
    public void Standard_the_last_kingdom_standing_wins_and_walls_do_not_keep_a_kingdom_alive()
    {
        using var d = new SimDriver(
            TestMap(48, 16, Unit("euro.footman", 0, 10, 8), Unit("euro.peon", 1, 12, 8), Structure("comm.wall_wood", 1, 30, 3), Unit("euro.peon", 2, 40, 12)),
            players: 3,
            ruleset: Classic,
            configure: c => c with { Victory = VictoryCondition.Standard });
        World w = d.World;
        d.Order(2, CommandKind.Resign, []);
        d.RunUntil(() => VictorySystem.IsOver(w), 1200);
        Assert.True((w.Players[0].Flags & PlayerFlags.Victorious) != 0);
        Assert.True((w.Players[1].Flags & PlayerFlags.Defeated) != 0); // its wall does not count
        Assert.True((w.Players[2].Flags & (PlayerFlags.Defeated | PlayerFlags.Resigned)) == (PlayerFlags.Defeated | PlayerFlags.Resigned));
        Assert.Equal(1, w.Kingdoms[0].UnitsKilled);
        Assert.Equal(1, w.Kingdoms[1].UnitsLost);
        Assert.True(w.Kingdoms[0].KillValue > 0);
        Assert.Equal(1, d.Count(SimEventKind.PlayerVictorious));

        // The winner's commands are refused from now on (the match is over).
        d.Order(0, CommandKind.Stop, [OfType(w, "euro.footman").Single()]);
        d.Step();
        Assert.Contains(CommandRejection.InvalidPlayer, d.Rejections());
    }

    [Fact]
    public void Destroy_castles_defeats_a_kingdom_without_a_castle_even_with_units_left()
    {
        using var d = new SimDriver(
            TestMap(48, 16, Structure("euro.castle", 0, 2, 2), Structure("euro.castle", 1, 30, 2), Unit("euro.footman", 1, 40, 12)),
            ruleset: Classic,
            configure: c => c with { Victory = VictoryCondition.DestroyCastles });
        World w = d.World;
        d.Step();
        Assert.False(VictorySystem.IsOver(w));
        d.Order(0, CommandKind.DebugKill, [OfType(w, "euro.castle", 1).Single()]);
        d.RunUntil(() => VictorySystem.IsOver(w), 60);
        Assert.True((w.Players[1].Flags & PlayerFlags.Defeated) != 0);
        Assert.True((w.Players[0].Flags & PlayerFlags.Victorious) != 0);
        Assert.Single(OfType(w, "euro.footman", 1)); // its army is still there
    }

    [Fact]
    public void Score_and_time_limits_and_allied_victory()
    {
        MapData map = TestMap(48, 16, Unit("euro.peon", 0, 5, 5), Unit("euro.peon", 1, 20, 5), Unit("euro.peon", 2, 40, 5));
        using (var score = new SimDriver(map, players: 3, ruleset: Classic, configure: c => c with { Victory = VictoryCondition.ScoreLimit, ScoreLimit = 50 }))
        {
            score.Step();
            score.World.Kingdoms[2].GatheredTotal = 600; // 60 points (1 per 10 gathered, UNVERIFIED UNK-29)
            score.RunUntil(() => VictorySystem.IsOver(score.World), 40);
            Assert.True((score.World.Players[2].Flags & PlayerFlags.Victorious) != 0);
            Assert.Equal(60, VictorySystem.Score(score.World.Ruleset.World, score.World.Kingdoms[2]));
        }

        using (var time = new SimDriver(map, players: 3, ruleset: Classic, configure: c => c with { Victory = VictoryCondition.TimeLimit, TimeLimitMinutes = 1 }))
        {
            time.Step();
            time.World.Kingdoms[1].KillValue = 100;
            time.RunUntil(() => VictorySystem.IsOver(time.World), 1300);
            Assert.InRange(time.World.Tick.Value, 1180, 1221);
            Assert.True((time.World.Players[1].Flags & PlayerFlags.Victorious) != 0);
            Assert.True((time.World.Players[0].Flags & PlayerFlags.Defeated) != 0);
        }

        // Kingdoms 0 and 1 share victory (lobby team): when 2 resigns, both win.
        using var team = new SimDriver(map, players: 3, ruleset: Classic, configure: c => c with { Victory = VictoryCondition.Standard, Teams = 0x011 });
        team.Order(2, CommandKind.Resign, []);
        team.RunUntil(() => VictorySystem.IsOver(team.World), 60);
        Assert.True((team.World.Players[0].Flags & PlayerFlags.Victorious) != 0);
        Assert.True((team.World.Players[1].Flags & PlayerFlags.Victorious) != 0);
    }

    [Fact]
    public void Cheats_need_the_match_flag_never_work_in_ranked_matches_and_replay_as_commands()
    {
        MapData map = TestMap(48, 16, Unit("euro.peon", 0, 5, 8), Unit("euro.footman", 1, 6, 8), Structure("euro.castle", 0, 20, 2));
        using (var off = new SimDriver(map, ruleset: Classic, configure: c => c with { CheatsAllowed = false }))
        {
            off.Order(0, CommandKind.Cheat, [], arg0: (int)CheatCode.Reveal);
            off.Order(0, CommandKind.DebugSpawn, [], 1000, 1000, arg0: Id("euro.peon"), arg1: 1);
            off.Step();
            Assert.Equal(2, off.Rejections().Count(r => r == CommandRejection.CheatsDisabled));
            Assert.Equal(TileVisibility.Unexplored, off.World.Visibility.StateOf(0, 40, 8));
        }

        Assert.Throws<ArgumentException>(() => new SimulationConfig { Ranked = true, CheatsAllowed = true }.Validate(8));

        using var d = new SimDriver(map, ruleset: Classic);
        World w = d.World;
        int gold = Resource("gold");
        int before = w.Players[0].Resources[gold];
        d.Order(0, CommandKind.Cheat, [], arg0: (int)CheatCode.Reveal);
        d.Order(0, CommandKind.Cheat, [], arg0: (int)CheatCode.SmallResources);
        d.Order(0, CommandKind.Cheat, [], arg0: (int)CheatCode.God);
        d.Order(0, CommandKind.Cheat, [], arg0: (int)CheatCode.Buildozer);
        d.Order(0, CommandKind.Cheat, [], arg0: (int)CheatCode.NoTech);
        d.Order(0, CommandKind.Cheat, [], arg0: 99);
        d.Step();
        Assert.Equal(TileVisibility.Explored, w.Visibility.StateOf(0, 40, 8));
        Assert.Equal(before + w.Ruleset.World.CheatSmallResources, w.Players[0].Resources[gold]);
        Assert.Contains(CommandRejection.InvalidArgument, d.Rejections());
        Assert.Equal(5, d.Count(SimEventKind.CheatUsed));

        // hmgod: the enemy footman's blows do nothing; hmbuildozer: a peasant trains at once; hmnotech: a knight needs nothing.
        EntityId peon = OfType(w, "euro.peon").Single();
        for (int t = 0; t < 100; t++)
        {
            d.Step();
        }

        Assert.Equal(w.Healths.Get(peon).Max, w.Healths.Get(peon).Current);
        d.Order(0, CommandKind.Train, [OfType(w, "euro.castle").Single()], arg0: Id("euro.peon"), arg1: 1);
        d.Step();
        d.Step();
        Assert.Equal(2, OfType(w, "euro.peon", 0).Count());
        Assert.True(ResearchRules.CanMake(w, 0, "euro", w.Ruleset.EntityTypes[Id("euro.knight")]));

        // hmnext: win now.
        d.Order(0, CommandKind.Cheat, [], arg0: (int)CheatCode.Next);
        d.Step();
        Assert.True((w.Players[0].Flags & PlayerFlags.Victorious) != 0);
    }

    [Fact]
    public void A_transitive_chain_does_not_make_enemies_one_team()
    {
        // Review finding: A↔B shared victory, B↔C shared victory, but A and C are enemies. The three kingdoms
        // must NOT form one team: B and C are a team, A is alone, and the match continues (two factions alive).
        MapData map = TestMap(48, 16, Unit("euro.peon", 0, 5, 5), Unit("euro.peon", 1, 20, 5), Unit("euro.peon", 2, 40, 5));
        using var d = new SimDriver(map, players: 3, ruleset: Classic, configure: c => c with { Victory = VictoryCondition.Standard });
        World w = d.World;
        // 0↔1 ally with shared victory.
        d.Order(0, CommandKind.SetDiplomacy, [], x: 1, arg0: (int)Stance.Ally, arg1: (int)Sharing.Victory);
        d.Order(1, CommandKind.SetDiplomacy, [], x: 0, arg0: (int)Stance.Ally, arg1: (int)Sharing.Victory);
        d.Step();
        // Wait for the stance-change interval so 1↔2 can be set.
        for (int t = 0; t < 210; t++)
        {
            d.Step();
        }

        // 1↔2 ally with shared victory (but 0 and 2 stay enemies).
        d.Order(1, CommandKind.SetDiplomacy, [], x: 2, arg0: (int)Stance.Ally, arg1: (int)Sharing.Victory);
        d.Order(2, CommandKind.SetDiplomacy, [], x: 1, arg0: (int)Stance.Ally, arg1: (int)Sharing.Victory);
        d.Step();
        Assert.True(w.Diplomacy.Shares(0, 1, Sharing.Victory));
        Assert.True(w.Diplomacy.Shares(1, 2, Sharing.Victory));
        Assert.False(w.Diplomacy.Shares(0, 2, Sharing.Victory));

        // Kill kingdom 1 (the bridge between 0 and 2). 0 and 2 remain, each on its own team (they do not share
        // victory directly), so the match continues — neither wins.
        d.Order(0, CommandKind.DebugKill, [OfType(w, "euro.peon", 1).Single()]);
        d.RunUntil(() => (w.Players[1].Flags & PlayerFlags.Defeated) != 0, 60);
        Assert.True((w.Players[1].Flags & PlayerFlags.Defeated) != 0);
        Assert.False(VictorySystem.IsOver(w)); // the match is NOT over: 0 and 2 are separate teams
        Assert.Equal(PlayerFlags.None, w.Players[0].Flags & (PlayerFlags.Defeated | PlayerFlags.Victorious));
        Assert.Equal(PlayerFlags.None, w.Players[2].Flags & (PlayerFlags.Defeated | PlayerFlags.Victorious));
    }
}
