using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Ui;

namespace Tzar2.Client.Game;

/// <summary>
/// The list of saves shared by the save and load dialogs (<c>D_SAVE</c>, <c>D_LOAD</c>: the panel's inner frame): a row
/// per save, name and label, the picked one lit; the wheel scrolls. Positions inside the art are ours (⚠ UNVERIFIED).
/// </summary>
public abstract partial class SaveListDialog : ArtScreen
{
    private const int Rows = 9;
    private const int RowHeight = 17;
    private static readonly Rect2I ListArea = new(44, 74, 430, Rows * RowHeight);
    private readonly List<Control> _rows = [];
    private int _scroll;

    /// <summary>Creates the dialog on <paramref name="background"/>.</summary>
    protected SaveListDialog(UiAssets ui, Vector2I screen, string background)
        : base(ui, screen, background, dim: true)
    {
        Theme = ui.ScreenTheme(); // the save name in the original font (UI-F1)
        var area = new Control { Position = Origin + ListArea.Position, Size = ListArea.Size, MouseFilter = MouseFilterEnum.Stop };
        area.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true } b)
            {
                if (b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
                {
                    _scroll = Math.Clamp(_scroll + (b.ButtonIndex == MouseButton.WheelDown ? 1 : -1), 0, Math.Max(0, Count - Rows));
                    RefreshList();
                }
                else if (b.ButtonIndex == MouseButton.Left)
                {
                    int row = _scroll + (int)(b.Position.Y / RowHeight);
                    if (row < Count)
                    {
                        Pick(row);
                        RefreshList();
                    }
                }

                area.AcceptEvent();
            }
        };
        AddChild(area);
    }

    /// <summary>The list of saves on the art.</summary>
    protected static Rect2I ListRect => ListArea;

    /// <summary>Raised when the dialog closes.</summary>
    public event Action? Closed;

    /// <summary>How many saves are listed.</summary>
    protected abstract int Count { get; }

    /// <summary>The picked row, or −1.</summary>
    protected abstract int Picked { get; }

    /// <summary>A row's save.</summary>
    protected abstract SaveEntry Row(int index);

    /// <summary>A click on row <paramref name="index"/>.</summary>
    protected abstract void Pick(int index);

    /// <summary>Closes the dialog.</summary>
    protected void Close() => Closed?.Invoke();

    /// <summary>Redraws the rows.</summary>
    protected void RefreshList()
    {
        foreach (Control c in _rows)
        {
            c.QueueFree();
        }

        _rows.Clear();
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, Count - Rows));
        for (int i = 0; i < Rows && _scroll + i < Count; i++)
        {
            int index = _scroll + i;
            SaveEntry e = Row(index);
            Vector2I at = ListArea.Position + new Vector2I(0, i * RowHeight);
            if (index == Picked)
            {
                var lit = new ColorRect { Position = Origin + at, Size = new Vector2(ListArea.Size.X, RowHeight), Color = new Color(1f, 0.85f, 0.4f, 0.25f), MouseFilter = MouseFilterEnum.Ignore };
                AddChild(lit);
                _rows.Add(lit);
            }

            _rows.Add(Text(at + new Vector2I(4, 2), Fit(e.Name, 220, UiAssets.BoldFont)));
            _rows.Add(Text(at + new Vector2I(240, 3), Fit(e.Label.Length > 0 ? e.Label : e.Written.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), 186, UiAssets.SmallFont), UiAssets.SmallFont, new Color(0.85f, 0.85f, 0.8f)));
        }
    }

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Close();
        }
    }
}

/// <summary>
/// SAVE GAME (F2, the in-game menu; SHELL-021, SHELL-022) on <c>D_SAVE</c>: the name in the top bar (typed, or a picked
/// save's), NEW, the saves, SAVE, DELETE and CANCEL. The model is <see cref="SaveGameScreen"/>.
/// </summary>
public partial class SaveDialog : SaveListDialog
{
    private readonly SaveGameScreen _model;
    private readonly LineEdit _name;

