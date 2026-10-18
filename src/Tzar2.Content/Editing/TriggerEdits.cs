using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>
/// Edits of the trigger program (Appendix C.3–C.8): events, actions, rules, variables, conversations, custom sounds and
/// music. Deleting keeps every remaining reference valid (<see cref="ScenarioRewriter"/>).
/// </summary>
public static class TriggerEdits
{
    /// <summary>Replaces the scenario part through <paramref name="change"/>.</summary>
    public static DocumentState WithScenario(this DocumentState state, Func<ScenarioModel, ScenarioModel> change)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(change);
        return state with { Scenario = change(state.Scenario) };
    }

    /// <summary>Appends an event.</summary>
    public static ScenarioModel AddEvent(this ScenarioModel m, EventDef e, out int index)
    {
        ArgumentNullException.ThrowIfNull(m);
        index = m.Events.Length;
        return m with { Events = m.Events.Items.Add(e) };
    }

    /// <summary>Replaces event <paramref name="index"/>.</summary>
    public static ScenarioModel SetEvent(this ScenarioModel m, int index, EventDef e)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m with { Events = m.Events.Items.SetItem(index, e) };
    }

    /// <summary>Removes event <paramref name="index"/>; conditions that named it now refer to an unknown name (validator warns).</summary>
    public static ScenarioModel RemoveEvent(this ScenarioModel m, int index)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m with { Events = m.Events.Items.RemoveAt(index) };
    }

    /// <summary>Appends an action.</summary>
    public static ScenarioModel AddAction(this ScenarioModel m, ActionDef a, out int index)
    {
        ArgumentNullException.ThrowIfNull(m);
        index = m.Actions.Length;
        return m with { Actions = m.Actions.Items.Add(a) };
    }

    /// <summary>Replaces action <paramref name="index"/> (a rename of a Set Timer or Add Quest keeps the references, which are indices).</summary>
    public static ScenarioModel SetAction(this ScenarioModel m, int index, ActionDef a)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m with { Actions = m.Actions.Items.SetItem(index, a) };
    }

    /// <summary>Removes action <paramref name="index"/> from the table and from every rule (Appendix C.3).</summary>
    public static ScenarioModel RemoveAction(this ScenarioModel m, int index) => ScenarioRewriter.RemoveActions(m, [index]);

    /// <summary>Appends a rule.</summary>
    public static ScenarioModel AddRule(this ScenarioModel m, RuleDef r, out int index)
    {
        ArgumentNullException.ThrowIfNull(m);
        index = m.Rules.Length;
        return m with { Rules = m.Rules.Items.Add(r) };
    }

    /// <summary>Replaces rule <paramref name="index"/>.</summary>
    public static ScenarioModel SetRule(this ScenarioModel m, int index, RuleDef r)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m with { Rules = m.Rules.Items.SetItem(index, r) };
    }

    /// <summary>Removes rule <paramref name="index"/> (its actions stay defined).</summary>
    public static ScenarioModel RemoveRule(this ScenarioModel m, int index)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m with { Rules = m.Rules.Items.RemoveAt(index) };
    }

    /// <summary>Moves rule <paramref name="from"/> to position <paramref name="to"/> (rules run in order).</summary>
    public static ScenarioModel MoveRule(this ScenarioModel m, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(m);
        RuleDef r = m.Rules[from];
        return m with { Rules = m.Rules.Items.RemoveAt(from).Insert(to, r) };
    }

    /// <summary>Moves the action at <paramref name="from"/> of rule <paramref name="rule"/>'s list to <paramref name="to"/> (action ordering).</summary>
    public static ScenarioModel MoveRuleAction(this ScenarioModel m, int rule, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(m);
        RuleDef r = m.Rules[rule];
        int a = r.ActionIndices[from];
        return m.SetRule(rule, r with { ActionIndices = r.ActionIndices.Items.RemoveAt(from).Insert(to, a) });
    }

    /// <summary>Declares a user variable (without <c>$</c>); returns the model unchanged if it exists.</summary>
    public static ScenarioModel AddVariable(this ScenarioModel m, string name)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (string v in m.Variables)
        {
            if (string.Equals(v, name, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }

        return m with { Variables = m.Variables.Items.Add(name) };
    }

    /// <summary>Removes declared variable <paramref name="index"/> (expressions that use it declare it again on build).</summary>
    public static ScenarioModel RemoveVariable(this ScenarioModel m, int index)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m with { Variables = m.Variables.Items.RemoveAt(index) };
    }

    /// <summary>Appends a conversation.</summary>
    public static ScenarioModel AddConversation(this ScenarioModel m, ConversationDef c, out int index)
    {
        ArgumentNullException.ThrowIfNull(m);
        index = m.Conversations.Length;
        return m with { Conversations = m.Conversations.Items.Add(c) };
    }

    /// <summary>Replaces conversation <paramref name="index"/>.</summary>
    public static ScenarioModel SetConversation(this ScenarioModel m, int index, ConversationDef c)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m with { Conversations = m.Conversations.Items.SetItem(index, c) };
    }

    /// <summary>Removes conversation <paramref name="index"/> (Conversation actions that started it become unset).</summary>
    public static ScenarioModel RemoveConversation(this ScenarioModel m, int index) => ScenarioRewriter.RemoveConversation(m, index);

    /// <summary>Appends a phrase to conversation <paramref name="conversation"/>.</summary>
    public static ScenarioModel AddPhrase(this ScenarioModel m, int conversation, PhraseDef p, out int index)
    {
        ArgumentNullException.ThrowIfNull(m);
        ConversationDef c = m.Conversations[conversation];
        index = c.Phrases.Length;
        return m.SetConversation(conversation, c with { Phrases = c.Phrases.Items.Add(p) });
    }

    /// <summary>Replaces a phrase.</summary>
    public static ScenarioModel SetPhrase(this ScenarioModel m, int conversation, int phrase, PhraseDef p)
    {
        ArgumentNullException.ThrowIfNull(m);
        ConversationDef c = m.Conversations[conversation];
        return m.SetConversation(conversation, c with { Phrases = c.Phrases.Items.SetItem(phrase, p) });
    }

    /// <summary>Removes a phrase (it leaves every follow-up list).</summary>
    public static ScenarioModel RemovePhrase(this ScenarioModel m, int conversation, int phrase)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m.SetConversation(conversation, ScenarioRewriter.RemovePhrase(m.Conversations[conversation], phrase));
    }

    /// <summary>Appends a custom sound.</summary>
    public static ScenarioModel AddSound(this ScenarioModel m, CustomSoundDef s, out int index)
    {
        ArgumentNullException.ThrowIfNull(m);
        index = m.Sounds.Length;
        return m with { Sounds = m.Sounds.Items.Add(s) };
    }

    /// <summary>Replaces a custom sound.</summary>
    public static ScenarioModel SetSound(this ScenarioModel m, int index, CustomSoundDef s)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m with { Sounds = m.Sounds.Items.SetItem(index, s) };
    }

    /// <summary>Removes a custom sound.</summary>
    public static ScenarioModel RemoveSound(this ScenarioModel m, int index)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m with { Sounds = m.Sounds.Items.RemoveAt(index) };
    }

    /// <summary>Replaces the music playlist.</summary>
    public static ScenarioModel WithMusic(this ScenarioModel m, IEnumerable<ResourceRef> music)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m with { Music = EquatableArray<ResourceRef>.From(music) };
    }

    /// <summary>A name not used by any event or action yet: <paramref name="stem"/>, <paramref name="stem"/>2, …</summary>
    public static string FreshName(this ScenarioModel m, string stem)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(stem);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EventDef e in m.Events)
        {
            used.Add(e.Name);
        }

        foreach (ActionDef a in m.Actions)
        {
            used.Add(a.Name);
        }

        foreach (RuleDef r in m.Rules)
        {
            used.Add(r.Name);
        }

        foreach (ConversationDef c in m.Conversations)
        {
            used.Add(c.Name);
        }

        if (!used.Contains(stem))
        {
            return stem;
        }

        for (int i = 2; ; i++)
        {
            string name = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!used.Contains(name))
            {
                return name;
            }
        }
    }
}
