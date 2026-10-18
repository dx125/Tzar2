using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Game.Scenario;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Kingdoms;

namespace Tzar2.Game.Campaigns;

/// <summary>Settings of a headless campaign run.</summary>
public sealed record AutoplayOptions
{
    /// <summary>Most ticks per mission before it counts as not completed (default 60 minutes of game time; some original missions wait 30 minutes on a timer).</summary>
    public int MaxTicksPerMission { get; init; } = 72_000;

    /// <summary>Most missions played (loops and retries included).</summary>
    public int MaxMissions { get; init; } = 64;

    /// <summary>Retries of a lost mission before the run stops.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Whether computer kingdoms run the legacy AI.</summary>
    public bool ComputerAi { get; init; } = true;

    /// <summary>
    /// Cheat assistance (the solver may use debug commands): from <see cref="ReinforceAfterTicks"/> it spawns
    /// reinforcements by the player's army, from <see cref="ClearAfterTicks"/> it kills the enemies' units and buildings.
    /// </summary>
    public bool CheatAssist { get; init; } = true;

    /// <summary>When reinforcements start.</summary>
    public int ReinforceAfterTicks { get; init; } = 6_000;

    /// <summary>When the enemies are cleared.</summary>
    public int ClearAfterTicks { get; init; } = 12_000;

    /// <summary>Start at this map instead of the first entry map (diagnostics), or null.</summary>
    public string? StartMap { get; init; }

    /// <summary>Protect the heroes (<c>hmgod</c>) from the first attempt, not only on retries (diagnostics).</summary>
    public bool ProtectFromStart { get; init; }

    /// <summary>
    /// Survey mode: after a mission failed all its attempts, continue at the map it leads to (the campaign graph), so a
    /// report covers every map. The campaign then does not count as completed.
    /// </summary>
    public bool ContinueAfterFailure { get; init; }

    /// <summary>Play only one mission (with <see cref="StartMap"/>: that map alone).</summary>
    public bool SingleMission { get; init; }

    /// <summary>The computer kingdoms' AI script (null: the engine default; an imported package's for original maps).</summary>
    public SkirmishAi? Ai { get; init; }

    /// <summary>Picks a conversation answer from the candidates (default: the first).</summary>
    public Func<ConversationView, int>? Answer { get; init; }
}

/// <summary>How a mission went in a headless run.</summary>
/// <param name="MapId">The campaign map.</param>
/// <param name="Completed">Whether it ended in a win or a scenario transition to another map.</param>
/// <param name="Result">The mission result (or Lost on a timeout).</param>
/// <param name="Ticks">Ticks played.</param>
/// <param name="Assist">"play" (no cheats), "resources", "spawned", "reinforced", "cleared", "god" or "god+cleared" (the strongest assistance used), or "timeout".</param>
/// <param name="NextMapId">Where the campaign went next, or null.</param>
/// <param name="Faults">Scenario faults reported (unknown actions, unsupported functions…).</param>
/// <param name="Trace">The end state for diagnosis: the rules that fired (by name) and the user variables.</param>
public sealed record MissionReport(string MapId, bool Completed, MissionResult Result, int Ticks, string Assist, string? NextMapId, int Faults, string Trace = "");

/// <summary>A headless campaign run.</summary>
/// <param name="CampaignId">The campaign.</param>
/// <param name="Completed">Whether the campaign was finished.</param>
/// <param name="Missions">Every mission played, in order.</param>
/// <param name="Progress">The final progress (what a profile would store).</param>
/// <param name="Error">Why the run stopped early, or null.</param>
public sealed record CampaignReport(string CampaignId, bool Completed, ImmutableArray<MissionReport> Missions, CampaignProgress Progress, string? Error);

/// <summary>
/// Plays a campaign headless to its end (TDD §22 item 5 "campaign completion bots"; ADR 0133): each mission runs in a
/// <see cref="GameSession"/> stepped synchronously while a scripted solver plays the local kingdom — it answers
/// conversations, walks the units a scenario names into the areas its events wait for, and attacks the nearest enemy;
/// with <see cref="AutoplayOptions.CheatAssist"/> it then reinforces and finally clears the enemies with debug commands.
/// The campaign layer decides what comes next exactly as in the game.
/// </summary>
public static class CampaignAutoplay
{
    private const int Tile = MapData.UnitsPerTile;

