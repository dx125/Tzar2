using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.MapGen;
using Tzar2.Game.Maps;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Components;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Map;

namespace Tzar2.Game.Diagnostics;

/// <summary>
/// The S2 scenario for the client's render bench (TDD §4.3 S2, §21; ADR 0244): a 512 × 512 random map (the Real World
/// size), eight kingdoms all run by the Hard AI (the viewer's own kingdom through AI assistance, ADR 0183), armies
/// spawned beside each castle up to <see cref="Units"/> units, day/night and weather on, no fog so everything is drawn.
/// </summary>
public static class RenderBench
{
    /// <summary>Units on the map at the start (S2).</summary>
    public const int Units = 4000;

    /// <summary>Kingdoms (S2).</summary>
    public const int Players = 8;

    /// <summary>The bench's match seed (fixed, so every run plays the same match).</summary>
    public const ulong Seed = 0x5_2B_E4C4UL;

    /// <summary>Soldier types tried per civilization, first found wins; any foot unit that is not a worker otherwise.</summary>
    private static readonly string[] SoldierNames = ["footman", "swordsman", "spearman", "militia", "axeman", "knight", "archer"];

    /// <summary>The match of the bench; null when the ruleset has no castles.</summary>
    public static MatchSetup? Setup(Ruleset rules, SkirmishAi ai, RandomMapSize size = RandomMapSize.RealWorld, int units = Units)
    {
        ArgumentNullException.ThrowIfNull(rules);
        string[] civs = new[] { "euro", "asia", "arab" }.Where(c => rules.EntityTypeId(c + ".castle") >= 0).ToArray();
        if (civs.Length == 0)
        {
            return null;
        }

        var players = new PlayerSetup[Math.Min(Players, MapGenerator.MaxPlayersFor(size))];
        for (int i = 0; i < players.Length; i++)
        {
            players[i] = new PlayerSetup((byte)i, civs[i % civs.Length], i == 0 ? ControllerKind.Human : ControllerKind.Computer, Difficulty.Hard);
        }

        return new MatchSetup
        {
            Map = new RandomMapSource(RandomMapType.Continental, MapTerrainSet.Grass, size, MapResourceLevel.Normal),
            Players = players,
            Seed = Seed,
            NoFog = true,
            Explored = true,
            DayNight = true,
            Weather = true,
            Ai = ai,
            AiAssist = true,
            Preparation = new Armies(units),
        };
    }

    /// <summary>The castle of each kingdom (world units), by slot; the camera's route.</summary>
    public static IReadOnlyList<(int X, int Y)> Castles(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var castles = new (int X, int Y)[world.Players.Length];
        var found = new bool[castles.Length];
        ReadOnlySpan<Core.Ids.EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            Identity identity = identities[i];
            int p = world.Positions.IndexOf(ids[i]);
            if (identity.Owner < castles.Length && !found[identity.Owner] && p >= 0
                && world.Ruleset.EntityTypes[identity.TypeId].Key.EndsWith(".castle", StringComparison.Ordinal))
            {
                Position at = world.Positions.At(p);
                castles[identity.Owner] = (at.X, at.Y);
                found[identity.Owner] = true;
            }
        }

        return castles;
    }

    /// <summary>Mobile units on the map.</summary>
    public static int MobileUnits(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        int n = 0;
        ReadOnlySpan<Core.Ids.EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            if (world.Ruleset.EntityTypes[identities[i].TypeId].IsMobile && world.Positions.IndexOf(ids[i]) >= 0)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>The soldier type spawned for a civilization, or -1.</summary>
    public static int SoldierType(Ruleset rules, string civilization)
    {
        ArgumentNullException.ThrowIfNull(rules);
        foreach (string name in SoldierNames)
        {
            int id = rules.EntityTypeId(civilization + "." + name);
            if (id >= 0 && rules.EntityTypes[id].IsMobile && !rules.EntityTypes[id].HasFootprint)
            {
                return id;
            }
        }

        for (int id = 0; id < rules.EntityTypes.Count; id++)
        {
            EntityTypeDefinition type = rules.EntityTypes[id];
            if (type.Key.StartsWith(civilization + ".", StringComparison.Ordinal) && type.IsMobile && !type.HasFootprint
                && type.Locomotor == Locomotor.Foot && !type.Key.Contains("peon", StringComparison.Ordinal))
            {
                return id;
            }
        }

        return -1;
    }

    /// <summary>Spawns each kingdom's share of the armies beside its castle and turns on AI assistance for the viewer.</summary>
    private sealed class Armies(int units) : IMatchPreparation
    {
        public void Prepare(Simulation simulation, Scripting.Legacy.LegacyTriggerSystem? triggers)
        {
            World world = simulation.World;
            for (int p = 0; p < world.Players.Length; p++)
            {
                world.Players[p].Flags |= PlayerFlags.AssistEconomy | PlayerFlags.AssistWar;
            }

            IReadOnlyList<(int X, int Y)> castles = Castles(world);
            int missing = Math.Max(0, units - MobileUnits(world));
            var used = new HashSet<(int, int)>();
            MapState map = world.Map;
            for (int p = 0; p < castles.Count; p++)
            {
                int type = SoldierType(world.Ruleset, CivilizationOf(world, p));
                if (type < 0 || castles[p] == default)
                {
                    continue;
                }

                Locomotor locomotor = world.Ruleset.EntityTypes[type].Locomotor;
                int share = (missing / castles.Count) + (p < missing % castles.Count ? 1 : 0);
                int cx = castles[p].X / MapState.UnitsPerTile, cy = castles[p].Y / MapState.UnitsPerTile;
                for (int r = 4; r < 80 && share > 0; r++)
                {
                    for (int dy = -r; dy <= r && share > 0; dy++)
                    {
                        for (int dx = -r; dx <= r && share > 0; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                            {
                                continue; // the ring of radius r only
                            }

                            int tx = cx + dx, ty = cy + dy;
                            if (tx < 0 || ty < 0 || tx >= map.Width || ty >= map.Height || !map.IsPassable(locomotor, tx, ty) || !used.Add((tx, ty)))
                            {
                                continue;
                            }

                            world.Spawn((ushort)type, p, (tx * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2), (ty * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2));
                            share--;
                        }
                    }
                }
            }
        }

        private static string CivilizationOf(World world, int player)
        {
            ReadOnlySpan<Core.Ids.EntityId> ids = world.Identities.Entities;
            Span<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length; i++)
            {
                string key = world.Ruleset.EntityTypes[identities[i].TypeId].Key;
                if (identities[i].Owner == player && key.EndsWith(".castle", StringComparison.Ordinal))
                {
                    return key[..key.IndexOf('.', StringComparison.Ordinal)];
                }
            }

            return "euro";
        }
    }
}

