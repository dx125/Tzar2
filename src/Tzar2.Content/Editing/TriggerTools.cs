using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Content.Editing;

/// <summary>What an expression name refers to.</summary>
public enum NameKind : byte
{
    /// <summary>An event (bare name).</summary>
    Event,

    /// <summary>A Set Timer action (bare name).</summary>
    Timer,

    /// <summary>A user variable (<c>$name</c> or <c>#name</c>).</summary>
    Variable,
}

/// <summary>One entry of the expression editor's vocabulary.</summary>
/// <param name="Text">What is inserted (<c>$health</c>, <c>@gold(</c>, an event name).</param>
/// <param name="Kind">What it is ("reserved", "function", "variable", "event", "timer").</param>
public readonly record struct Completion(string Text, string Kind);

/// <summary>
/// The expression editor's support (TDD §18.2 "expression editor", Appendix C.6): the vocabulary for completion, a live
/// parse with the error position, and renaming of events, timers and variables together with every expression that uses
/// them (search and replace, §18.3).
/// </summary>
public static class ExpressionHelp
{
    /// <summary>Everything an expression of <paramref name="model"/> may name, sorted by kind then text.</summary>
    public static ImmutableArray<Completion> Vocabulary(ScenarioModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var list = new List<Completion>();
        foreach (ReservedVariable v in Enum.GetValues<ReservedVariable>())
        {
            list.Add(new Completion(ExpressionParser.Spelling(v), "reserved"));
        }

        foreach (CallKind f in Enum.GetValues<CallKind>())
        {
            list.Add(new Completion(ExpressionParser.Spelling(f) + "(", "function"));
        }

        foreach (string v in model.Variables)
        {
            list.Add(new Completion(v.StartsWith('#') ? v : "$" + v, "variable"));
        }

        foreach (EventDef e in model.Events)
        {
            list.Add(new Completion(e.Name, "event"));
        }

        foreach (ActionDef a in model.Actions)
        {
            if (a is SetTimerAction)
            {
                list.Add(new Completion(a.Name, "timer"));
            }
        }

        list.Sort((a, b) => !string.Equals(a.Kind, b.Kind, StringComparison.Ordinal) ? string.CompareOrdinal(a.Kind, b.Kind) : string.CompareOrdinal(a.Text, b.Text));
        return list.ToImmutableArray();
    }

    /// <summary>The vocabulary entries that start with <paramref name="prefix"/> (ignoring case).</summary>
    public static ImmutableArray<Completion> Complete(ScenarioModel model, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var b = ImmutableArray.CreateBuilder<Completion>();
        foreach (Completion c in Vocabulary(model))
        {
            if (c.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                b.Add(c);
            }
        }

        return b.ToImmutable();
    }