    /// <summary>Runs <paramref name="source"/> from its first entry map.</summary>
    public static CampaignReport Run(ICampaignSource source, Ruleset ruleset, AutoplayOptions? options = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ruleset);
        options ??= new AutoplayOptions();
        CampaignData campaign = source.Campaign;
        CampaignProgress progress = CampaignFlow.Begin(campaign);
        if (options.StartMap is { } start && CampaignFlow.Resolve(campaign, start) is { } startId)
        {
            progress = progress with { Current = startId, Unlocked = progress.Unlocked.Contains(startId) ? progress.Unlocked : progress.Unlocked.Add(startId) };
        }

        var missions = ImmutableArray.CreateBuilder<MissionReport>();
        int retries = 0;
        bool skipped = false;
        while (progress.Current is { } mapId && missions.Count < options.MaxMissions)
        {
            MissionReport report;
            MissionEnd end;
            try
            {
                // Each retry of a lost mission escalates the assistance (attempt 2+: the heroes are invulnerable).
                (report, end) = Play(campaign, source.Load(mapId), mapId, ruleset, progress, options, attempt: retries);
            }
#pragma warning disable CA1031 // A broken mission is reported, the run stops.
            catch (Exception e)
#pragma warning restore CA1031
            {
                missions.Add(new MissionReport(mapId, false, MissionResult.Lost, 0, "error", null, 0));
                return new CampaignReport(campaign.Id, false, missions.ToImmutable(), progress, $"{mapId}: {e.GetType().Name}: {e.Message}");
            }

            missions.Add(report);
            if (options.SingleMission)
            {
                log?.Invoke($"{mapId}: {(report.Completed ? "completed" : "not completed")} ({report.Result}, {report.Ticks} ticks, {report.Assist}) {report.Trace}");
                return new CampaignReport(campaign.Id, false, missions.ToImmutable(), end.Progress, report.Completed ? null : $"{mapId} was not completed.");
            }

            log?.Invoke($"{mapId}: {(report.Completed ? "completed" : "not completed")} ({report.Result}, {report.Ticks} ticks, {report.Assist}{(report.NextMapId is { } n ? ", next " + n : string.Empty)})");
            progress = end.Progress;
            if (!report.Completed)
            {
                if (++retries > options.MaxRetries)
                {
                    // Survey mode: skip to the map the failed one leads to, so every map of the campaign gets played.
                    string? skip = options.ContinueAfterFailure ? NextByGraph(campaign, mapId, missions) : null;
                    if (skip is null)
                    {
                        return new CampaignReport(campaign.Id, false, missions.ToImmutable(), progress, $"{mapId} was not completed after {retries} attempts.");
                    }

                    log?.Invoke($"{mapId}: skipped after {retries} attempts, continuing at {skip}");
                    skipped = true;
                    progress = progress with { Current = skip, Unlocked = progress.Unlocked.Contains(skip) ? progress.Unlocked : progress.Unlocked.Add(skip) };
                    retries = 0;
                }
            }
            else
            {
                retries = 0;
            }
        }

