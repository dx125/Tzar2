using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    /// <summary>Most ticks per mission before it counts as not completed (default 30 minutes of game time).</summary>
    public int MaxTicksPerMission { get; init; } = 36_000;

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

    /// <summary>Picks a conversation answer from the candidates (default: the first).</summary>
    public Func<ConversationView, int>? Answer { get; init; }
}

/// <summary>How a mission went in a headless run.</summary>
/// <param name="MapId">The campaign map.</param>
/// <param name="Completed">Whether it ended in a win or a scenario transition to another map.</param>
/// <param name="Result">The mission result (or Lost on a timeout).</param>
/// <param name="Ticks">Ticks played.</param>
/// <param name="Assist">"play" (no cheats), "reinforced" or "cleared" (the strongest assistance used), or "timeout".</param>
/// <param name="NextMapId">Where the campaign went next, or null.</param>
/// <param name="Faults">Scenario faults reported (unknown actions, unsupported functions…).</param>
public sealed record MissionReport(string MapId, bool Completed, MissionResult Result, int Ticks, string Assist, string? NextMapId, int Faults);

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
        var missions = ImmutableArray.CreateBuilder<MissionReport>();
        int retries = 0;
        while (progress.Current is { } mapId && missions.Count < options.MaxMissions)
        {
            MissionReport report;
            MissionEnd end;
            try
            {
                (report, end) = Play(campaign, source.Load(mapId), mapId, ruleset, progress, options);
            }
#pragma warning disable CA1031 // A broken mission is reported, the run stops.
            catch (Exception e)
#pragma warning restore CA1031
            {
                missions.Add(new MissionReport(mapId, false, MissionResult.Lost, 0, "error", null, 0));
                return new CampaignReport(campaign.Id, false, missions.ToImmutable(), progress, $"{mapId}: {e.GetType().Name}: {e.Message}");
            }

            missions.Add(report);
            log?.Invoke($"{mapId}: {(report.Completed ? "completed" : "not completed")} ({report.Result}, {report.Ticks} ticks, {report.Assist}{(report.NextMapId is { } n ? ", next " + n : string.Empty)})");
            progress = end.Progress;
            if (!report.Completed)
            {
                if (++retries > options.MaxRetries)
                {
                    return new CampaignReport(campaign.Id, false, missions.ToImmutable(), progress, $"{mapId} was not completed after {retries} attempts.");
                }
            }
            else
            {
                retries = 0;
            }
        }

        return new CampaignReport(campaign.Id, progress.Completed, missions.ToImmutable(), progress, progress.Completed ? null : "the mission limit was reached");
    }

    /// <summary>Plays one mission and ends it in the campaign flow.</summary>
    public static (MissionReport Report, MissionEnd End) Play(CampaignData campaign, MissionContent mission, string mapId, Ruleset ruleset, CampaignProgress progress, AutoplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(options);
        MatchSetup setup = ScenarioMatch.Setup(mission, ruleset, progress, options.ComputerAi, cheats: options.CheatAssist);
        using var session = new GameSession(setup, ruleset, fog: false);
        var pilot = new Solver(session, mission.Scenario, options);
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

        MissionEnd end = CampaignFlow.Finish(campaign, progress, mapId, session, requested, transition);
        MissionResult result = end.Result;
        bool completed = result != MissionResult.Lost;
        string assist = !completed && ticks >= options.MaxTicksPerMission ? "timeout" : pilot.Assist;
        return (new MissionReport(mapId, completed, result, ticks, assist, end.NextMapId, faults), end);
    }

    // The scripted player of the local kingdom.
    private sealed class Solver(GameSession session, ScenarioData scenario, AutoplayOptions options)
    {
        private readonly CommandList _batch = new();
        private readonly List<EntityId> _army = [];

        public string Assist { get; private set; } = "play";

        public void Act(int tick)
        {
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
            if (_army.Count > 0)
            {
                if (!WalkToObjective(world, me) && tick > 200)
                {
                    Attack(world, me);
                }
            }

            if (options.CheatAssist && tick >= options.ReinforceAfterTicks && tick % 2000 == 7 && _army.Count < 60)
            {
                Reinforce(world, me);
                Assist = Assist == "play" ? "reinforced" : Assist;
            }

            if (options.CheatAssist && tick >= options.ClearAfterTicks && tick % 1000 == 7)
            {
                Clear(world, me);
                Assist = "cleared";
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
                if (identities[i].Owner == me && world.Motions.Has(ids[i]) && world.Ruleset.EntityTypes[identities[i].TypeId].Traits.Armament is not null && !world.IsHeld(ids[i]))
                {
                    _army.Add(ids[i]);
                }
            }
        }

        // Areas the scenario waits for: units it names (or of the local kingdom) walk into Object In Area areas.
        private bool WalkToObjective(World world, byte me)
        {
            if (session.Scenario?.Triggers is not { } triggers)
            {
                return false;
            }

            for (int e = 0; e < scenario.Events.Length; e++)
            {
                if (scenario.Events[e] is not ObjectInAreaEvent { Area: RectArea area } inArea || triggers.State.EventValues[e])
                {
                    continue;
                }

                var units = new List<EntityId>();
                if (inArea.Objects is NamedObjects named)
                {
                    foreach (int index in named.ObjectIndices)
                    {
                        EntityId u = EntityId.FromValue(triggers.State.MapEntities[index]);
                        if (world.Entities.IsAlive(u) && world.Identities.Get(u).Owner == me && world.Motions.Has(u))
                        {
                            units.Add(u);
                        }
                    }
                }
                else if (inArea.Objects is TypedObjects or CategoryObjects && (Mask(inArea.Objects) & (1 << me)) != 0)
                {
                    units.AddRange(_army);
                }

                if (units.Count == 0)
                {
                    continue;
                }

                int x = ((area.Left + area.Right + 1) * Tile) / 2;
                int y = ((area.Top + area.Bottom + 1) * Tile) / 2;
                _batch.Add(CommandKind.Move, units.ToArray(), x, y);
                return true;
            }

            return false;
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

        private void Reinforce(World world, byte me)
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

            if (type < 0 || _army.Count == 0)
            {
                return;
            }

            Position at = world.Positions.Get(_army[0]);
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
