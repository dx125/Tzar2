using System;
using Tzar2.Content.Compiled;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Session;
using Tzar2.Sim.Events;

namespace Tzar2.Game.Editing;

/// <summary>The result of a smoke run.</summary>
/// <param name="Ticks">Ticks played.</param>
/// <param name="Faults">Scenario faults the trigger engine reported (unknown names, divisions by zero, cut rules…).</param>
/// <param name="Error">The exception that stopped the match (or that refused it at setup), or null.</param>
public sealed record SmokeResult(int Ticks, int Faults, string? Error)
{
    /// <summary>Whether the map loaded and ran without an exception.</summary>
    public bool Passed => Error is null;
}

/// <summary>
/// The headless smoke test of converted maps (TDD §18.5 REQ-EDT-12): load the map as the game would (a scenario match
/// with the triggers, the AI and the resources), run 600 ticks, and report whether everything resolved.
/// </summary>
public static class MapSmoke
{
    /// <summary>The ticks of the smoke test (REQ-EDT-12).</summary>
    public const int DefaultTicks = 600;

    /// <summary>Runs <paramref name="ticks"/> ticks of <paramref name="mission"/>.</summary>
    public static SmokeResult Run(MissionContent mission, Ruleset ruleset, int ticks = DefaultTicks)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(ruleset);
        GameSession session;
        try
        {
            session = new GameSession(ScenarioMatch.Setup(mission, ruleset), ruleset, fog: false);
        }
        catch (ArgumentException e)
        {
            return new SmokeResult(0, 0, e.Message);
        }

        using (session)
        {
            int faults = 0;
            var events = new System.Collections.Generic.List<SimEvent>();
            for (int t = 0; t < ticks; t++)
            {
                try
                {
                    session.Step(1);
                }
#pragma warning disable CA1031 // The smoke test reports whatever stops the match.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    return new SmokeResult(t, faults, e.GetType().Name + ": " + e.Message);
                }

                session.DrainEvents(events);
                foreach (SimEvent e in events)
                {
                    faults += e.Kind == SimEventKind.ScenarioFault ? 1 : 0;
                }

                events.Clear();
            }

            return new SmokeResult(ticks, faults, null);
        }
    }
}
