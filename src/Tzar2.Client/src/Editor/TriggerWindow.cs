using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Content.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// The rule editor (TDD §18.2 "Rules", "Conversations"; §18.3 areas and groups, Lua, localization): rules with their
/// condition and ordered action list, the event and action lists with generated property pages for every kind, user
/// variables with rename-everywhere, conversations with phrases and follow-ups, quests, named areas and object groups,
/// Lua scripts, and .po export/import.
/// </summary>
public partial class TriggerWindow : Window
{
    private readonly EditorScreen _screen;
    private readonly TabContainer _tabs = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly Dictionary<string, int> _selected = new(StringComparer.Ordinal);
    private bool _dirty = true;

    /// <summary>Creates the window over the editor screen's document.</summary>
    public TriggerWindow(EditorScreen screen)
    {
        _screen = screen;
        Title = "Triggers, conversations and scripts";
        Size = new Vector2I(1100, 760);
        Transient = true;
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.AddChild(_tabs);
        AddChild(margin);
        CloseRequested += QueueFree;
        _tabs.TabChanged += _ => _dirty = true;
    }

    private MapDocument Doc => _screen.Document;

    private ScenarioModel Model => Doc.State.Scenario;

    /// <summary>Shows the page of a validation finding (rule, event, action, conversation, phrase).</summary>
    public void Locate(EditorLocation at)
    {
        (string tab, int index) = at.Kind switch
        {
            LocationKind.Rule => ("Rules", at.Index),
            LocationKind.Event => ("Events", at.Index),
            LocationKind.Action => ("Actions", at.Index),
            LocationKind.Variable => ("Variables", at.Index),
            LocationKind.Conversation or LocationKind.Phrase => ("Conversations", at.Index),
            _ => (string.Empty, -1),
        };
        if (tab.Length == 0)
        {
            return;
        }

        _selected[tab] = index;
        if (at.Kind == LocationKind.Phrase)
        {
            _selected["Phrase"] = at.SubIndex;
        }

        _tabs.CurrentTab = Array.IndexOf(Tabs, tab);
        _dirty = true;
    }

    private static readonly string[] Tabs = ["Rules", "Events", "Actions", "Variables", "Conversations", "Quests", "Areas & groups", "Lua", "Localization"];

    /// <inheritdoc/>
    public override void _Ready()
    {
        foreach (string t in Tabs)
        {
            _tabs.AddChild(new HSplitContainer { Name = t });
        }

        Doc.Changed += OnChanged;
        Forms.ShowCentered(this);
    }