        // Survey mode: maps the path never reached (other branches of the graph) are played once each, on their own.
        if (options.ContinueAfterFailure)
        {
            foreach (CampaignMap m in campaign.Maps)
            {
                if (missions.Any(r => r.MapId == m.Id) || missions.Count >= options.MaxMissions)
                {
                    continue;
                }

                skipped = true;
                CampaignProgress alone = progress with { Current = m.Id, Unlocked = progress.Unlocked.Contains(m.Id) ? progress.Unlocked : progress.Unlocked.Add(m.Id) };
                try
                {
                    (MissionReport report, _) = Play(campaign, source.Load(m.Id), m.Id, ruleset, alone, options);
                    missions.Add(report);
                    log?.Invoke($"{m.Id} (not on the played path): {(report.Completed ? "completed" : "not completed")} ({report.Result}, {report.Ticks} ticks, {report.Assist})");
                }
#pragma warning disable CA1031 // A broken mission is reported.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    missions.Add(new MissionReport(m.Id, false, MissionResult.Lost, 0, "error", null, 0, e.Message));
                    log?.Invoke($"{m.Id} (not on the played path): error {e.Message}");
                }
            }
        }

        bool completed = progress.Completed && !skipped;
        return new CampaignReport(campaign.Id, completed, missions.ToImmutable(), progress, completed ? null : skipped ? "survey: some maps were not completed or not on the played path" : "the mission limit was reached");
    }

    // The first map an edge of the campaign graph leads to from mapId that has not been played yet.
    private static string? NextByGraph(CampaignData campaign, string mapId, ImmutableArray<MissionReport>.Builder played)
    {
        foreach (CampaignEdge e in campaign.Edges)
        {
            if (e.FromMapId == mapId && e.ToMapId is { } to && !played.Any(m => m.MapId == to))
            {
                return to;
            }
        }

        return null;
    }

    /// <summary>Plays one mission and ends it in the campaign flow.</summary>
    public static (MissionReport Report, MissionEnd End) Play(CampaignData campaign, MissionContent mission, string mapId, Ruleset ruleset, CampaignProgress progress, AutoplayOptions options, int attempt = 0)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(options);
        MatchSetup setup = ScenarioMatch.Setup(mission, ruleset, progress, options.ComputerAi, cheats: options.CheatAssist);
        if (options.Ai is { } ai)
        {
            setup = setup with { Ai = ai };
        }

        using var session = new GameSession(setup, ruleset, fog: false);
        var pilot = new Solver(session, mission.Scenario, options);
        if (options.CheatAssist && (attempt > 0 || options.ProtectFromStart))
        {
            pilot.Protect();
        }

        var notices = new List<ScenarioNotice>();
        string? requested = null;
        bool transition = false;
        int faults = 0;
        int ticks = 0;
        var events = new List<Sim.Events.SimEvent>();
        for (; ticks < options.MaxTicksPerMission; ticks++)
        {
            pilot.Act(ticks);
            session.Step(1);
            session.DrainNotices(notices);
            foreach (ScenarioNotice n in notices)
            {
                if (n.Kind == ScenarioNoticeKind.MapRequested && !transition)
                {
                    transition = true;
                    requested = n.Text.Length > 0 ? n.Text : null;
                }
            }

            notices.Clear();
            session.DrainEvents(events);
            foreach (Sim.Events.SimEvent e in events)
            {
                faults += e.Kind == Sim.Events.SimEventKind.ScenarioFault ? 1 : 0;
            }

            events.Clear();
            PlayerFlags flags = session.Simulation.World.Players[session.LocalPlayer].Flags;
            if (transition || (flags & (PlayerFlags.Victorious | PlayerFlags.Defeated)) != 0)
            {
                ticks++;
                break;
            }
        }

        string trace = Trace(session) + " | goal: " + pilot.Goal;
        MissionEnd end = CampaignFlow.Finish(campaign, progress, mapId, session, requested, transition);
        MissionResult result = end.Result;
        bool completed = result != MissionResult.Lost;
        string assist = !completed && ticks >= options.MaxTicksPerMission ? "timeout" : pilot.Assist;
        return (new MissionReport(mapId, completed, result, ticks, assist, end.NextMapId, faults, trace), end);
    }

    // Fired rules and variable values at the end of a mission (the report's diagnosis line).
    private static string Trace(GameSession session)
    {
        if (session.Scenario?.Triggers is not { } triggers)
        {
            return string.Empty;
        }

        World world = session.Simulation.World;
        var text = new System.Text.StringBuilder("units:");
        ReadOnlySpan<Identity> ids = world.Identities.Components;
        for (int p = 0; p < world.Players.Length; p++)
        {
            int units = 0;
            int armed = 0;
            foreach (Identity id in ids)
            {
                if (id.Owner == p && world.Ruleset.EntityTypes[id.TypeId].IsMobile)
                {
                    units++;
                    armed += world.Ruleset.EntityTypes[id.TypeId].Traits.Armament is not null ? 1 : 0;
                }
            }

            text.Append(' ').Append(p).Append(':').Append(units).Append('/').Append(armed);
        }

        text.Append(" | fired:");
        ScenarioData s = triggers.Scenario;
        for (int r = 0; r < s.Rules.Length; r++)
        {
            if (triggers.State.RuleDisabled[r])
            {
                text.Append(' ').Append(r);
            }
        }

        text.Append(" | vars:");
        for (int v = 0; v < s.Variables.Length; v++)
        {
            text.Append(' ').Append(s.Variables[v]).Append('=').Append(triggers.State.Variables[v]);
        }

        text.Append(" | events:");
        for (int e = 0; e < s.Events.Length; e++)
        {
            text.Append(triggers.State.EventValues[e] ? '1' : '0');
        }

        text.Append(" | timers:");
        for (int a = 0; a < triggers.State.Timers.Length; a++)
        {
            if (triggers.State.Timers[a].Started)
            {
                text.Append(' ').Append(a).Append('=').Append(triggers.State.Timers[a].ValueMs);
            }
        }

        return text.ToString();
    }

    // The scripted player of the local kingdom.
    private sealed class Solver(GameSession session, ScenarioData scenario, AutoplayOptions options)
    {
        private readonly CommandList _batch = new();
        private readonly List<EntityId> _army = [];

        public string Assist { get; private set; } = "play";

        /// <summary>
        /// Cheat assistance for a retry: <c>hmgod</c> for the local kingdom and every kingdom it may command whose units
        /// the scenario must keep alive (a Destroyed event in a rule that makes the local kingdom lose).
        /// </summary>
        public void Protect()
        {
            World world = session.Simulation.World;
            byte me = session.LocalPlayer;
            var kingdoms = new SortedSet<int> { me };
            if (session.Scenario?.Triggers is { } triggers)
            {
                var wanted = new List<int>();
                foreach (RuleDef rule in scenario.Rules)
                {
                    if (rule.Condition.Root is not { } condition || !LosesMe(rule, me))
                    {
                        continue;
                    }

                    wanted.Clear();
                    Wanted(condition, negated: false, wanted);
                    foreach (int e in wanted)
                    {
                        if (scenario.Events[e] is ObjectDestroyedEvent { Objects: NamedObjects named })
                        {
                            foreach (int index in named.ObjectIndices)
                            {
                                if (Entity(world, triggers, index) is { } unit && world.Identities.Get(unit).Owner is var owner && world.HasPlayer(owner))
                                {
                                    kingdoms.Add(owner);
                                }
                            }
                        }
                    }
                }
            }

            foreach (int k in kingdoms)
            {
                var cheat = new CommandList();
                cheat.Add(CommandKind.Cheat, [], 0, 0, default, (int)Sim.Systems.CheatCode.God, 0);
                session.SubmitAs((byte)k, cheat);
            }

            Assist = "god";
        }

        /// <summary>The last thing the solver went after (diagnosis).</summary>
        public string Goal { get; private set; } = "none";

        public void Act(int tick)
        {
            _tick = tick;
            World world = session.Simulation.World;
            byte me = session.LocalPlayer;
            if (session.Scenario is { } presenter && presenter.View.Conversation is { } talk && presenter.AwaitingReply && tick % 5 == 0)
            {
                int answer = Math.Clamp(options.Answer?.Invoke(talk) ?? 0, 0, talk.Choices.Length - 1);
                session.ReplyToConversation(talk.Serial, answer);
            }

            if (tick % 40 != 7)
            {
                return;
            }

            _batch.Clear();
            Army(world, me);
            // Guard first: an area that enemies must not reach (a Player Lose rule waits for them there) takes the army,
            // the scenario's own goals take the units it names; with nothing to do, the army attacks.
            bool guarding = Guard(world, me);
            if (!Pursue(world, me, guarding) && !guarding && _army.Count > 0 && tick > 200)
            {
                Attack(world, me);
            }

            if (options.CheatAssist && tick >= options.ReinforceAfterTicks && tick % 2000 == 7 && _army.Count < 60)
            {
                Reinforce(world, me);
                Assist = Assist is "play" ? "reinforced" : Assist;
            }

            if (options.CheatAssist && tick >= options.ClearAfterTicks && tick % 1000 == 7)
            {
                Clear(world, me);
                Assist = Assist == "god" ? "god+cleared" : "cleared";
            }

            if (_batch.Count > 0)
            {
                session.Submit(_batch);
            }
        }

        private void Army(World world, byte me)
        {
            _army.Clear();
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length && _army.Count < 200; i++)
            {
                if (world.Diplomacy.MayCommand(me, identities[i].Owner) && world.Motions.Has(ids[i]) && world.Ruleset.EntityTypes[identities[i].TypeId].Traits.Armament is not null && !world.IsHeld(ids[i]))
                {
                    _army.Add(ids[i]);
                }
            }
        }

        private bool _armyBusy;
        private int _tick;
        private int _lastResources = int.MinValue / 2;
        private int _lastSpawn = int.MinValue / 2;

        private static bool UsesResources(Content.Compiled.Scenario.Expressions.Expr node) => node switch
        {
            Content.Compiled.Scenario.Expressions.ReservedVar r => r.Variable is Content.Compiled.Scenario.Expressions.ReservedVariable.Gold or Content.Compiled.Scenario.Expressions.ReservedVariable.Wood
                or Content.Compiled.Scenario.Expressions.ReservedVariable.Food or Content.Compiled.Scenario.Expressions.ReservedVariable.Stone,
            Content.Compiled.Scenario.Expressions.CallExpr c => c.Function is Content.Compiled.Scenario.Expressions.CallKind.Gold or Content.Compiled.Scenario.Expressions.CallKind.Wood
                or Content.Compiled.Scenario.Expressions.CallKind.Food or Content.Compiled.Scenario.Expressions.CallKind.Stone || c.Arguments.Any(UsesResources),
            Content.Compiled.Scenario.Expressions.UnaryExpr u => UsesResources(u.Operand),
            Content.Compiled.Scenario.Expressions.BinaryExpr b => UsesResources(b.Left) || UsesResources(b.Right),
            Content.Compiled.Scenario.Expressions.SequenceExpr q => q.Statements.Any(UsesResources),
            _ => false,
        };

        /// <summary>
        /// Areas the enemy must not reach: Object In Area events (not under '!') of rules that make the local kingdom
        /// lose, about objects that are not its own. While such objects exist, the army attack-moves to the area.
        /// </summary>
        private bool Guard(World world, byte me)
        {
            if (session.Scenario?.Triggers is not { } triggers || _army.Count == 0)
            {
                return false;
            }

            var wanted = new List<int>();
            Position? nearest = null;
            long best = long.MaxValue;
            Position from = world.Positions.Get(_army[0]);
            var doomed = new List<EntityId>();
            string name = string.Empty;
            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                if (triggers.State.RuleDisabled[r] || scenario.Rules[r].Condition.Root is not { } condition || !LosesMe(scenario.Rules[r], me))
                {
                    continue;
                }

                wanted.Clear();
                Wanted(condition, negated: false, wanted);
                foreach (int e in wanted)
                {
                    if (scenario.Events[e] is not ObjectInAreaEvent { Objects: NamedObjects named } inArea || Target(world, triggers, inArea.Area) is not { } point)
                    {
                        continue;
                    }

                    foreach (int index in named.ObjectIndices)
                    {
                        if (Entity(world, triggers, index) is not { } threat || world.Identities.Get(threat).Owner == me)
                        {
                            continue;
                        }

                        Position at = world.Positions.Get(threat);
                        long dx = (at.X - point.X) / Tile;
                        long dy = (at.Y - point.Y) / Tile;
                        if (options.CheatAssist && (dx * dx) + (dy * dy) <= 25 * 25)
                        {
                            doomed.Add(threat); // cheat assistance, last resort: a threat close to the area is removed
                        }

                        long fx = at.X - from.X;
                        long fy = at.Y - from.Y;
                        if ((fx * fx) + (fy * fy) < best)
                        {
                            best = (fx * fx) + (fy * fy);
                            nearest = at;
                            name = inArea.Name;
                        }
                    }
                }
            }

            if (nearest is not { } target)
            {
                return false;
            }

            // Intercept: the army attack-moves to the nearest threat (the area must stay clear of it).
            _batch.Add(CommandKind.Move, _army.ToArray(), target.X, target.Y, default, 0, 0, CommandFlags.Ctrl);
            Goal = $"guard {name} against a threat at ({target.X / Tile},{target.Y / Tile})";
            if (doomed.Count > 0)
            {
                _batch.Add(CommandKind.DebugKill, doomed.ToArray());
                Assist = Assist.StartsWith("god", StringComparison.Ordinal) ? "god+cleared" : "cleared";
            }

            return true;
        }

        private bool LosesMe(RuleDef rule, byte me)
        {
            foreach (int a in rule.ActionIndices)
            {
                if (scenario.Actions[a] is PlayerLoseAction lose && (lose.Players & (1 << me)) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The scenario's next goal (ADR 0133): the first rule still enabled whose condition waits for an event (not
        /// under a negation) that is false, and that the local kingdom can make true — walk the
        /// named own units (or the army for typed specifications) into an Object In Area area, board a holder, or
        /// attack the named objects of a Destroyed event that are not its own.
        /// </summary>
        private bool Pursue(World world, byte me, bool armyBusy)
        {
            _armyBusy = armyBusy;
            if (session.Scenario?.Triggers is not { } triggers)
            {
                return false;
            }

            var wanted = new List<int>();
            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                // Never pursue what makes the local kingdom lose.
                if (triggers.State.RuleDisabled[r] || scenario.Rules[r].Condition.Root is not { } condition || LosesMe(scenario.Rules[r], me))
                {
                    continue;
                }

                // A rule waiting on the player's stockpile ($wood > 40, @gold(1) > 200): cheat assistance gives resources.
                if (options.CheatAssist && _tick - _lastResources >= 2000 && UsesResources(condition))
                {
                    _lastResources = _tick;
                    _batch.Add(CommandKind.Cheat, [], 0, 0, default, (int)Sim.Systems.CheatCode.LargeResources, 0);
                    Assist = Assist == "play" ? "resources" : Assist;
                }

                wanted.Clear();
                Wanted(condition, negated: false, wanted);
                foreach (int e in wanted)
                {
                    if (!triggers.State.EventValues[e] && Act(world, me, triggers, scenario.Events[e]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Event references a condition needs to be true (not those under '!').
        private static void Wanted(Content.Compiled.Scenario.Expressions.Expr node, bool negated, List<int> into)
        {
            switch (node)
            {
                case Content.Compiled.Scenario.Expressions.EventRef r when !negated && !into.Contains(r.EventIndex):
                    into.Add(r.EventIndex);
                    break;
                case Content.Compiled.Scenario.Expressions.UnaryExpr u:
                    Wanted(u.Operand, negated ^ (u.Op == Content.Compiled.Scenario.Expressions.UnaryOp.Not), into);
                    break;
                case Content.Compiled.Scenario.Expressions.BinaryExpr b:
                    Wanted(b.Left, negated, into);
                    Wanted(b.Right, negated, into);
                    break;
                case Content.Compiled.Scenario.Expressions.SequenceExpr q:
                    foreach (Content.Compiled.Scenario.Expressions.Expr statement in q.Statements)
                    {
                        Wanted(statement, negated, into);
                    }

                    break;
            }
        }

        private bool Act(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers, EventDef e)
        {
            switch (e)
            {
                case ObjectInAreaEvent inArea when Target(world, triggers, inArea.Area) is { } point:
                {
                    List<EntityId> units = OwnUnits(world, me, triggers, inArea.Objects, point);
                    if (units.Count == 0)
                    {
                        Goal = $"{inArea.Name}: no unit of mine can go";
                        return false;
                    }

                    if (inArea.Objects is NamedObjects && units.Count <= 8)
                    {
                        // A few named units: each walks to the point itself (a group move would spread them over
                        // arrival slots, possibly outside a small area).
                        foreach (EntityId u in units)
                        {
                            _batch.Add(CommandKind.Move, [u], point.X, point.Y);
                        }
                    }
                    else
                    {
                        _batch.Add(CommandKind.Move, units.ToArray(), point.X, point.Y);
                    }

                    Goal = $"{inArea.Name} with {units.Count} units to ({point.X / Tile},{point.Y / Tile}) from {string.Join(" ", units.ConvertAll(u => $"({world.Positions.Get(u).X / Tile},{world.Positions.Get(u).Y / Tile}){(world.IsHeld(u) ? "held" : "")}{world.Motions.Get(u).State}"))}";
                    return true;
                }

                case ObjectsEnterHolderEvent board when board.HolderIndex >= 0 && Entity(world, triggers, board.HolderIndex) is { } holder:
                {
                    List<EntityId> units = OwnUnits(world, me, triggers, board.Objects, world.Positions.Get(holder));
                    if (units.Count == 0)
                    {
                        return false;
                    }

                    _batch.Add(CommandKind.Board, units.ToArray(), 0, 0, holder);
                    Goal = $"{board.Name}: board";
                    return true;
                }

                case ObjectDestroyedEvent { Objects: NamedObjects named } when _army.Count > 0:
                    foreach (int index in named.ObjectIndices)
                    {
                        if (Entity(world, triggers, index) is { } target && world.Identities.Get(target).Owner is var owner && owner != me
                            && !(world.HasPlayer(owner) && world.Diplomacy.StanceOf(me, owner) == Sim.Kingdoms.Stance.Ally))
                        {
                            _batch.Add(CommandKind.Attack, _army.ToArray(), 0, 0, target);
                            Goal = $"{e.Name}: attack ({world.Positions.Get(target).X / Tile},{world.Positions.Get(target).Y / Tile})";
                            return true;
                        }
                    }

                    return false;

                // An expression about other kingdoms' named objects (typically "$health <= 50"): attack them.
                case ObjectExpressionEvent { Objects: NamedObjects named } x when _army.Count > 0 && x.Expression.Source.Contains("health", StringComparison.OrdinalIgnoreCase):
                    foreach (int index in named.ObjectIndices)
                    {
                        if (Entity(world, triggers, index) is { } target && world.Identities.Get(target).Owner is var owner && owner != me
                            && !(world.HasPlayer(owner) && world.Diplomacy.StanceOf(me, owner) == Sim.Kingdoms.Stance.Ally))
                        {
                            _batch.Add(CommandKind.Attack, _army.ToArray(), 0, 0, target);
                            Goal = $"{x.Name}: attack ({world.Positions.Get(target).X / Tile},{world.Positions.Get(target).Y / Tile})";
                            return true;
                        }
                    }

                    return false;

                // "At least N of my objects of a type or category" (have a barracks, have five soldiers): cheat
                // assistance spawns what is missing by the army.
                case ObjectExistEvent { Objects: TypedObjects or CategoryObjects } have when options.CheatAssist && Comparison(have.Objects) == CountComparison.AtLeast
                    && (Mask(have.Objects) & (1 << me)) != 0 && _tick - _lastSpawn >= 400:
                {
                    int type = have.Objects is TypedObjects t ? world.Ruleset.EntityTypeId(t.TypeKey) : world.Ruleset.EntityTypeId(world.PlayerCivilization(me) + ".footman");
                    int count = have.Objects is TypedObjects tt ? tt.Count : ((CategoryObjects)have.Objects).Count;
                    if (type < 0 || count <= 0)
                    {
                        return false;
                    }

                    Position at = _army.Count > 0 ? world.Positions.Get(_army[0]) : MyAnchor(world, me);
                    _lastSpawn = _tick;
                    _batch.Add(CommandKind.DebugSpawn, [], at.X, at.Y, default, type, Math.Clamp(count, 1, 64));
                    Assist = Assist == "play" || Assist == "resources" ? "spawned" : Assist;
                    Goal = $"{have.Name}: spawn {count} {world.Ruleset.EntityTypes[type].Key}";
                    return true;
                }

                // "At most N objects of these kingdoms" (all enemies dead, the guards gone): attack the nearest of
                // them; with cheat assistance, after ClearAfterTicks they are removed.
                case ObjectExistEvent { Objects: TypedObjects or CategoryObjects } exist when Comparison(exist.Objects) == CountComparison.AtMost && (Mask(exist.Objects) & (1 << me)) == 0:
                {
                    List<EntityId> targets = Matching(world, triggers, exist.Objects, me);
                    if (targets.Count == 0)
                    {
                        return false;
                    }

                    if (options.CheatAssist && _tick >= options.ClearAfterTicks)
                    {
                        for (int i = 0; i < targets.Count; i += 64)
                        {
                            _batch.Add(CommandKind.DebugKill, targets.GetRange(i, Math.Min(64, targets.Count - i)).ToArray());
                        }

                        Assist = Assist.StartsWith("god", StringComparison.Ordinal) ? "god+cleared" : "cleared";
                    }
                    else if (_army.Count > 0)
                    {
                        Position at = world.Positions.Get(targets[0]);
                        _batch.Add(CommandKind.Move, _army.ToArray(), at.X, at.Y, default, 0, 0, CommandFlags.Ctrl);
                    }

                    Goal = $"{exist.Name}: {targets.Count} objects to remove";
                    return true;
                }

                default:
                    return false;
            }
        }

        private static EntityId? Entity(World world, Scripting.Legacy.LegacyTriggerSystem triggers, int index)
        {
            if ((uint)index >= (uint)triggers.State.MapEntities.Length)
            {
                return null;
            }

            EntityId e = EntityId.FromValue(triggers.State.MapEntities[index]);
            return world.Entities.IsAlive(e) && world.Positions.Has(e) ? e : null;
        }

        private static Position? Target(World world, Scripting.Legacy.LegacyTriggerSystem triggers, AreaSpec area) => area switch
        {
            RectArea r when r.Right < r.Left || r.Bottom < r.Top => null, // an unset area
            RectArea r => new Position(((r.Left + r.Right + 1) * Tile) / 2, ((r.Top + r.Bottom + 1) * Tile) / 2),
            ObjectArea o when Entity(world, triggers, o.ObjectIndex) is { } e => world.Positions.Get(e),
            _ => null,
        };

        private List<EntityId> OwnUnits(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers, ObjectSpec spec, Position? target)
        {
            var units = new List<EntityId>();
            if (spec is NamedObjects named)
            {
                foreach (int index in named.ObjectIndices)
                {
                    if (Entity(world, triggers, index) is { } u && world.Diplomacy.MayCommand(me, world.Identities.Get(u).Owner) && world.Motions.Has(u))
                    {
                        units.Add(u);
                    }
                }
            }
            else if ((Mask(spec) & (1 << me)) != 0 && _army.Count > 0 && target is { } at)
            {
                // "So many of my units": the ones nearest to the target, as many as the count asks (at least one); the
                // rest of the army stays free, and a crowd does not block the scenario's own movers.
                int wanted = Math.Clamp(spec is TypedObjects t ? t.Count : spec is CategoryObjects c ? c.Count : 1, 1, _army.Count);
                var nearest = new List<(long Distance, int Index)>(_army.Count);
                for (int i = 0; i < _army.Count; i++)
                {
                    Position p = world.Positions.Get(_army[i]);
                    long dx = p.X - at.X;
                    long dy = p.Y - at.Y;
                    nearest.Add(((dx * dx) + (dy * dy), i));
                }

                nearest.Sort();
                for (int i = 0; i < wanted; i++)
                {
                    units.Add(_army[nearest[i].Index]);
                }
            }

            return units;
        }

        // Some point of the local kingdom (its first entity) when it has no army.
        private static Position MyAnchor(World world, byte me)
        {
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length; i++)
            {
                if (identities[i].Owner == me && world.Positions.Has(ids[i]))
                {
                    return world.Positions.Get(ids[i]);
                }
            }

            return new Position(Tile * 4, Tile * 4);
        }

        private static CountComparison Comparison(ObjectSpec spec) => spec switch
        {
            TypedObjects t => t.Comparison,
            CategoryObjects c => c.Comparison,
            _ => CountComparison.AtLeast,
        };

        // The live objects of a typed or category specification (not the local kingdom's), ascending by entity.
        private static List<EntityId> Matching(World world, Scripting.Legacy.LegacyTriggerSystem triggers, ObjectSpec spec, byte me)
        {
            var list = new List<EntityId>();
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length; i++)
            {
                Identity id = identities[i];
                if (id.Owner == me || !world.HasPlayer(id.Owner) || (Mask(spec) & (1 << id.Owner)) == 0)
                {
                    continue;
                }

                EntityTypeDefinition type = world.Ruleset.EntityTypes[id.TypeId];
                bool match = spec switch
                {
                    TypedObjects t => type.Key == t.TypeKey,
                    CategoryObjects c => Scripting.Legacy.LegacyTriggerSystem.InCategory(c.Category, type),
                    _ => false,
                };
                if (match)
                {
                    list.Add(ids[i]);
                }
            }

            return list;
        }

        private static int Mask(ObjectSpec spec) => spec switch
        {
            TypedObjects t => t.Players,
            CategoryObjects c => c.Players,
            _ => 0,
        };

        // Attack-move toward the nearest enemy entity (buildings and units of kingdoms that are not allies).
        private void Attack(World world, byte me)
        {
            Position from = world.Positions.Get(_army[0]);
            long best = long.MaxValue;
            Position target = default;
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length; i++)
            {
                if (!IsEnemy(world, me, identities[i]) || world.IsHeld(ids[i]))
                {
                    continue;
                }

                Position p = world.Positions.Get(ids[i]);
                long dx = p.X - from.X;
                long dy = p.Y - from.Y;
                long d = (dx * dx) + (dy * dy);
                if (d < best)
                {
                    best = d;
                    target = p;
                }
            }

            if (best != long.MaxValue)
            {
                _batch.Add(CommandKind.Move, _army.ToArray(), target.X, target.Y, default, 0, 0, CommandFlags.Ctrl);
                Goal = $"attack the nearest enemy at ({target.X / Tile},{target.Y / Tile}) with {_army.Count}";
            }
            else
            {
                Goal = $"idle with {_army.Count}";
            }
        }

        private static bool IsEnemy(World world, byte me, Identity identity)
        {
            int owner = identity.Owner;
            if (!world.HasPlayer(owner) || owner == me || (world.Players[owner].Flags & PlayerFlags.Defeated) != 0)
            {
                return false;
            }

            EntityCategory category = world.Ruleset.EntityTypes[identity.TypeId].Traits.Category;
            return category is EntityCategory.Unit or EntityCategory.Building && world.Diplomacy.StanceOf(me, owner) != Sim.Kingdoms.Stance.Ally;
        }

        private void Reinforce(World world, byte me, Position? where = null)
        {
            int type = -1;
            foreach (string key in new[] { world.PlayerCivilization(me) + ".knight", world.PlayerCivilization(me) + ".footman", "euro.footman" })
            {
                if (world.Ruleset.EntityTypeId(key) is >= 0 and var id)
                {
                    type = id;
                    break;
                }
            }

            if (type < 0 || (_army.Count == 0 && where is null))
            {
                return;
            }

            Position at = where ?? world.Positions.Get(_army[0]);
            _batch.Add(CommandKind.DebugSpawn, [], at.X, at.Y, default, type, 10);
        }

        private void Clear(World world, byte me)
        {
            var targets = new List<EntityId>();
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length && targets.Count < 1000; i++)
            {
                if (IsEnemy(world, me, identities[i]))
                {
                    targets.Add(ids[i]);
                }
            }

            for (int i = 0; i < targets.Count; i += 64)
            {
                _batch.Add(CommandKind.DebugKill, targets.GetRange(i, Math.Min(64, targets.Count - i)).ToArray());
            }
        }
    }
}
