using System;
using System.Collections.Generic;
using Godot;

namespace Tzar2.Client.Editor;

/// <summary>
/// Small builders for the editor's property forms (Godot's standard controls: the editor is a tool, not the authentic
/// game UI). Every field reports a committed value through a callback; the panels turn it into a document edit.
/// </summary>
public static class Forms
{
    /// <summary>A two-column grid for label/field rows.</summary>
    public static GridContainer Grid(Node parent, int columns = 2)
    {
        var grid = new GridContainer { Columns = columns, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        parent.AddChild(grid);
        return grid;
    }

    /// <summary>A section heading.</summary>
    public static Label Heading(Node parent, string text)
    {
        var label = new Label { Text = text };
        label.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.5f));
        parent.AddChild(label);
        return label;
    }

    /// <summary>A label.</summary>
    public static Label Label(Node parent, string text)
    {
        var label = new Label { Text = text };
        parent.AddChild(label);
        return label;
    }

    /// <summary>A text field committed on Enter or focus loss.</summary>
    public static LineEdit Text(Node parent, string label, string value, Action<string> commit)
    {
        Label(parent, label);
        var edit = new LineEdit { Text = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(160, 0) };
        edit.TextSubmitted += text => commit(text);
        edit.FocusExited += () =>
        {
            if (edit.Text != value)
            {
                value = edit.Text;
                commit(edit.Text);
            }
        };
        parent.AddChild(edit);
        return edit;
    }

    /// <summary>A multi-line text field committed on focus loss.</summary>
    public static TextEdit Multiline(Node parent, string label, string value, Action<string> commit, int height = 80)
    {
        Label(parent, label);
        var edit = new TextEdit { Text = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(220, height), WrapMode = TextEdit.LineWrappingMode.Boundary };
        edit.FocusExited += () =>
        {
            if (edit.Text != value)
            {
                value = edit.Text;
                commit(edit.Text);
            }
        };
        parent.AddChild(edit);
        return edit;
    }

    /// <summary>An integer field.</summary>
    public static SpinBox Number(Node parent, string label, int value, int min, int max, Action<int> commit)
    {
        Label(parent, label);
        var spin = new SpinBox { MinValue = min, MaxValue = max, Step = 1, Value = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AllowGreater = false };
        spin.ValueChanged += v => commit((int)v);
        parent.AddChild(spin);
        return spin;
    }

    /// <summary>An optional integer: a "default" checkbox plus the value (Appendix C.2 "use default").</summary>
    public static HBoxContainer Optional(Node parent, string label, int? value, int fallback, int min, int max, Action<int?> commit)
    {
        Label(parent, label);
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var useDefault = new CheckBox { Text = "default", ButtonPressed = value is null };
        var spin = new SpinBox { MinValue = min, MaxValue = max, Step = 1, Value = value ?? fallback, Editable = value is not null, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        useDefault.Toggled += on =>
        {
            spin.Editable = !on;
            commit(on ? null : (int)spin.Value);
        };
        spin.ValueChanged += v =>
        {
            if (!useDefault.ButtonPressed)
            {
                commit((int)v);
            }
        };
        row.AddChild(useDefault);
        row.AddChild(spin);
        parent.AddChild(row);
        return row;
    }

    /// <summary>A checkbox.</summary>
    public static CheckBox Check(Node parent, string label, bool value, Action<bool> commit, bool inGrid = true)
    {
        if (inGrid)
        {
            Label(parent, string.Empty);
        }

        var box = new CheckBox { Text = label, ButtonPressed = value };
        box.Toggled += on => commit(on);
        parent.AddChild(box);
        return box;
    }

    /// <summary>A drop-down over <paramref name="items"/>.</summary>
    public static OptionButton Choice(Node parent, string label, IReadOnlyList<string> items, int selected, Action<int> commit)
    {
        ArgumentNullException.ThrowIfNull(items);
        Label(parent, label);
        var option = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (string item in items)
        {
            option.AddItem(item);
        }

        if (selected >= 0 && selected < items.Count)
        {
            option.Selected = selected;
        }

        option.ItemSelected += i => commit((int)i);
        parent.AddChild(option);
        return option;
    }

    /// <summary>A drop-down over an enum.</summary>
    public static OptionButton Enum<T>(Node parent, string label, T value, Action<T> commit)
        where T : struct, System.Enum
    {
        T[] values = System.Enum.GetValues<T>();
        string[] names = Array.ConvertAll(values, v => v.ToString());
        return Choice(parent, label, names, Array.IndexOf(values, value), i => commit(values[i]));
    }

    /// <summary>A push button.</summary>
    public static Button Button(Node parent, string text, Action pressed)
    {
        var button = new Button { Text = text };
        button.Pressed += pressed;
        parent.AddChild(button);
        return button;
    }

    /// <summary>A scrollable vertical box filling its parent.</summary>
    public static VBoxContainer Scroll(Node parent, string name = "")
    {
        var scroll = new ScrollContainer { Name = name.Length > 0 ? name : "Scroll", SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(box);
        parent.AddChild(scroll);
        return box;
    }

    /// <summary>Removes and frees every child of <paramref name="node"/>.</summary>
    public static void Clear(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>A modal window with a scrollable body and a Close button; <paramref name="build"/> fills the body.</summary>
    public static Window Dialog(Node host, string title, Vector2I size, Action<VBoxContainer> build)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(build);
        var window = new Window { Title = title, Size = size, Exclusive = false, Transient = true, WrapControls = false };
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride("margin_" + side, 8);
        }

        var outer = new VBoxContainer();
        margin.AddChild(outer);
        VBoxContainer body = Scroll(outer);
        build(body);
        Button(outer, "Close", window.QueueFree);
        window.AddChild(margin);
        window.CloseRequested += window.QueueFree;
        host.AddChild(window);
        window.PopupCentered();
        return window;
    }
}
