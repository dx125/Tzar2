using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>What part of the trigger program holds a reference (validation, click-to-locate, "referenced by").</summary>
public enum ReferenceOwner : byte
{
    /// <summary>An event (<see cref="ScenarioModel.Events"/> index).</summary>
    Event,

    /// <summary>An action (<see cref="ScenarioModel.Actions"/> index).</summary>
    Action,

    /// <summary>A phrase (conversation index, phrase index).</summary>
    Phrase,

    /// <summary>A custom sound (<see cref="ScenarioModel.Sounds"/> index).</summary>
    Sound,
}

/// <summary>One object reference of the trigger program.</summary>
/// <param name="Owner">What holds it.</param>
/// <param name="Index">Index of the event, action, conversation or sound.</param>
/// <param name="SubIndex">Phrase index for <see cref="ReferenceOwner.Phrase"/>, else −1.</param>
/// <param name="Object">The object reference (an id inside a document).</param>
public readonly record struct ObjectReference(ReferenceOwner Owner, int Index, int SubIndex, int Object);

/// <summary>
/// Rewrites the references of a scenario: object references (ids ↔ indices, deletion, search and replace) and the
/// structural indices that point at actions, conversations and phrases when those are deleted (Appendix C.3: "deleting an
/// action removes it from all rules"). Pure functions over immutable records.
/// </summary>
public static class ScenarioRewriter
{
    /// <summary>
    /// Maps every object reference through <paramref name="map"/>. A reference that maps to null is removed: dropped from
    /// named sets, a holder becomes unset (−1), a phrase or sound source becomes none, and an object area becomes the
    /// 1×1 rectangle <paramref name="fallbackTile"/> gives for it (or an empty rectangle).
    /// </summary>
    public static ScenarioModel MapObjects(ScenarioModel model, Func<int, int?> map, Func<int, (int X, int Y)?>? fallbackTile = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(map);
        var r = new ObjectMapper(map, fallbackTile);
        return model with
        {
            Events = EquatableArray<EventDef>.From(Each(model.Events, r.Event)),
            Actions = EquatableArray<ActionDef>.From(Each(model.Actions, r.Action)),
            Conversations = EquatableArray<ConversationDef>.From(Each(model.Conversations, c => c with
            {
                Phrases = EquatableArray<PhraseDef>.From(Each(c.Phrases, p => p with { SourceObject = r.Optional(p.SourceObject) })),
            })),
            Sounds = EquatableArray<CustomSoundDef>.From(Each(model.Sounds, s => r.Sound(s))),
        };
    }

