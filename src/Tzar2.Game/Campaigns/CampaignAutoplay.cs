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
        CommandKind.PickItem or CommandKind.DropItem or CommandKind.SetDiplomacy or CommandKind.Tribute => true,
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
        var faulted = new SortedDictionary<string, int>(StringComparer.Ordinal);
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
                if (e.Kind == Sim.Events.SimEventKind.ScenarioFault)
                {
                    faults++;
                    string key = $"{e.X}.{e.Y}:{(Scripting.Legacy.ScenarioFault)e.Arg}";
                    faulted[key] = faulted.TryGetValue(key, out int f) ? f + 1 : 1;
                }

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
            + (rejected.Count > 0 ? " | rejected (kingdom:reason×n): " + string.Join(" ", rejected.Select(r => $"{r.Key}×{r.Value}")) : string.Empty)
            + (faulted.Count > 0 ? " | faults (rule.action:kind×n): " + string.Join(" ", faulted.Select(r => $"{r.Key}×{r.Value}")) : string.Empty);
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

        // Kingdoms whose invulnerability is off while a goal wants one of their heroes wounded.
        private readonly SortedSet<int> _mortal = [];
        private readonly Dictionary<int, int> _mortalFor = [];

        private bool Vulnerable(World world, int e, string name, List<EntityId> heroes)
        {
            foreach (EntityId hero in heroes)
            {
                int owner = world.Identities.Get(hero).Owner;
                _mortalFor[owner] = e;
                if (_mortal.Add(owner) && _god.Remove(owner))
                {
                    var cheat = new CommandList();
                    cheat.Add(CommandKind.Cheat, [], 0, 0, default, (int)Sim.Systems.CheatCode.God, 0); // toggles it off
                    EnsureAllowed(cheat);
                    session.SubmitAs((byte)owner, cheat);
                    Note($"invulnerability off for {name}");
                }
            }

            Goal = $"{name}: waiting for the scenario to wound its units";
            return false; // nothing to order: the rest of the solver keeps going
        }
        private readonly SortedSet<int> _god = [];

        private void GrantGod()
        {
            World world = session.Simulation.World;
            byte me = session.LocalPlayer;

            // Invulnerable again once the wound happened (the event came true).
            if (session.Scenario?.Triggers is { } state)
            {
                _mortal.RemoveWhere(k => !_mortalFor.TryGetValue(k, out int e) || (uint)e >= (uint)state.State.EventValues.Length || state.State.EventValues[e]);
            }

            var kingdoms = new SortedSet<int> { me };
            if (session.Scenario?.Triggers is { } triggers)
            {
                var wanted = new List<int>();
                for (int r = 0; r < scenario.Rules.Length; r++)
                {
                    RuleDef rule = scenario.Rules[r];
                    if (rule.Condition.Root is not { } condition)
                    {
                        continue;
                    }

                    wanted.Clear();
                    Wanted(condition, negated: false, wanted);
                    if (!LosesMe(rule, me))
                    {
                        // An allied kingdom whose unit a pending goal is measured around (the heroes must come to
                        // Marsim): its units are protected too, or a raid kills the goal (ADR 0220).
                        foreach (int e in wanted)
                        {
                            if (!triggers.State.RuleDisabled[r] && scenario.Events[e] is ObjectInAreaEvent { Area: ObjectArea around }
                                && Entity(world, triggers, around.ObjectIndex) is { } anchor && world.Identities.Get(anchor).Owner is var keeper
                                && world.HasPlayer(keeper) && keeper != me && world.Diplomacy.StanceOf(me, keeper) == Sim.Kingdoms.Stance.Ally)
                            {
                                kingdoms.Add(keeper);
                            }
                        }

                        continue;
                    }

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
                if (_mortal.Contains(k) || !_god.Add(k))
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

        // Units of the kingdom the solver created itself (reinforcements, spawned objectives): seen first right after one
        // of its spawns, near the spawn point. Everything else is the scenario's.
        private readonly HashSet<uint> _owned = [];
        private readonly HashSet<uint> _spawned = [];
        private Position? _spawnPoint;

        private void Census(World world, byte me)
        {
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length; i++)
            {
                if (identities[i].Owner != me || _owned.Contains(ids[i].Value) || _spawned.Contains(ids[i].Value) || !world.Motions.Has(ids[i]))
                {
                    continue;
                }

                bool near = _spawnPoint is { } sp && Math.Abs(Pos(world, ids[i]).X - sp.X) <= 8 * Tile && Math.Abs(Pos(world, ids[i]).Y - sp.Y) <= 8 * Tile;
                (near ? _spawned : _owned).Add(ids[i].Value);
            }

            _spawnPoint = null;
        }

        private void Army(World world, byte me)
        {
            Census(world, me);
            _army.Clear();
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length && _army.Count < 200; i++)
            {
                // A ship with units aboard or on its way to a shore is a ferry: the army's orders would cancel its trip.
                if (world.Diplomacy.MayCommand(me, identities[i].Owner) && world.Motions.Has(ids[i]) && world.Ruleset.EntityTypes[identities[i].TypeId].Traits.Armament is not null && !world.IsHeld(ids[i])
                    && !(world.IsShip(ids[i]) && (world.Dockings.Has(ids[i]) || (world.Holders.IndexOf(ids[i]) is >= 0 and var hold && world.Holders.At(hold).Count > 0))))
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
            _named = null;
            _protected = Scripted(world, me, triggers);
            _needed = Needed(triggers, me);
            _protected.UnionWith(_needed);
            _props = Props(world, triggers);
            var visited = new HashSet<int>();
            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                // Never pursue what makes the local kingdom lose.
                if (triggers.State.RuleDisabled[r] || scenario.Rules[r].Condition.Root is not { } condition || LosesMe(scenario.Rules[r], me))
                {
                    continue;
                }

                // A rule whose events the solver once made true without it firing waits for something else (another
                // rule's work, "!GoldNotTaken"): later rules come first, or the solver walks the same units back and forth
                // between the two (ADR 0220).
                if (Waiting(triggers, r, condition))
                {
                    continue;
                }

                if (PursueRule(world, me, triggers, r, 0, visited))
                {
                    return true;
                }
            }

            foreach (int r in _waiting)
            {
                if (!triggers.State.RuleDisabled[r] && PursueRule(world, me, triggers, r, 0, visited))
                {
                    return true;
                }
            }

            return false;
        }

        private readonly SortedSet<int> _waiting = [];

        // Whether rule r waits for more than its events: all of them (not under '!') are true, no variable gate is open,
        // and it is still enabled. Once seen, a rule stays waiting until it fires.
        private bool Waiting(Scripting.Legacy.LegacyTriggerSystem triggers, int r, Content.Compiled.Scenario.Expressions.Expr condition)
        {
            if (_waiting.Contains(r))
            {
                return true;
            }

            var wanted = new List<int>();
            Wanted(condition, negated: false, wanted);
            if (wanted.Count == 0 || wanted.Any(e => !triggers.State.EventValues[e]))
            {
                return false;
            }

            var variables = new List<int>();
            Variables(condition, negated: false, variables);
            if (variables.Any(v => (uint)v < (uint)triggers.State.Variables.Length && triggers.State.Variables[v] == 0 && _setters!.ContainsKey(v)))
            {
                return false;
            }

            if (Unwanted(condition).Any(e => triggers.State.EventValues[e] && Lying(session.Simulation.World, triggers, scenario.Events[e]) is not null))
            {
                return false; // "!GoldNotTaken": the solver can take the item
            }

            _waiting.Add(r);
            return true;
        }

        private int _lastTribute = int.MinValue / 2;

        // "@gold(k) >= n" (or > n) about another kingdom that is not an enemy: what it lacks (kingdom k as the script
        // numbers it, the resource id and name, the amount).
        private static void Shortfalls(World world, byte me, Content.Compiled.Scenario.Expressions.Expr node, List<(int Kingdom, int Resource, string Name, int Need)> into)
        {
            switch (node)
            {
                case Content.Compiled.Scenario.Expressions.UnaryExpr { Op: Content.Compiled.Scenario.Expressions.UnaryOp.Not }:
                    return; // "!(@gold(7) >= n)" wants less, not more
                case Content.Compiled.Scenario.Expressions.UnaryExpr u:
                    Shortfalls(world, me, u.Operand, into);
                    return;
                case Content.Compiled.Scenario.Expressions.BinaryExpr { Op: Content.Compiled.Scenario.Expressions.BinaryOp.And or Content.Compiled.Scenario.Expressions.BinaryOp.Or } b:
                    Shortfalls(world, me, b.Left, into);
                    Shortfalls(world, me, b.Right, into);
                    return;
                case Content.Compiled.Scenario.Expressions.BinaryExpr { Left: Content.Compiled.Scenario.Expressions.CallExpr { Arguments.Length: 1 } c, Right: Content.Compiled.Scenario.Expressions.IntLiteral n } b
                    when b.Op is Content.Compiled.Scenario.Expressions.BinaryOp.Greater or Content.Compiled.Scenario.Expressions.BinaryOp.GreaterOrEqual
                    && c.Arguments[0] is Content.Compiled.Scenario.Expressions.IntLiteral { Value: var k } && k - 1 != me && world.HasPlayer(k - 1)
                    && (world.Players[k - 1].Flags & PlayerFlags.Defeated) == 0 && world.Diplomacy.StanceOf(me, k - 1) != Sim.Kingdoms.Stance.Enemy
                    && c.Function switch
                    {
                        Content.Compiled.Scenario.Expressions.CallKind.Gold => "gold",
                        Content.Compiled.Scenario.Expressions.CallKind.Wood => "wood",
                        Content.Compiled.Scenario.Expressions.CallKind.Food => "food",
                        Content.Compiled.Scenario.Expressions.CallKind.Stone => "stone",
                        _ => null,
                    } is { } resource && world.Ruleset.ResourceId(resource) is >= 0 and var id:
                {
                    int need = n.Value + (b.Op == Content.Compiled.Scenario.Expressions.BinaryOp.Greater ? 1 : 0) - world.Players[k - 1].Resources[id];
                    if (need > 0)
                    {
                        into.Add((k, id, resource, need));
                    }

                    return;
                }

                default:
                    return;
            }
        }

        // A tribute of what a friendly kingdom lacks, a player's order (ADR 0220); the local kingdom's own stock is topped
        // up by cheat assistance when it is short.
        private void Tribute(World world, byte me, List<(int Kingdom, int Resource, string Name, int Need)> shortfalls)
        {
            foreach ((int k, int id, string resource, int need) in shortfalls)
            {
                if (need > world.Ruleset.World.MaxTributeAmount)
                {
                    continue;
                }

                if (world.Players[me].Resources[id] < need)
                {
                    if (!options.CheatAssist)
                    {
                        continue;
                    }

                    _batch.Add(CommandKind.Cheat, [], 0, 0, default, (int)Sim.Systems.CheatCode.LargeResources, 0);
                    Assist = Assist == "play" ? "resources" : Assist;
                }

                _lastTribute = _tick;
                _batch.Add(CommandKind.Tribute, x: k - 1, arg0: id, arg1: need);
                Note($"tribute of {need} {resource} to kingdom {k}");
                return;
            }
        }

        // The rules still to come that give kingdom k objects (bigisland's "Azim changes kingdom back", then his load
        // goes into its store, ECO-023): the scenario fills that kingdom's stockpile itself.
        private List<int> Givers(Scripting.Legacy.LegacyTriggerSystem triggers, int kingdom)
        {
            var rules = new List<int>();
            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                if (triggers.State.RuleDisabled[r])
                {
                    continue;
                }

                foreach (int a in scenario.Rules[r].ActionIndices)
                {
                    if ((uint)a < (uint)scenario.Actions.Length && scenario.Actions[a] is ChangeObjectOwnerAction { Kingdom: var to } && to == kingdom)
                    {
                        rules.Add(r);
                        break;
                    }
                }
            }

            return rules;
        }

        // The value of a part of a condition that the solver cannot change, or null when it may change or is not known
        // (ADR 0220). Only an enemy kingdom's stockpile counts as fixed (a friend's takes tribute): a rule that waits for
        // it is left alone, or the solver walks units to its other events again and again, taking them off the next
        // goal. Events, variables, timers and the local kingdom's resources (cheat assistance) are unknown.
        private static int? Known(World world, Content.Compiled.Scenario.Expressions.Expr node, byte me)
        {
            switch (node)
            {
                case Content.Compiled.Scenario.Expressions.IntLiteral n:
                    return n.Value;
                case Content.Compiled.Scenario.Expressions.CallExpr { Arguments.Length: 1 } c when c.Arguments[0] is Content.Compiled.Scenario.Expressions.IntLiteral { Value: var k }
                    && k - 1 != me && world.HasPlayer(k - 1) && world.Diplomacy.StanceOf(me, k - 1) == Sim.Kingdoms.Stance.Enemy && c.Function switch
                    {
                        Content.Compiled.Scenario.Expressions.CallKind.Gold => "gold",
                        Content.Compiled.Scenario.Expressions.CallKind.Wood => "wood",
                        Content.Compiled.Scenario.Expressions.CallKind.Food => "food",
                        Content.Compiled.Scenario.Expressions.CallKind.Stone => "stone",
                        _ => null,
                    } is { } resource && world.Ruleset.ResourceId(resource) is >= 0 and var id:
                    return world.Players[k - 1].Resources[id];
                case Content.Compiled.Scenario.Expressions.UnaryExpr u:
                    return Known(world, u.Operand, me) is not { } v ? null : u.Op == Content.Compiled.Scenario.Expressions.UnaryOp.Not ? (v == 0 ? 1 : 0) : -v;
                case Content.Compiled.Scenario.Expressions.BinaryExpr { Op: Content.Compiled.Scenario.Expressions.BinaryOp.And } b:
                {
                    int? l = Known(world, b.Left, me);
                    int? r = Known(world, b.Right, me);
                    return l == 0 || r == 0 ? 0 : l is not null && r is not null ? 1 : null;
                }

                case Content.Compiled.Scenario.Expressions.BinaryExpr { Op: Content.Compiled.Scenario.Expressions.BinaryOp.Or } b:
                {
                    int? l = Known(world, b.Left, me);
                    int? r = Known(world, b.Right, me);
                    return l is { } x && x != 0 || r is { } y && y != 0 ? 1 : l == 0 && r == 0 ? 0 : null;
                }

                case Content.Compiled.Scenario.Expressions.BinaryExpr b when Known(world, b.Left, me) is { } l && Known(world, b.Right, me) is { } r:
                    return b.Op switch
                    {
                        Content.Compiled.Scenario.Expressions.BinaryOp.Less => l < r ? 1 : 0,
                        Content.Compiled.Scenario.Expressions.BinaryOp.LessOrEqual => l <= r ? 1 : 0,
                        Content.Compiled.Scenario.Expressions.BinaryOp.Greater => l > r ? 1 : 0,
                        Content.Compiled.Scenario.Expressions.BinaryOp.GreaterOrEqual => l >= r ? 1 : 0,
                        Content.Compiled.Scenario.Expressions.BinaryOp.Equal => l == r ? 1 : 0,
                        Content.Compiled.Scenario.Expressions.BinaryOp.NotEqual => l != r ? 1 : 0,
                        Content.Compiled.Scenario.Expressions.BinaryOp.Add => l + r,
                        Content.Compiled.Scenario.Expressions.BinaryOp.Subtract => l - r,
                        Content.Compiled.Scenario.Expressions.BinaryOp.Multiply => l * r,
                        _ => null,
                    };
                default:
                    return null;
            }
        }

        private bool PursueRule(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers, int r, int depth, HashSet<int> visited)
        {
            // Nor a rule that waits for something only other kingdoms can do (directly or as another rule's gate).
            if (!visited.Add(r) || scenario.Rules[r].Condition.Root is not { } condition || Known(world, condition, me) == 0)
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

            // A rule waiting on a friendly kingdom's stockpile ("@gold(7) >= 1000"): the scenario's own rules that give
            // that kingdom objects are its gates (they bring it the resources); with none, the player sends a tribute.
            var gates = new List<int>();
            bool gated = false;
            var shortfalls = new List<(int Kingdom, int Resource, string Name, int Need)>();
            Shortfalls(world, me, condition, shortfalls);
            foreach ((int k, _, _, _) in shortfalls)
            {
                List<int> givers = Givers(triggers, k);
                givers.Remove(r);
                gated |= givers.Count > 0;
                gates.AddRange(givers);
            }

            if (!gated && shortfalls.Count > 0 && _tick - _lastTribute >= 600)
            {
                Tribute(world, me, shortfalls);
            }

            // Gates first: variables still 0 that some other rule sets, objects some other rule makes appear. While a gate
            // is open the rule's own events are premature (walking the heroes off before the conversation that needs
            // them), so only the gate's rules are pursued.
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

            // A resource item that a peasant the rule names must take ("AzimNearStorage && !GoldNotTaken"): first the
            // item, then the walk with its load, or the two goals pull the peasant back and forth.
            foreach (int e in Unwanted(condition))
            {
                if (triggers.State.EventValues[e] && Lying(world, triggers, scenario.Events[e]) is { } item
                    && world.TypeOf(item).Traits.Item is { Kind: ItemKind.Resource } definition && NamedGatherer(world, triggers, condition, definition) is not null)
                {
                    return Take(world, me, triggers, scenario.Events[e].Name, item, condition);
                }
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

            // "!ForceNotToTheEast" (no unit of mine with $x < 380): the kingdom's units go over the line (ADR 0220).
            foreach (int e in Unwanted(condition))
            {
                if (triggers.State.EventValues[e] && scenario.Events[e] is ObjectExpressionEvent { Objects: TypedObjects or CategoryObjects } away
                    && (Mask(away.Objects) & (1 << me)) != 0 && Line(world, away.Expression.Root) is { } stay && Evacuate(world, me, away, stay))
                {
                    return true;
                }
            }

            // "!GoldNotTaken" (a named item must stop existing): a unit the kingdom commands picks it up (ADR 0220).
            foreach (int e in Unwanted(condition))
            {
                if (triggers.State.EventValues[e] && Lying(world, triggers, scenario.Events[e]) is { } item && Take(world, me, triggers, scenario.Events[e].Name, item, condition))
                {
                    return true;
                }
            }

            return false;
        }

        // Every unit of the kingdom that the event counts goes to the other side of its line: walking, ferried, or aboard a
        // ship that sails there when only water lies beyond. Reinforcements the solver spawned itself are removed instead
        // (undoing its own assistance; the scenario's units all go).
        private bool Evacuate(World world, byte me, ObjectExpressionEvent away, (bool Vertical, int Limit, bool Below) stay)
        {
            (bool vertical, int limit, bool below) = (stay.Vertical, stay.Below ? stay.Limit + 1 : stay.Limit - 1, !stay.Below);
            bool Over(Position p)
            {
                int c = vertical ? p.Y / Tile : p.X / Tile;
                return below ? c <= limit : c >= limit;
            }

            var walkers = new List<EntityId>();
            var stranded = new List<EntityId>();
            var undo = new List<EntityId>();
            var ships = new List<EntityId>();
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length; i++)
            {
                EntityId u = ids[i];
                if (identities[i].Owner >= 32 || (Mask(away.Objects) & (1 << identities[i].Owner)) == 0 || !world.Diplomacy.MayCommand(me, identities[i].Owner) || !(world.Motions.Has(u) || world.IsHeld(u)) || Over(Pos(world, u))
                    || !(away.Objects is CategoryObjects c ? Scripting.Legacy.LegacyTriggerSystem.InCategory(c.Category, world.Ruleset.EntityTypes[identities[i].TypeId])
                        : world.Ruleset.EntityTypeId(((TypedObjects)away.Objects).TypeKey) == identities[i].TypeId))
                {
                    continue;
                }

                if (_spawned.Contains(u.Value) && !world.IsHeld(u))
                {
                    undo.Add(u);
                }
                else if (world.IsShip(u))
                {
                    ships.Add(u);
                }
                else if (!world.IsHeld(u) && Across(world, u, vertical, limit, below, Pos(world, u)) is { } to)
                {
                    _batch.Add(CommandKind.Move, [u], to.X, to.Y);
                    walkers.Add(u);
                }
                else
                {
                    stranded.Add(u);
                }
            }

            for (int i = 0; i < undo.Count && options.CheatAssist; i += 64)
            {
                _batch.Add(CommandKind.DebugKill, undo.GetRange(i, Math.Min(64, undo.Count - i)).ToArray());
            }

            // Empty ships sail over the line themselves; ships carrying the stranded are the ferries below.
            foreach (EntityId ship in ships)
            {
                bool loaded = world.Holders.IndexOf(ship) is >= 0 and var h && world.Holders.At(h).Count > 0;
                if (!loaded && !world.Dockings.Has(ship) && WaterBeyond(world, me, ship, vertical, limit, below) is { } sea && Due(ship))
                {
                    _batch.Add(CommandKind.Move, [ship], sea.X, sea.Y);
                }
            }

            Goal = $"{away.Name}: {walkers.Count} walk, {stranded.Count} to ferry, {undo.Count} reinforcements removed, {ships.Count} ships";
            if (stranded.Count > 0)
            {
                // One ferry load at a time, the ones aboard first.
                List<EntityId> load = stranded.OrderBy(u => world.IsHeld(u) ? 0 : 1).ThenBy(u => u.Value).Take(20).ToList();
                Position? beyond = Beyond(world, load[0], vertical, limit, below);
                bool sail = beyond is null;
                if ((beyond ?? WaterBeyond(world, me, load[0], vertical, limit, below)) is { } target && Ferry(world, me, away.Name, load, target, sail))
                {
                    return true;
                }
            }

            if (undo.Count > 0 && options.CheatAssist)
            {
                Assist = Assist.StartsWith("god", StringComparison.Ordinal) ? "god+cleared" : "cleared";
            }

            return walkers.Count > 0 || undo.Count > 0 || ships.Count > 0;
        }

        // Event references under '!' (the condition wants them false).
        private static List<int> Unwanted(Content.Compiled.Scenario.Expressions.Expr condition)
        {
            var all = new List<int>();
            var wanted = new List<int>();
            Events(condition, all);
            Wanted(condition, negated: false, wanted);
            all.RemoveAll(wanted.Contains);
            return all;
        }

        private static void Events(Content.Compiled.Scenario.Expressions.Expr node, List<int> into)
        {
            switch (node)
            {
                case Content.Compiled.Scenario.Expressions.EventRef r when !into.Contains(r.EventIndex):
                    into.Add(r.EventIndex);
                    break;
                case Content.Compiled.Scenario.Expressions.UnaryExpr u:
                    Events(u.Operand, into);
                    break;
                case Content.Compiled.Scenario.Expressions.BinaryExpr b:
                    Events(b.Left, into);
                    Events(b.Right, into);
                    break;
                case Content.Compiled.Scenario.Expressions.SequenceExpr q:
                    foreach (Content.Compiled.Scenario.Expressions.Expr statement in q.Statements)
                    {
                        Events(statement, into);
                    }

                    break;
            }
        }

        // The item an Object Exist event names that lies on the ground (picking it up makes the event false), or null.
        private static EntityId? Lying(World world, Scripting.Legacy.LegacyTriggerSystem triggers, EventDef e)
        {
            if (e is not ObjectExistEvent { Objects: NamedObjects named })
            {
                return null;
            }

            foreach (int index in named.ObjectIndices)
            {
                if (Entity(world, triggers, index) is { } item && world.Items.Has(item) && !world.IsHeld(item))
                {
                    return item;
                }
            }

            return null;
        }

        private readonly Dictionary<uint, EntityId> _takers = [];

        // The nearest unit the kingdom commands that can hold the item walks to it and picks it up; across water or a
        // wall it is ferried or breaches first, as for a delivery.
        private bool Take(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers, string name, EntityId item, Content.Compiled.Scenario.Expressions.Expr? condition = null)
        {
            Position where = Pos(world, item);
            EntityId? picker = null;
            long best = long.MaxValue;
            ItemDefinition definition = world.Ruleset.EntityTypes[world.Identities.Get(item).TypeId].Traits.Item!;
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;

            // A resource item the rule's other events name a peasant for ("AzimNearStorage && !GoldNotTaken"): that
            // peasant gathers it as its load and carries it where the scenario wants it (program 0x1e, ReTzar 0x464f60);
            // any other unit would put it in its own kingdom's stockpile. Until the kingdom commands that peasant, the
            // item waits.
            EntityId? named = definition.Kind == ItemKind.Resource && condition is not null ? NamedGatherer(world, triggers, condition, definition) : null;
            if (named is { } gatherer && !world.Diplomacy.MayCommand(me, world.Identities.Get(gatherer).Owner))
            {
                Goal = $"{name}: waiting to command the {world.TypeOf(gatherer).Key} that takes the item";
                return false;
            }

            // Else the unit chosen first keeps the job (aboard a ferry it is no longer the nearest, and another would board).
            if (named is not null)
            {
                picker = named;
            }
            else if (_takers.TryGetValue(item.Value, out EntityId kept) && world.Entities.IsAlive(kept) && world.Diplomacy.MayCommand(me, world.Identities.Get(kept).Owner)
                && (world.Motions.Has(kept) || world.IsHeld(kept)))
            {
                picker = kept;
            }

            bool keep = picker is not null;
            for (int i = 0; i < ids.Length && !keep; i++)
            {
                if (world.Diplomacy.MayCommand(me, identities[i].Owner) && world.Motions.Has(ids[i]) && !world.IsHeld(ids[i]) && !world.IsShip(ids[i])
                    && definition.CanBeHeldBy(identities[i].TypeId) && (definition.Kind == ItemKind.Resource || CanCarry(world, ids[i], item))
                    && Cost(world, triggers, ids[i], where) is var cost && cost < best)
                {
                    best = cost;
                    picker = ids[i];
                }
            }

            if (picker is not { } unit)
            {
                Goal = $"{name}: nobody to take the item";
                return false;
            }

            _takers[item.Value] = unit;

            var around = new RectArea((where.X / Tile) - 1, (where.Y / Tile) - 1, (where.X / Tile) + 1, (where.Y / Tile) + 1);
            if (Reach(world, triggers, unit, around, where) is null)
            {
                return Breach(world, me, name, [unit], where) || Ferry(world, me, name, [unit], where);
            }

            if (!world.Errands.Has(unit))
            {
                _batch.Add(CommandKind.PickItem, [unit], 0, 0, item);
            }

            Goal = $"{name}: {world.Ruleset.EntityTypes[world.Identities.Get(unit).TypeId].Key}/{world.Identities.Get(unit).Owner} takes the item at ({where.X / Tile},{where.Y / Tile})";
            Note($"take an item for {name}");
            return true;
        }

        // A peasant that an area event of the condition names and that gathers the item's resource, or null.
        private EntityId? NamedGatherer(World world, Scripting.Legacy.LegacyTriggerSystem triggers, Content.Compiled.Scenario.Expressions.Expr condition, ItemDefinition definition)
        {
            var mates = new List<int>();
            Events(condition, mates);
            foreach (int m in mates)
            {
                if (scenario.Events[m] is not ObjectInAreaEvent { Objects: NamedObjects named })
                {
                    continue;
                }

                foreach (int index in named.ObjectIndices)
                {
                    if (Entity(world, triggers, index) is { } u && world.TypeOf(u).Traits.Harvester is { } harvester && harvester.CanGather(definition.Resource)
                        && definition.CanBeHeldBy(world.Identities.Get(u).TypeId))
                    {
                        return u;
                    }
                }
            }

            return null;
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

                    // The object an area event is measured around (the heroes must come to Marsim): killing it, by a
                    // fight or by clearing, would leave the goal nowhere (ADR 0220).
                    if (scenario.Events[e] is ObjectInAreaEvent { Area: ObjectArea around } && Entity(world, triggers, around.ObjectIndex) is { } anchor
                        && !world.Diplomacy.MayCommand(me, world.Identities.Get(anchor).Owner))
                    {
                        set.Add(anchor);
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
                    List<EntityId> units = OwnUnits(world, me, triggers, inArea.Objects, point, inArea.Name);
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
                            // Only water beyond (leave by the west edge, at sea): the units sail over the line aboard
                            // a ship; a unit aboard is where its ship is (ADR 0220).
                            if (WaterBeyond(world, me, stranded[0], bound.Vertical, bound.Limit, bound.Below) is { } sea)
                            {
                                return Ferry(world, me, line.Name, stranded, sea, sail: true) || moved.Count > 0;
                            }

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

                // "$health < $WoolinHealth" about a hero of the kingdom (any wound since the health a script recorded): the
                // scenario wounds him itself (an ambush), so cheat invulnerability must not stand in its way (ADR 0220).
                // "$health <= 50" (a duel to weaken someone) is left to the fight.
                case ObjectExpressionEvent { Objects: NamedObjects hurt } w when w.Expression.Root is Content.Compiled.Scenario.Expressions.BinaryExpr
                    {
                        Op: Content.Compiled.Scenario.Expressions.BinaryOp.Less,
                        Left: Content.Compiled.Scenario.Expressions.ReservedVar { Variable: Content.Compiled.Scenario.Expressions.ReservedVariable.Health },
                        Right: Content.Compiled.Scenario.Expressions.UserVar,
                    }
                    && OwnUnits(world, me, triggers, hurt, null) is { Count: > 0 } heroes && heroes.Count == hurt.ObjectIndices.Length:
                    return Vulnerable(world, Array.IndexOf(scenario.Events.ToArray(), w), w.Name, heroes);

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
                    _spawnPoint = at;
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
            else if (walkers.Any(_stuck.Contains) && _army.Where(u => !units.Contains(u) && !world.IsShip(u)).ToArray() is { Length: > 0 } crowd)
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
            Position landing = area is RectArea && Ground(world, stranded[0], area, point) is { } ground ? ground : point;
            return (pocket && Breach(world, me, name, stranded, point)) || Ferry(world, me, name, stranded, landing) || (!pocket && Breach(world, me, name, stranded, point)) || walkers.Count > 0;
        }

        // Ground of the rectangle the unit could stand on (any land, not only the land it is on), nearest to the point:
        // a ferry lands there, not at the middle of a harbour that is mostly water (ADR 0220).
        private static Position? Ground(World world, EntityId unit, AreaSpec area, Position point)
        {
            if (area is not RectArea r || !world.Navigation.IsBuilt || world.Navigation.ForType(world.Identities.Get(unit).TypeId) is not { } nav)
            {
                return null;
            }

            // Inside the edge first (as walkers aim), then the edge itself (a harbour's only land may be its rim).
            (int a0, int a1) = r.Right - r.Left >= 2 ? (r.Left + 1, r.Right - 1) : (r.Left, r.Right);
            (int b0, int b1) = r.Bottom - r.Top >= 2 ? (r.Top + 1, r.Bottom - 1) : (r.Top, r.Bottom);
            return Ground(nav, a0, b0, a1, b1, point) ?? Ground(nav, r.Left, r.Top, r.Right, r.Bottom, point);
        }

        private static Position? Ground(Sim.Navigation.NavLayer nav, int x0, int y0, int x1, int y1, Position point)
        {
            x0 = Math.Clamp(x0, 0, nav.Width - 1);
            x1 = Math.Clamp(x1, 0, nav.Width - 1);
            y0 = Math.Clamp(y0, 0, nav.Height - 1);
            y1 = Math.Clamp(y1, 0, nav.Height - 1);
            long best = long.MaxValue;
            Position? found = null;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int tile = (y * nav.Width) + x;
                    long d = Distance(new Position(nav.CenterX(tile), nav.CenterY(tile)), point);
                    if (nav.IsWalkable(x, y) && d < best)
                    {
                        best = d;
                        found = new Position(nav.CenterX(tile), nav.CenterY(tile));
                    }
                }
            }

            return found;
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
        private bool Ferry(World world, byte me, string name, List<EntityId> stranded, Position point, bool sail = false)
        {
            // Aboard already: unload at the point, once the others have boarded too (or a minute has passed).
            foreach (EntityId u in stranded)
            {
                if (world.Helds.IndexOf(u) is >= 0 and var h && EntityId.FromValue(world.Helds.At(h).Holder) is var ship && world.IsShip(ship)
                    && world.Diplomacy.MayCommand(me, world.Identities.Get(ship).Owner))
                {
                    if (!Sails(world, ship, point) || Refused(world, ship, point))
                    {
                        // Aboard a ship on other water (a lake): out again where it is, then another ship.
                        if (!world.Dockings.Has(ship) && Due(ship))
                        {
                            Position here = Pos(world, ship);
                            _batch.Add(CommandKind.Unload, [ship], here.X, here.Y);
                        }

                        Goal = $"{name}: off a ship that cannot reach ({point.X / Tile},{point.Y / Tile})";
                        return true;
                    }

                    if (stranded.Any(o => !world.IsHeld(o)) && _ferried.TryGetValue(ship.Value, out int boarded) && _tick - boarded < 1200)
                    {
                        // The ones whose boarding gave up (no errand) are sent again, to the same ship.
                        EntityId[] again = Unboarding(world, stranded);
                        if (again.Length > 0 && _tick - _reboarded >= 200)
                        {
                            _reboarded = _tick;
                            _batch.Add(CommandKind.Board, again, 0, 0, ship);
                        }

                        Goal = $"{name}: waiting for {stranded.Count(o => !world.IsHeld(o))} more to board";
                        MakeRoom(world, name, stranded, Pos(world, ship));
                        return true;
                    }

                    if (world.Dockings.Has(ship) || !Due(ship))
                    {
                        Goal = $"{name}: ferrying to ({point.X / Tile},{point.Y / Tile})";
                        return true;
                    }

                    _batch.Add(sail ? CommandKind.Move : CommandKind.Unload, [ship], point.X, point.Y);
                    _asked[ship.Value] = (Pos(world, ship), _asked.TryGetValue(ship.Value, out var before) && Distance(before.At, Pos(world, ship)) < (long)Tile * Tile ? before.Times + 1 : 1);
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
                    || holder.Capacity - (world.Holders.IndexOf(ids[i]) is >= 0 and var hs ? world.Holders.At(hs).Count : 0) < stranded.Count
                    || !Sails(world, ids[i], from) || !Sails(world, ids[i], point) || Refused(world, ids[i], point))
                {
                    continue; // full, or on another water (a lake) than the units or the goal (ADR 0220)
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

            EntityId[] idle = Unboarding(world, stranded);
            if (Due(boat) || (idle.Length > 0 && _tick - _reboarded >= 200))
            {
                // Everyone on the first order; afterwards only those whose boarding gave up (no errand any more).
                _reboarded = _tick;
                _batch.Add(CommandKind.Board, stranded.Where(u => !world.IsHeld(u)).ToArray(), 0, 0, boat);
                Note($"ferry boards {stranded.Count} for {name}");
            }

            Goal = $"{name}: {stranded.Count} units board a ship at ({Pos(world, boat).X / Tile},{Pos(world, boat).Y / Tile})";
            MakeRoom(world, name, stranded, Pos(world, boat));
            return true;
        }

        private int _reboarded = int.MinValue / 2;

        // Unload orders per ship: where it was, and how many it refused there (no dock for that point from its water).
        private readonly Dictionary<uint, (Position At, int Times)> _asked = [];

        // A ship that was ordered three times to unload for this goal without leaving its place has no dock for it.
        private bool Refused(World world, EntityId ship, Position point)
            => _asked.TryGetValue(ship.Value, out var asked) && asked.Times >= 3 && !world.Dockings.Has(ship) && Distance(asked.At, Pos(world, ship)) < (long)Tile * Tile;

        // Whether the ship's water comes within radius tiles of the point (48: the dock search of an unload, Shore.FindDock).
        private static bool Sails(World world, EntityId ship, Position point, int radius = 48)
        {
            if (!world.Navigation.IsBuilt || world.Navigation.ForType(world.Identities.Get(ship).TypeId) is not { } sea)
            {
                return true;
            }

            Position at = Pos(world, ship);
            int component = sea.ComponentOf(sea.TileOf(at.X, at.Y));
            if (component < 0)
            {
                return true; // between tiles or unknown: do not rule it out
            }

            int cx = point.X / Tile;
            int cy = point.Y / Tile;
            for (int y = Math.Max(0, cy - radius); y <= Math.Min(sea.Height - 1, cy + radius); y++)
            {
                for (int x = Math.Max(0, cx - radius); x <= Math.Min(sea.Width - 1, cx + radius); x++)
                {
                    if (sea.ComponentOf((y * sea.Width) + x) == component)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        private int _madeRoom = int.MinValue / 2;

        // Boarders hemmed in by their own kingdom's army (it stands between them and the shore, where a scenario made it
        // appear): the army steps back inland, away from the ship, so the stuck boarder can pass. A player's order.
        private void MakeRoom(World world, string name, List<EntityId> stranded, Position ship)
        {
            EntityId stuck = EntityId.None;
            foreach (EntityId u in stranded)
            {
                if (!world.IsHeld(u))
                {
                    Track(world, u);
                    stuck = stuck.IsNone && _stuck.Contains(u) ? u : stuck;
                }
            }

            EntityId[] crowd = _army.Where(u => !stranded.Contains(u) && !world.IsShip(u)).ToArray(); // ships keep their own orders (a ferry)
            if (stuck.IsNone || crowd.Length == 0 || _tick - _madeRoom < 600)
            {
                return;
            }

            _madeRoom = _tick;
            Position at = Pos(world, stuck);
            long dx = at.X - ship.X;
            long dy = at.Y - ship.Y;
            long length = Math.Max(1, Core.Numerics.IntMath.ISqrt((dx * dx) + (dy * dy)));
            int x = (int)Math.Clamp(at.X + (dx * 8 * Tile / length), Tile / 2, world.Map.WorldWidth - (Tile / 2));
            int y = (int)Math.Clamp(at.Y + (dy * 8 * Tile / length), Tile / 2, world.Map.WorldHeight - (Tile / 2));
            _batch.Add(CommandKind.Move, crowd, x, y);
            Note($"the army makes way for the ferry of {name}");
        }

        // Units of a ferry that are neither aboard nor walking to board.
        private static EntityId[] Unboarding(World world, List<EntityId> units)
            => units.Where(u => !world.IsHeld(u) && !world.Errands.Has(u)).ToArray();

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

        private HashSet<EntityId>? _named;
        private HashSet<EntityId> _needed = [];

        // Enemies a rule still to come needs alive ("!ev_MagesKilled" in v_02's rule for the High Priest's conversation):
        // neither the solver's attacks nor cheat assistance kill them before that rule has fired, or it never can. Rules
        // that make the local kingdom lose do not count.
        private HashSet<EntityId> Needed(Scripting.Legacy.LegacyTriggerSystem triggers, byte me)
        {
            var set = new HashSet<EntityId>();
            World world = session.Simulation.World;
            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                if (triggers.State.RuleDisabled[r] || scenario.Rules[r].Condition.Root is not { } condition || LosesMe(scenario.Rules[r], me))
                {
                    continue;
                }

                foreach (int e in Unwanted(condition))
                {
                    if (triggers.State.EventValues[e] || scenario.Events[e] is not ObjectDestroyedEvent { Objects: NamedObjects named })
                    {
                        continue;
                    }

                    foreach (int index in named.ObjectIndices)
                    {
                        if (Entity(world, triggers, index) is { } u && !world.Diplomacy.MayCommand(me, world.Identities.Get(u).Owner))
                        {
                            set.Add(u);
                        }
                    }
                }
            }

            return set;
        }

        // Units the events of the enabled rules name (heroes, messengers), refreshed each decision.
        private HashSet<EntityId> Named(World world, Scripting.Legacy.LegacyTriggerSystem triggers)
        {
            var set = new HashSet<EntityId>();
            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                if (triggers.State.RuleDisabled[r] || scenario.Rules[r].Condition.Root is not { } condition)
                {
                    continue;
                }

                var events = new List<int>();
                Events(condition, events);
                foreach (int e in events)
                {
                    ObjectSpec? spec = scenario.Events[e] switch
                    {
                        ObjectInAreaEvent x => x.Objects,
                        ObjectDestroyedEvent x => x.Objects,
                        ObjectsEnterHolderEvent x => x.Objects,
                        _ => null,
                    };
                    if (spec is NamedObjects named)
                    {
                        foreach (int index in named.ObjectIndices)
                        {
                            if (Entity(world, triggers, index) is { } u && world.Motions.Has(u) && !world.IsShip(u))
                            {
                                set.Add(u);
                            }
                        }
                    }
                }
            }

            return set;
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
                // Not the units a pending goal names (the heroes): as cargo they would be unloaded with it (ADR 0220).
                _named ??= Named(world, triggers);
                List<EntityId> free = _army.Where(u => !taken.Contains(u) && !_named.Contains(u) && definition.CanHold(world.Identities.Get(u).TypeId))
                    .OrderBy(u => Distance(Pos(world, u), at)).ThenBy(u => u.Value).Take(missing).ToList();
                if (free.Count == 0)
                {
                    continue;
                }

                taken.UnionWith(free);
                any = true;
                if (Due(holder) || (Unboarding(world, free).Length > 0 && _tick - _reboarded >= 200))
                {
                    _reboarded = _tick;
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

                if (_needed.Contains(target))
                {
                    continue; // another rule needs it alive first
                }

                Position t = Pos(world, target);
                bool ally = world.HasPlayer(owner) && world.Diplomacy.StanceOf(me, owner) == Sim.Kingdoms.Stance.Ally;
                if (!ally)
                {
                    // Not the heroes a pending goal names: sent after a far target, they leave their own goals (v_02's
                    // Sartor and the mages across the water). With nobody else, the whole army.
                    _named ??= Named(world, triggers);
                    EntityId[] attackers = [.. _army.Where(u => !_named.Contains(u))];
                    _batch.Add(CommandKind.Attack, attackers.Length > 0 ? attackers : [.. _army], 0, 0, target);
                    Goal = $"{name}: attack ({t.X / Tile},{t.Y / Tile})";
                    return true;
                }

                if (FoughtByScript(world, target) || StagedByScript(index))
                {
                    if (!weaken)
                    {
                        // The script kills the ally itself (the uncle): invulnerability the solver granted must not save
                        // him (ADR 0220).
                        Vulnerable(world, Array.FindIndex(scenario.Events.ToArray(), ev => ev.Name == name), name, [target]);
                    }

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

            // The carrier chosen first keeps the job, also while a ferry takes it to the item (ADR 0220).
            if (_takers.TryGetValue(item.Value, out EntityId kept) && world.Entities.IsAlive(kept) && world.Inventories.Has(kept)
                && world.Diplomacy.MayCommand(me, world.Identities.Get(kept).Owner) && (world.Motions.Has(kept) || world.IsHeld(kept)) && CanCarry(world, kept, item))
            {
                picker = kept;
            }

            bool keep = picker is not null;

            for (int i = 0; i < ids.Length && !keep; i++)
            {
                if (world.Diplomacy.MayCommand(me, world.Identities.Get(ids[i]).Owner) && !world.IsHeld(ids[i]) && world.Motions.Has(ids[i])
                    && CanCarry(world, ids[i], item) && Cost(world, triggers, ids[i], where) is var cost && cost < best)
                {
                    best = cost;
                    picker = ids[i];
                }
            }

            if (picker is not { } unit)
            {
                Goal = $"{goal.Name}: nobody to carry the item";
                return false;
            }

            _takers[item.Value] = unit;

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

        // How far a unit is from an item it would fetch: a unit that can walk there comes before every unit that cannot
        // (a ninja climbs to a book on a cliff-ringed ledge, WORLD-018), then the nearest.
        private static long Cost(World world, Scripting.Legacy.LegacyTriggerSystem triggers, EntityId unit, Position where)
        {
            var around = new RectArea((where.X / Tile) - 1, (where.Y / Tile) - 1, (where.X / Tile) + 1, (where.Y / Tile) + 1);
            long d = Distance(Pos(world, unit), where);
            return Reach(world, triggers, unit, around, where) is null ? (long.MaxValue / 2) + Math.Min(d, long.MaxValue / 4) : d;
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

        // Water beyond the line that one of the kingdom's ships can sail to, nearest the unit's row (or column), within 32
        // tiles of the line.
        private static Position? WaterBeyond(World world, byte me, EntityId unit, bool vertical, int limit, bool below)
        {
            if (!world.Navigation.IsBuilt)
            {
                return null;
            }

            Position at = Pos(world, unit);
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length; i++)
            {
                if (!world.IsShip(ids[i]) || !world.Diplomacy.MayCommand(me, identities[i].Owner) || world.Navigation.ForType(identities[i].TypeId) is not { } sea)
                {
                    continue;
                }

                Position s = Pos(world, ids[i]);
                int component = sea.ComponentOf(sea.TileOf(s.X, s.Y));
                int size = vertical ? sea.Height : sea.Width;
                int other = vertical ? at.X / Tile : at.Y / Tile;
                int otherSize = vertical ? sea.Width : sea.Height;
                for (int d = 0; d < otherSize && component >= 0; d++)
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

                            int tile = vertical ? (line * sea.Width) + o : (o * sea.Width) + line;
                            if (sea.ComponentOf(tile) == component)
                            {
                                return new Position(sea.CenterX(tile), sea.CenterY(tile));
                            }
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

        private readonly Dictionary<string, List<EntityId>> _assigned = new(StringComparer.Ordinal);

        private List<EntityId> OwnUnits(World world, byte me, Scripting.Legacy.LegacyTriggerSystem triggers, ObjectSpec spec, Position? target, string? goal = null)
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
            else if (goal is not null && _assigned.TryGetValue(goal, out List<EntityId>? kept)
                && kept.All(u => world.Entities.IsAlive(u) && world.Diplomacy.MayCommand(me, world.Identities.Get(u).Owner) && (world.IsHeld(u) || (world.Motions.Has(u) && !_stuck.Contains(u)))))
            {
                // The units sent first keep the goal, also aboard a ferry (else the next nearest board too, ADR 0220).
                units.AddRange(kept);
            }
            else if ((Mask(spec) & (1 << me)) != 0 && _army.Count > 0 && target is { } at)
            {
                // "So many of my units": the ones nearest to the target, as many as the count asks (at least one); the
                // rest of the army stays free, and a crowd does not block the scenario's own movers.
                var nearest = new List<(long Distance, int Index)>(_army.Count);
                for (int i = 0; i < _army.Count; i++)
                {
                    // Ships ferry the walkers; they are not walkers themselves (a galleon would try to board a ship).
                    if (_stuck.Contains(_army[i]) || world.IsShip(_army[i]))
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

                if (goal is not null)
                {
                    _assigned[goal] = [.. units];
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
            _spawnPoint = at;
        }

        // The units a rule still to come waits to see hurt ("WoolinIsWounded": $health < $WoolinHealth, when that is all
        // it lacks): the scenario's own enemies must land that blow, so cheat assistance spares the enemies near them.
        private readonly List<EntityId> _wounded = [];
        private readonly List<int> _woundEvents = [];
        private readonly List<int> _woundVariables = [];

        private List<EntityId> AwaitingWounds(World world)
        {
            // Buffers reused across calls (once a second with cheat assistance on).
            List<EntityId> units = _wounded;
            List<int> wanted = _woundEvents, variables = _woundVariables;
            units.Clear();
            if (session.Scenario?.Triggers is not { } triggers)
            {
                return units;
            }

            for (int r = 0; r < scenario.Rules.Length; r++)
            {
                if (triggers.State.RuleDisabled[r] || scenario.Rules[r].Condition.Root is not { } condition)
                {
                    continue;
                }

                wanted.Clear();
                Wanted(condition, negated: false, wanted);
                variables.Clear();
                Variables(condition, negated: false, variables);
                int open = 0;
                foreach (int e in wanted)
                {
                    open += triggers.State.EventValues[e] ? 0 : 1;
                }

                // Only when the wound is all the rule still lacks (Kaffar has spoken).
                if (open != 1 || variables.Exists(v => (uint)v < (uint)triggers.State.Variables.Length && triggers.State.Variables[v] == 0))
                {
                    continue;
                }

                foreach (int e in wanted)
                {
                    if (!triggers.State.EventValues[e] && scenario.Events[e] is ObjectExpressionEvent
                        {
                            Objects: NamedObjects named,
                            Expression.Root: Content.Compiled.Scenario.Expressions.BinaryExpr
                            {
                                Op: Content.Compiled.Scenario.Expressions.BinaryOp.Less,
                                Left: Content.Compiled.Scenario.Expressions.ReservedVar { Variable: Content.Compiled.Scenario.Expressions.ReservedVariable.Health },
                            },
                        })
                    {
                        foreach (int index in named.ObjectIndices)
                        {
                            if (Entity(world, triggers, index) is { } u)
                            {
                                units.Add(u);
                            }
                        }
                    }
                }
            }

            return units;
        }

        // Whether an object stands within 12 tiles of one of the points.
        private static bool Near(World world, EntityId e, List<Position> points)
        {
            if (points.Count == 0 || !world.Positions.Has(e))
            {
                return false;
            }

            Position at = world.Positions.Get(e);
            return points.Exists(p => Math.Max(Math.Abs(p.X - at.X), Math.Abs(p.Y - at.Y)) <= 12 * Tile);
        }

        private void Clear(World world, byte me)
        {
            var targets = new List<EntityId>();
            List<Position> spared = AwaitingWounds(world).ConvertAll(u => Pos(world, u));
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            ReadOnlySpan<Identity> identities = world.Identities.Components;
            for (int i = 0; i < ids.Length && targets.Count < 1000; i++)
            {
                if (IsEnemy(world, me, identities[i]) && !_protected.Contains(ids[i]) && !Near(world, ids[i], spared))
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