    /// <summary>Parses <paramref name="source"/> against the model: null when it is valid, else the error.</summary>
    public static string? Check(ScenarioModel model, string source)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(source);
        return model.Parse(source).Error;
    }

    /// <summary>
    /// Renames an event, a timer (its Set Timer action) or a declared variable, and rewrites every expression that uses the
    /// old name (names are matched as whole identifiers, ignoring case).
    /// </summary>
    /// <returns>The model and the number of expressions changed.</returns>
    public static ScenarioModel Rename(ScenarioModel model, NameKind kind, string oldName, string newName, out int changed)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(oldName);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        ScenarioModel m = kind switch
        {
            NameKind.Event => RenameEvent(model, oldName, newName),
            NameKind.Timer => RenameTimer(model, oldName, newName),
            _ => model with { Variables = RenameVariable(model.Variables, oldName, newName) },
        };
        int count = 0;
        ExpressionDef R(ExpressionDef e)
        {
            string next = Replace(e.Source, kind, oldName.TrimStart('$'), newName.TrimStart('$'));
            if (string.Equals(next, e.Source, StringComparison.Ordinal))
            {
                return e;
            }

            count++;
            return ScenarioModel.Source(next);
        }

        m = MapExpressions(m, R);
        changed = count;
        return m;
    }

    /// <summary>Applies <paramref name="map"/> to every expression of the model.</summary>
    public static ScenarioModel MapExpressions(ScenarioModel m, Func<ExpressionDef, ExpressionDef> map)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(map);
        var events = new List<EventDef>();
        foreach (EventDef e in m.Events)
        {
            events.Add(e is ObjectExpressionEvent x ? x with { Expression = map(x.Expression) } : e);
        }

        var actions = new List<ActionDef>();
        foreach (ActionDef a in m.Actions)
        {
            actions.Add(a switch
            {
                ExpressionAction x => x with { Expression = map(x.Expression) },
                SetObjectPropertiesAction x => x with { Expression = map(x.Expression) },
                SetTimerAction x => x with { Initial = map(x.Initial) },
                _ => a,
            });
        }

        var rules = new List<RuleDef>();
        foreach (RuleDef r in m.Rules)
        {
            rules.Add(r with { Condition = map(r.Condition) });
        }

        var conversations = new List<ConversationDef>();
        foreach (ConversationDef c in m.Conversations)
        {
            var phrases = new List<PhraseDef>();
            foreach (PhraseDef p in c.Phrases)
            {
                phrases.Add(p with { Condition = map(p.Condition), Result = map(p.Result) });
            }

            conversations.Add(c with { Phrases = EquatableArray<PhraseDef>.From(phrases) });
        }

        return m with
        {
            Events = EquatableArray<EventDef>.From(events),
            Actions = EquatableArray<ActionDef>.From(actions),
            Rules = EquatableArray<RuleDef>.From(rules),
            Conversations = EquatableArray<ConversationDef>.From(conversations),
        };
    }

    /// <summary>Replaces whole-identifier uses of a name in expression text.</summary>
    public static string Replace(string source, NameKind kind, string oldName, string newName)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            bool sigil = c is '$' or '#' or '@';
            int start = sigil ? i + 1 : i;
            if ((sigil && start < source.Length && IsStart(source[start])) || (!sigil && IsStart(c)))
            {
                int end = start;
                while (end < source.Length && IsPart(source[end]))
                {
                    end++;
                }

                string word = source[start..end];
                bool match = string.Equals(word, oldName.TrimStart('#'), StringComparison.OrdinalIgnoreCase)
                    && kind switch
                    {
                        NameKind.Variable => c is '$' or '#',
                        _ => !sigil,
                    };
                sb.Append(source, i, start - i);
                sb.Append(match ? newName.TrimStart('#') : word);
                i = end;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsStart(char c) => char.IsAsciiLetter(c) || c == '_' || c > 127;

    private static bool IsPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c > 127;

    private static ScenarioModel RenameEvent(ScenarioModel m, string oldName, string newName)
    {
        var events = new List<EventDef>();
        foreach (EventDef e in m.Events)
        {
            events.Add(string.Equals(e.Name, oldName, StringComparison.OrdinalIgnoreCase) ? e with { Name = newName } : e);
        }

        return m with { Events = EquatableArray<EventDef>.From(events) };
    }

    private static ScenarioModel RenameTimer(ScenarioModel m, string oldName, string newName)
    {
        var actions = new List<ActionDef>();
        foreach (ActionDef a in m.Actions)
        {
            actions.Add(a is SetTimerAction && string.Equals(a.Name, oldName, StringComparison.OrdinalIgnoreCase) ? a with { Name = newName } : a);
        }

        return m with { Actions = EquatableArray<ActionDef>.From(actions) };
    }

    private static EquatableArray<string> RenameVariable(EquatableArray<string> variables, string oldName, string newName)
    {
        var list = new List<string>();
        foreach (string v in variables)
        {
            list.Add(string.Equals(v, oldName.TrimStart('$'), StringComparison.OrdinalIgnoreCase) ? newName.TrimStart('$') : v);
        }

        return EquatableArray<string>.From(list);
    }
}

/// <summary>Named areas and object groups reusable across triggers (TDD §18.3).</summary>
public static class ExtrasEdits
{
    /// <summary>
    /// Adds named area <paramref name="name"/> or changes its rectangle; when it changes, every trigger area equal to the
    /// old rectangle follows (the area is reused by value).
    /// </summary>
    /// <returns>The state and the number of trigger areas updated.</returns>
    public static DocumentState SetArea(this DocumentState state, string name, RectArea area, out int updated)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(area);
        updated = 0;
        var areas = new List<NamedArea>();
        RectArea? old = null;
        foreach (NamedArea a in state.Extras.Areas)
        {
            if (string.Equals(a.Name, name, StringComparison.Ordinal))
            {
                old = a.Area;
                areas.Add(a with { Area = area });
            }
            else
            {
                areas.Add(a);
            }
        }

        if (old is null)
        {
            areas.Add(new NamedArea(name, area));
        }

        ScenarioModel scenario = state.Scenario;
        if (old is not null && old != area)
        {
            scenario = ReplaceAreas(scenario, old, area, out updated);
        }

