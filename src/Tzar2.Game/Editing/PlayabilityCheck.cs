using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Campaigns;

namespace Tzar2.Game.Editing;

/// <summary>One mission of a playability check.</summary>
/// <param name="MapId">The map's campaign id.</param>
/// <param name="Completed">Whether the bot completed it.</param>
/// <param name="ByPlay">Whether it did so by play alone, with no cheat assistance.</param>
/// <param name="How">What happened, in words.</param>
/// <param name="GameSeconds">Game time the attempt took.</param>
/// <param name="NextMapId">Where the campaign went next, or null.</param>
/// <param name="Faults">Scenario faults (unknown actions, unsupported functions).</param>
/// <param name="Trace">The end state: units per kingdom, the rules that fired, the variables, the bot's goal.</param>
public sealed record PlayabilityRow(string MapId, bool Completed, bool ByPlay, string How, int GameSeconds, string? NextMapId, int Faults, string Trace);

/// <summary>A playability check's result.</summary>
/// <param name="Rows">Every attempt, in order (a lost mission is tried again with more help).</param>
/// <param name="Completed">Whether the map, or the whole campaign, was completed.</param>
/// <param name="Note">Why it stopped early ("cancelled", a broken map), or null.</param>
public sealed record PlayabilityReport(ImmutableArray<PlayabilityRow> Rows, bool Completed, string? Note)
{
    /// <summary>One line of the result: maps completed, by play alone, and what stopped the check.</summary>
    public string Summary
    {
        get
        {
            var done = new HashSet<string>(StringComparer.Ordinal);
            var byPlay = new HashSet<string>(StringComparer.Ordinal);
            var all = new HashSet<string>(StringComparer.Ordinal);
            foreach (PlayabilityRow r in Rows)
            {
                all.Add(r.MapId);
                if (r.Completed)
                {
                    done.Add(r.MapId);
                }

                if (r.ByPlay)
                {
                    byPlay.Add(r.MapId);
                }
            }

            return string.Create(CultureInfo.InvariantCulture, $"{(Completed ? "Completable" : "Not completed")}: {done.Count} of {all.Count} map(s), {byPlay.Count} by play alone") + (Note is { } n ? " (" + n + ")" : string.Empty);
        }
    }
}

/// <summary>What a playability check plays: its maps, built (<see cref="PlayabilityCheck.Prepare(MapDocument)"/>).</summary>
/// <param name="Source">The maps as a campaign.</param>
/// <param name="Survey">Whether a mission the bot cannot complete is skipped to report every map (a campaign).</param>
public sealed record PlayabilitySubject(ICampaignSource Source, bool Survey);

/// <summary>
/// The editor's playability check (ADR 0290): the campaign acceptance's scripted bot plays a map or a whole campaign
/// headless, as a player would, and tells where it gets stuck. It answers conversations, walks the units a rule waits
/// for into their areas and attacks; a lost mission is tried again with cheat assistance (reinforcements, the enemies
/// cleared, the heroes protected), so a map the bot only completes with help still shows where its plain play stopped.
/// </summary>
public static class PlayabilityCheck
{
    /// <summary>
    /// What the check plays, built now from a map (call it where the document is edited; the run may go on another
    /// thread while the map is edited further).
    /// </summary>
    /// <exception cref="ArgumentException">The map does not build (the validator's blocking findings).</exception>
    public static PlayabilitySubject Prepare(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        MissionContent mission = Playtest.Mission(document);
        const string Id = "map";
        string name = mission.Scenario.Properties.Title.Length > 0 ? mission.Scenario.Properties.Title : document.State.Name;
        var campaign = new CampaignData(Id, name, string.Empty, EquatableArray<CampaignMap>.From([new CampaignMap(Id, name, name, string.Empty, IsEntry: true)]), EquatableArray<CampaignEdge>.Empty);
        return new PlayabilitySubject(new InMemoryCampaignSource(campaign, new Dictionary<string, MissionContent> { [Id] = mission }), Survey: false);
    }