    /// <inheritdoc/>
    public override void _ExitTree() => Doc.Changed -= OnChanged;

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        var page = (Control)_tabs.GetCurrentTabControl();
        Forms.Clear(page);
        switch (page.Name.ToString())
        {
            case "Rules":
                RulesPage(page);
                break;
            case "Events":
                TablePage(page, "Events", Model.Events.Items.Select(e => $"{e.Name}  [{TriggerSchema.Of(e).Title}]").ToArray(), TriggerSchema.Events, Event, AddEvent, RemoveEvent);
                break;
            case "Actions":
                TablePage(page, "Actions", Model.Actions.Items.Select(a => $"{a.Name}  [{TriggerSchema.Of(a).Title}]").ToArray(), TriggerSchema.Actions, Action, AddAction, RemoveAction);
                break;
            case "Variables":
                VariablesPage(page);
                break;
            case "Conversations":
                ConversationsPage(page);
                break;
            case "Quests":
                QuestsPage(page);
                break;
            case "Areas & groups":
                AreasPage(page);
                break;
            case "Lua":
                LuaPage(page);
                break;
            default:
                LocalizationPage(page);
                break;
        }
    }

    private void OnChanged(DocumentChange change)
    {
        // Rebuild after undo/redo and after edits made elsewhere; edits of this window rebuild too (cheap).
        if (change.StateChanged && GetViewport()?.GuiGetFocusOwner() is not (LineEdit or TextEdit or SpinBox))
        {
            _dirty = true;
        }
    }

    private void Apply(string label, Func<ScenarioModel, ScenarioModel> change)
    {
        Doc.Apply(label, s => s.WithScenario(change));
        _dirty = true;
    }

    private int Selected(string tab, int count)
    {
        int i = _selected.TryGetValue(tab, out int v) ? v : 0;
        return count == 0 ? -1 : Math.Clamp(i, 0, count - 1);
    }

    private ItemList List(Control parent, string tab, string[] items, Action<int>? activated = null)
    {
        var list = new ItemList { CustomMinimumSize = new Vector2(300, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        foreach (string item in items)
        {
            list.AddItem(item);
        }

        int sel = Selected(tab, items.Length);
        if (sel >= 0)
        {
            list.Select(sel);
        }

        list.ItemSelected += i =>
        {
            _selected[tab] = (int)i;
            _dirty = true;
            activated?.Invoke((int)i);
        };
        parent.AddChild(list);
        return list;
    }

    // ----- rules ---------------------------------------------------------------------------------------------------

    private void RulesPage(Control page)
    {
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        page.AddChild(left);
        List(left, "Rules", Model.Rules.Items.Select((r, i) => $"{i + 1}. {r.Name}").ToArray());
        var buttons = new HBoxContainer();
        Forms.Button(buttons, "Add rule", () =>
        {
            Apply("Add rule", m => m.AddRule(new RuleDef(m.FreshName("rule"), ExpressionDef.Empty, EquatableArray<int>.Empty), out int i));
            _selected["Rules"] = Model.Rules.Length - 1;
        });
        int sel = Selected("Rules", Model.Rules.Length);
        Forms.Button(buttons, "Delete", () => { if (sel >= 0) { Apply("Delete rule", m => m.RemoveRule(sel)); } });
        Forms.Button(buttons, "Up", () => { if (sel > 0) { Apply("Move rule", m => m.MoveRule(sel, sel - 1)); _selected["Rules"] = sel - 1; } });
        Forms.Button(buttons, "Down", () => { if (sel >= 0 && sel < Model.Rules.Length - 1) { Apply("Move rule", m => m.MoveRule(sel, sel + 1)); _selected["Rules"] = sel + 1; } });
        left.AddChild(buttons);
        Forms.Label(left, "Rules are checked about every second, in this order. A rule runs its actions once when its condition is true, unless one of them is Keep Rule.").AutowrapMode = TextServer.AutowrapMode.WordSmart;

        VBoxContainer right = Forms.Scroll(page);
        if (sel < 0)
        {
            Forms.Label(right, "No rules yet.");
            return;
        }

        RuleDef rule = Model.Rules[sel];
        GridContainer g = Forms.Grid(right);
        Forms.Text(g, "Name", rule.Name, v => Apply("Rename rule", m => m.SetRule(sel, m.Rules[sel] with { Name = v })));
        new SchemaForm(_screen, g).Expression(g, "Condition", rule.Condition.Source, v => Apply("Rule condition", m => m.SetRule(sel, m.Rules[sel] with { Condition = ScenarioModel.Source(v) })));
        Forms.Heading(right, "Actions, in order");
        var actions = new ItemList { CustomMinimumSize = new Vector2(0, 200) };
        foreach (int a in rule.ActionIndices)
        {
            actions.AddItem((uint)a < (uint)Model.Actions.Length ? $"{Model.Actions[a].Name}  [{TriggerSchema.Of(Model.Actions[a]).Title}]" : "#" + a.ToString(CultureInfo.InvariantCulture));
        }

        right.AddChild(actions);
        var row = new HBoxContainer();
        int Picked() => actions.GetSelectedItems() is { Length: > 0 } s ? s[0] : -1;
        Forms.Button(row, "Up", () => { int k = Picked(); if (k > 0) { Apply("Order actions", m => m.MoveRuleAction(sel, k, k - 1)); } });
        Forms.Button(row, "Down", () => { int k = Picked(); if (k >= 0 && k < rule.ActionIndices.Length - 1) { Apply("Order actions", m => m.MoveRuleAction(sel, k, k + 1)); } });
        Forms.Button(row, "Remove from rule", () => { int k = Picked(); if (k >= 0) { Apply("Remove action from rule", m => m.SetRule(sel, m.Rules[sel] with { ActionIndices = m.Rules[sel].ActionIndices.Items.RemoveAt(k) })); } });
        Forms.Button(row, "Edit action", () =>
        {
            int k = Picked();
            if (k >= 0)
            {
                _selected["Actions"] = rule.ActionIndices[k];
                _tabs.CurrentTab = 2;
            }
        });
        right.AddChild(row);
        var add = new OptionButton();
        add.AddItem("Add an existing action…");
        foreach (ActionDef a in Model.Actions)
        {
            add.AddItem(a.Name);
        }

        add.ItemSelected += i =>
        {
            if (i > 0)
            {
                int a = (int)i - 1;
                Apply("Add action to rule", m => m.SetRule(sel, m.Rules[sel] with { ActionIndices = m.Rules[sel].ActionIndices.Items.Add(a) }));
            }
        };
        right.AddChild(add);
        var create = new OptionButton();
        create.AddItem("Add a new action…");
        foreach (TriggerKind k in TriggerSchema.Actions.Where(k => k.RecordType != typeof(UnknownAction)))
        {
            create.AddItem(k.Title);
        }

        create.ItemSelected += i =>
        {
            if (i > 0)
            {
                TriggerKind kind = TriggerSchema.Actions.Where(k => k.RecordType != typeof(UnknownAction)).ElementAt((int)i - 1);
                Apply("New action in rule", m =>
                {
                    m = m.AddAction((ActionDef)kind.Create(m.FreshName(kind.Id)), out int index);
                    return m.SetRule(sel, m.Rules[sel] with { ActionIndices = m.Rules[sel].ActionIndices.Items.Add(index) });
                });
            }
        };
        right.AddChild(create);
    }

    // ----- events and actions ---------------------------------------------------------------------------------------

    private void TablePage(Control page, string tab, string[] items, System.Collections.Immutable.ImmutableArray<TriggerKind> kinds, Action<VBoxContainer, int> edit, Action<TriggerKind> add, Action<int> remove)
    {
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        page.AddChild(left);
        List(left, tab, items);
        var create = new OptionButton();
        create.AddItem("New…");
        TriggerKind[] creatable = kinds.Where(k => k.Id != "unknown").ToArray();
        foreach (TriggerKind k in creatable)
        {
            create.AddItem(k.Title);
        }

        create.ItemSelected += i =>
        {
            if (i > 0)
            {
                add(creatable[(int)i - 1]);
                _selected[tab] = items.Length;
            }
        };
        left.AddChild(create);
        int sel = Selected(tab, items.Length);
        Forms.Button(left, "Delete", () => { if (sel >= 0) { remove(sel); } });
        VBoxContainer right = Forms.Scroll(page);
        if (sel >= 0)
        {
            edit(right, sel);
        }
    }

    private void AddEvent(TriggerKind k) => Apply("New event", m => m.AddEvent((EventDef)k.Create(m.FreshName(k.Id)), out _));

    private void RemoveEvent(int i) => Apply("Delete event", m => m.RemoveEvent(i));

    private void AddAction(TriggerKind k) => Apply("New action", m => m.AddAction((ActionDef)k.Create(m.FreshName(k.Id)), out _));

    private void RemoveAction(int i) => Apply("Delete action", m => m.RemoveAction(i));

    private void Event(VBoxContainer right, int i)
    {
        EventDef e = Model.Events[i];
        TriggerKind kind = TriggerSchema.Of(e);
        Forms.Heading(right, kind.Title);
        GridContainer g = Forms.Grid(right);
        Forms.Text(g, "Name", e.Name, v =>
        {
            int changed = 0;
            Apply("Rename event", m => ExpressionHelp.Rename(m, NameKind.Event, e.Name, v, out changed));
            _screen.Status($"Renamed; {changed} expression(s) updated.");
        });
        Forms.Label(right, "Conditions refer to the event by its name; it is true while its condition holds.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        new SchemaForm(_screen, right).Build(kind, e, r => Apply("Edit event", m => m.SetEvent(i, (EventDef)r)));
    }

    private void Action(VBoxContainer right, int i)
    {
        ActionDef a = Model.Actions[i];
        TriggerKind kind = TriggerSchema.Of(a);
        Forms.Heading(right, kind.Title);
        GridContainer g = Forms.Grid(right);
        Forms.Text(g, "Name", a.Name, v =>
        {
            if (a is SetTimerAction)
            {
                Apply("Rename timer", m => ExpressionHelp.Rename(m, NameKind.Timer, a.Name, v, out _));
            }
            else
            {
                Apply("Rename action", m => m.SetAction(i, m.Actions[i] with { Name = v }));
            }
        });
        var retype = new OptionButton();
        TriggerKind[] kinds = TriggerSchema.Actions.Where(k => k.Id != "unknown").ToArray();
        foreach (TriggerKind k in kinds)
        {
            retype.AddItem(k.Title);
        }

        retype.Selected = Array.IndexOf(kinds, kind);
        retype.ItemSelected += k => Apply("Change action kind", m => m.SetAction(i, (ActionDef)kinds[(int)k].Create(a.Name)));
        Forms.Label(g, "Kind");
        g.AddChild(retype);
        IEnumerable<string> usedBy = Model.Rules.Items.Where(r => r.ActionIndices.Items.Contains(i)).Select(r => r.Name);
        Forms.Label(right, "Used by rules: " + (usedBy.Any() ? string.Join(", ", usedBy) : "none"));
        new SchemaForm(_screen, right).Build(kind, a, r => Apply("Edit action", m => m.SetAction(i, (ActionDef)r)));
    }

    // ----- variables ------------------------------------------------------------------------------------------------

    private void VariablesPage(Control page)
    {
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        page.AddChild(left);
        List(left, "Variables", Model.Variables.Items.Select(v => v.StartsWith('#') ? v : "$" + v).ToArray());
        string name = "MyVar";
        GridContainer g = Forms.Grid(left);
        Forms.Text(g, "New variable", name, v => name = v);
        Forms.Button(left, "Declare", () => { if (name.Trim().TrimStart('$').Length > 0) { Apply("Declare variable", m => m.AddVariable(name.Trim().TrimStart('$'))); } });
        int sel = Selected("Variables", Model.Variables.Length);
        VBoxContainer right = Forms.Scroll(page);
        Forms.Label(right, "Variables are integers, global to the map; $name variables are created by use, #name ones are carried to the next campaign map (ADR 0133). Reserved names ($health, $gold, …) describe the current object or player.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        if (sel < 0)
        {
            return;
        }

        string current = Model.Variables[sel];
        GridContainer r = Forms.Grid(right);
        Forms.Text(r, "Rename (everywhere)", current, v =>
        {
            int changed = 0;
            Apply("Rename variable", m => ExpressionHelp.Rename(m, NameKind.Variable, current, v, out changed));
            _screen.Status($"Renamed; {changed} expression(s) updated.");
        });
        Forms.Button(right, "Delete declaration", () => Apply("Delete variable", m => m.RemoveVariable(sel)));
    }

    // ----- conversations --------------------------------------------------------------------------------------------

    private void ConversationsPage(Control page)
    {
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        page.AddChild(left);
        Forms.Heading(left, "Conversations");
        List(left, "Conversations", Model.Conversations.Items.Select(c => c.Name).ToArray());
        int sel = Selected("Conversations", Model.Conversations.Length);
        var row = new HBoxContainer();
        Forms.Button(row, "Add", () => Apply("Add conversation", m => m.AddConversation(new ConversationDef(m.FreshName("talk"), EquatableArray<PhraseDef>.Empty), out _)));
        Forms.Button(row, "Delete", () => { if (sel >= 0) { Apply("Delete conversation", m => m.RemoveConversation(sel)); } });
        left.AddChild(row);
        if (sel < 0)
        {
            return;
        }

        ConversationDef c = Model.Conversations[sel];
        GridContainer g = Forms.Grid(left);
        Forms.Text(g, "Name", c.Name, v => Apply("Rename conversation", m => m.SetConversation(sel, m.Conversations[sel] with { Name = v })));
        Forms.Heading(left, "Phrases");
        List(left, "Phrase", c.Phrases.Items.Select(p => (p.Initial ? "▶ " : "   ") + p.Name).ToArray());
        int phrase = Selected("Phrase", c.Phrases.Length);
        var prow = new HBoxContainer();
        Forms.Button(prow, "Add phrase", () => Apply("Add phrase", m => m.AddPhrase(sel, new PhraseDef("phrase" + (c.Phrases.Length + 1).ToString(CultureInfo.InvariantCulture), c.Phrases.Length == 0, null, ExpressionDef.Empty, ExpressionDef.Empty, string.Empty, null, null, EquatableArray<int>.Empty), out _)));
        Forms.Button(prow, "Delete phrase", () => { if (phrase >= 0) { Apply("Delete phrase", m => m.RemovePhrase(sel, phrase)); } });
        left.AddChild(prow);

        VBoxContainer right = Forms.Scroll(page);
        if (phrase < 0)
        {
            Forms.Label(right, "A conversation is a graph of phrases: initial phrases are offered when it starts; a phrase's follow-ups are offered after it. The player chooses when several are offered.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
            return;
        }

        PhraseDef p = c.Phrases[phrase];
        void Set(string label, Func<PhraseDef, PhraseDef> change) => Apply(label, m => m.SetPhrase(sel, phrase, change(m.Conversations[sel].Phrases[phrase])));
        GridContainer f = Forms.Grid(right);
        Forms.Text(f, "Name", p.Name, v => Set("Rename phrase", x => x with { Name = v }));
        Forms.Check(f, "Initial phrase", p.Initial, v => Set("Initial phrase", x => x with { Initial = v }));
        Forms.Label(f, "Speaker");
        var speaker = new HBoxContainer();
        Forms.Label(speaker, p.SourceObject is int id ? Doc.State.Find(id)?.Label ?? "#" + id.ToString(CultureInfo.InvariantCulture) : "(none)");
        Forms.Button(speaker, "Use selected", () => { if (!_screen.Controller.Selection.IsEmpty) { int s0 = _screen.Controller.Selection[0]; Set("Speaker", x => x with { SourceObject = s0 }); } });
        Forms.Button(speaker, "None", () => Set("Speaker", x => x with { SourceObject = null }));
        f.AddChild(speaker);
        var form = new SchemaForm(_screen, f);
        form.Expression(f, "Condition (offered when true)", p.Condition.Source, v => Set("Phrase condition", x => x with { Condition = ScenarioModel.Source(v) }));
        form.Expression(f, "Result (run when said)", p.Result.Source, v => Set("Phrase result", x => x with { Result = ScenarioModel.Source(v) }));
        Forms.Multiline(f, $"Text ({p.Text.Length}/{MapValidator.MaxPhraseText})", p.Text, v => Set("Phrase text", x => x with { Text = v }), 120);
        string[] images = ["(none)", .. Doc.State.Of(AssetKind.Image)];
        Forms.Choice(f, "Icon", images, p.Icon is null ? 0 : Math.Max(0, Array.IndexOf(images, p.Icon.Path)), i => Set("Phrase icon", x => x with { Icon = i == 0 ? null : new ResourceRef(images[i]) }));
        string[] sounds = ["(none)", .. Doc.State.Of(AssetKind.Sound)];
        Forms.Choice(f, "Sound", sounds, p.Sound is null ? 0 : Math.Max(0, Array.IndexOf(sounds, p.Sound.Path)), i => Set("Phrase sound", x => x with { Sound = i == 0 ? null : new ResourceRef(sounds[i]) }));
        Forms.Heading(right, $"Follow-ups ({p.FollowUps.Length}/{MapValidator.MaxPhraseLinks})");
        for (int k = 0; k < c.Phrases.Length; k++)
        {
            int target = k;
            Forms.Check(right, c.Phrases[k].Name, p.FollowUps.Items.Contains(k), on => Set("Follow-ups", x => x with
            {
                FollowUps = on ? x.FollowUps.Items.Add(target) : x.FollowUps.Items.Remove(target),
            }), inGrid: false);
        }
    }

    // ----- quests ---------------------------------------------------------------------------------------------------

    private void QuestsPage(Control page)
    {
        VBoxContainer box = Forms.Scroll(page);
        Forms.Label(box, "Quests are Add Quest actions (the quest is named after the action); Remove Quest actions take them off the list (F8).").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        for (int i = 0; i < Model.Actions.Length; i++)
        {
            if (Model.Actions[i] is AddQuestAction q)
            {
                int index = i;
                Forms.Heading(box, q.Name);
                Forms.Label(box, q.Text);
                Forms.Button(box, "Edit", () =>
                {
                    _selected["Actions"] = index;
                    _tabs.CurrentTab = 2;
                });
            }
        }

        Forms.Button(box, "Add quest", () => Apply("Add quest", m => m.AddAction(new AddQuestAction(m.FreshName("Quest"), "Describe the goal.", null), out _)));
    }

    // ----- areas and groups -----------------------------------------------------------------------------------------

    private void AreasPage(Control page)
    {
        VBoxContainer box = Forms.Scroll(page);
        Forms.Heading(box, "Named areas (changing one updates every trigger area with the same rectangle)");
        foreach (NamedArea a in Doc.State.Extras.Areas)
        {
            NamedArea area = a;
            var row = new HBoxContainer();
            Forms.Label(row, $"{a.Name}: ({a.Area.Left}, {a.Area.Top})–({a.Area.Right}, {a.Area.Bottom})");
            Forms.Button(row, "Redraw", () => _screen.DrawArea(spec =>
            {
                if (spec is RectArea r)
                {
                    int updated = 0;
                    Doc.Apply("Named area", s => s.SetArea(area.Name, r, out updated));
                    _screen.Status($"Area '{area.Name}' changed; {updated} trigger area(s) followed.");
                    _dirty = true;
                }
            }));
            Forms.Button(row, "Show", () =>
            {
                _screen.ShowArea(area.Area, area.Name);
                _screen.View.CenterOnTile(area.Area.Left, area.Area.Top);
            });
            Forms.Button(row, "Delete", () => { Doc.Apply("Delete area", s => s.RemoveArea(area.Name)); _dirty = true; });
            box.AddChild(row);
        }

        string areaName = "Area";
        GridContainer g = Forms.Grid(box);
        Forms.Text(g, "New area", areaName, v => areaName = v);
        Forms.Button(box, "Draw new area on the map", () => _screen.DrawArea(spec =>
        {
            if (spec is RectArea r)
            {
                Doc.Apply("Named area", s => s.SetArea(areaName, r, out _));
                _dirty = true;
            }
        }));

        Forms.Heading(box, "Object groups (changing one updates every trigger object set with the same members)");
        foreach (ObjectGroup gr in Doc.State.Extras.Groups)
        {
            ObjectGroup group = gr;
            var row = new HBoxContainer();
            Forms.Label(row, $"{gr.Name}: {gr.ObjectIds.Length} object(s)");
            Forms.Button(row, "Select", () => _screen.Controller.Select(group.ObjectIds.Items));
            Forms.Button(row, "Set to selection", () =>
            {
                int updated = 0;
                Doc.Apply("Object group", s => s.SetGroup(group.Name, _screen.Controller.Selection, out updated));
                _screen.Status($"Group '{group.Name}' changed; {updated} trigger set(s) followed.");
                _dirty = true;
            });
            Forms.Button(row, "Delete", () => { Doc.Apply("Delete group", s => s.RemoveGroup(group.Name)); _dirty = true; });
            box.AddChild(row);
        }

        string groupName = "Group";
        GridContainer gg = Forms.Grid(box);
        Forms.Text(gg, "New group", groupName, v => groupName = v);
        Forms.Button(box, "Create from the selection", () => { Doc.Apply("Object group", s => s.SetGroup(groupName, _screen.Controller.Selection, out _)); _dirty = true; });

        Forms.Heading(box, "Search and replace object references");
        int from = 0, to = 0;
        GridContainer sr = Forms.Grid(box);
        Forms.Number(sr, "Replace object #", from, 0, 1 << 20, v => from = v);
        Forms.Number(sr, "with object #", to, 0, 1 << 20, v => to = v);
        Forms.Button(box, "Replace in all triggers", () =>
        {
            int replaced = 0;
            Doc.Apply("Replace references", s => s.ReplaceReferences(from, to, out replaced));
            _screen.Status($"{replaced} reference(s) replaced.");
        });
    }

    // ----- Lua ------------------------------------------------------------------------------------------------------

    private void LuaPage(Control page)
    {
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(240, 0) };
        page.AddChild(left);
        string[] names = Doc.State.Scripts.Keys.ToArray();
        List(left, "Lua", names);
        string newName = "main";
        GridContainer g = Forms.Grid(left);
        Forms.Text(g, "New script", newName, v => newName = v);
        Forms.Button(left, "Add", () => { Doc.Apply("Add script", s => s.SetScript(newName, "-- " + newName + "\n")); _dirty = true; });
        int sel = Selected("Lua", names.Length);
        if (sel >= 0)
        {
            Forms.Button(left, "Delete", () => { Doc.Apply("Delete script", s => s.RemoveScript(names[sel])); _dirty = true; });
        }

        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(right);
        if (sel < 0)
        {
            Forms.Label(right, "Lua scripts (Tier B, TDD §17.2) are stored in scripts/*.lua. The Lua host and its API v1 come with M8; until then the editor highlights and checks the syntax.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
            return;
        }

        string file = names[sel];
        var issues = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        var code = new CodeEdit { Text = Doc.State.Scripts[file], SizeFlagsVertical = Control.SizeFlags.ExpandFill, GuttersDrawLineNumbers = true };
        var highlighter = new CodeHighlighter
        {
            NumberColor = new Color(0.7f, 0.9f, 0.6f),
            SymbolColor = new Color(0.8f, 0.8f, 0.8f),
            FunctionColor = new Color(0.6f, 0.8f, 1f),
            MemberVariableColor = new Color(0.9f, 0.8f, 0.6f),
        };
        foreach (string k in LuaScripts.Keywords)
        {
            highlighter.AddKeywordColor(k, new Color(1f, 0.6f, 0.4f));
        }

        highlighter.AddColorRegion("--", string.Empty, new Color(0.5f, 0.6f, 0.5f), lineOnly: true);
        highlighter.AddColorRegion("\"", "\"", new Color(0.9f, 0.85f, 0.5f));
        highlighter.AddColorRegion("'", "'", new Color(0.9f, 0.85f, 0.5f));
        code.SyntaxHighlighter = highlighter;
        void Check()
        {
            for (int l = 0; l < code.GetLineCount(); l++)
            {
                code.SetLineBackgroundColor(l, new Color(0, 0, 0, 0));
            }

            System.Collections.Immutable.ImmutableArray<ScriptIssue> found = LuaScripts.Check(code.Text);
            foreach (ScriptIssue i in found)
            {
                if (i.Line - 1 < code.GetLineCount())
                {
                    code.SetLineBackgroundColor(i.Line - 1, new Color(0.6f, 0.1f, 0.1f, 0.5f));
                }
            }

            issues.Text = found.IsEmpty ? "No syntax problems found." : string.Join("\n", found.Select(i => $"line {i.Line}: {i.Message}"));
        }

        code.TextChanged += Check;
        code.FocusExited += () => Doc.Apply("Edit script", s => s.SetScript(file, code.Text));
        right.AddChild(code);
        right.AddChild(issues);
        Check();
    }

    // ----- localization ---------------------------------------------------------------------------------------------

    private void LocalizationPage(Control page)
    {
        VBoxContainer box = Forms.Scroll(page);
        System.Collections.Immutable.ImmutableArray<MapText> texts = MapStrings.Collect(Doc.State);
        Forms.Label(box, $"{texts.Length} translatable text(s); source language '{Doc.State.Extras.Language}'.");
        Forms.Button(box, "Export .po…", () => Files.Save(this, MapPackage.Slug(Doc.State.Name) + "." + Doc.State.Extras.Language + ".po", path => File.WriteAllText(path, MapStrings.ExportPo(Doc.State, Doc.State.Extras.Language)), ["*.po ; gettext catalogue"]));
        Forms.Button(box, "Import a translated .po…", () => Files.Open(this, ["*.po ; gettext catalogue"], paths =>
        {
            int applied = 0;
            try
            {
                Doc.Apply("Import translation", s => MapStrings.ImportPo(s, File.ReadAllText(paths[0]), out applied));
                _screen.Status($"{applied} text(s) translated.");
            }
            catch (FormatException e)
            {
                _screen.Status("Not a .po file: " + e.Message);
            }
        }, many: false));
        foreach (MapText t in texts.Take(300))
        {
            Forms.Label(box, $"{t.Context}: {t.Text}").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }
    }
}