/// <summary>
/// What the render bench measured, and its one-line report (ADR 0244). Frame times are wall time between drawn frames;
/// sim ticks are the session's own tick times (<see cref="GameSession.TickTimes"/>).
/// </summary>
public sealed class RenderBenchResult
{
    /// <summary>The line's prefix and format version.</summary>
    public const string Prefix = "RENDER-BENCH v1";

    private readonly List<double> _frames = [];
    private readonly List<double> _ticks = [];

    /// <summary>Window size measured (pixels).</summary>
    public int Width { get; init; }

    /// <summary>Window height measured (pixels).</summary>
    public int Height { get; init; }

    /// <summary>Renderer and driver (Godot's names; <c>dummy</c> headless).</summary>
    public string Renderer { get; init; } = "?";

    /// <summary>Graphics card.</summary>
    public string Gpu { get; init; } = "?";

    /// <summary>Processor.</summary>
    public string Cpu { get; init; } = "?";

    /// <summary>Version of the game.</summary>
    public string Version { get; init; } = GameInfo.Version;

    /// <summary>Seconds from the start of the match build to its first drawn frame.</summary>
    public double LoadSeconds { get; set; }

    /// <summary>Units on the map when the measurement started.</summary>
    public int Units { get; set; }

    /// <summary>Peak process memory (bytes).</summary>
    public long PeakRam { get; private set; }

    /// <summary>Peak video memory in use (bytes; 0 when the renderer does not tell).</summary>
    public long PeakVram { get; private set; }

    /// <summary>Frames measured.</summary>
    public int FrameCount => _frames.Count;

    /// <summary>Records a frame time.</summary>
    public void AddFrame(double milliseconds) => _frames.Add(milliseconds);

    /// <summary>Records sim tick times.</summary>
    public void AddTicks(IEnumerable<double> milliseconds) => _ticks.AddRange(milliseconds);

    /// <summary>Records a memory sample.</summary>
    public void SampleMemory(long ram, long vram)
    {
        PeakRam = Math.Max(PeakRam, ram);
        PeakVram = Math.Max(PeakVram, vram);
    }

    /// <summary>Frames per second over the measurement (frames / total time).</summary>
    public double Fps => _frames.Count == 0 ? 0 : _frames.Count * 1000.0 / _frames.Sum();

    /// <summary>The FPS of the slowest 1 % of frames (1000 / mean of the slowest 1 %).</summary>
    public double Low1Fps
    {
        get
        {
            if (_frames.Count == 0)
            {
                return 0;
            }

            int n = Math.Max(1, _frames.Count / 100);
            return 1000.0 / _frames.OrderByDescending(f => f).Take(n).Average();
        }
    }

    /// <summary>A percentile of the frame times (ms).</summary>
    public double FramePercentile(double percentile) => Percentile(_frames, percentile);

    /// <summary>A percentile of the sim tick times (ms).</summary>
    public double TickPercentile(double percentile) => Percentile(_ticks, percentile);

    /// <summary>
    /// The S2 verdict: <c>PASS</c> or <c>FAIL</c> at 2560 × 1440 with a real renderer (≥ 60 FPS and sim p99 ≤ 12 ms),
    /// <c>n/a</c> otherwise (another resolution, or headless).
    /// </summary>
    public string S2Verdict
        => Width != 2560 || Height != 1440 || Renderer.StartsWith("dummy", StringComparison.Ordinal) || _frames.Count == 0
            ? "n/a"
            : Fps >= 60 && TickPercentile(99) <= 12 ? "PASS" : "FAIL";

    /// <summary>The one line the owner pastes back.</summary>
    public string Line()
    {
        string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture);
        string Q(string s) => "\"" + s.Replace("\"", "'", StringComparison.Ordinal) + "\"";
        return string.Create(CultureInfo.InvariantCulture,
            $"{Prefix} S2={S2Verdict} res={Width}x{Height} fps={F(Fps)} low1={F(Low1Fps)} frame_p50={F(FramePercentile(50))}ms frame_p99={F(FramePercentile(99))}ms "
            + $"sim_p50={F(TickPercentile(50))}ms sim_p99={F(TickPercentile(99))}ms ticks={_ticks.Count} frames={_frames.Count} units={Units} load={F(LoadSeconds)}s "
            + $"ram={Gb(PeakRam)}GB vram={Gb(PeakVram)}GB renderer={Renderer} gpu={Q(Gpu)} cpu={Q(Cpu)} version={Version}");
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double[] sorted = [.. values.Order()];
        int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