    /// <summary>What the check plays, every map of a campaign built now; a mission the bot cannot complete is skipped, so every map is reported.</summary>
    /// <exception cref="ArgumentException">The campaign or one of its maps does not build.</exception>
    public static PlayabilitySubject Prepare(CampaignDocument campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var playtest = new CampaignPlaytest(campaign);
        var maps = new Dictionary<string, MissionContent>(StringComparer.Ordinal);
        foreach (CampaignMap m in playtest.Campaign.Maps)
        {
            maps[m.Id] = playtest.Load(m.Id);
        }

        return new PlayabilitySubject(new InMemoryCampaignSource(playtest.Campaign, maps), Survey: true);
    }

    /// <summary>Plays one map to its end (a win, or a scenario's move to another map).</summary>
    public static PlayabilityReport Map(MapDocument document, Ruleset ruleset, CancellationToken cancellation = default, Action<string>? progress = null, int maxMinutesPerMission = 60)
        => Run(Prepare(document), ruleset, cancellation, progress, maxMinutesPerMission);

    /// <summary>Plays a campaign from its entry map.</summary>
    public static PlayabilityReport Campaign(CampaignDocument campaign, Ruleset ruleset, CancellationToken cancellation = default, Action<string>? progress = null, int maxMinutesPerMission = 60)
        => Run(Prepare(campaign), ruleset, cancellation, progress, maxMinutesPerMission);

    /// <summary>Says an assistance level of the bot in words.</summary>
    public static string Describe(MissionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string help = report.Assist switch
        {
            "play" => "by play alone",
            "resources" => "with extra resources",
            "spawned" => "with units spawned where a rule waits for them",
            "reinforced" => "with reinforcements",
            "cleared" => "with the enemies cleared",
            "god" => "with the heroes protected",
            "god+cleared" => "with the heroes protected and the enemies cleared",
            "timeout" => "ran out of time",
            "cancelled" => "cancelled",
            "error" => "the map failed to start",
            var other => other,
        };
        return report.Completed ? $"completed ({report.Result}) {help}" : report.Assist is "timeout" or "cancelled" or "error" ? help : $"lost ({help})";
    }

    /// <summary>Plays what <see cref="Prepare(MapDocument)"/> built (any thread; minutes on a large map).</summary>
    public static PlayabilityReport Run(PlayabilitySubject subject, Ruleset ruleset, CancellationToken cancellation = default, Action<string>? progress = null, int maxMinutesPerMission = 60)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(ruleset);
        ICampaignSource source = subject.Source;
        bool survey = subject.Survey;
        int tickMs = Core.Ids.Tick.DefaultLengthMilliseconds;
        var options = new AutoplayOptions
        {
            ContinueAfterFailure = survey,
            MaxTicksPerMission = Math.Max(1, maxMinutesPerMission) * 60_000 / tickMs,
            Cancellation = cancellation,
            Progress = progress is null ? null : (map, ticks) => progress(string.Create(CultureInfo.InvariantCulture, $"{map}: {ticks * tickMs / 60_000}:{ticks * tickMs / 1000 % 60:00} of game time")),
        };
        CampaignReport report = CampaignAutoplay.Run(source, ruleset, options, progress);
        var rows = ImmutableArray.CreateBuilder<PlayabilityRow>(report.Missions.Length);
        foreach (MissionReport m in report.Missions)
        {
            rows.Add(new PlayabilityRow(m.MapId, m.Completed, m.Completed && m.Assist == "play", Describe(m), (int)((long)m.Ticks * tickMs / 1000), m.NextMapId, m.Faults, m.Trace));
        }

        bool cancelled = cancellation.IsCancellationRequested;
        return new PlayabilityReport(rows.MoveToImmutable(), report.Completed && !cancelled, cancelled ? "cancelled" : report.Error);
    }
}