    /// <summary>Every object reference of the model, in table order.</summary>
    public static ImmutableArray<ObjectReference> References(ScenarioModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var found = ImmutableArray.CreateBuilder<ObjectReference>();
        ReferenceOwner owner = ReferenceOwner.Event;
        int index = 0, sub = -1;
        var r = new ObjectMapper(
            id =>
            {
                found.Add(new ObjectReference(owner, index, sub, id));
                return id;
            },
            null);
        for (index = 0; index < model.Events.Length; index++)
        {
            r.Event(model.Events[index]);
        }

        owner = ReferenceOwner.Action;
        for (index = 0; index < model.Actions.Length; index++)
        {
            r.Action(model.Actions[index]);
        }

        owner = ReferenceOwner.Phrase;
        for (index = 0; index < model.Conversations.Length; index++)
        {
            EquatableArray<PhraseDef> phrases = model.Conversations[index].Phrases;
            for (sub = 0; sub < phrases.Length; sub++)
            {
                r.Optional(phrases[sub].SourceObject);
            }
        }

        owner = ReferenceOwner.Sound;
        sub = -1;
        for (index = 0; index < model.Sounds.Length; index++)
        {
            r.Optional(model.Sounds[index].SourceObject);
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// Removes the actions in <paramref name="removed"/>: they leave every rule, and Remove Quest / Stop Timer actions that
    /// pointed at one become unset (−1); later indices shift down.
    /// </summary>
    public static ScenarioModel RemoveActions(ScenarioModel model, IReadOnlyCollection<int> removed)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(removed);
        int[] map = Remap(model.Actions.Length, removed);
        var actions = new List<ActionDef>();
        for (int i = 0; i < model.Actions.Length; i++)
        {
            if (map[i] < 0)
            {
                continue;
            }

            actions.Add(model.Actions[i] switch
            {
                RemoveQuestAction q => q with { AddQuestActionIndex = Through(map, q.AddQuestActionIndex) },
                StopTimerAction t => t with { SetTimerActionIndex = Through(map, t.SetTimerActionIndex) },
                ActionDef a => a,
            });
        }

        var rules = new List<RuleDef>();
        foreach (RuleDef rule in model.Rules)
        {
            var list = new List<int>();
            foreach (int a in rule.ActionIndices)
            {
                int m = Through(map, a);
                if (m >= 0)
                {
                    list.Add(m);
                }
            }

            rules.Add(rule with { ActionIndices = list.ToArray() });
        }

        return model with { Actions = EquatableArray<ActionDef>.From(actions), Rules = EquatableArray<RuleDef>.From(rules) };
    }

    /// <summary>Moves action <paramref name="from"/> to position <paramref name="to"/> of the action table, keeping every reference.</summary>
    public static ScenarioModel MoveAction(ScenarioModel model, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(model);
        int n = model.Actions.Length;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)from, (uint)n, nameof(from));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)to, (uint)n, nameof(to));
        var order = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            order.Add(i);
        }

        order.RemoveAt(from);
        order.Insert(to, from);
        int[] map = new int[n];
        for (int i = 0; i < n; i++)
        {
            map[order[i]] = i;
        }

        var actions = new ActionDef[n];
        for (int i = 0; i < n; i++)
        {
            actions[map[i]] = model.Actions[i] switch
            {
                RemoveQuestAction q => q with { AddQuestActionIndex = Through(map, q.AddQuestActionIndex) },
                StopTimerAction t => t with { SetTimerActionIndex = Through(map, t.SetTimerActionIndex) },
                ActionDef a => a,
            };
        }

        var rules = new List<RuleDef>();
        foreach (RuleDef rule in model.Rules)
        {
            int[] list = new int[rule.ActionIndices.Length];
            for (int k = 0; k < list.Length; k++)
            {
                list[k] = Through(map, rule.ActionIndices[k]);
            }

            rules.Add(rule with { ActionIndices = list });
        }

        return model with { Actions = actions, Rules = EquatableArray<RuleDef>.From(rules) };
    }

    /// <summary>Removes conversation <paramref name="index"/>; Conversation actions that started it become unset (−1).</summary>
    public static ScenarioModel RemoveConversation(ScenarioModel model, int index)
    {
        ArgumentNullException.ThrowIfNull(model);
        int[] map = Remap(model.Conversations.Length, [index]);
        var conversations = new List<ConversationDef>();
        for (int i = 0; i < model.Conversations.Length; i++)
        {
            if (map[i] >= 0)
            {
                conversations.Add(model.Conversations[i]);
            }
        }

        return model with
        {
            Conversations = EquatableArray<ConversationDef>.From(conversations),
            Actions = EquatableArray<ActionDef>.From(Each(model.Actions, a => a is ConversationAction c ? c with { ConversationIndex = Through(map, c.ConversationIndex) } : a)),
        };
    }

    /// <summary>Removes phrase <paramref name="phrase"/> of a conversation; it leaves every follow-up list.</summary>
    public static ConversationDef RemovePhrase(ConversationDef conversation, int phrase)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        int[] map = Remap(conversation.Phrases.Length, [phrase]);
        var phrases = new List<PhraseDef>();
        for (int i = 0; i < conversation.Phrases.Length; i++)
        {
            if (map[i] < 0)
            {
                continue;
            }

            PhraseDef p = conversation.Phrases[i];
            var follow = new List<int>();
            foreach (int f in p.FollowUps)
            {
                int m = Through(map, f);
                if (m >= 0)
                {
                    follow.Add(m);
                }
            }

            phrases.Add(p with { FollowUps = follow.ToArray() });
        }

        return conversation with { Phrases = EquatableArray<PhraseDef>.From(phrases) };
    }

    private static int[] Remap(int count, IReadOnlyCollection<int> removed)
    {
        int[] map = new int[count];
        var gone = new bool[count];
        foreach (int r in removed)
        {
            if ((uint)r < (uint)count)
            {
                gone[r] = true;
            }
        }

        int next = 0;
        for (int i = 0; i < count; i++)
        {
            map[i] = gone[i] ? -1 : next++;
        }

        return map;
    }

    private static int Through(int[] map, int index) => (uint)index < (uint)map.Length ? map[index] : -1;

    private static List<TOut> Each<TIn, TOut>(EquatableArray<TIn> items, Func<TIn, TOut> map)
    {
        var list = new List<TOut>(items.Length);
        foreach (TIn item in items)
        {
            list.Add(map(item));
        }

        return list;
    }

    private sealed class ObjectMapper(Func<int, int?> map, Func<int, (int X, int Y)?>? fallbackTile)
    {
        public int? Optional(int? id) => id is int i ? map(i) : null;

        public ObjectSpec Spec(ObjectSpec spec)
        {
            if (spec is not NamedObjects named)
            {
                return spec;
            }

            var list = new List<int>(named.ObjectIndices.Length);
            foreach (int id in named.ObjectIndices)
            {
                if (map(id) is int m)
                {
                    list.Add(m);
                }
            }

            return named with { ObjectIndices = list.ToArray() };
        }

        public ObjectSpec? OptionalSpec(ObjectSpec? spec) => spec is null ? null : Spec(spec);

        public AreaSpec Area(AreaSpec area)
        {
            if (area is not ObjectArea o)
            {
                return area;
            }

            if (map(o.ObjectIndex) is int m)
            {
                return o with { ObjectIndex = m };
            }

            return fallbackTile?.Invoke(o.ObjectIndex) is (int x, int y) ? new RectArea(x, y, x, y) : new RectArea(0, 0, -1, -1);
        }

        public int Holder(int id) => id >= 0 && map(id) is int m ? m : -1;

        public CustomSoundDef Sound(CustomSoundDef s)
        {
            if (s.SourceObject is not int id)
            {
                return s;
            }

            if (map(id) is int m)
            {
                return s with { SourceObject = m };
            }

            return fallbackTile?.Invoke(id) is (int x, int y) ? s with { SourceObject = null, X = x, Y = y } : s with { SourceObject = null };
        }

        public EventDef Event(EventDef e) => e switch
        {
            ObjectInAreaEvent x => x with { Objects = Spec(x.Objects), Area = Area(x.Area) },
            ObjectDestroyedEvent x => x with { Objects = Spec(x.Objects) },
            ObjectsEnterHolderEvent x => x with { Objects = Spec(x.Objects), HolderIndex = Holder(x.HolderIndex), Holders = OptionalSpec(x.Holders) },
            ObjectLeaveHolderEvent x => x with { Objects = Spec(x.Objects), HolderIndex = Holder(x.HolderIndex), Holders = OptionalSpec(x.Holders) },
            ObjectExistEvent x => x with { Objects = Spec(x.Objects) },
            ObjectExpressionEvent x => x with { Objects = Spec(x.Objects) },
            _ => e,
        };

        public ActionDef Action(ActionDef a) => a switch
        {
            AppearObjectAction x => x with { Objects = Spec(x.Objects) },
            ChangeObjectOwnerAction x => x with { Objects = Spec(x.Objects) },
            DestroyObjectAction x => x with { Objects = Spec(x.Objects) },
            EraseObjectAction x => x with { Objects = Spec(x.Objects) },
            LockUnitsAction x => x with { Objects = Spec(x.Objects) },
            ExploreAreaAction x => x with { Area = Area(x.Area) },
            LockViewAction x => x with { Area = Area(x.Area) },
            ObjectOrderAction x => x with { Objects = Spec(x.Objects), Area = Area(x.Area) },
            ScrollViewAction x => x with { From = Area(x.From), To = Area(x.To) },
            SpawnObjectAction x => x with { Area = Area(x.Area) },
            SetObjectPropertiesAction x => x with { Objects = Spec(x.Objects) },
            _ => a,
        };
    }
}
