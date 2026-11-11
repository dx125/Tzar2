using System;
using System.Collections.Immutable;
using System.Globalization;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Content.Editing;

/// <summary>One place of the trigger program that uses a name or an object.</summary>
/// <param name="At">Where (a rule, event, action, phrase or sound; click to locate).</param>
/// <param name="Text">What it is, in words ("rule 'Goal 1 done': condition").</param>
public sealed record TriggerUse(EditorLocation At, string Text);

/// <summary>
/// Where a name or an object is used in the triggers (ADR 0290): every expression that names an event, a timer or a
/// variable (as <see cref="ExpressionHelp.Rename"/> matches them: whole identifiers, ignoring case), and every event,
/// action, phrase or sound that refers to an object. The rule editor lists them before a rename or a deletion.
/// </summary>
public static class TriggerUses
{
    /// <summary>The places whose expressions name <paramref name="name"/> as a <paramref name="kind"/>.</summary>
    public static ImmutableArray<TriggerUse> OfName(ScenarioModel model, NameKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(name);
        string bare = name.TrimStart('$', '#');
        bool Uses(ExpressionDef e) => !e.IsEmpty && !string.Equals(ExpressionHelp.Replace(e.Source, kind, bare, bare + "\u0001"), e.Source, StringComparison.Ordinal);
        var found = ImmutableArray.CreateBuilder<TriggerUse>();
        for (int i = 0; i < model.Rules.Length; i++)
        {
            if (Uses(model.Rules[i].Condition))
            {
                found.Add(new TriggerUse(new EditorLocation(LocationKind.Rule, i), $"rule '{model.Rules[i].Name}': condition"));
            }
        }

        for (int i = 0; i < model.Events.Length; i++)
        {
            if (model.Events[i] is ObjectExpressionEvent x && Uses(x.Expression))
            {
                found.Add(new TriggerUse(new EditorLocation(LocationKind.Event, i), $"event '{x.Name}': expression"));
            }
        }

        for (int i = 0; i < model.Actions.Length; i++)
        {
            ExpressionDef? e = model.Actions[i] switch
            {
                ExpressionAction x => x.Expression,
                SetObjectPropertiesAction x => x.Expression,
                SetTimerAction x => x.Initial,
                _ => null,
            };
            if (e is not null && Uses(e))
            {
                found.Add(new TriggerUse(new EditorLocation(LocationKind.Action, i), $"action '{model.Actions[i].Name}': expression"));
            }

            // A timer is also stopped by name's action.
            if (kind == NameKind.Timer && model.Actions[i] is StopTimerAction stop && (uint)stop.SetTimerActionIndex < (uint)model.Actions.Length
                && string.Equals(model.Actions[stop.SetTimerActionIndex].Name, bare, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new TriggerUse(new EditorLocation(LocationKind.Action, i), $"action '{stop.Name}': stops it"));
            }
        }

        for (int c = 0; c < model.Conversations.Length; c++)
        {
            ConversationDef conversation = model.Conversations[c];
            for (int p = 0; p < conversation.Phrases.Length; p++)
            {
                PhraseDef phrase = conversation.Phrases[p];
                if (Uses(phrase.Condition) || Uses(phrase.Result))
                {
                    found.Add(new TriggerUse(new EditorLocation(LocationKind.Phrase, c, p), $"conversation '{conversation.Name}', phrase '{phrase.Name}'"));
                }
            }
        }

        return found.ToImmutable();
    }

    /// <summary>The places that refer to the object with document id <paramref name="objectId"/>.</summary>
    public static ImmutableArray<TriggerUse> OfObject(ScenarioModel model, int objectId)
    {
        ArgumentNullException.ThrowIfNull(model);
        var found = ImmutableArray.CreateBuilder<TriggerUse>();
        int lastOwner = -1, lastIndex = -1, lastSub = -1;
        foreach (ObjectReference r in ScenarioRewriter.References(model))
        {
            if (r.Object != objectId || ((int)r.Owner == lastOwner && r.Index == lastIndex && r.SubIndex == lastSub))
            {
                continue;
            }

            (lastOwner, lastIndex, lastSub) = ((int)r.Owner, r.Index, r.SubIndex);
            found.Add(r.Owner switch
            {
                ReferenceOwner.Event => new TriggerUse(new EditorLocation(LocationKind.Event, r.Index), $"event '{model.Events[r.Index].Name}'"),
                ReferenceOwner.Action => new TriggerUse(new EditorLocation(LocationKind.Action, r.Index), $"action '{model.Actions[r.Index].Name}'"),
                ReferenceOwner.Phrase => new TriggerUse(new EditorLocation(LocationKind.Phrase, r.Index, r.SubIndex), $"conversation '{model.Conversations[r.Index].Name}', phrase '{model.Conversations[r.Index].Phrases[r.SubIndex].Name}': its speaker"),
                _ => new TriggerUse(new EditorLocation(LocationKind.Sound, r.Index), string.Create(CultureInfo.InvariantCulture, $"sound '{model.Sounds[r.Index].Name}': its source")),
            });
        }

        return found.ToImmutable();
    }
}
