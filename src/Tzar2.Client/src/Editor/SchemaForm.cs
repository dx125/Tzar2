using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Content.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// Builds an event's or action's property page from <see cref="TriggerSchema"/> (Appendix C.4 shared pages: objects,
/// holder, area, expression, players, texts, user resources, map, conversation, quest, timer). Every change calls
/// <c>commit</c> with the changed record.
/// </summary>
public sealed class SchemaForm
{
    private readonly EditorScreen _screen;
    private readonly Node _parent;

    /// <summary>Creates a form builder in <paramref name="parent"/>.</summary>
    public SchemaForm(EditorScreen screen, Node parent)
    {
        _screen = screen;
        _parent = parent;
    }

    private MapDocument Doc => _screen.Document;

    private ScenarioModel Model => Doc.State.Scenario;

    /// <summary>Builds the page of <paramref name="record"/>.</summary>
    public void Build(TriggerKind kind, object record, Action<object> commit)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(commit);
        GridContainer g = Forms.Grid(_parent);
        foreach (TriggerField f in kind.Fields)
        {
            TriggerField field = f;
            void Set(object? value) => commit(field.Set(record, value));
            object? value = f.Get(record);
            switch (f.Kind)
            {
                case FieldKind.Text or FieldKind.TypeKey or FieldKind.Order:
                    Forms.Text(g, f.Name, (string?)value ?? string.Empty, v => Set(v));
                    break;
                case FieldKind.MapName:
                    Forms.Text(g, f.Name + " (map name)", (string?)value ?? string.Empty, v => Set(v));
                    break;
                case FieldKind.LongText:
                    Forms.Multiline(g, f.Name, (string?)value ?? string.Empty, v => Set(v));
                    break;
                case FieldKind.Integer:
                    Forms.Number(g, f.Name, (int)value!, 0, 100_000, v => Set(v));
                    break;
                case FieldKind.Milliseconds:
                    Forms.Number(g, f.Name + " (ms)", (int)value!, 0, 3_600_000, v => Set(v));
                    break;
                case FieldKind.Boolean:
                    Forms.Check(g, f.Name, (bool)value!, v => Set(v));
                    break;
                case FieldKind.Players:
                    Players(g, f.Name, (ushort)value!, v => Set(v));
                    break;
                case FieldKind.Kingdom:
                    Forms.Choice(g, f.Name, Enumerable.Range(1, 8).Select(k => "Kingdom " + k.ToString(CultureInfo.InvariantCulture)).ToArray(), Math.Clamp((int)value! - 1, 0, 7), i => Set(i + 1));
                    break;
                case FieldKind.Owner:
                    Forms.Choice(g, f.Name, ["Nature", .. Enumerable.Range(1, 8).Select(k => "Kingdom " + k.ToString(CultureInfo.InvariantCulture))], Math.Clamp((int)value!, 0, 8), i => Set(i));
                    break;
                case FieldKind.Stance:
                    Forms.Enum(g, f.Name, (Stance)value!, v => Set(v));
                    break;
                case FieldKind.Objects or FieldKind.OptionalObjects:
                    Objects(g, f.Name, (ObjectSpec?)value, f.Kind == FieldKind.OptionalObjects, v => Set(v));
                    break;
                case FieldKind.Holder:
                    ObjectId(g, f.Name, (int)value!, v => Set(v));
                    break;
                case FieldKind.Area:
                    Area(g, f.Name, (AreaSpec)value!, v => Set(v));
                    break;
                case FieldKind.Expression:
                    Expression(g, f.Name, ((ExpressionDef)value!).Source, v => Set(ScenarioModel.Source(v)));
                    break;
                case FieldKind.Image:
                    Resource(g, f.Name, (ResourceRef?)value, AssetKind.Image, v => Set(v));
                    break;
                case FieldKind.Sound:
                    Resource(g, f.Name, (ResourceRef?)value, AssetKind.Sound, v => Set(v));
                    break;
                case FieldKind.Music:
                    Resource(g, f.Name, (ResourceRef?)value, AssetKind.Music, v => Set(v));
                    break;
                case FieldKind.Conversation:
                    Index(g, f.Name, (int)value!, Model.Conversations.Items.Select(c => c.Name).ToArray(), null, v => Set(v));
                    break;
                case FieldKind.QuestAction:
                    Index(g, f.Name, (int)value!, Model.Actions.Items.Select(a => a.Name).ToArray(), i => Model.Actions[i] is AddQuestAction, v => Set(v));
                    break;
                case FieldKind.TimerAction:
                    Index(g, f.Name, (int)value!, Model.Actions.Items.Select(a => a.Name).ToArray(), i => Model.Actions[i] is SetTimerAction, v => Set(v));
                    break;
                default:
                    Forms.Label(g, f.Name);
                    Forms.Label(g, $"legacy type {value} (kept, not editable)");
                    break;
            }
        }
    }

    /// <summary>An expression field with a live check and the vocabulary.</summary>
    public void Expression(Node g, string label, string source, Action<string> commit)
    {
        Forms.Label(g, label);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var edit = new LineEdit { Text = source, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PlaceholderText = "e.g. Clock == 0 && $gold > 100" };
        var status = new Label { Text = Status(source), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        var insert = new MenuButton { Text = "Insert…", Flat = false };
        PopupMenu popup = insert.GetPopup();
        System.Collections.Immutable.ImmutableArray<Completion> vocabulary = ExpressionHelp.Vocabulary(Model);
        for (int i = 0; i < vocabulary.Length; i++)
        {
            popup.AddItem($"{vocabulary[i].Text}   ({vocabulary[i].Kind})", i);
        }

        popup.IdPressed += id =>
        {
            int caret = edit.CaretColumn;
            edit.Text = edit.Text.Insert(caret, vocabulary[(int)id].Text);
            edit.CaretColumn = caret + vocabulary[(int)id].Text.Length;
            status.Text = Status(edit.Text);
        };
        edit.TextChanged += t => status.Text = Status(t);
        string committed = source;
        void Commit()
        {
            if (edit.Text != committed)
            {
                committed = edit.Text;
                commit(edit.Text);
            }
        }

        edit.TextSubmitted += _ => Commit();
        edit.FocusExited += Commit;
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddChild(edit);
        row.AddChild(insert);
        box.AddChild(row);
        box.AddChild(status);
        g.AddChild(box);
    }

    private string Status(string source)
    {
        if (source.Trim().Length == 0)
        {
            return "empty";
        }

        ExpressionDef parsed = Model.Parse(source);
        if (parsed.Error is { } error)
        {
            return "⚠ " + error;
        }

        var unknown = new List<string>();
        Walk(parsed.Root, unknown);
        return unknown.Count == 0 ? "✓ valid" : "✓ valid; unknown names evaluate to 0: " + string.Join(", ", unknown);
    }

    private static void Walk(Expr? e, List<string> unknown)
    {
        switch (e)
        {
            case UnknownRef u:
                unknown.Add(u.Name);
                break;
            case UnaryExpr u:
                Walk(u.Operand, unknown);
                break;
            case BinaryExpr b:
                Walk(b.Left, unknown);
                Walk(b.Right, unknown);
                break;
            case AssignExpr a:
                Walk(a.Target, unknown);
                Walk(a.Value, unknown);
                break;
            case CallExpr c:
                foreach (Expr x in c.Arguments)
                {
                    Walk(x, unknown);
                }

                break;
            case SequenceExpr q:
                foreach (Expr x in q.Statements)
                {
                    Walk(x, unknown);
                }

                break;
        }
    }

    private static void Players(Node g, string label, ushort mask, Action<ushort> commit)
    {
        Forms.Label(g, label);
        var row = new HBoxContainer();
        for (int p = 1; p <= 8; p++)
        {
            int player = p;
            var box = new CheckBox { Text = p.ToString(CultureInfo.InvariantCulture), ButtonPressed = PlayerMasks.Contains(mask, p) };
            box.Toggled += on =>
            {
                mask = on ? (ushort)(mask | PlayerMasks.Of(player)) : (ushort)(mask & ~PlayerMasks.Of(player));
                commit(mask);
            };
            row.AddChild(box);
        }

        g.AddChild(row);
    }

    private void ObjectId(Node g, string label, int id, Action<int> commit)
    {
        Forms.Label(g, label);
        var row = new HBoxContainer();
        var spin = new SpinBox { MinValue = -1, MaxValue = 1 << 20, Value = id };
        spin.ValueChanged += v => commit((int)v);
        row.AddChild(spin);
        Forms.Button(row, "Use selected", () =>
        {
            if (!_screen.Controller.Selection.IsEmpty)
            {
                commit(_screen.Controller.Selection[0]);
            }
        });
        Forms.Label(row, id >= 0 && Doc.State.Find(id) is { } o ? o.Label : "(none)");
        g.AddChild(row);
    }

    private void Objects(Node g, string label, ObjectSpec? spec, bool optional, Action<ObjectSpec?> commit)
    {
        Forms.Label(g, label);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        string[] modes = optional ? ["(none)", "Named objects", "Type", "Category"] : ["Named objects", "Type", "Category"];
        int mode = spec switch { NamedObjects => 0, TypedObjects => 1, CategoryObjects => 2, _ => -1 } + (optional ? 1 : 0);
        var option = new OptionButton();
        foreach (string m in modes)
        {
            option.AddItem(m);
        }

        option.Selected = Math.Max(0, mode);
        option.ItemSelected += i =>
        {
            int k = (int)i - (optional ? 1 : 0);
            commit(k switch
            {
                < 0 => null,
                0 => new NamedObjects(EquatableArray<int>.Empty, Quantifier.All),
                1 => new TypedObjects(string.Empty, PlayerMasks.Of(1), CountComparison.AtLeast, 1, Quantifier.All),
                _ => new CategoryObjects(ObjectCategory.Units, PlayerMasks.Of(1), CountComparison.AtLeast, 1, Quantifier.All),
            });
        };
        box.AddChild(option);
        GridContainer inner = Forms.Grid(box);
        switch (spec)
        {
            case NamedObjects n:
                Forms.Label(inner, "Objects");
                Forms.Label(inner, n.ObjectIndices.Length == 0 ? "(none)" : string.Join(", ", n.ObjectIndices.Items.Select(id => Doc.State.Find(id)?.Label ?? "#" + id.ToString(CultureInfo.InvariantCulture))));
                Forms.Enum(inner, "All / Any", n.Quantifier, q => commit(n with { Quantifier = q }));
                var row = new HBoxContainer();
                Forms.Button(row, "Use selection", () => commit(n with { ObjectIndices = EquatableArray<int>.From(_screen.Controller.Selection) }));
                Forms.Button(row, "Select them", () => _screen.Controller.Select(n.ObjectIndices.Items));
                if (Doc.State.Extras.Groups.Length > 0)
                {
                    var groups = new OptionButton();
                    groups.AddItem("Use group…");
                    foreach (ObjectGroup gr in Doc.State.Extras.Groups)
                    {
                        groups.AddItem(gr.Name);
                    }

                    groups.ItemSelected += i =>
                    {
                        if (i > 0)
                        {
                            commit(n with { ObjectIndices = Doc.State.Extras.Groups[(int)i - 1].ObjectIds });
                        }
                    };
                    row.AddChild(groups);
                }

                inner.AddChild(new Label());
                inner.AddChild(row);
                break;
            case TypedObjects t:
                Forms.Text(inner, "Type key", t.TypeKey, v => commit(t with { TypeKey = v }));
                Players(inner, "Players", t.Players, v => commit(t with { Players = v }));
                Forms.Enum(inner, "Count is", t.Comparison, v => commit(t with { Comparison = v }));
                Forms.Number(inner, "Count", t.Count, 0, 100_000, v => commit(t with { Count = v }));
                Forms.Enum(inner, "All / Any", t.Quantifier, v => commit(t with { Quantifier = v }));
                break;
            case CategoryObjects c:
                Forms.Enum(inner, "Category", c.Category, v => commit(c with { Category = v }));
                Players(inner, "Players", c.Players, v => commit(c with { Players = v }));
                Forms.Enum(inner, "Count is", c.Comparison, v => commit(c with { Comparison = v }));
                Forms.Number(inner, "Count", c.Count, 0, 100_000, v => commit(c with { Count = v }));
                Forms.Enum(inner, "All / Any", c.Quantifier, v => commit(c with { Quantifier = v }));
                break;
        }

        g.AddChild(box);
    }

    private void Area(Node g, string label, AreaSpec area, Action<AreaSpec> commit)
    {
        Forms.Label(g, label);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var mode = new OptionButton();
        mode.AddItem("Rectangle");
        mode.AddItem("Object");
        mode.Selected = area is ObjectArea ? 1 : 0;
        mode.ItemSelected += i => commit(i == 0 ? new RectArea(0, 0, 0, 0) : new ObjectArea(_screen.Controller.Selection.IsEmpty ? -1 : _screen.Controller.Selection[0]));
        box.AddChild(mode);
        GridContainer inner = Forms.Grid(box);
        if (area is RectArea r)
        {
            Forms.Number(inner, "Left", r.Left, 0, 1024, v => commit(r with { Left = v }));
            Forms.Number(inner, "Top", r.Top, 0, 1024, v => commit(r with { Top = v }));
            Forms.Number(inner, "Right", r.Right, 0, 1024, v => commit(r with { Right = v }));
            Forms.Number(inner, "Bottom", r.Bottom, 0, 1024, v => commit(r with { Bottom = v }));
            var row = new HBoxContainer();
            Forms.Button(row, "Draw on map", () => _screen.DrawArea(commit));
            Forms.Button(row, "Show", () =>
            {
                _screen.ShowArea(r, label);
                _screen.View.CenterOnTile((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            });
            if (Doc.State.Extras.Areas.Length > 0)
            {
                var named = new OptionButton();
                named.AddItem("Use named area…");
                foreach (NamedArea a in Doc.State.Extras.Areas)
                {
                    named.AddItem(a.Name);
                }

                named.ItemSelected += i =>
                {
                    if (i > 0)
                    {
                        commit(Doc.State.Extras.Areas[(int)i - 1].Area);
                    }
                };
                row.AddChild(named);
            }

            box.AddChild(row);
        }
        else if (area is ObjectArea o)
        {
            ObjectId(inner, "Object", o.ObjectIndex, v => commit(o with { ObjectIndex = v }));
            Forms.Optional(inner, "Radius (tiles)", o.RadiusTiles, 0, 0, 64, v => commit(o with { RadiusTiles = v }));
        }

        g.AddChild(box);
    }

    private void Resource(Node g, string label, ResourceRef? current, AssetKind kind, Action<ResourceRef?> commit)
    {
        string[] paths = ["(none)", .. Doc.State.Of(kind)];
        int selected = current is null ? 0 : Array.IndexOf(paths, current.Path);
        if (selected < 0)
        {
            paths = [.. paths, current!.Path + " (missing)"];
            selected = paths.Length - 1;
        }

        Forms.Choice(g, label, paths, selected, i => commit(i == 0 ? null : new ResourceRef(paths[i].Replace(" (missing)", string.Empty, StringComparison.Ordinal))));
    }

    private static void Index(Node g, string label, int current, string[] names, Func<int, bool>? filter, Action<int> commit)
    {
        var options = new List<(int Index, string Name)> { (-1, "(none)") };
        for (int i = 0; i < names.Length; i++)
        {
            if (filter is null || filter(i))
            {
                options.Add((i, names[i]));
            }
        }

        int selected = Math.Max(0, options.FindIndex(o => o.Index == current));
        Forms.Choice(g, label, options.Select(o => o.Name).ToArray(), selected, i => commit(options[i].Index));
    }
}