        return state with { Scenario = scenario, Extras = state.Extras with { Areas = EquatableArray<NamedArea>.From(areas) } };
    }

    /// <summary>Removes a named area (trigger areas keep their rectangles).</summary>
    public static DocumentState RemoveArea(this DocumentState state, string name)
    {
        ArgumentNullException.ThrowIfNull(state);
        var areas = new List<NamedArea>();
        foreach (NamedArea a in state.Extras.Areas)
        {
            if (!string.Equals(a.Name, name, StringComparison.Ordinal))
            {
                areas.Add(a);
            }
        }

        return state with { Extras = state.Extras with { Areas = EquatableArray<NamedArea>.From(areas) } };
    }

    /// <summary>
    /// Adds object group <paramref name="name"/> or changes its members; when it changes, every named object set of the
    /// triggers with exactly the old members follows.
    /// </summary>
    /// <returns>The state and the number of object sets updated.</returns>
    public static DocumentState SetGroup(this DocumentState state, string name, IReadOnlyList<int> ids, out int updated)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(ids);
        updated = 0;
        EquatableArray<int> members = EquatableArray<int>.From(ids);
        var groups = new List<ObjectGroup>();
        EquatableArray<int>? old = null;
        foreach (ObjectGroup g in state.Extras.Groups)
        {
            if (string.Equals(g.Name, name, StringComparison.Ordinal))
            {
                old = g.ObjectIds;
                groups.Add(g with { ObjectIds = members });
            }
            else
            {
                groups.Add(g);
            }
        }

        if (old is null)
        {
            groups.Add(new ObjectGroup(name, members));
        }

        ScenarioModel scenario = state.Scenario;
        if (old is { } before && before != members)
        {
            int count = 0;
            scenario = MapSpecs(scenario, spec =>
            {
                if (spec is NamedObjects n && SameSet(n.ObjectIndices, before))
                {
                    count++;
                    return n with { ObjectIndices = members };
                }

                return spec;
            });
            updated = count;
        }

        return state with { Scenario = scenario, Extras = state.Extras with { Groups = EquatableArray<ObjectGroup>.From(groups) } };
    }

    /// <summary>Removes an object group (trigger sets keep their members).</summary>
    public static DocumentState RemoveGroup(this DocumentState state, string name)
    {
        ArgumentNullException.ThrowIfNull(state);
        var groups = new List<ObjectGroup>();
        foreach (ObjectGroup g in state.Extras.Groups)
        {
            if (!string.Equals(g.Name, name, StringComparison.Ordinal))
            {
                groups.Add(g);
            }
        }

        return state with { Extras = state.Extras with { Groups = EquatableArray<ObjectGroup>.From(groups) } };
    }

    /// <summary>Replaces every trigger area equal to <paramref name="from"/> with <paramref name="to"/>.</summary>
    public static ScenarioModel ReplaceAreas(ScenarioModel m, AreaSpec from, AreaSpec to, out int replaced)
    {
        ArgumentNullException.ThrowIfNull(m);
        int count = 0;
        AreaSpec A(AreaSpec a)
        {
            if (a == from)
            {
                count++;
                return to;
            }

            return a;
        }

        var events = new List<EventDef>();
        foreach (EventDef e in m.Events)
        {
            events.Add(e is ObjectInAreaEvent x ? x with { Area = A(x.Area) } : e);
        }

        var actions = new List<ActionDef>();
        foreach (ActionDef a in m.Actions)
        {
            actions.Add(a switch
            {
                ExploreAreaAction x => x with { Area = A(x.Area) },
                LockViewAction x => x with { Area = A(x.Area) },
                ObjectOrderAction x => x with { Area = A(x.Area) },
                ScrollViewAction x => x with { From = A(x.From), To = A(x.To) },
                SpawnObjectAction x => x with { Area = A(x.Area) },
                _ => a,
            });
        }

        replaced = count;
        return m with { Events = EquatableArray<EventDef>.From(events), Actions = EquatableArray<ActionDef>.From(actions) };
    }

    private static ScenarioModel MapSpecs(ScenarioModel m, Func<ObjectSpec, ObjectSpec> map)
    {
        ObjectSpec S(ObjectSpec s) => map(s);
        var events = new List<EventDef>();
        foreach (EventDef e in m.Events)
        {
            events.Add(e switch
            {
                ObjectInAreaEvent x => x with { Objects = S(x.Objects) },
                ObjectDestroyedEvent x => x with { Objects = S(x.Objects) },
                ObjectsEnterHolderEvent x => x with { Objects = S(x.Objects) },
                ObjectLeaveHolderEvent x => x with { Objects = S(x.Objects) },
                ObjectExistEvent x => x with { Objects = S(x.Objects) },
                ObjectExpressionEvent x => x with { Objects = S(x.Objects) },
                _ => e,
            });
        }

        var actions = new List<ActionDef>();
        foreach (ActionDef a in m.Actions)
        {
            actions.Add(a switch
            {
                AppearObjectAction x => x with { Objects = S(x.Objects) },
                ChangeObjectOwnerAction x => x with { Objects = S(x.Objects) },
                DestroyObjectAction x => x with { Objects = S(x.Objects) },
                EraseObjectAction x => x with { Objects = S(x.Objects) },
                LockUnitsAction x => x with { Objects = S(x.Objects) },
                ObjectOrderAction x => x with { Objects = S(x.Objects) },
                SetObjectPropertiesAction x => x with { Objects = S(x.Objects) },
                _ => a,
            });
        }

        return m with { Events = EquatableArray<EventDef>.From(events), Actions = EquatableArray<ActionDef>.From(actions) };
    }

    private static bool SameSet(EquatableArray<int> a, EquatableArray<int> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var sa = new SortedSet<int>(a.Items);
        var sb = new SortedSet<int>(b.Items);
        return sa.SetEquals(sb);
    }
}

