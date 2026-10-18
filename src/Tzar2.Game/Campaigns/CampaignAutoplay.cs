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
    /// <summary>Most ticks per mission before it counts as not completed (default 75 minutes of game time; some original missions wait an hour on a timer).</summary>
    public int MaxTicksPerMission { get; init; } = 90_000;

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
public sealed record CampaignReport(string CampaignId, bool Completed, ImmutableArray<MissionReport> Missions, CampaignProgress Progress, string? Error)
{
    /// <summary>Distinct maps completed at least once.</summary>
    public int MapsCompleted => Missions.Where(m => m.Completed).Select(m => m.MapId).Distinct(StringComparer.Ordinal).Count();

    /// <summary>Distinct maps completed without any assistance (the secondary number of ADR 0134).</summary>
    public int MapsByPurePlay => Missions.Where(m => m.Completed && m.Assist == "play").Select(m => m.MapId).Distinct(StringComparer.Ordinal).Count();
}

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

    // Where an entity is: its own position, or its holder's while it is inside one.
    private static Position Pos(World world, EntityId e)
    {
        for (int depth = 0; depth < 4 && !world.Positions.Has(e) && world.Helds.IndexOf(e) is >= 0 and var h; depth++)
        {
            e = EntityId.FromValue(world.Helds.At(h).Holder);
        }

        return world.Positions.Has(e) ? world.Positions.Get(e) : default;
    }

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
                    // Retried with escalating assistance like the maps on the path.
                    for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
                    {
                        (MissionReport report, _) = Play(campaign, source.Load(m.Id), m.Id, ruleset, alone, options, attempt);
                        missions.Add(report);
                        log?.Invoke($"{m.Id} (not on the played path): {(report.Completed ? "completed" : "not completed")} ({report.Result}, {report.Ticks} ticks, {report.Assist})");
                        if (report.Completed)
                        {
                            break;
                        }
                    }
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

    /// <summary>
    /// The commands the solver may issue (ADR 0134): orders any player could give, and assistance that only stands in
    /// for player skill — debug spawns and kills, resources, invulnerability. Nothing that sets a script variable, fires
    /// a trigger or skips a scripted step (no <c>hmnext</c>, no scenario state writes: the solver only reads the trigger
    /// state).
    /// </summary>
    public static bool IsAllowedAssist(CommandKind kind, int arg0) => kind switch
    {
        CommandKind.Move or CommandKind.Attack or CommandKind.Stop or CommandKind.Board or CommandKind.Unload or CommandKind.ScenarioReply => true,
        CommandKind.PickItem or CommandKind.DropItem or CommandKind.SetDiplomacy => true,
        CommandKind.DebugSpawn or CommandKind.DebugKill => true,
        CommandKind.Cheat => arg0 is (int)Sim.Systems.CheatCode.God or (int)Sim.Systems.CheatCode.LargeResources or (int)Sim.Systems.CheatCode.SmallResources,
        _ => false,
    };

    private static void EnsureAllowed(CommandList batch)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            if (!IsAllowedAssist(batch[i].Kind, batch[i].Arg0))
            {
                throw new InvalidOperationException($"The solver may not issue {batch[i].Kind} ({batch[i].Arg0}): only player orders and skill-replacing assistance are allowed (ADR 0134).");
            }
        }
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
        var rejected = new SortedDictionary<string, int>(StringComparer.Ordinal);
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
                if (e.Kind == Sim.Events.SimEventKind.CommandRejected)
                {
                    string key = $"{e.Player + 1}:{(Sim.Events.CommandRejection)e.Arg}";
                    rejected[key] = rejected.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }

            events.Clear();
            PlayerFlags flags = session.Simulation.World.Players[session.LocalPlayer].Flags;
            if (transition || (flags & (PlayerFlags.Victorious | PlayerFlags.Defeated)) != 0)
            {
                ticks++;
                break;
            }
        }

        string trace = Trace(session) + " | goal: " + pilot.Goal + (pilot.Log.Count > 0 ? " | log: " + string.Join("; ", pilot.Log) : string.Empty)
            + (rejected.Count > 0 ? " | rejected (kingdom:reason×n): " + string.Join(" ", rejected.Select(r => $"{r.Key}×{r.Value}")) : string.Empty);
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

        /// <summary>Notable decisions, once each (a scripted fight against an ally, a ferry, a breach…).</summary>
        public List<string> Log { get; } = [];

        private void Note(string text)
        {
            if (Log.Count < 32 && !Log.Contains(text))
            {
                Log.Add(text);
            }
        }

        /// <summary>
        /// Cheat assistance for a retry: <c>hmgod</c> for the local kingdom and every kingdom it may command whose units
        /// the scenario must keep alive (a Destroyed event in a rule that makes the local kingdom lose). Heroes that appear
        /// later are covered when they appear (each kingdom gets the code once: it toggles).
        /// </summary>
        public void Protect()
        {
            _protect = true;
            Assist = "god";
            GrantGod();
        }

        private bool _protect;
        private readonly SortedSet<int> _god = [];

        private void GrantGod()
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
                if (!_god.Add(k))
                {
                    continue;
                }

                var cheat = new CommandList();
                cheat.Add(CommandKind.Cheat, [], 0, 0, default, (int)Sim.Systems.CheatCode.God, 0);
                EnsureAllowed(cheat);
                session.SubmitAs((byte)k, cheat);
            }
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

            if (_protect && tick % 200 == 107)
            {
                GrantGod();
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

            if (_wars.Count > 0 && !Goal.Contains("fight the ally", StringComparison.Ordinal))
            {
                MakePeace(world, me);
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
                EnsureAllowed(_batch);
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
            Content.Compiled.Scenario.Expressions.AssignExpr a => UsesResources(a.Target) || UsesResources(a.Value),
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
            Position from = Pos(world, _army[0]);
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

                        Position at = Pos(world, threat);
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
        /// The scenario's next goal (ADRs 0133, 0135): the first rule still enabled whose condition waits for an event (not
        /// under a negation) that is false, and that the local kingdom can make true — walk the
        /// named own units (or the army for typed specifications) into an Object In Area area, board a holder, or
        /// attack the named objects of a Destroyed event that are not its own. A rule gated on a user variable that is
        /// still 0, or on objects that have not appeared yet, first pursues the rules that set the variable (in an action
        /// or a conversation's phrase result) or make the objects appear.
        /// </summary>
        private bool Pursue(World world, byte me, bool armyBusy)
        {
            _armyBusy = armyBusy;
            if (session.Scenario?.Triggers is not { } triggers)
            {
                return false;
            }

            _setters ??= Setters(scenario);
            _protected = Scripted(world, me, triggers);
            _props = Props(world, triggers);
            var visited = new HashSet<int>();
            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                // Never pursue what makes the local kingdom lose.
                if (triggers.State.RuleDisabled[r] || scenario.Rules[r].Condition.Root is null || LosesMe(scenario.Rules[r], me))
                {
                    continue;
                }

                if (PursueRule(world, me, triggers, r, 0, visited))
                {
                    return true;
                }
            }

            return false;
        }

        private bool PursueRule(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers, int r, int depth, HashSet<int> visited)
        {
            if (!visited.Add(r) || scenario.Rules[r].Condition.Root is not { } condition)
            {
                return false;
            }

            // A rule waiting on the player's stockpile ($wood > 40, @gold(1) > 200): cheat assistance gives resources.
            if (options.CheatAssist && _tick - _lastResources >= 2000 && UsesResources(condition))
            {
                _lastResources = _tick;
                _batch.Add(CommandKind.Cheat, [], 0, 0, default, (int)Sim.Systems.CheatCode.LargeResources, 0);
                Assist = Assist == "play" ? "resources" : Assist;
            }

            // Gates first: variables still 0 that some other rule sets, objects some other rule makes appear. While a gate
            // is open the rule's own events are premature (walking the heroes off before the conversation that needs
            // them), so only the gate's rules are pursued.
            var gates = new List<int>();
            bool gated = false;
            var variables = new List<int>();
            Variables(condition, negated: false, variables);
            foreach (int v in variables)
            {
                if ((uint)v < (uint)triggers.State.Variables.Length && triggers.State.Variables[v] == 0 && _setters!.TryGetValue(v, out List<int>? rules))
                {
                    gated = true;
                    gates.AddRange(rules);
                }
            }

            var wanted = new List<int>();
            Wanted(condition, negated: false, wanted);
            foreach (int e in wanted)
            {
                if (!triggers.State.EventValues[e])
                {
                    foreach (int hidden in HiddenObjects(scenario.Events[e], triggers))
                    {
                        List<int> appearing = Appearing(hidden);
                        gated |= appearing.Count > 0;
                        gates.AddRange(appearing);
                    }
                }
            }

            if (gated)
            {
                if (depth < 6)
                {
                    foreach (int g in gates)
                    {
                        if (!triggers.State.RuleDisabled[g] && !LosesMe(scenario.Rules[g], me) && PursueRule(world, me, triggers, g, depth + 1, visited))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            // Loading holders before moving them: a "$cargo >= N" goal sends the holders to the units, an area goal for
            // the same holders then takes them away loaded.
            wanted.Sort((a, b) => IsCargo(scenario.Events[b]).CompareTo(IsCargo(scenario.Events[a])));
            foreach (int e in wanted)
            {
                if (!triggers.State.EventValues[e] && Act(world, me, triggers, scenario.Events[e]))
                {
                    return true;
                }
            }

            return false;
        }

        private Dictionary<int, List<int>>? _setters;
        private HashSet<EntityId> _protected = [];
        private HashSet<EntityId> _props = [];

        // Objects the scenario's pending rules still erase or destroy (ships that sail off and vanish):
        // never used as ferries, or the units aboard go down with them.
        private HashSet<EntityId> Props(World world, Scripting.Legacy.LegacyTriggerSystem triggers)
        {
            var set = new HashSet<EntityId>();
            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                if (triggers.State.RuleDisabled[r])
                {
                    continue;
                }

                foreach (int a in scenario.Rules[r].ActionIndices)
                {
                    ObjectSpec? spec = (uint)a < (uint)scenario.Actions.Length ? scenario.Actions[a] switch
                    {
                        EraseObjectAction x => x.Objects,
                        DestroyObjectAction x => x.Objects,
                        _ => null,
                    } : null;
                    if (spec is NamedObjects named)
                    {
                        foreach (int index in named.ObjectIndices)
                        {
                            if (Entity(world, triggers, index) is { } e)
                            {
                                set.Add(e);
                            }
                        }
                    }
                }
            }

            return set;
        }

        // For each user variable, the rules that set it: an action of the rule assigns it, or the rule starts a
        // conversation one of whose phrases does in its result.
        private static Dictionary<int, List<int>> Setters(ScenarioData scenario)
        {
            var byConversation = new Dictionary<int, List<int>>();
            for (int c = 0; c < scenario.Conversations.Length; c++)
            {
                var assigned = new List<int>();
                foreach (PhraseDef phrase in scenario.Conversations[c].Phrases)
                {
                    if (phrase.Result.Root is { } result)
                    {
                        Assigned(result, assigned);
                    }
                }

                byConversation[c] = assigned;
            }

            var setters = new Dictionary<int, List<int>>();
            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                var assigned = new List<int>();
                foreach (int a in scenario.Rules[r].ActionIndices)
                {
                    switch ((uint)a < (uint)scenario.Actions.Length ? scenario.Actions[a] : null)
                    {
                        case ExpressionAction { Expression.Root: { } root }:
                            Assigned(root, assigned);
                            break;
                        case ConversationAction talk when byConversation.TryGetValue(talk.ConversationIndex, out List<int>? vars):
                            assigned.AddRange(vars);
                            break;
                    }
                }

                foreach (int v in assigned)
                {
                    if (!setters.TryGetValue(v, out List<int>? list))
                    {
                        setters[v] = list = [];
                    }

                    if (!list.Contains(r))
                    {
                        list.Add(r);
                    }
                }
            }

            return setters;
        }

        // User variables an expression assigns a value other than 0 to.
        private static void Assigned(Content.Compiled.Scenario.Expressions.Expr node, List<int> into)
        {
            switch (node)
            {
                case Content.Compiled.Scenario.Expressions.AssignExpr { Target: Content.Compiled.Scenario.Expressions.UserVar v } a
                    when a.Value is not Content.Compiled.Scenario.Expressions.IntLiteral { Value: 0 }:
                    into.Add(v.VariableIndex);
                    break;
                case Content.Compiled.Scenario.Expressions.SequenceExpr q:
                    foreach (Content.Compiled.Scenario.Expressions.Expr statement in q.Statements)
                    {
                        Assigned(statement, into);
                    }

                    break;
            }
        }

        // User variables a condition needs set (not under '!', not compared equal to 0).
        private static void Variables(Content.Compiled.Scenario.Expressions.Expr node, bool negated, List<int> into)
        {
            switch (node)
            {
                case Content.Compiled.Scenario.Expressions.UserVar v when !negated && !into.Contains(v.VariableIndex):
                    into.Add(v.VariableIndex);
                    break;
                case Content.Compiled.Scenario.Expressions.BinaryExpr { Op: Content.Compiled.Scenario.Expressions.BinaryOp.Equal or Content.Compiled.Scenario.Expressions.BinaryOp.LessOrEqual } zero
                    when zero.Right is Content.Compiled.Scenario.Expressions.IntLiteral { Value: 0 } || zero.Left is Content.Compiled.Scenario.Expressions.IntLiteral { Value: 0 }:
                    break; // "$v == 0" wants it unset
                case Content.Compiled.Scenario.Expressions.UnaryExpr u:
                    Variables(u.Operand, negated ^ (u.Op == Content.Compiled.Scenario.Expressions.UnaryOp.Not), into);
                    break;
                case Content.Compiled.Scenario.Expressions.BinaryExpr b:
                    Variables(b.Left, negated, into);
                    Variables(b.Right, negated, into);
                    break;
                case Content.Compiled.Scenario.Expressions.SequenceExpr q:
                    foreach (Content.Compiled.Scenario.Expressions.Expr statement in q.Statements)
                    {
                        Variables(statement, negated, into);
                    }

                    break;
            }
        }

        // Named objects of an event (its objects, its area's object) that have not appeared yet.
        private static List<int> HiddenObjects(EventDef e, Scripting.Legacy.LegacyTriggerSystem triggers)
        {
            var hidden = new List<int>();
            void Add(int index)
            {
                if ((uint)index < (uint)triggers.State.Hidden.Length && triggers.State.Hidden[index] && !hidden.Contains(index))
                {
                    hidden.Add(index);
                }
            }

            ObjectSpec? objects = e switch
            {
                ObjectInAreaEvent x => x.Objects,
                ObjectExpressionEvent x => x.Objects,
                ObjectsEnterHolderEvent x => x.Objects,
                _ => null,
            };
            if (objects is NamedObjects named)
            {
                foreach (int index in named.ObjectIndices)
                {
                    Add(index);
                }
            }

            if (e is ObjectInAreaEvent { Area: ObjectArea around })
            {
                Add(around.ObjectIndex);
            }

            return hidden;
        }

        private Dictionary<int, List<int>>? _appearing;

        // The rules whose actions make a named object appear (indexed once per mission).
        private List<int> Appearing(int index)
        {
            _appearing ??= AppearingIndex();
            return _appearing.TryGetValue(index, out List<int>? rules) ? rules : [];
        }

        private Dictionary<int, List<int>> AppearingIndex()
        {
            var byObject = new Dictionary<int, List<int>>();
            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                foreach (int a in scenario.Rules[r].ActionIndices)
                {
                    if ((uint)a < (uint)scenario.Actions.Length && scenario.Actions[a] is AppearObjectAction { Objects: NamedObjects named })
                    {
                        foreach (int index in named.ObjectIndices)
                        {
                            if (!byObject.TryGetValue(index, out List<int>? rules))
                            {
                                byObject[index] = rules = [];
                            }

                            if (!rules.Contains(r))
                            {
                                rules.Add(r);
                            }
                        }
                    }
                }
            }

            return byObject;
        }

        // Objects of other kingdoms the scenario moves itself (named in an area event of an enabled rule): the solver's
        // fights and clearing leave them alone, or a scripted arrival never comes.
        private HashSet<EntityId> Scripted(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers)
        {
            var set = new HashSet<EntityId>();
            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                if (triggers.State.RuleDisabled[r] || scenario.Rules[r].Condition.Root is not { } condition)
                {
                    continue;
                }

                var wanted = new List<int>();
                Wanted(condition, negated: false, wanted);
                bool loses = LosesMe(scenario.Rules[r], me);
                foreach (int e in wanted)
                {
                    // The heroes a losing rule must keep alive are never cleared, whoever owns them.
                    if (loses && scenario.Events[e] is ObjectDestroyedEvent { Objects: NamedObjects keep })
                    {
                        foreach (int index in keep.ObjectIndices)
                        {
                            if (Entity(world, triggers, index) is { } u)
                            {
                                set.Add(u);
                            }
                        }
                    }

                    if (scenario.Events[e] is ObjectInAreaEvent { Objects: NamedObjects named })
                    {
                        foreach (int index in named.ObjectIndices)
                        {
                            if (Entity(world, triggers, index) is { } u && !world.Diplomacy.MayCommand(me, world.Identities.Get(u).Owner))
                            {
                                set.Add(u);
                            }
                        }
                    }
                }
            }

            return set;
        }

        private static bool IsCargo(EventDef e) => e is ObjectExpressionEvent x && Comparison(x.Expression.Root, Content.Compiled.Scenario.Expressions.ReservedVariable.Cargo) is not null;

        // "$var OP n" (or "n OP $var") of a reserved variable: the operator as if the variable were on the left, and n.
        private static (Content.Compiled.Scenario.Expressions.BinaryOp Op, int Value)? Comparison(Content.Compiled.Scenario.Expressions.Expr? node, Content.Compiled.Scenario.Expressions.ReservedVariable variable)
        {
            if (node is not Content.Compiled.Scenario.Expressions.BinaryExpr b)
            {
                return null;
            }

            if (b.Left is Content.Compiled.Scenario.Expressions.ReservedVar l && l.Variable == variable && b.Right is Content.Compiled.Scenario.Expressions.IntLiteral n)
            {
                return (b.Op, n.Value);
            }

            if (b.Right is Content.Compiled.Scenario.Expressions.ReservedVar r && r.Variable == variable && b.Left is Content.Compiled.Scenario.Expressions.IntLiteral m)
            {
                return (b.Op switch
                {
                    Content.Compiled.Scenario.Expressions.BinaryOp.Less => Content.Compiled.Scenario.Expressions.BinaryOp.Greater,
                    Content.Compiled.Scenario.Expressions.BinaryOp.LessOrEqual => Content.Compiled.Scenario.Expressions.BinaryOp.GreaterOrEqual,
                    Content.Compiled.Scenario.Expressions.BinaryOp.Greater => Content.Compiled.Scenario.Expressions.BinaryOp.Less,
                    Content.Compiled.Scenario.Expressions.BinaryOp.GreaterOrEqual => Content.Compiled.Scenario.Expressions.BinaryOp.LessOrEqual,
                    _ => b.Op,
                }, m.Value);
            }

            return null;
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
                case ObjectInAreaEvent { Objects: NamedObjects items } carry when Target(world, triggers, carry.Area) is { } spot && Carried(world, triggers, items, me) is { } item:
                    return Deliver(world, me, triggers, carry, item, spot);

                case ObjectInAreaEvent inArea when Target(world, triggers, inArea.Area) is { } point:
                {
                    List<EntityId> units = OwnUnits(world, me, triggers, inArea.Objects, point);
                    if (units.Count == 0)
                    {
                        Goal = $"{inArea.Name}: no unit of mine can go";
                        return false;
                    }

                    return Go(world, me, triggers, inArea.Name, units, inArea.Area, point, individually: inArea.Objects is NamedObjects && units.Count <= 8);
                }

                // "$x <= 5", "$y >= 200" about own named units: walk them over the line.
                case ObjectExpressionEvent { Objects: NamedObjects } line when Line(world, line.Expression.Root) is { } bound:
                {
                    List<EntityId> units = OwnUnits(world, me, triggers, line.Objects, null);
                    var moved = new List<EntityId>();
                    var stranded = new List<EntityId>();
                    foreach (EntityId u in units)
                    {
                        Position at = Pos(world, u);
                        Position? to = Across(world, u, bound.Vertical, bound.Limit, bound.Below, at);
                        if (to is { } p)
                        {
                            _batch.Add(CommandKind.Move, [u], p.X, p.Y);
                            moved.Add(u);
                        }
                        else
                        {
                            stranded.Add(u);
                        }
                    }

                    Goal = $"{line.Name}: {moved.Count} of {units.Count} units over the line";
                    if (stranded.Count > 0)
                    {
                        // Over the line only by water: a ferry to ground beyond the line (the nearest walkable tile on
                        // the far side, nearest the units' row or column).
                        if (Beyond(world, stranded[0], bound.Vertical, bound.Limit, bound.Below) is not { } beyond)
                        {
                            Goal = $"{line.Name}: no ground beyond the line";
                            return moved.Count > 0;
                        }

                        return Ferry(world, me, line.Name, stranded, beyond) || moved.Count > 0;
                    }

                    return moved.Count > 0;
                }

                // "$cargo >= N" about own named holders (ships): board that many of the nearest free units into each.
                case ObjectExpressionEvent { Objects: NamedObjects holders } cargo when Comparison(cargo.Expression.Root, Content.Compiled.Scenario.Expressions.ReservedVariable.Cargo) is { } want
                    && want.Op is Content.Compiled.Scenario.Expressions.BinaryOp.Greater or Content.Compiled.Scenario.Expressions.BinaryOp.GreaterOrEqual:
                    return Load(world, me, triggers, cargo.Name, holders, want.Op == Content.Compiled.Scenario.Expressions.BinaryOp.Greater ? want.Value + 1 : want.Value);

                // An item "entering" a unit: that unit picks it up (a hero takes the letter).
                case ObjectsEnterHolderEvent { Objects: NamedObjects letters } take when take.HolderIndex >= 0 && Entity(world, triggers, take.HolderIndex) is { } taker
                    && world.Diplomacy.MayCommand(me, world.Identities.Get(taker).Owner) && world.Inventories.Has(taker) && Carried(world, triggers, letters, me) is { } letter && !world.IsHeld(letter):
                {
                    Position where = Pos(world, letter);
                    var around = new RectArea((where.X / Tile) - 1, (where.Y / Tile) - 1, (where.X / Tile) + 1, (where.Y / Tile) + 1);
                    if (Reach(world, triggers, taker, around, where) is null)
                    {
                        return Go(world, me, triggers, take.Name, [taker], around, where, individually: true);
                    }

                    _batch.Add(CommandKind.PickItem, [taker], 0, 0, letter);
                    Goal = $"{take.Name}: pick the item up at ({where.X / Tile},{where.Y / Tile})";
                    Note($"take an item for {take.Name}");
                    return true;
                }

                case ObjectsEnterHolderEvent board when board.HolderIndex >= 0 && Entity(world, triggers, board.HolderIndex) is { } holder:
                {
                    List<EntityId> units = OwnUnits(world, me, triggers, board.Objects, Pos(world, holder));
                    if (units.Count == 0 || !world.Diplomacy.MayCommand(me, world.Identities.Get(holder).Owner))
                    {
                        return false;
                    }

                    _batch.Add(CommandKind.Board, units.ToArray(), 0, 0, holder);
                    Goal = $"{board.Name}: board";
                    return true;
                }

                case ObjectDestroyedEvent { Objects: NamedObjects named } when _army.Count > 0:
                    return Fight(world, me, triggers, e.Name, named, weaken: false);

                // An expression about other kingdoms' named objects (typically "$health <= 50"): attack them.
                case ObjectExpressionEvent { Objects: NamedObjects named } x when _army.Count > 0 && x.Expression.Source.Contains("health", StringComparison.OrdinalIgnoreCase):
                    return Fight(world, me, triggers, x.Name, named, weaken: true);

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

                    Position at = _army.Count > 0 ? Pos(world, _army[0]) : MyAnchor(world, me);
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
                        Position at = Pos(world, targets[0]);
                        _batch.Add(CommandKind.Move, _army.ToArray(), at.X, at.Y, default, 0, 0, CommandFlags.Ctrl);
                    }

                    Goal = $"{exist.Name}: {targets.Count} objects to remove";
                    return true;
                }

                default:
                    return false;
            }
        }

        // Units to an area: each to a point of the area it can walk to. A unit that cannot reach the area waits for a
        // breach (an enemy wall or building next to the area is attacked) or a ferry (one of its kingdom's ships takes
        // it there, ADR 0140's boarding and unloading).
        private bool Go(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers, string name, List<EntityId> units, AreaSpec area, Position point, bool individually)
        {
            var stranded = new List<EntityId>();
            var walkers = new List<EntityId>();
            Position walkTo = point;
            foreach (EntityId u in units)
            {
                if (Reach(world, triggers, u, area, point) is { } to)
                {
                    Track(world, u);
                    walkers.Add(u);
                    walkTo = to;
                    if (individually)
                    {
                        _batch.Add(CommandKind.Move, [u], to.X, to.Y);
                    }
                }
                else
                {
                    stranded.Add(u);
                }
            }

            if (!individually && walkers.Count > 0)
            {
                _batch.Add(CommandKind.Move, walkers.ToArray(), walkTo.X, walkTo.Y);
            }
            else if (walkers.Any(_stuck.Contains) && _army.Where(u => !units.Contains(u)).ToArray() is { Length: > 0 } crowd)
            {
                // Hemmed in by their own kingdom's crowd: the army walks the same way and makes room.
                _batch.Add(CommandKind.Move, crowd, walkTo.X, walkTo.Y);
                Note($"the army makes way for {name}");
            }

            Goal = $"{name} with {walkers.Count} units to ({walkTo.X / Tile},{walkTo.Y / Tile}) from {string.Join(" ", units.ConvertAll(u => $"{world.Ruleset.EntityTypes[world.Identities.Get(u).TypeId].Key}/{world.Identities.Get(u).Owner}({Pos(world, u).X / Tile},{Pos(world, u).Y / Tile}){(world.IsHeld(u) ? "held" : "")}{(world.Motions.Has(u) ? world.Motions.Get(u).State.ToString() : string.Empty)}"))}";
            if (stranded.Count == 0)
            {
                return true;
            }

            // An area walled in (a small pocket of ground): breach first; otherwise (across water) ferry first.
            bool pocket = Pocket(world, stranded[0], point);
            return (pocket && Breach(world, me, name, stranded, point)) || Ferry(world, me, name, stranded, point) || (!pocket && Breach(world, me, name, stranded, point)) || walkers.Count > 0;
        }

        // Whether the ground around the point, as the unit walks, is a pocket closed within 12 tiles of it.
        private readonly HashSet<int> _seen = [];
        private readonly Queue<int> _queue = new();

        private bool Pocket(World world, EntityId unit, Position point)
        {
            if (!world.Navigation.IsBuilt || world.Navigation.ForType(world.Identities.Get(unit).TypeId) is not { } nav)
            {
                return false;
            }

            const int Reach = 12;
            int cx = Math.Clamp(point.X / Tile, 0, nav.Width - 1);
            int cy = Math.Clamp(point.Y / Tile, 0, nav.Height - 1);
            int start = -1;
            for (int r = 0; r <= 2 && start < 0; r++)
            {
                for (int dy = -r; dy <= r && start < 0; dy++)
                {
                    for (int dx = -r; dx <= r && start < 0; dx++)
                    {
                        if (nav.IsWalkable(cx + dx, cy + dy))
                        {
                            start = ((cy + dy) * nav.Width) + cx + dx;
                        }
                    }
                }
            }

            if (start < 0)
            {
                return false;
            }

            HashSet<int> seen = _seen;
            Queue<int> queue = _queue;
            seen.Clear();
            queue.Clear();
            seen.Add(start);
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                int x = t % nav.Width;
                int y = t / nav.Width;
                if (Math.Abs(x - cx) >= Reach || Math.Abs(y - cy) >= Reach)
                {
                    return false; // the ground goes on: not a pocket
                }

                foreach ((int nx, int ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                {
                    int n = (ny * nav.Width) + nx;
                    if (nav.IsWalkable(nx, ny) && seen.Add(n))
                    {
                        queue.Enqueue(n);
                    }
                }
            }

            return true;
        }

        private readonly Dictionary<uint, (int X, int Y, int Since)> _moving = [];
        private readonly HashSet<EntityId> _stuck = [];

        // A unit sent somewhere that has not moved a tile in 30 seconds is stuck: typed goals pick other units.
        private void Track(World world, EntityId unit)
        {
            Position p = Pos(world, unit);
            if (_moving.TryGetValue(unit.Value, out (int X, int Y, int Since) m) && Math.Abs(m.X - p.X) < Tile && Math.Abs(m.Y - p.Y) < Tile)
            {
                if (_tick - m.Since >= 600 && _stuck.Add(unit))
                {
                    Note($"a {world.Ruleset.EntityTypes[world.Identities.Get(unit).TypeId].Key} got stuck at ({p.X / Tile},{p.Y / Tile})");
                }
            }
            else
            {
                _moving[unit.Value] = (p.X, p.Y, _tick);
                _stuck.Remove(unit);
            }
        }

        // A point of the area that the unit can walk to from where it stands (its navigation layer connects them),
        // nearest to the area's centre; the centre when the grid is not built yet; null when no point of it is reachable.
        private static Position? Reach(World world, Scripting.Legacy.LegacyTriggerSystem triggers, EntityId unit, AreaSpec area, Position centre)
        {
            if (world.IsHeld(unit))
            {
                return null;
            }

            if (!world.Navigation.IsBuilt || world.Navigation.ForType(world.Identities.Get(unit).TypeId) is not { } nav)
            {
                return centre;
            }

            Position at = Pos(world, unit);
            int from = nav.TileOf(at.X, at.Y);
            int cx = centre.X / Tile;
            int cy = centre.Y / Tile;
            (int x0, int y0, int x1, int y1) = area switch
            {
                RectArea r => (r.Left, r.Top, r.Right, r.Bottom),
                ObjectArea o => (cx - Math.Max(1, o.RadiusTiles ?? 1), cy - Math.Max(1, o.RadiusTiles ?? 1), cx + Math.Max(1, o.RadiusTiles ?? 1), cy + Math.Max(1, o.RadiusTiles ?? 1)),
                _ => (cx, cy, cx, cy),
            };
            if (area is RectArea)
            {
                // A rectangle: its interior (off the edge when it is wide enough), the point nearest the unit.
                (x0, x1) = x1 - x0 >= 2 ? (x0 + 1, x1 - 1) : (x0, x1);
                (y0, y1) = y1 - y0 >= 2 ? (y0 + 1, y1 - 1) : (y0, y1);
                cx = Math.Clamp(at.X / Tile, x0, x1);
                cy = Math.Clamp(at.Y / Tile, y0, y1);
            }

            x0 = Math.Clamp(Math.Max(x0, cx - 32), 0, nav.Width - 1);
            x1 = Math.Clamp(Math.Min(x1, cx + 32), 0, nav.Width - 1);
            y0 = Math.Clamp(Math.Max(y0, cy - 32), 0, nav.Height - 1);
            y1 = Math.Clamp(Math.Min(y1, cy + 32), 0, nav.Height - 1);
            long best = long.MaxValue;
            int bestTile = -1;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int tile = (y * nav.Width) + x;
                    long d = ((long)(x - cx) * (x - cx)) + ((long)(y - cy) * (y - cy));
                    if (d < best && nav.AreConnected(from, tile))
                    {
                        best = d;
                        bestTile = tile;
                    }
                }
            }

            return bestTile < 0 ? null : new Position(nav.CenterX(bestTile), nav.CenterY(bestTile));
        }

        // Breach: an enemy building (a wall, a tower) within a few tiles of an area the units cannot reach is attacked
        // by them and the army.
        private bool Breach(World world, byte me, string name, List<EntityId> stranded, Position point)
        {
            Position from = Pos(world, stranded[0]);
            EntityId wall = EntityId.None;
            long best = long.MaxValue;
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length; i++)
            {
                if (!IsEnemy(world, me, identities[i]) || world.Ruleset.EntityTypes[identities[i].TypeId].Traits.Category != EntityCategory.Building || !world.Positions.Has(ids[i]))
                {
                    continue;
                }

                Position p = Pos(world, ids[i]);
                if (Math.Abs(p.X - point.X) > 6 * Tile || Math.Abs(p.Y - point.Y) > 6 * Tile)
                {
                    continue;
                }

                long dx = p.X - from.X;
                long dy = p.Y - from.Y;
                if ((dx * dx) + (dy * dy) < best)
                {
                    best = (dx * dx) + (dy * dy);
                    wall = ids[i];
                }
            }

            if (wall.IsNone)
            {
                return false;
            }

            var attackers = new List<EntityId>(stranded.Where(u => world.Combats.Has(u) && !world.IsHeld(u)));
            attackers.AddRange(_army.Where(u => !attackers.Contains(u)));
            if (attackers.Count == 0)
            {
                return false;
            }

            Position w = Pos(world, wall);
            _batch.Add(CommandKind.Attack, attackers.ToArray(), 0, 0, wall);
            Goal = $"{name}: breach {world.Ruleset.EntityTypes[world.Identities.Get(wall).TypeId].Key} at ({w.X / Tile},{w.Y / Tile})";
            Note($"breach at ({w.X / Tile},{w.Y / Tile}) for {name}");
            return true;
        }

        private readonly Dictionary<uint, int> _ferried = [];

        // Ferry (ADR 0140): units that cannot walk to the point board one of their kingdom's ships (it sails to the shore
        // nearest them), and the ship unloads them at the shore nearest the point (they walk on to it). Orders are
        // repeated at most every 30 seconds: a new order to the ship drops its pending unload.
        private bool Ferry(World world, byte me, string name, List<EntityId> stranded, Position point)
        {
            // Aboard already: unload at the point, once the others have boarded too (or a minute has passed).
            foreach (EntityId u in stranded)
            {
                if (world.Helds.IndexOf(u) is >= 0 and var h && EntityId.FromValue(world.Helds.At(h).Holder) is var ship && world.IsShip(ship)
                    && world.Diplomacy.MayCommand(me, world.Identities.Get(ship).Owner))
                {
                    if (stranded.Any(o => !world.IsHeld(o)) && _ferried.TryGetValue(ship.Value, out int boarded) && _tick - boarded < 1200)
                    {
                        Goal = $"{name}: waiting for {stranded.Count(o => !world.IsHeld(o))} more to board";
                        return true;
                    }

                    if (!Due(ship))
                    {
                        Goal = $"{name}: ferrying to ({point.X / Tile},{point.Y / Tile})";
                        return true;
                    }

                    _batch.Add(CommandKind.Unload, [ship], point.X, point.Y);
                    Goal = $"{name}: ferry unloads at ({point.X / Tile},{point.Y / Tile})";
                    Note($"ferry to ({point.X / Tile},{point.Y / Tile}) for {name}");
                    return true;
                }
            }

            EntityId? best = null;
            long nearest = long.MaxValue;
            Position from = Pos(world, stranded[0]);
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length; i++)
            {
                if (!world.Diplomacy.MayCommand(me, identities[i].Owner) || !world.IsShip(ids[i]) || world.IsHeld(ids[i]) || _props.Contains(ids[i])
                    || world.Ruleset.EntityTypes[identities[i].TypeId].Traits.Holder is not { } holder
                    || holder.Capacity - (world.Holders.IndexOf(ids[i]) is >= 0 and var hs ? world.Holders.At(hs).Count : 0) < stranded.Count)
                {
                    continue;
                }

                Position p = Pos(world, ids[i]);
                long dx = p.X - from.X;
                long dy = p.Y - from.Y;
                if ((dx * dx) + (dy * dy) < nearest)
                {
                    nearest = (dx * dx) + (dy * dy);
                    best = ids[i];
                }
            }

            if (best is not { } boat)
            {
                return false;
            }

            if (Due(boat))
            {
                _batch.Add(CommandKind.Board, stranded.Where(u => !world.IsHeld(u)).ToArray(), 0, 0, boat);
                Note($"ferry boards {stranded.Count} for {name}");
            }

            Goal = $"{name}: {stranded.Count} units board a ship at ({Pos(world, boat).X / Tile},{Pos(world, boat).Y / Tile})";
            return true;
        }

        // Whether an order to this ship may be given again (at most every 30 seconds).
        private bool Due(EntityId ship)
        {
            if (_ferried.TryGetValue(ship.Value, out int last) && _tick - last < 600)
            {
                return false;
            }

            _ferried[ship.Value] = _tick;
            return true;
        }

        // "$cargo >= N" about named holders: the nearest free units of the army board each holder short of N.
        private bool Load(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers, string name, NamedObjects holders, int want)
        {
            var taken = new HashSet<EntityId>();
            bool any = false;
            foreach (int index in holders.ObjectIndices)
            {
                if (Entity(world, triggers, index) is not { } holder || !world.Diplomacy.MayCommand(me, world.Identities.Get(holder).Owner)
                    || world.Ruleset.EntityTypes[world.Identities.Get(holder).TypeId].Traits.Holder is not { } definition)
                {
                    continue;
                }

                int inside = world.Holders.IndexOf(holder) is >= 0 and var hs ? world.Holders.At(hs).Count : 0;
                int missing = Math.Min(want, definition.Capacity) - inside;
                if (missing <= 0)
                {
                    continue;
                }

                Position at = Pos(world, holder);
                List<EntityId> free = _army.Where(u => !taken.Contains(u) && definition.CanHold(world.Identities.Get(u).TypeId))
                    .OrderBy(u => Distance(Pos(world, u), at)).ThenBy(u => u.Value).Take(missing).ToList();
                if (free.Count == 0)
                {
                    continue;
                }

                taken.UnionWith(free);
                any = true;
                if (Due(holder))
                {
                    _batch.Add(CommandKind.Board, free.ToArray(), 0, 0, holder);
                }
            }

            Goal = $"{name}: load {taken.Count} units";
            if (any)
            {
                Note($"load holders for {name}");
            }

            return any;
        }

        private static long Distance(Position a, Position b) => (((long)a.X - b.X) * ((long)a.X - b.X)) + (((long)a.Y - b.Y) * ((long)a.Y - b.Y));

        // Attack the named objects of a Destroyed or "$health" goal that are not the local kingdom's. An ally is fought
        // only when the goal needs it and no scripted fight already does (ADR 0135): the local kingdom declares war on its
        // kingdom (a player's diplomacy order, logged), fights it with its own units, and makes peace again afterwards.
        private bool Fight(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers, string name, NamedObjects named, bool weaken)
        {
            foreach (int index in named.ObjectIndices)
            {
                // Never the local kingdom's own units, nor those it commands (its heroes).
                if (Entity(world, triggers, index) is not { } target || world.Identities.Get(target).Owner is var owner && world.Diplomacy.MayCommand(me, owner))
                {
                    continue;
                }

                Position t = Pos(world, target);
                bool ally = world.HasPlayer(owner) && world.Diplomacy.StanceOf(me, owner) == Sim.Kingdoms.Stance.Ally;
                if (!ally)
                {
                    _batch.Add(CommandKind.Attack, _army.ToArray(), 0, 0, target);
                    Goal = $"{name}: attack ({t.X / Tile},{t.Y / Tile})";
                    return true;
                }

                if (FoughtByScript(world, target) || StagedByScript(index))
                {
                    Goal = $"{name}: a scripted fight against the ally at ({t.X / Tile},{t.Y / Tile}) is on";
                    Note($"waited for the scripted fight of {name}");
                    return true;
                }

                if (!weaken)
                {
                    continue; // destroying an ally is never the player's goal; weakening one in a duel may be
                }

                EntityId[] mine = _army.Where(u => world.Identities.Get(u).Owner == me).ToArray();
                if (mine.Length == 0)
                {
                    continue;
                }

                if (world.Diplomacy.StanceOf(me, owner) != Sim.Kingdoms.Stance.Enemy && _tick - _lastDiplomacy >= 600)
                {
                    _lastDiplomacy = _tick;
                    _wars.Add(owner);
                    _batch.Add(CommandKind.SetDiplomacy, [], owner, 0, default, (int)Sim.Kingdoms.Stance.Enemy, 0);
                    Note($"declared war on kingdom {owner + 1} for {name}");
                }

                _batch.Add(CommandKind.Attack, mine, 0, 0, target);
                Goal = $"{name}: fight the ally at ({t.X / Tile},{t.Y / Tile})";
                return true;
            }

            return false;
        }

        private readonly SortedSet<int> _wars = [];
        private int _lastDiplomacy = int.MinValue / 2;

        // Peace again with the allies the solver fought for a goal, once no goal needs the fight.
        private void MakePeace(World world, byte me)
        {
            if (_tick - _lastDiplomacy < 600)
            {
                return; // the players' diplomacy rate limit
            }

            _wars.RemoveWhere(owner => !world.HasPlayer(owner) || world.Diplomacy.StanceOf(me, owner) == Sim.Kingdoms.Stance.Ally);
            foreach (int owner in _wars)
            {
                if (world.Diplomacy.StanceOf(me, owner) != Sim.Kingdoms.Stance.Ally)
                {
                    _lastDiplomacy = _tick;
                    _batch.Add(CommandKind.SetDiplomacy, [], owner, 0, default, (int)Sim.Kingdoms.Stance.Ally, (int)world.Diplomacy.OffersOf(me, owner));
                }
            }
        }

        // Whether an Object Order of the scenario attacks the object itself (a staged fight, ADR 0135).
        private bool StagedByScript(int index)
        {
            foreach (ActionDef a in scenario.Actions)
            {
                if (a is ObjectOrderAction { Area: ObjectArea around } order && around.ObjectIndex == index && order.OrderKey.Contains("attack", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool FoughtByScript(World world, EntityId target)
        {
            ReadOnlySpan<Combat> combats = world.Combats.Components;
            for (int i = 0; i < combats.Length; i++)
            {
                if (combats[i].Mode == CombatMode.Scripted && combats[i].Target == target.Value)
                {
                    return true;
                }
            }

            return false;
        }

        // An item a goal wants in an area, lying on the ground or carried by a unit the local kingdom commands.
        private static EntityId? Carried(World world, Scripting.Legacy.LegacyTriggerSystem triggers, NamedObjects named, byte me)
        {
            foreach (int index in named.ObjectIndices)
            {
                EntityId e = (uint)index < (uint)triggers.State.MapEntities.Length ? EntityId.FromValue(triggers.State.MapEntities[index]) : EntityId.None;
                if (world.Entities.IsAlive(e) && world.Items.Has(e))
                {
                    return e;
                }
            }

            return null;
        }

        // Deliver an item: a unit of the local kingdom with an inventory picks it up, walks to the area and drops it there.
        private bool Deliver(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers, ObjectInAreaEvent goal, EntityId item, Position spot)
        {
            if (world.Helds.IndexOf(item) is >= 0 and var h)
            {
                EntityId carrier = EntityId.FromValue(world.Helds.At(h).Holder);
                if (!world.Entities.IsAlive(carrier) || !world.Diplomacy.MayCommand(me, world.Identities.Get(carrier).Owner) || world.Inventories.IndexOf(carrier) is not (>= 0 and var inv))
                {
                    Goal = $"{goal.Name}: the item is carried by another kingdom";
                    return false;
                }

                Position at = Pos(world, carrier);
                if (Math.Abs(at.X - spot.X) <= Tile && Math.Abs(at.Y - spot.Y) <= Tile)
                {
                    Inventory items = world.Inventories.At(inv);
                    for (int k = 0; k < items.Count; k++)
                    {
                        if (items.Items[k] == item.Value)
                        {
                            _batch.Add(CommandKind.DropItem, [carrier], 0, 0, default, k, 0);
                            break;
                        }
                    }

                    Goal = $"{goal.Name}: drop the item";
                    return true;
                }

                return Go(world, me, triggers, goal.Name, [carrier], goal.Area, spot, individually: true);
            }

            Position where = Pos(world, item);
            EntityId? picker = null;
            long best = long.MaxValue;
            ReadOnlySpan<EntityId> ids = world.Inventories.Entities;
            for (int i = 0; i < ids.Length; i++)
            {
                if (world.Diplomacy.MayCommand(me, world.Identities.Get(ids[i]).Owner) && !world.IsHeld(ids[i]) && world.Motions.Has(ids[i])
                    && CanCarry(world, ids[i], item) && Distance(Pos(world, ids[i]), where) < best)
                {
                    best = Distance(Pos(world, ids[i]), where);
                    picker = ids[i];
                }
            }

            if (picker is not { } unit)
            {
                Goal = $"{goal.Name}: nobody to carry the item";
                return false;
            }

            var around = new RectArea((where.X / Tile) - 1, (where.Y / Tile) - 1, (where.X / Tile) + 1, (where.Y / Tile) + 1);
            if (Reach(world, triggers, unit, around, where) is null)
            {
                return Breach(world, me, goal.Name, [unit], where) || Ferry(world, me, goal.Name, [unit], where);
            }

            _batch.Add(CommandKind.PickItem, [unit], 0, 0, item);
            Goal = $"{goal.Name}: {world.Ruleset.EntityTypes[world.Identities.Get(unit).TypeId].Key}/{world.Identities.Get(unit).Owner} at ({Pos(world, unit).X / Tile},{Pos(world, unit).Y / Tile}) picks the item up at ({where.X / Tile},{where.Y / Tile})";
            Note($"carry an item for {goal.Name}");
            return true;
        }

        private static bool CanCarry(World world, EntityId unit, EntityId item)
            => world.Ruleset.EntityTypes[world.Identities.Get(item).TypeId].Traits.Item is { } it && it.CanBeHeldBy(world.Identities.Get(unit).TypeId)
                && world.Inventories.Get(unit).Count < world.Ruleset.EntityTypes[world.Identities.Get(unit).TypeId].Traits.InventorySlots;

        // "$x <= n" and the like: which coordinate, the limit tile and on which side.
        private static (bool Vertical, int Limit, bool Below)? Line(World world, Content.Compiled.Scenario.Expressions.Expr? root)
        {
            foreach (Content.Compiled.Scenario.Expressions.ReservedVariable v in new[] { Content.Compiled.Scenario.Expressions.ReservedVariable.X, Content.Compiled.Scenario.Expressions.ReservedVariable.Y })
            {
                if (Comparison(root, v) is { } c)
                {
                    int limit = c.Op switch
                    {
                        Content.Compiled.Scenario.Expressions.BinaryOp.Less => c.Value - 1,
                        Content.Compiled.Scenario.Expressions.BinaryOp.Greater => c.Value + 1,
                        _ => c.Value,
                    };
                    bool below = c.Op is Content.Compiled.Scenario.Expressions.BinaryOp.Less or Content.Compiled.Scenario.Expressions.BinaryOp.LessOrEqual;
                    if (c.Op is Content.Compiled.Scenario.Expressions.BinaryOp.Less or Content.Compiled.Scenario.Expressions.BinaryOp.LessOrEqual
                        or Content.Compiled.Scenario.Expressions.BinaryOp.Greater or Content.Compiled.Scenario.Expressions.BinaryOp.GreaterOrEqual)
                    {
                        return (v == Content.Compiled.Scenario.Expressions.ReservedVariable.Y, limit, below);
                    }
                }
            }

            return null;
        }

        // Walkable ground for the unit's layer on the far side of the line (any component), nearest the unit's row (or
        // column), within 32 tiles of the line.
        private static Position? Beyond(World world, EntityId unit, bool vertical, int limit, bool below)
        {
            if (!world.Navigation.IsBuilt || world.Navigation.ForType(world.Identities.Get(unit).TypeId) is not { } nav)
            {
                return null;
            }

            Position at = Pos(world, unit);
            int size = vertical ? nav.Height : nav.Width;
            int other = vertical ? at.X / Tile : at.Y / Tile;
            int otherSize = vertical ? nav.Width : nav.Height;
            for (int d = 0; d < otherSize; d++)
            {
                foreach (int o in new[] { other - d, other + d })
                {
                    if (o < 0 || o >= otherSize)
                    {
                        continue;
                    }

                    for (int step = 0; step < 32; step++)
                    {
                        int line = below ? limit - step : limit + step;
                        if (line < 0 || line >= size)
                        {
                            break;
                        }

                        int x = vertical ? o : line;
                        int y = vertical ? line : o;
                        if (nav.IsWalkable(x, y))
                        {
                            return new Position(nav.CenterX((y * nav.Width) + x), nav.CenterY((y * nav.Width) + x));
                        }
                    }
                }
            }

            return null;
        }

        // The reachable tile on the far side of the line nearest to the unit's row (or column).
        private static Position? Across(World world, EntityId unit, bool vertical, int limit, bool below, Position at)
        {
            if (world.IsHeld(unit) || !world.Navigation.IsBuilt || world.Navigation.ForType(world.Identities.Get(unit).TypeId) is not { } nav)
            {
                return null;
            }

            int from = nav.TileOf(at.X, at.Y);
            int size = vertical ? nav.Height : nav.Width;
            int other = vertical ? at.X / Tile : at.Y / Tile;
            int otherSize = vertical ? nav.Width : nav.Height;
            int start = Math.Clamp(limit, 0, size - 1);
            for (int step = 0; step < 16; step++)
            {
                int line = below ? start - step : start + step;
                if (line < 0 || line >= size)
                {
                    break;
                }

                for (int d = 0; d < otherSize; d++)
                {
                    foreach (int o in new[] { other - d, other + d })
                    {
                        if (o < 0 || o >= otherSize)
                        {
                            continue;
                        }

                        int tile = vertical ? (line * nav.Width) + o : (o * nav.Width) + line;
                        if (nav.AreConnected(from, tile))
                        {
                            return new Position(nav.CenterX(tile), nav.CenterY(tile));
                        }
                    }
                }
            }

            return null;
        }

        private static EntityId? Entity(World world, Scripting.Legacy.LegacyTriggerSystem triggers, int index)
        {
            if ((uint)index >= (uint)triggers.State.MapEntities.Length)
            {
                return null;
            }

            EntityId e = EntityId.FromValue(triggers.State.MapEntities[index]);
            return world.Entities.IsAlive(e) && (world.Positions.Has(e) || world.IsHeld(e)) ? e : null;
        }

        private static Position? Target(World world, Scripting.Legacy.LegacyTriggerSystem triggers, AreaSpec area) => area switch
        {
            RectArea r when r.Right < r.Left || r.Bottom < r.Top => null, // an unset area
            RectArea r => new Position(((r.Left + r.Right + 1) * Tile) / 2, ((r.Top + r.Bottom + 1) * Tile) / 2),
            ObjectArea o when Entity(world, triggers, o.ObjectIndex) is { } e => Pos(world, e),
            _ => null,
        };

        private List<EntityId> OwnUnits(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers, ObjectSpec spec, Position? target)
        {
            var units = new List<EntityId>();
            if (spec is NamedObjects named)
            {
                foreach (int index in named.ObjectIndices)
                {
                    if (Entity(world, triggers, index) is { } u && world.Diplomacy.MayCommand(me, world.Identities.Get(u).Owner) && (world.Motions.Has(u) || world.IsHeld(u)))
                    {
                        units.Add(u);
                    }
                }
            }
            else if ((Mask(spec) & (1 << me)) != 0 && _army.Count > 0 && target is { } at)
            {
                // "So many of my units": the ones nearest to the target, as many as the count asks (at least one); the
                // rest of the army stays free, and a crowd does not block the scenario's own movers.
                var nearest = new List<(long Distance, int Index)>(_army.Count);
                for (int i = 0; i < _army.Count; i++)
                {
                    if (_stuck.Contains(_army[i]))
                    {
                        continue;
                    }

                    Position p = Pos(world, _army[i]);
                    long dx = p.X - at.X;
                    long dy = p.Y - at.Y;
                    nearest.Add(((dx * dx) + (dy * dy), i));
                }

                nearest.Sort();
                int wanted = Math.Clamp(spec is TypedObjects t ? t.Count : spec is CategoryObjects c ? c.Count : 1, 1, Math.Max(1, nearest.Count));
                for (int i = 0; i < Math.Min(wanted, nearest.Count); i++)
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
                    return Pos(world, ids[i]);
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
                    TypedObjects t => id.TypeId == world.Ruleset.EntityTypeId(t.TypeKey),
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
            Position from = Pos(world, _army[0]);
            long best = long.MaxValue;
            Position target = default;
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length; i++)
            {
                if (!IsEnemy(world, me, identities[i]) || world.IsHeld(ids[i]) || _protected.Contains(ids[i]))
                {
                    continue;
                }

                Position p = Pos(world, ids[i]);
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

            Position at = where ?? Pos(world, _army[0]);
            _batch.Add(CommandKind.DebugSpawn, [], at.X, at.Y, default, type, 10);
        }

        private void Clear(World world, byte me)
        {
            var targets = new List<EntityId>();
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length && targets.Count < 1000; i++)
            {
                if (IsEnemy(world, me, identities[i]) && !_protected.Contains(ids[i]))
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
