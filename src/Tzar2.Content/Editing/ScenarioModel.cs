using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Content.Editing;

/// <summary>
/// The scenario half of a <see cref="MapDocument"/> (ADR 0150): the same records as <see cref="ScenarioData"/> (ADR 0120)
/// with two differences that keep editing simple. Object references (<see cref="NamedObjects"/>, <see cref="ObjectArea"/>,
/// holders, phrase and sound sources) are <see cref="EditorObject.Id"/>s, and expressions are source text only
/// (<see cref="ExpressionDef.Root"/> is null): <see cref="MapDocument.BuildScenario"/> re-parses every expression against
/// the tables of the moment, so renaming or deleting an event, a timer or a variable never leaves a stale tree.
/// </summary>
/// <param name="Properties">Map properties.</param>
/// <param name="Kingdoms">Kingdoms (numbers unique).</param>
/// <param name="Diplomacy">Diplomacy pairs.</param>
/// <param name="Variables">Declared user variables (without <c>$</c>), in index order; expressions may add more on build.</param>
/// <param name="Events">Events.</param>
/// <param name="Actions">Actions.</param>
/// <param name="Rules">Rules, in evaluation order.</param>
/// <param name="Conversations">Conversations.</param>
/// <param name="Sounds">Custom sounds.</param>
/// <param name="Music">Custom music playlist.</param>
public sealed record ScenarioModel(
    MapProperties Properties,
    EquatableArray<KingdomDef> Kingdoms,
    EquatableArray<DiplomacyDef> Diplomacy,
    EquatableArray<string> Variables,
    EquatableArray<EventDef> Events,
    EquatableArray<ActionDef> Actions,
    EquatableArray<RuleDef> Rules,
    EquatableArray<ConversationDef> Conversations,
    EquatableArray<CustomSoundDef> Sounds,
    EquatableArray<ResourceRef> Music)
{
    /// <summary>A scenario with default properties and nothing else.</summary>
    public static ScenarioModel Empty(string title) => new(
        new MapProperties(title, string.Empty, string.Empty, GameType.Standard, VictoryCondition.Standard, 0, 1, false, false),
        EquatableArray<KingdomDef>.Empty,
        EquatableArray<DiplomacyDef>.Empty,
        EquatableArray<string>.Empty,
        EquatableArray<EventDef>.Empty,
        EquatableArray<ActionDef>.Empty,
        EquatableArray<RuleDef>.Empty,
        EquatableArray<ConversationDef>.Empty,
        EquatableArray<CustomSoundDef>.Empty,
        EquatableArray<ResourceRef>.Empty);

    /// <summary>The kingdom with legacy number <paramref name="number"/>, or null.</summary>
    public KingdomDef? Kingdom(int number)
    {
        foreach (KingdomDef k in Kingdoms)
        {
            if (k.Number == number)
            {
                return k;
            }
        }

        return null;
    }

    /// <summary>A default kingdom (Appendix C.1): active, Human or AI for 1, AI only otherwise, no start location.</summary>
    public static KingdomDef DefaultKingdom(int number) => new(
        number,
        "Kingdom " + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string.Empty,
        string.Empty,
        number == 1 ? PlayerType.HumanOrAi : PlayerType.AiOnly,
        true,
        0,
        0,
        0,
        0,
        string.Empty,
        true,
        true,
        false,
        -1,
        -1,
        EquatableArray<string>.Empty,
        EquatableArray<string>.Empty,
        EquatableArray<string>.Empty,
        EquatableArray<string>.Empty,
        EquatableArray<string>.Empty);

    /// <summary>A copy with kingdom <paramref name="kingdom"/> replaced or added (kingdoms stay sorted by number).</summary>
    public ScenarioModel WithKingdom(KingdomDef kingdom)
    {
        ArgumentNullException.ThrowIfNull(kingdom);
        var list = new List<KingdomDef>(Kingdoms.Length + 1);
        bool placed = false;
        foreach (KingdomDef k in Kingdoms)
        {
            if (k.Number == kingdom.Number)
            {
                list.Add(kingdom);
                placed = true;
            }
            else
            {
                list.Add(k);
            }
        }

        if (!placed)
        {
            int at = 0;
            while (at < list.Count && list[at].Number < kingdom.Number)
            {
                at++;
            }

            list.Insert(at, kingdom);
        }

        return this with { Kingdoms = EquatableArray<KingdomDef>.From(list) };
    }

    /// <summary>Diplomacy of the ordered pair, or null when not set.</summary>
    public DiplomacyDef? DiplomacyOf(int from, int to)
    {
        foreach (DiplomacyDef d in Diplomacy)
        {
            if (d.From == from && d.To == to)
            {
                return d;
            }
        }

        return null;
    }

    /// <summary>A copy with the pair's diplomacy replaced or added (sorted by from, then to).</summary>
    public ScenarioModel WithDiplomacy(DiplomacyDef pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var list = new List<DiplomacyDef>();
        foreach (DiplomacyDef d in Diplomacy)
        {
            if (d.From != pair.From || d.To != pair.To)
            {
                list.Add(d);
            }
        }

        list.Add(pair);
        list.Sort((a, b) => a.From != b.From ? a.From.CompareTo(b.From) : a.To.CompareTo(b.To));
        return this with { Diplomacy = EquatableArray<DiplomacyDef>.From(list) };
    }

    /// <summary>The scope the model's expressions resolve in: declared variables, events, Set Timer actions.</summary>
    public IExpressionScope Scope() => new TableScope(
        Variables.Items,
        Events.Items.Map(e => e.Name),
        Actions.Items.Map(a => a is SetTimerAction ? a.Name : string.Empty));

    /// <summary>Parses <paramref name="source"/> against this model (for live checks in property pages).</summary>
    public ExpressionDef Parse(string source) => ExpressionParser.Parse(source, new BuildScope(this, new List<string>(Variables.Items)));

    /// <summary>A source-only expression (the model's form).</summary>
    public static ExpressionDef Source(string text) => text.Length == 0 ? ExpressionDef.Empty : new ExpressionDef(text, null);

    /// <summary>
    /// The scope used when the scenario is built: names resolve against the model's tables; a user variable that is not
    /// declared is appended to <see cref="Variables"/> (the original creates variables by use, Appendix C.6).
    /// </summary>
    internal sealed class BuildScope(ScenarioModel model, List<string> variables) : IExpressionScope
    {
        private readonly Dictionary<string, int> _events = Index(model.Events.Items.Map(e => e.Name));
        private readonly Dictionary<string, int> _timers = Index(model.Actions.Items.Map(a => a is SetTimerAction ? a.Name : string.Empty));
        private readonly Dictionary<string, int> _variables = Index(variables.ToImmutableArray());

        public List<string> Variables => variables;

        public int UserVariable(string name)
        {
            if (_variables.TryGetValue(name, out int i))
            {
                return i;
            }

            if (ExpressionParser.IsReserved(name))
            {
                return -1;
            }

            variables.Add(name);
            _variables[name] = variables.Count - 1;
            return variables.Count - 1;
        }

        public int Event(string name) => _events.TryGetValue(name, out int i) ? i : -1;

        public int Timer(string name) => _timers.TryGetValue(name, out int i) ? i : -1;

        private static Dictionary<string, int> Index(ImmutableArray<string> names)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].Length > 0)
                {
                    map.TryAdd(names[i], i);
                }
            }

            return map;
        }
    }
}

/// <summary>Small LINQ-free helpers over immutable arrays (keeps the deterministic assembly free of hidden orderings).</summary>
internal static class EditingArrays
{
    public static ImmutableArray<TOut> Map<TIn, TOut>(this ImmutableArray<TIn> items, Func<TIn, TOut> map)
    {
        var builder = ImmutableArray.CreateBuilder<TOut>(items.Length);
        foreach (TIn item in items)
        {
            builder.Add(map(item));
        }

        return builder.MoveToImmutable();
    }
}
