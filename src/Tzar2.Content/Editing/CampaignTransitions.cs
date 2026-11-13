using System;
using System.Globalization;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>
/// The campaign graph's arrows (ADR 0292): an arrow drawn from one map to another makes the first map's victory (or
/// defeat) lead to the second, and a transition is retargeted or cut from the map's page. The changes are ordinary
/// actions and rules of the map, each one undoable step of that map's history.
/// </summary>
/// <remarks>
/// A victory arrow first sends the map's wins that end it without a next map (<see cref="PlayerWinAction"/> with no map)
/// to the target; a map with none gets the goal wizard's "destroy the enemies" goal leading there (ADR 0290). A defeat
/// arrow alike sends the map's losses without a next map; a map with none gets a rule that loses into the target when
/// the player's kingdom is defeated.
/// </remarks>
public static class CampaignTransitions
{
    /// <summary>The kingdom the map's player plays: the first active one a human may play, else kingdom 1.</summary>
    public static int PlayerOf(ScenarioModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        foreach (KingdomDef k in model.Kingdoms)
        {
            if (k.Active && k.PlayerType == PlayerType.HumanOrAi)
            {
                return k.Number;
            }
        }

        return 1;
    }

    /// <summary>Makes map <paramref name="fromId"/>'s victory or defeat lead to map <paramref name="toId"/>.</summary>
    /// <returns>What was done, for the status line.</returns>
    /// <exception cref="ArgumentException">A map is not in the campaign, or the kind is not a victory or a defeat.</exception>
    public static string Link(CampaignDocument campaign, string fromId, string toId, CampaignEdgeKind kind)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        CampaignEntry from = campaign.Find(fromId) ?? throw new ArgumentException($"No map '{fromId}' in the campaign.", nameof(fromId));
        CampaignEntry to = campaign.Find(toId) ?? throw new ArgumentException($"No map '{toId}' in the campaign.", nameof(toId));
        if (kind is not (CampaignEdgeKind.Win or CampaignEdgeKind.Lose))
        {
            throw new ArgumentException("An arrow makes a victory or a defeat lead to a map.", nameof(kind));
        }

        bool win = kind == CampaignEdgeKind.Win;
        string what = win ? "victory" : "defeat";
        ScenarioModel model = from.Document.State.Scenario;
        ScenarioModel linked = model;
        int retargeted = 0;
        for (int i = 0; i < model.Actions.Length; i++)
        {
            ActionDef? next = model.Actions[i] switch
            {
                PlayerWinAction w when win && string.IsNullOrEmpty(w.Map) => w with { Map = to.MapName },
                PlayerLoseAction l when !win && string.IsNullOrEmpty(l.Map) => l with { Map = to.MapName },
                _ => null,
            };
            if (next is not null)
            {
                linked = linked.SetAction(i, next);
                retargeted++;
            }
        }

        if (retargeted > 0)
        {
            from.Document.Apply($"The {what} leads to {to.MapName}", s => s.WithScenario(_ => linked));
            return string.Create(CultureInfo.InvariantCulture, $"{from.MapName}: {retargeted} {what} action(s) now lead to {to.MapName}.");
        }

        int player = PlayerOf(model);
        if (win)
        {
            from.Document.Apply($"The victory leads to {to.MapName}", s => s.WithScenario(m => MissionGoals.Add(m, new MissionGoal(GoalKind.DestroyEnemies, string.Empty, player, NextMap: to.MapName))));
            return $"{from.MapName} had no victory: destroying the enemies now wins it and leads to {to.MapName}.";
        }

        ushort me = PlayerMasks.Of(player);
        from.Document.Apply($"The defeat leads to {to.MapName}", s => s.WithScenario(m =>
        {
            string name = m.FreshName("Defeat to " + to.MapName);
            m = m.AddEvent(new PlayerDefeatedEvent(name, me), out _);
            m = m.AddAction(new PlayerLoseAction(name, me, to.MapName), out int lose);
            return m.AddRule(new RuleDef(name, ScenarioModel.Source(name), new[] { lose }), out _);
        }));
        return $"{from.MapName}: the defeat of kingdom {player} now leads to {to.MapName}.";
    }

    /// <summary>
    /// Sends the transition of map <paramref name="fromId"/>'s action <paramref name="actionIndex"/> to the map named
    /// <paramref name="mapName"/>, or cuts it with null (a victory or a defeat then ends the map; a Load Map cannot be cut).
    /// </summary>
    /// <exception cref="ArgumentException">The map or the action is not there, or a Load Map would be cut.</exception>
    public static void Retarget(CampaignDocument campaign, string fromId, int actionIndex, string? mapName)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        CampaignEntry from = campaign.Find(fromId) ?? throw new ArgumentException($"No map '{fromId}' in the campaign.", nameof(fromId));
        ScenarioModel model = from.Document.State.Scenario;
        if ((uint)actionIndex >= (uint)model.Actions.Length)
        {
            throw new ArgumentException("No such action.", nameof(actionIndex));
        }

        string? target = string.IsNullOrWhiteSpace(mapName) ? null : mapName.Trim();
        ActionDef changed = model.Actions[actionIndex] switch
        {
            PlayerWinAction w => w with { Map = target },
            PlayerLoseAction l => l with { Map = target },
            LoadMapAction m when target is not null => m with { Map = target },
            LoadMapAction => throw new ArgumentException("A Load Map action needs a map; delete the action in the rule editor instead.", nameof(mapName)),
            _ => throw new ArgumentException("The action makes no transition.", nameof(actionIndex)),
        };
        from.Document.Apply(target is null ? "Cut the transition" : "The transition leads to " + target, s => s.WithScenario(m => m.SetAction(actionIndex, changed)));
    }
}