/// <summary>A translatable text of a map.</summary>
/// <param name="Context">Stable context (gettext <c>msgctxt</c>), e.g. <c>action/Briefing/text</c>.</param>
/// <param name="Text">The text.</param>
public readonly record struct MapText(string Context, string Text);

/// <summary>
/// Localization tables for map strings (TDD §18.3): every player-visible text of a map, exported as a gettext catalogue
/// (<c>.po</c>) and imported back from a translated one.
/// </summary>
public static class MapStrings
{
    /// <summary>The map's texts with stable contexts, in a fixed order.</summary>
    public static ImmutableArray<MapText> Collect(DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var b = ImmutableArray.CreateBuilder<MapText>();
        void Add(string context, string? text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                b.Add(new MapText(context, text));
            }
        }

        MapProperties p = state.Scenario.Properties;
        Add("map/title", p.Title);
        Add("map/description", p.Description);
        foreach (KingdomDef k in state.Scenario.Kingdoms)
        {
            Add(Ctx("kingdom", k.Number, "name"), k.Name);
            Add(Ctx("kingdom", k.Number, "tzar"), k.TzarName);
        }

        foreach (EditorObject o in state.Objects)
        {
            Add(Ctx("object", o.Id, "title"), o.Info.Title);
            Add(Ctx("object", o.Id, "scroll"), o.Info.Item?.Text);
        }

        foreach (ActionDef a in state.Scenario.Actions)
        {
            switch (a)
            {
                case AddQuestAction x:
                    Add($"action/{a.Name}/text", x.Text);
                    break;
                case ShowMessageAction x:
                    Add($"action/{a.Name}/title", x.Title);
                    Add($"action/{a.Name}/text", x.Text);
                    break;
                case ShowStoryScreenAction x:
                    Add($"action/{a.Name}/text", x.Text);
                    break;
                case SendChatAction x:
                    Add($"action/{a.Name}/text", x.Text);
                    break;
            }
        }

        foreach (ConversationDef c in state.Scenario.Conversations)
        {
            foreach (PhraseDef ph in c.Phrases)
            {
                Add($"conversation/{c.Name}/{ph.Name}", ph.Text);
            }
        }

