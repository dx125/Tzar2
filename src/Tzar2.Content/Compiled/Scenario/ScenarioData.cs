using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Core.Hashing;

namespace Tzar2.Content.Compiled.Scenario;

/// <summary>
/// The scenario half of a map (ADR 0120): map properties, kingdoms, diplomacy, per-object scenario properties, and the
/// legacy trigger program (variables, events, actions, rules, conversations, custom sounds and music). It sits next to
/// <see cref="MapData"/> (terrain and placed objects) and refers to placed objects by their index in
/// <see cref="MapData.Objects"/>. Built by the legacy converter (Tzar2.Import) or the editor, consumed by the trigger
/// engine (Tzar2.Scripting). Immutable; the constructor checks every cross-reference.
/// </summary>
public sealed class ScenarioData
{
    /// <summary>Version of the encoding behind <see cref="ContentHash"/>.</summary>
    public const int HashVersion = 1;

    /// <summary>Creates a scenario and checks its references.</summary>
    /// <param name="properties">Map properties.</param>
    /// <param name="objectCount">Number of placed objects in the companion <see cref="MapData"/>.</param>
    /// <param name="objects">Scenario properties per placed object (empty, or exactly <paramref name="objectCount"/>).</param>
    /// <param name="kingdoms">Kingdoms (numbers 1–16, unique).</param>
    /// <param name="diplomacy">Diplomacy pairs.</param>
    /// <param name="variables">User variable names (without <c>$</c>).</param>
    /// <param name="events">Events.</param>
    /// <param name="actions">Actions.</param>
    /// <param name="rules">Rules, in evaluation order.</param>
    /// <param name="conversations">Conversations.</param>
    /// <param name="sounds">Custom sounds.</param>
    /// <param name="music">Custom music playlist.</param>
    /// <exception cref="ArgumentException">A reference is out of range.</exception>
    public ScenarioData(
        MapProperties properties,
        int objectCount,
        EquatableArray<ObjectInfo> objects,
        EquatableArray<KingdomDef> kingdoms,
        EquatableArray<DiplomacyDef> diplomacy,
        EquatableArray<string> variables,
        EquatableArray<EventDef> events,
        EquatableArray<ActionDef> actions,
        EquatableArray<RuleDef> rules,
        EquatableArray<ConversationDef> conversations,
        EquatableArray<CustomSoundDef> sounds,
        EquatableArray<ResourceRef> music)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        if (objects.Length != 0 && objects.Length != objectCount)
        {
            throw new ArgumentException($"Expected 0 or {objectCount} object infos, got {objects.Length}.", nameof(objects));
        }

