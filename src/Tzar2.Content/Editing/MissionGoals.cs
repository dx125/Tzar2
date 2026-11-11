using System;
using System.Globalization;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>The mission goals the editor's wizard makes (ADR 0290).</summary>
public enum GoalKind : byte
{
    /// <summary>Win when no enemy unit or building is left.</summary>
    DestroyEnemies,

    /// <summary>Win when an object reaches an area.</summary>
    ReachArea,

    /// <summary>Win when a timer of so many minutes runs out.</summary>
    HoldOut,

    /// <summary>Win when the kingdom has stocked so much of a resource.</summary>
    Gather,

    /// <summary>Lose when an object is destroyed (a hero to keep alive).</summary>
    KeepAlive,
}

/// <summary>One goal to add to a scenario.</summary>
/// <param name="Kind">What the goal is.</param>
/// <param name="Quest">The quest text the player reads (empty: no quest).</param>
/// <param name="Kingdom">The player's kingdom, 1-8.</param>
/// <param name="ObjectId">The document id of the object of <see cref="GoalKind.ReachArea"/> and <see cref="GoalKind.KeepAlive"/> (<see cref="EditorObject.Id"/>), or -1.</param>
/// <param name="Area">The area of <see cref="GoalKind.ReachArea"/>, or null.</param>
/// <param name="Minutes">The time of <see cref="GoalKind.HoldOut"/>.</param>
/// <param name="Resource">The resource of <see cref="GoalKind.Gather"/>: gold, wood, food or stone.</param>
/// <param name="Amount">The stock of <see cref="GoalKind.Gather"/>.</param>
/// <param name="NextMap">The campaign map the win leads to, or null (the end of a single map).</param>
public sealed record MissionGoal(GoalKind Kind, string Quest, int Kingdom = 1, int ObjectId = -1, RectArea? Area = null, int Minutes = 10, string Resource = "gold", int Amount = 1000, string? NextMap = null);

/// <summary>
/// The mission goal wizard (ADR 0290): one goal becomes the events, actions and rules a map maker would write by hand -
/// a start rule that adds the quest (and starts the timer), an event or a condition for the goal, and a rule that wins
/// (or loses) and takes the quest away. The names are numbered ("Goal 1 ...") and legacy-safe, so the converter's
/// original format and the validator accept them, and the rules stay ordinary rules to edit afterwards.
/// </summary>
public static class MissionGoals
{
    /// <summary>The resources a gather goal can count (the original's <c>@gold</c>, <c>@wood</c>, <c>@food</c>, <c>@stone</c>).</summary>
    public static readonly System.Collections.Immutable.ImmutableArray<string> Resources = ["gold", "wood", "food", "stone"];

    /// <summary>Adds <paramref name="goal"/> to the scenario.</summary>
    /// <exception cref="ArgumentException">The goal lacks what its kind needs (an object, an area, a positive time or amount).</exception>
    public static ScenarioModel Add(ScenarioModel model, MissionGoal goal)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(goal);
        if (goal.Kingdom is < 1 or > 8)
        {
            throw new ArgumentException("The kingdom is 1-8.", nameof(goal));
        }

        int n = Next(model);
        string tag = "Goal" + n.ToString(CultureInfo.InvariantCulture);
        string title = "Goal " + n.ToString(CultureInfo.InvariantCulture);
        ushort me = PlayerMasks.Of(goal.Kingdom);
        ScenarioModel m = model;
        int quest = -1;
        if (goal.Quest.Length > 0)
        {
            m = m.AddAction(new AddQuestAction(title + " quest", goal.Quest, null), out quest);
        }

        string condition;
        switch (goal.Kind)
        {
            case GoalKind.DestroyEnemies:
                m = m.AddEvent(new ObjectExistEvent(tag + "Enemies", new CategoryObjects(ObjectCategory.MajorObjects, (ushort)(PlayerMasks.AllPlayers & ~me), CountComparison.AtMost, 0, Quantifier.All)), out _);
                condition = tag + "Enemies";
                break;
            case GoalKind.ReachArea:
                if (goal.ObjectId < 0 || goal.Area is null)
                {
                    throw new ArgumentException("A reach goal needs an object and an area.", nameof(goal));
                }

                m = m.AddEvent(new ObjectInAreaEvent(tag + "Reached", new NamedObjects(EquatableArray<int>.From([goal.ObjectId]), Quantifier.Any), goal.Area), out _);
                condition = tag + "Reached";
                break;
            case GoalKind.HoldOut:
                if (goal.Minutes < 1)
                {
                    throw new ArgumentException("A hold-out goal needs at least a minute.", nameof(goal));
                }

                m = m.AddAction(new SetTimerAction(tag + "Timer", ScenarioModel.Source((goal.Minutes * 60_000).ToString(CultureInfo.InvariantCulture)), CountDown: true), out int timer);
                m = m.AddRule(new RuleDef(title + " starts", ScenarioModel.Source("1"), new[] { timer }), out _);
                condition = tag + "Timer == 0";
                break;
            case GoalKind.Gather:
                if (goal.Amount < 1 || Resources.IndexOf(goal.Resource) < 0)
                {
                    throw new ArgumentException("A gather goal needs gold, wood, food or stone and a positive amount.", nameof(goal));
                }

                condition = string.Create(CultureInfo.InvariantCulture, $"@{goal.Resource}({goal.Kingdom}) >= {goal.Amount}");
                break;
            case GoalKind.KeepAlive:
                if (goal.ObjectId < 0)
                {
                    throw new ArgumentException("A keep-alive goal needs an object.", nameof(goal));
                }

                m = m.AddEvent(new ObjectDestroyedEvent(tag + "Lost", new NamedObjects(EquatableArray<int>.From([goal.ObjectId]), Quantifier.Any)), out _);
                condition = tag + "Lost";
                break;
            default:
                throw new ArgumentException("Unknown goal kind.", nameof(goal));
        }

        if (quest >= 0)
        {
            m = m.AddRule(new RuleDef(title + " quest", ScenarioModel.Source("1"), new[] { quest }), out _);
        }

        ActionDef end = goal.Kind == GoalKind.KeepAlive
            ? new PlayerLoseAction(title + " lost", me, null)
            : new PlayerWinAction(title + " won", me, goal.NextMap is { Length: > 0 } next ? next : null);
        m = m.AddAction(end, out int ending);
        int[] actions = quest >= 0 ? [EndQuest(ref m, title, quest), ending] : [ending];
        m = m.AddRule(new RuleDef(title + (goal.Kind == GoalKind.KeepAlive ? " fails" : " done"), ScenarioModel.Source(condition), actions), out _);
        return m;
    }

    // The quest leaves the log when the goal ends.
    private static int EndQuest(ref ScenarioModel m, string title, int quest)
    {
        m = m.AddAction(new RemoveQuestAction(title + " quest done", quest), out int remove);
        return remove;
    }

    // The next free goal number: one more than the highest "Goal N" rule.
    private static int Next(ScenarioModel m)
    {
        int highest = 0;
        foreach (RuleDef r in m.Rules)
        {
            if (r.Name.StartsWith("Goal ", StringComparison.Ordinal))
            {
                int end = 5;
                while (end < r.Name.Length && char.IsAsciiDigit(r.Name[end]))
                {
                    end++;
                }

                if (end > 5 && int.TryParse(r.Name.AsSpan(5, end - 5), NumberStyles.None, CultureInfo.InvariantCulture, out int k))
                {
                    highest = Math.Max(highest, k);
                }
            }
        }

        return highest + 1;
    }
}