        return b.ToImmutable();
    }

    /// <summary>The texts as a gettext catalogue for language <paramref name="language"/> (msgstr empty).</summary>
    public static string ExportPo(DocumentState state, string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var sb = new StringBuilder();
        sb.Append("msgid \"\"\nmsgstr \"\"\n\"Content-Type: text/plain; charset=UTF-8\\n\"\n\"Language: ").Append(language).Append("\\n\"\n");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapText t in Collect(state))
        {
            if (seen.Add(t.Context))
            {
                sb.Append("\nmsgctxt ").Append(Quote(t.Context)).Append("\nmsgid ").Append(Quote(t.Text)).Append("\nmsgstr \"\"\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>Parses a gettext catalogue: context → translation (entries with an empty msgstr are left out).</summary>
    /// <exception cref="FormatException">A string is malformed.</exception>
    public static SortedDictionary<string, string> ParsePo(string po)
    {
        ArgumentNullException.ThrowIfNull(po);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? ctx = null, field = null;
        var values = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        void Flush()
        {
            if (ctx is not null && values.TryGetValue("msgstr", out StringBuilder? s) && s.Length > 0)
            {
                result[ctx] = s.ToString();
            }

            ctx = null;
            values.Clear();
        }

        foreach (string raw in po.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("msgctxt ", StringComparison.Ordinal))
            {
                Flush();
                field = "msgctxt";
                ctx = Unquote(line["msgctxt ".Length..]);
                continue;
            }

            foreach (string key in (ReadOnlySpan<string>)["msgid", "msgstr"])
            {
                if (line.StartsWith(key + " ", StringComparison.Ordinal))
                {
                    field = key;
                    values[key] = new StringBuilder(Unquote(line[(key.Length + 1)..]));
                    goto next;
                }
            }

            if (line.StartsWith('"') && field is not null)
            {
                if (field == "msgctxt")
                {
                    ctx += Unquote(line);
                }
                else
                {
                    values[field].Append(Unquote(line));
                }
            }

        next:;
        }

        Flush();
        return result;
    }

    /// <summary>Applies the translations of a catalogue to the map's texts.</summary>
    /// <returns>The state and the number of texts replaced.</returns>
    public static DocumentState ImportPo(DocumentState state, string po, out int applied)
    {
        ArgumentNullException.ThrowIfNull(state);
        SortedDictionary<string, string> t = ParsePo(po);
        int count = 0;
        string T(string context, string text)
        {
            if (t.TryGetValue(context, out string? translated) && !string.Equals(translated, text, StringComparison.Ordinal))
            {
                count++;
                return translated;
            }

            return text;
        }

        ScenarioModel m = state.Scenario;
        m = m with { Properties = m.Properties with { Title = T("map/title", m.Properties.Title), Description = T("map/description", m.Properties.Description) } };
        var kingdoms = new List<KingdomDef>();
        foreach (KingdomDef k in m.Kingdoms)
        {
            kingdoms.Add(k with { Name = T(Ctx("kingdom", k.Number, "name"), k.Name), TzarName = T(Ctx("kingdom", k.Number, "tzar"), k.TzarName) });
        }

        var actions = new List<ActionDef>();
        foreach (ActionDef a in m.Actions)
        {
            string at = $"action/{a.Name}/";
            actions.Add(a switch
            {
                AddQuestAction x => x with { Text = T(at + "text", x.Text) },
                ShowMessageAction x => x with { Title = T(at + "title", x.Title), Text = T(at + "text", x.Text) },
                ShowStoryScreenAction x => x with { Text = T(at + "text", x.Text) },
                SendChatAction x => x with { Text = T(at + "text", x.Text) },
                _ => a,
            });
        }

        var conversations = new List<ConversationDef>();
        foreach (ConversationDef c in m.Conversations)
        {
            var phrases = new List<PhraseDef>();
            foreach (PhraseDef ph in c.Phrases)
            {
                phrases.Add(ph with { Text = T($"conversation/{c.Name}/{ph.Name}", ph.Text) });
            }

            conversations.Add(c with { Phrases = EquatableArray<PhraseDef>.From(phrases) });
        }

        ImmutableList<EditorObject>.Builder objects = state.Objects.ToBuilder();
        for (int i = 0; i < objects.Count; i++)
        {
            EditorObject o = objects[i];
            ObjectInfo info = o.Info with { Title = T(Ctx("object", o.Id, "title"), o.Info.Title) };
            if (info.Item is { Text: { } scroll } item)
            {
                info = info with { Item = item with { Text = T(Ctx("object", o.Id, "scroll"), scroll) } };
            }

            objects[i] = o with { Info = info };
        }

        applied = count;
        return state with
        {
            Objects = objects.ToImmutable(),
            Scenario = m with
            {
                Kingdoms = EquatableArray<KingdomDef>.From(kingdoms),
                Actions = EquatableArray<ActionDef>.From(actions),
                Conversations = EquatableArray<ConversationDef>.From(conversations),
            },
        };
    }

    private static string Ctx(string kind, int n, string what) => string.Create(CultureInfo.InvariantCulture, $"{kind}/{n}/{what}");

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\t' => "\\t", '\r' => string.Empty, _ => c.ToString() });
        }

        return sb.Append('"').ToString();
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"')
        {
            throw new FormatException($"Expected a quoted string: {s}");
        }

        var sb = new StringBuilder();
        for (int i = 1; i < s.Length - 1; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length - 1)
            {
                char e = s[++i];
                sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