        Properties = properties;
        ObjectCount = objectCount;
        Objects = objects;
        Kingdoms = kingdoms;
        Diplomacy = diplomacy;
        Variables = variables;
        Events = events;
        Actions = actions;
        Rules = rules;
        Conversations = conversations;
        Sounds = sounds;
        Music = music;
        new ReferenceChecker(this).Check();
        ContentHash = StateHasher.Hash(ScenarioJson.Write(this, indented: false), HashVersion);
    }

    /// <summary>A scenario with nothing in it.</summary>
    public static ScenarioData Empty(int objectCount, string title = "") => new(
        new MapProperties(title, string.Empty, string.Empty, GameType.Standard, VictoryCondition.Standard, 0, 1, false, false),
        objectCount,
        EquatableArray<ObjectInfo>.Empty,
        EquatableArray<KingdomDef>.Empty,
        EquatableArray<DiplomacyDef>.Empty,
        EquatableArray<string>.Empty,
        EquatableArray<EventDef>.Empty,
        EquatableArray<ActionDef>.Empty,
        EquatableArray<RuleDef>.Empty,
        EquatableArray<ConversationDef>.Empty,
        EquatableArray<CustomSoundDef>.Empty,
        EquatableArray<ResourceRef>.Empty);

    /// <summary>Map properties.</summary>
    public MapProperties Properties { get; }

    /// <summary>Number of placed objects in the companion <see cref="MapData"/>.</summary>
    public int ObjectCount { get; }

    /// <summary>Per placed object (same index as <see cref="MapData.Objects"/>), or empty when no object has properties.</summary>
    public EquatableArray<ObjectInfo> Objects { get; }

    /// <summary>Kingdoms.</summary>
    public EquatableArray<KingdomDef> Kingdoms { get; }

    /// <summary>Diplomacy pairs.</summary>
    public EquatableArray<DiplomacyDef> Diplomacy { get; }

    /// <summary>User variable names, without <c>$</c> (<see cref="UserVar.VariableIndex"/>).</summary>
    public EquatableArray<string> Variables { get; }

    /// <summary>Events (<see cref="EventRef.EventIndex"/>).</summary>
    public EquatableArray<EventDef> Events { get; }

    /// <summary>Actions (<see cref="RuleDef.ActionIndices"/>, <see cref="TimerRef.ActionIndex"/>).</summary>
    public EquatableArray<ActionDef> Actions { get; }

    /// <summary>Rules, in evaluation order.</summary>
    public EquatableArray<RuleDef> Rules { get; }

    /// <summary>Conversations (<see cref="ConversationAction.ConversationIndex"/>).</summary>
    public EquatableArray<ConversationDef> Conversations { get; }

    /// <summary>Custom ambient sounds.</summary>
    public EquatableArray<CustomSoundDef> Sounds { get; }

    /// <summary>Custom music, played in random order.</summary>
    public EquatableArray<ResourceRef> Music { get; }

    /// <summary>xxHash64 of the canonical JSON encoding (<see cref="ScenarioJson"/>), seeded with <see cref="HashVersion"/>.</summary>
    public ulong ContentHash { get; }

    /// <summary>The scenario properties of placed object <paramref name="index"/> (plain when none were stored).</summary>
    public ObjectInfo ObjectAt(int index) => Objects.Length == 0 ? ObjectInfo.Plain : Objects[index];

    private sealed class ReferenceChecker(ScenarioData s)
    {
        public void Check()
        {
            var numbers = new HashSet<int>();
            foreach (KingdomDef k in s.Kingdoms)
            {
                if (k.Number is < 1 or > 16 || !numbers.Add(k.Number))
                {
                    throw new ArgumentException($"Kingdom number {k.Number} is out of range or repeated.");
                }
            }

            foreach (EventDef e in s.Events)
            {
                CheckEvent(e);
            }

            foreach (ActionDef a in s.Actions)
            {
                CheckAction(a);
            }

            foreach (RuleDef r in s.Rules)
            {
                Expression(r.Condition, r.Name);
                foreach (int i in r.ActionIndices)
                {
                    Index(i, s.Actions.Length, "action", r.Name);
                }
            }

            foreach (ConversationDef c in s.Conversations)
            {
                foreach (PhraseDef p in c.Phrases)
                {
                    OptionalObject(p.SourceObject, p.Name);
                    Expression(p.Condition, p.Name);
                    Expression(p.Result, p.Name);
                    foreach (int f in p.FollowUps)
                    {
                        Index(f, c.Phrases.Length, "phrase", p.Name);
                    }
                }
            }

            foreach (CustomSoundDef sound in s.Sounds)
            {
                OptionalObject(sound.SourceObject, sound.Name);
            }
        }

        private void CheckEvent(EventDef e)
        {
            switch (e)
            {
                case ObjectInAreaEvent x:
                    Objects(x.Objects, e.Name);
                    Area(x.Area, e.Name);
                    break;
                case ObjectDestroyedEvent x:
                    Objects(x.Objects, e.Name);
                    break;
                case ObjectsEnterHolderEvent x:
                    Objects(x.Objects, e.Name);
                    Holder(x.HolderIndex, e.Name);
                    break;
                case ObjectLeaveHolderEvent x:
                    Objects(x.Objects, e.Name);
                    Holder(x.HolderIndex, e.Name);
                    break;
                case ObjectExistEvent x:
                    Objects(x.Objects, e.Name);
                    break;
                case ObjectExpressionEvent x:
                    Objects(x.Objects, e.Name);
                    Expression(x.Expression, e.Name);
                    break;
            }
        }

        private void CheckAction(ActionDef a)
        {
            switch (a)
            {
                case RemoveQuestAction x when x.AddQuestActionIndex >= 0:
                    Typed<AddQuestAction>(x.AddQuestActionIndex, a.Name);
                    break;
                case StopTimerAction x when x.SetTimerActionIndex >= 0:
                    Typed<SetTimerAction>(x.SetTimerActionIndex, a.Name);
                    break;
                case ConversationAction x when x.ConversationIndex >= 0:
                    Index(x.ConversationIndex, s.Conversations.Length, "conversation", a.Name);
                    break;
                case AppearObjectAction x:
                    Objects(x.Objects, a.Name);
                    break;
                case ChangeObjectOwnerAction x:
                    Objects(x.Objects, a.Name);
                    break;
                case DestroyObjectAction x:
                    Objects(x.Objects, a.Name);
                    break;
                case EraseObjectAction x:
                    Objects(x.Objects, a.Name);
                    break;
                case LockUnitsAction x:
                    Objects(x.Objects, a.Name);
                    break;
                case ExploreAreaAction x:
                    Area(x.Area, a.Name);
                    break;
                case LockViewAction x:
                    Area(x.Area, a.Name);
                    break;
                case ObjectOrderAction x:
                    Objects(x.Objects, a.Name);
                    Area(x.Area, a.Name);
                    break;
                case ScrollViewAction x:
                    Area(x.From, a.Name);
                    Area(x.To, a.Name);
                    break;
                case SpawnObjectAction x:
                    Area(x.Area, a.Name);
                    break;
                case ExpressionAction x:
                    Expression(x.Expression, a.Name);
                    break;
                case SetObjectPropertiesAction x:
                    Objects(x.Objects, a.Name);
                    Expression(x.Expression, a.Name);
                    break;
                case SetTimerAction x:
                    Expression(x.Initial, a.Name);
                    break;
            }
        }

        private void Typed<T>(int index, string owner)
        {
            Index(index, s.Actions.Length, "action", owner);
            if (s.Actions[index] is not T)
            {
                throw new ArgumentException($"'{owner}' refers to action {index}, which is not a {typeof(T).Name}.");
            }
        }

        private void Objects(ObjectSpec spec, string owner)
        {
            if (spec is NamedObjects named)
            {
                foreach (int i in named.ObjectIndices)
                {
                    Index(i, s.ObjectCount, "object", owner);
                }
            }
        }

        private void Area(AreaSpec area, string owner)
        {
            if (area is ObjectArea o)
            {
                Index(o.ObjectIndex, s.ObjectCount, "object", owner);
            }
        }

        private void Holder(int index, string owner)
        {
            if (index >= 0)
            {
                Index(index, s.ObjectCount, "object", owner);
            }
        }

        private void OptionalObject(int? index, string owner)
        {
            if (index is int i)
            {
                Index(i, s.ObjectCount, "object", owner);
            }
        }

        private void Expression(ExpressionDef e, string owner)
        {
            if (e.Root is not null)
            {
                Walk(e.Root, owner);
            }
        }

        private void Walk(Expr e, string owner)
        {
            switch (e)
            {
                case UserVar v:
                    Index(v.VariableIndex, s.Variables.Length, "variable", owner);
                    break;
                case EventRef r:
                    Index(r.EventIndex, s.Events.Length, "event", owner);
                    break;
                case TimerRef t:
                    Typed<SetTimerAction>(t.ActionIndex, owner);
                    break;
                case UnaryExpr u:
                    Walk(u.Operand, owner);
                    break;
                case BinaryExpr b:
                    Walk(b.Left, owner);
                    Walk(b.Right, owner);
                    break;
                case AssignExpr a:
                    Walk(a.Target, owner);
                    Walk(a.Value, owner);
                    break;
                case CallExpr c:
                    foreach (Expr arg in c.Arguments)
                    {
                        Walk(arg, owner);
                    }

                    break;
                case SequenceExpr q:
                    foreach (Expr statement in q.Statements)
                    {
                        Walk(statement, owner);
                    }

                    break;
            }
        }

        private static void Index(int index, int count, string what, string owner)
        {
            if ((uint)index >= (uint)count)
            {
                throw new ArgumentException($"'{owner}' refers to {what} {index}, but there are {count}.");
            }
        }
    }
}
