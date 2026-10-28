using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Tzar2.Content.Compiled;
using Tzar2.Core.Diagnostics;
using Tzar2.Core.Ids;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Game.Diagnostics;

/// <summary>
/// Traces one match (ADR 0176): its setup when attached; on the sim thread after every tick the tick's commands, its
/// gameplay events and rejected commands (with the command they rejected), a summary of every kingdom every
/// <see cref="SummaryTicks"/> ticks, and each tick's hash at Verbose; the outcome when detached. It also gives the
/// simulation the sink, so the AI and the triggers can write to it. It only reads the world, after the tick, so a
/// traced match is the same match (the determinism tests check it).
/// </summary>
public sealed class MatchTracer : IDisposable
{
    /// <summary>Ticks between two kingdom summaries (20 s of game time at 50 ms per tick).</summary>
    public const int SummaryTicks = 400;

    private readonly GameSession _session;
    private readonly ITraceSink _sink;
    private readonly string _label;
    private bool _disposed;

    private MatchTracer(GameSession session, ITraceSink sink, string label)
    {
        _session = session;
        _sink = sink;
        _label = label;
    }

    /// <summary>Attaches a tracer to <paramref name="session"/> (before it starts) and writes the match's setup.</summary>
    public static MatchTracer Attach(GameSession session, ITraceSink sink, string label)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sink);
        var tracer = new MatchTracer(session, sink, label);
        session.Simulation.World.Trace = sink;
        tracer.WriteSetup();
        session.AfterTick += tracer.OnTick;
        return tracer;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.AfterTick -= OnTick;
        World world = _session.Simulation.World;
        if (_sink.IsEnabled(TraceArea.Session, LogLevel.Info))
        {
            _sink.Write(TraceArea.Session, LogLevel.Info, world.Tick.Value, $"{_label} ended after {GameTime(world.Tick.Value)} ({world.Tick.Value} ticks)");
            WriteSummary(world);
        }
    }

    private void WriteSetup()
    {
        if (!_sink.IsEnabled(TraceArea.Session, LogLevel.Info))
        {
            return;
        }

        MatchSetup setup = _session.Setup;
        World world = _session.Simulation.World;
        Ruleset rules = world.Ruleset;
        _sink.Write(TraceArea.Session, LogLevel.Info, world.Tick.Value, string.Create(CultureInfo.InvariantCulture,
            $"{_label} started: map '{setup.Map.Name}' ({world.Map.Width}x{world.Map.Height}), ruleset {rules.Name} {rules.ContentHash:X16}, seed {setup.Seed:X16}, " +
            $"victory {setup.Victory}, game type {setup.GameType}, starting resources {setup.StartingResources}, population limit {setup.PopulationLimit}, " +
            $"cheats {world.Config.CheatsAllowed}, multiplayer {_session.IsMultiplayer}, recording {_session.IsRecording}, speed {_session.SpeedPercent} %"));
        foreach (PlayerSetup p in setup.Players)
        {
            _sink.Write(TraceArea.Session, LogLevel.Info, world.Tick.Value, string.Create(CultureInfo.InvariantCulture,
                $"  player {p.Slot}: {p.Civilization}, {p.Controller}, {p.Difficulty}, team {world.Config.TeamOf(p.Slot)}"));
        }
    }

    // On the sim thread, after the tick; reads only.
    private void OnTick(GameSession session)
    {
        World world = session.Simulation.World;
        int tick = world.Tick.Value;
        CommandList commands = session.LastTickCommands;
        if (commands.Count > 0 && _sink.IsEnabled(TraceArea.Commands, LogLevel.Debug))
        {
            for (int i = 0; i < commands.Count; i++)
            {
                _sink.Write(TraceArea.Commands, LogLevel.Debug, tick, Describe(world, commands, i));
            }
        }

        foreach (SimEvent e in world.Events.Events)
        {
            if (e.Kind == SimEventKind.CommandRejected)
            {
                // The event names the command by its index in the list it came in: the tick's (players') or the AI's own. The
                // AI traces its rejections itself; a player's is the tick command with the same index, player and kind.
                bool fromTick = e.X >= 0 && e.X < commands.Count && commands[e.X].Player == e.Player && (int)commands[e.X].Kind == e.Y;
                if (fromTick && _sink.IsEnabled(TraceArea.Commands, LogLevel.Warn))
                {
                    _sink.Write(TraceArea.Commands, LogLevel.Warn, tick, $"rejected ({(CommandRejection)e.Arg}): {Describe(world, commands, e.X)}");
                }

                continue;
            }

            LogLevel level = LevelOf(e.Kind);
            if (_sink.IsEnabled(TraceArea.Events, level))
            {
                _sink.Write(TraceArea.Events, level, tick, Describe(world, e));
            }
        }

        if (_sink.IsEnabled(TraceArea.Sim, LogLevel.Verbose))
        {
            TickResult r = session.LastTickResult;
            _sink.Write(TraceArea.Sim, LogLevel.Verbose, tick, string.Create(CultureInfo.InvariantCulture,
                $"tick hash {r.StateHash:X16}{(r.FullHash is { } full ? $", full {full:X16}" : string.Empty)}, entities {world.Entities.AliveCount}"));
        }

        if (tick % SummaryTicks == 0 && _sink.IsEnabled(TraceArea.Session, LogLevel.Info))
        {
            WriteSummary(world);
        }
    }

    /// <summary>The level an event is traced at: the story of the match at Info, the details at Debug, every hit at Verbose.</summary>
    public static LogLevel LevelOf(SimEventKind kind) => kind switch
    {
        SimEventKind.ScenarioFault => LogLevel.Error,
        SimEventKind.GoalUnreachable or SimEventKind.ProductionStalled => LogLevel.Warn,
        SimEventKind.ConstructionCompleted or SimEventKind.ResearchCompleted or SimEventKind.UpgradeCompleted or SimEventKind.Captured
            or SimEventKind.BurnedDown or SimEventKind.DiplomacyChanged or SimEventKind.TributeSent or SimEventKind.PlayerDefeated
            or SimEventKind.PlayerVictorious or SimEventKind.CheatUsed or SimEventKind.Hired or SimEventKind.HeroPromoted
            or SimEventKind.CrusadePreached or SimEventKind.Bribed or SimEventKind.UnitConverted or SimEventKind.Converted
            or SimEventKind.LoanTaken or SimEventKind.LoanRepaid or SimEventKind.Gambled or SimEventKind.DayPhaseChanged
            or SimEventKind.WeatherChanged or SimEventKind.QuestAdded or SimEventKind.QuestRemoved or SimEventKind.StoryScreen
            or SimEventKind.ScenarioMessage or SimEventKind.ConversationOffered or SimEventKind.MapRequested => LogLevel.Info,
        SimEventKind.Damaged or SimEventKind.ProjectileLaunched or SimEventKind.Arrived or SimEventKind.DamageShared => LogLevel.Verbose,
        _ => LogLevel.Debug,
    };

    /// <summary>A command in words (<see cref="CommandText"/>).</summary>
    public static string Describe(World world, CommandList commands, int index) => CommandText.Describe(world, commands, index);

    /// <summary>An event in words.</summary>
    public static string Describe(World world, in SimEvent e)
    {
        Ruleset rules = world.Ruleset;
        string type = e.Arg >= 0 && e.Arg < rules.EntityTypes.Count ? rules.EntityTypes[e.Arg].Key : string.Create(CultureInfo.InvariantCulture, $"type {e.Arg}");
        string who = e.Entity.IsNone ? string.Empty : " " + Entity(world, e.Entity);
        string at = string.Create(CultureInfo.InvariantCulture, $" at {Tile(e.X)},{Tile(e.Y)}");
        return e.Kind switch
        {
            SimEventKind.Spawned => $"p{e.Player} spawned {type}{who}{at}",
            SimEventKind.Destroyed => $"p{e.Player} removed {type}{who}",
            SimEventKind.Killed => string.Create(CultureInfo.InvariantCulture, $"p{e.Player} {type}{who} killed by p{e.Y} (unit #{e.X})"),
            SimEventKind.ConstructionCompleted => $"p{e.Player} built {type}{who}",
            SimEventKind.ConstructionStarted => $"p{e.Player} started building {type}{who}{at}",
            SimEventKind.ConstructionCancelled => $"p{e.Player} cancelled building {type}{who}",
            SimEventKind.Dismantled => $"p{e.Player} took down {type}{who}",
            SimEventKind.UnitTrained => $"p{e.Player} trained {type}{who}",
            SimEventKind.Captured => string.Create(CultureInfo.InvariantCulture, $"p{e.Player} captured {type}{who} from p{e.X}"),
            SimEventKind.ResearchCompleted => string.Create(CultureInfo.InvariantCulture,
                $"p{e.Player} researched {(e.Arg >= 0 && e.Arg < rules.Technologies.Count ? rules.Technologies[e.Arg].Key : $"technology {e.Arg}")}"),
            SimEventKind.ResourceDelivered => string.Create(CultureInfo.InvariantCulture,
                $"p{e.Player} delivered {e.Arg} {(e.X >= 0 && e.X < rules.ResourceTypes.Count ? rules.ResourceTypes[e.X] : $"resource {e.X}")}{who}"),
            SimEventKind.Damaged => string.Create(CultureInfo.InvariantCulture, $"p{e.Player}{who} hit for {e.Arg} by unit #{e.X}, {e.Y} hit points left"),
            SimEventKind.GoalUnreachable or SimEventKind.Blocked => $"p{e.Player}{who} {e.Kind}{at}",
            _ => string.Create(CultureInfo.InvariantCulture, $"p{e.Player} {e.Kind}{who} x {e.X} y {e.Y} arg {e.Arg}"),
        };
    }

    private void WriteSummary(World world)
    {
        int tick = world.Tick.Value;
        Ruleset rules = world.Ruleset;
        var units = new int[16];
        var buildings = new int[16];
        foreach (EntityId id in world.Identities.Entities)
        {
            byte owner = world.Identities.Get(id).Owner;
            if (owner >= units.Length)
            {
                continue;
            }

            if (world.Footprints.Has(id))
            {
                buildings[owner]++;
            }
            else if (world.Motions.Has(id))
            {
                units[owner]++;
            }
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"summary at {GameTime(tick)}: entities {world.Entities.AliveCount}");
        _sink.Write(TraceArea.Session, LogLevel.Info, tick, text.ToString());
        for (int p = 0; p < world.Players.Length; p++)
        {
            ref readonly PlayerState state = ref world.Players[p];
            text.Clear();
            text.Append(CultureInfo.InvariantCulture, $"  p{p}: population {state.Population}/{state.PopulationCap} (limit {state.PopulationLimit}), units {units[p]}, buildings {buildings[p]}, ");
            for (int r = 0; r < rules.ResourceTypes.Count; r++)
            {
                text.Append(CultureInfo.InvariantCulture, $"{(r == 0 ? string.Empty : " ")}{rules.ResourceTypes[r]} {state.Resources[r]}");
            }

            text.Append(CultureInfo.InvariantCulture, $", flags {state.Flags}");
            _sink.Write(TraceArea.Session, LogLevel.Info, tick, text.ToString());
        }
    }

    private static string Entity(World world, EntityId id) => CommandText.Entity(world, id);

    private static int Tile(int units) => units / MapState.UnitsPerTile;

    private static string GameTime(int tick)
    {
        long seconds = (long)tick * Tick.DefaultLengthMilliseconds / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:D2}");
    }
}