    /// <summary>Creates the dialog.</summary>
    /// <param name="ui">The UI assets.</param>
    /// <param name="screen">The logical screen size.</param>
    /// <param name="model">The screen's model.</param>
    /// <param name="text">A string of the original's tables: section, key, fallback.</param>
    public SaveDialog(UiAssets ui, Vector2I screen, SaveGameScreen model, Func<string, string, string, string> text)
        : base(ui, screen, "art/screens/d_save/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(text);
        _model = model;
        _name = new LineEdit { Position = Origin + new Vector2I(122, 37), Size = new Vector2(276, 22), MaxLength = SaveLibrary.MaxName, Text = model.Name, Flat = true };
        _name.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.65f));
        _name.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        _name.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _name.TextChanged += t => _model.Name = t;
        _name.TextSubmitted += _ => SavePressed();
        AddChild(_name);
        Button(new Vector2I(408, 35), new Vector2I(105, 27), "art/screens/d_save/new.t2img", text("save", "new", "NEW"), () =>
        {
            _model.New();
            _name.Text = _model.Name;
            RefreshList();
        });
        Button(new Vector2I(42, 252), new Vector2I(137, 27), "art/screens/d_save/btn.t2img", text("save", "save", "SAVE"), SavePressed);
        Button(new Vector2I(206, 252), new Vector2I(137, 27), "art/screens/d_save/btn.t2img", text("save", "del", "DELETE"), () =>
        {
            _model.Delete();
            _name.Text = _model.Name;
            RefreshList();
        });
        Button(new Vector2I(370, 252), new Vector2I(137, 27), "art/screens/d_save/btn.t2img", text("save", "cancel", "CANCEL"), Close);
        Hint(new Rect2I(122, 37, 276, 22), "SG_SAVE_GAME"); // SHELL-026
        Hint(new Rect2I(408, 35, 105, 27), "SG_NEW");
        Hint(new Rect2I(42, 252, 137, 27), "SG_SAVE");
        Hint(new Rect2I(206, 252, 137, 27), "SG_DELETE");
        Hint(new Rect2I(370, 252, 137, 27), "SG_CANCEL");
        Hint(ListRect, "SG_LIST");
        HelpBall(k => text("HELP", k, k), small: true);
        RefreshList();
        Callable.From(() => _name.GrabFocus()).CallDeferred();
    }

    /// <summary>SAVE with a valid name: the caller captures the match and calls <see cref="SaveGameScreen.Save"/>.</summary>
    public event Action<SaveGameScreen>? Saving;

    /// <inheritdoc/>
    protected override int Count => _model.Entries.Count;

    /// <inheritdoc/>
    protected override int Picked => _model.Selected;

    /// <inheritdoc/>
    protected override SaveEntry Row(int index) => _model.Entries[index];

    /// <inheritdoc/>
    protected override void Pick(int index)
    {
        _model.Select(index);
        _name.Text = _model.Name;
    }

    private void SavePressed()
    {
        if (_model.CanSave)
        {
            Saving?.Invoke(_model);
        }
    }
}

/// <summary>
/// LOAD GAME (F3, the in-game menu, single player; SHELL-014) on <c>D_LOAD</c>: the control in the top bar switches
/// between single player games and campaigns (STRINGS <c>[load] type_0</c>, <c>type_1</c>), the saves, LOAD, DELETE and
/// CANCEL. The model is <see cref="LoadGameScreen"/>.
/// </summary>
public partial class LoadDialog : SaveListDialog
{
    private readonly LoadGameScreen _model;
    private readonly Func<string, string, string, string> _text;
    private TextureRect? _type;

    /// <summary>Creates the dialog.</summary>
    public LoadDialog(UiAssets ui, Vector2I screen, LoadGameScreen model, Func<string, string, string, string> text)
        : base(ui, screen, "art/screens/d_load/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(text);
        _model = model;
        _text = text;
        // The type selector of the art (UI-A3): both arrows switch between single player and campaign saves.
        void Switch()
        {
            _model.SwitchType();
            RefreshType();
            RefreshList();
        }

        Area(Box(DialogBoxes.LoadTypePrevious), Switch);
        Area(Box(DialogBoxes.LoadTypeNext), Switch);
        Button(new Vector2I(42, 252), new Vector2I(137, 27), "art/screens/d_load/btn.t2img", text("load", "load", "LOAD"), () =>
        {
            if (_model.Picked is not null)
            {
                Loading?.Invoke(_model);
            }
        });
        Button(new Vector2I(206, 252), new Vector2I(137, 27), "art/screens/d_load/btn.t2img", text("load", "del", "DELETE"), () =>
        {
            _model.Delete();
            RefreshList();
        });
        Button(new Vector2I(370, 252), new Vector2I(137, 27), "art/screens/d_load/btn.t2img", text("load", "cancel", "CANCEL"), Close);
        Hint(Box(DialogBoxes.LoadTypePrevious), "LG_CHOOSE"); // SHELL-026
        Hint(Box(DialogBoxes.LoadTypeNext), "LG_CHOOSE");
        Hint(new Rect2I(42, 252, 137, 27), "LG_LOAD");
        Hint(new Rect2I(206, 252, 137, 27), "LG_DELETE");
        Hint(new Rect2I(370, 252, 137, 27), "LG_CANCEL");
        Hint(ListRect, "LG_SELECT_GAME");
        HelpBall(k => text("HELP", k, k), small: true);
        RefreshType();
        RefreshList();
    }

    /// <summary>LOAD with a save picked: the caller reads <see cref="LoadGameScreen.Load"/>.</summary>
    public event Action<LoadGameScreen>? Loading;

    /// <inheritdoc/>
    protected override int Count => _model.Entries.Count;

    /// <inheritdoc/>
    protected override int Picked => _model.Selected;

    /// <inheritdoc/>
    protected override SaveEntry Row(int index) => _model.Entries[index];

    /// <inheritdoc/>
    protected override void Pick(int index) => _model.Select(index);

    private static Rect2I Box(ArtBox box) => new(box.X, box.Y, box.Width, box.Height);

    private void RefreshType()
    {
        _type?.QueueFree();
        ArtBox field = DialogBoxes.LoadTypeField;
        string type = _model.Campaigns ? _text("load", "type_1", "Campaign") : _text("load", "type_0", "Single Player");
        _type = Text(new Vector2I(field.X, field.Y + 6), Fit(type, field.Width - 6, UiAssets.SmallFont), UiAssets.SmallFont, null, field.Width);
    }
}
