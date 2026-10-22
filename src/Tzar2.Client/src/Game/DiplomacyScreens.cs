using System;
using System.Globalization;
using Godot;
using Tzar2.Game.Ui;

namespace Tzar2.Client.Game;

/// <summary>
/// The diplomacy screen (DIPLOMACY, F5; HUD-012) on the original's <c>D_DIPLOMACY</c> art: one row per other kingdom
/// (race, colour and name, then ALLIES, NEUTRAL and ENEMIES, then SHARED VISION, SHARED RESOURCES and SHARED UNIT
/// CONTROL), Shared Victory, TRIBUTE, ACCEPT and CANCEL. The model is <see cref="DiplomacyScreen"/>; where the parts sit
/// on the panel is ours (the original's dialog code is not decoded; ⚠ UNVERIFIED layout).
/// </summary>
public partial class DiplomacyDialog : ArtScreen
{
    private const string Art = "art/screens/d_diplomacy/";
    private static readonly string[] Stances = ["allies", "neutral", "enemies"]; // DiplomacyScreen.StanceColumns
    private static readonly string[] Features = ["sh_vision", "sh_res", "cmd_units"]; // DiplomacyScreen.SharingColumns
    private readonly DiplomacyScreen _screen;
    private readonly System.Collections.Generic.List<Action> _refresh = [];
    private TextureRect? _check;

    /// <summary>Creates the dialog.</summary>
    /// <param name="ui">The UI assets.</param>
    /// <param name="screen">The logical screen size.</param>
    /// <param name="model">The screen's model.</param>
    /// <param name="text">A string of the original's tables: section, key, fallback.</param>
    /// <param name="name">A kingdom's name by slot.</param>
    /// <param name="colour">A kingdom's colour by slot.</param>
    public DiplomacyDialog(UiAssets ui, Vector2I screen, DiplomacyScreen model, Func<string, string, string, string> text, Func<int, string> name, Func<int, Color> colour)
        : base(ui, screen, Art + "back.t2img", dim: true)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(colour);
        _screen = model;
        Text(new Vector2I(20, 18), text("diplomacy", "t_title", "DIPLOMACY SETTINGS"), UiAssets.TitleFont, null, 460);
        int y = 56;
        foreach (DiplomacyRow row in model.Rows)
        {
            if (y > 56 + (7 * 34))
            {
                break;
            }

            KingdomRow(row, y, name(row.Slot), colour(row.Slot));
            for (int s = 0; s < Stances.Length; s++)
            {
                int column = s;
                Toggle(new Vector2I(236 + (s * 32), y + 2), Art + Stances[s] + ".t2img", () => DiplomacyScreen.StanceColumnFrame(row, column), () => model.ChooseColumn(row, column), row.Defeated);
            }

            for (int f = 0; f < Features.Length; f++)
            {
                int column = f;
                Toggle(new Vector2I(342 + (f * 32), y + 2), Art + Features[f] + ".t2img", () => DiplomacyScreen.SharingColumnFrame(row, column), () => model.ToggleColumn(row, column), row.Defeated);
            }

            y += 34;
        }

        // Shared Victory: COMMON chk (two 31 × 31 frames).
        _check = new TextureRect { Position = Origin + new Vector2I(40, 300), Size = new Vector2(31, 31), MouseFilter = MouseFilterEnum.Stop };
        _check.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                model.AlliedVictory = !model.AlliedVictory;
                Refresh();
            }
        };
        AddChild(_check);
        Text(new Vector2I(78, 308), text("diplomacy", "shvictory", "Shared Victory"));
        Button(new Vector2I(310, 302), new Vector2I(149, 27), Art + "advanced.t2img", text("diplomacy", "tribute", "TRIBUTE"), () => TributePressed?.Invoke());
        Button(new Vector2I(37, 352), new Vector2I(189, 27), Art + "btn.t2img", text("diplomacy", "accept", "ACCEPT"), () => Accepted?.Invoke());
        Button(new Vector2I(267, 352), new Vector2I(189, 27), Art + "btn.t2img", text("diplomacy", "cancel", "CANCEL"), () => Closed?.Invoke());
        Refresh();
    }

    /// <summary>ACCEPT: the caller sends <see cref="DiplomacyScreen.Accept"/> and closes the dialog.</summary>
    public event Action? Accepted;

    /// <summary>TRIBUTE: the caller opens the tribute screen.</summary>
    public event Action? TributePressed;

    /// <summary>CANCEL or Escape.</summary>
    public event Action? Closed;

    /// <summary>The model.</summary>
    public DiplomacyScreen Model => _screen;

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Closed?.Invoke();
        }
        else if (@event is InputEventKey { Pressed: true, Keycode: Key.Enter })
        {
            Accepted?.Invoke();
        }
    }

    // The race icon (race.bmp: three 20 × 20 frames, euro, asia, arab; which is which is ours), the row with the
    // kingdom's colour in its square and its name in the long field.
    private void KingdomRow(DiplomacyRow row, int y, string name, Color colour)
    {
        int race = row.Civilization switch { "asia" => 1, "arab" => 2, _ => 0 };
        AddChild(new TextureRect { Texture = Ui.Region(Art + "race.t2img", new Rect2I(0, race * 20, 20, 20)), Position = Origin + new Vector2I(24, y + 6), MouseFilter = MouseFilterEnum.Ignore });
        AddChild(new ColorRect { Position = Origin + new Vector2I(57, y + 5), Size = new Vector2(17, 20), Color = colour, MouseFilter = MouseFilterEnum.Ignore });
        AddChild(new TextureRect { Texture = Ui.Texture(Art + "row.t2img"), Position = Origin + new Vector2I(48, y), MouseFilter = MouseFilterEnum.Ignore });
        Text(new Vector2I(90, y + 9), Fit(name, 124, UiAssets.BoldFont), UiAssets.BoldFont, row.Defeated ? new Color(0.55f, 0.55f, 0.5f) : null);
    }

    // A 28 × 28 button of a strip of four frames; a click acts and redraws every button.
    private void Toggle(Vector2I at, string strip, Func<int> frame, Action click, bool disabled)
    {
        var button = new TextureRect { Position = Origin + at, Size = new Vector2(28, 28), MouseFilter = disabled ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop };
        if (disabled)
        {
            button.Modulate = new Color(0.5f, 0.5f, 0.5f);
        }

        button.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                click();
                Refresh();
                button.AcceptEvent();
            }
        };
        AddChild(button);
        _refresh.Add(() => button.Texture = Ui.Region(strip, new Rect2I(0, frame() * 28, 28, 28)));
    }

    private void Refresh()
    {
        foreach (Action a in _refresh)
        {
            a();
        }

        if (_check is not null)
        {
            _check.Texture = Ui.Region("art/screens/common/chk.t2img", new Rect2I(0, _screen.AlliedVictory ? 31 : 0, 31, 31));
        }
    }
}

/// <summary>
/// The tribute screen (TRIBUTE, F6; HUD-013) on <c>D_ADVANCED</c>: one row per other kingdom with an amount of gold,
/// food, wood and stone (left click raises it, right click lowers it), SEND and CANCEL. The model is
/// <see cref="TributeScreen"/>; the layout is ours (⚠ UNVERIFIED).
/// </summary>
public partial class TributeDialog : ArtScreen
{
    private static readonly string[] Cells = ["res_gold", "res_food", "res_wood", "res_stone"];
    private readonly TributeScreen _screen;
    private readonly System.Collections.Generic.List<Action> _refresh = [];

    /// <summary>Creates the dialog; the model's columns are gold, food, wood and stone.</summary>
    public TributeDialog(UiAssets ui, Vector2I screen, TributeScreen model, Func<string, string, string, string> text, Func<int, string> name, Func<int, Color> colour)
        : base(ui, screen, "art/screens/d_advanced/back.t2img", dim: true)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(colour);
        _screen = model;
        Text(new Vector2I(20, 18), text("tribute", "t_title", "TRANSFER RESOURCES"), UiAssets.TitleFont, null, 560);
        int y = 56;
        foreach (int slot in model.Recipients)
        {
            if (y > 56 + (6 * 36))
            {
                break;
            }

            AddChild(new ColorRect { Position = Origin + new Vector2I(33, y + 5), Size = new Vector2(17, 20), Color = colour(slot), MouseFilter = MouseFilterEnum.Ignore });
            AddChild(new TextureRect { Texture = Ui.Texture("art/screens/d_diplomacy/row.t2img"), Position = Origin + new Vector2I(24, y), MouseFilter = MouseFilterEnum.Ignore });
            Text(new Vector2I(66, y + 9), Fit(name(slot), 124, UiAssets.BoldFont));
            for (int c = 0; c < Math.Min(Cells.Length, model.Resources.Count); c++)
            {
                AmountCell(slot, c, new Vector2I(206 + (c * 90), y + 1));
            }

            y += 36;
        }

        Button(new Vector2I(38, 316), new Vector2I(189, 27), "art/screens/d_diplomacy/btn.t2img", text("tribute", "send", "SEND"), () => Sent?.Invoke());
        Button(new Vector2I(364, 316), new Vector2I(189, 27), "art/screens/d_diplomacy/btn.t2img", text("tribute", "cancel", "CANCEL"), () => Closed?.Invoke());
        Refresh();
    }

    /// <summary>SEND: the caller sends <see cref="TributeScreen.Send"/>.</summary>
    public event Action? Sent;

    /// <summary>CANCEL or Escape.</summary>
    public event Action? Closed;

    /// <summary>The model.</summary>
    public TributeScreen Model => _screen;

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Closed?.Invoke();
        }
    }

    // A resource cell (86 × 30: the icon, then the amount); left click and wheel up raise, right click and wheel down lower.
    private void AmountCell(int slot, int column, Vector2I at)
    {
        var cell = new TextureRect { Texture = Ui.Texture($"art/screens/d_advanced/{Cells[column]}.t2img"), Position = Origin + at, Size = new Vector2(86, 30), MouseFilter = MouseFilterEnum.Stop };
        var amount = new TextureRect { Position = new Vector2(34, 8), MouseFilter = MouseFilterEnum.Ignore };
        cell.AddChild(amount);
        cell.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true } b)
            {
                int step = b.ButtonIndex switch { MouseButton.Left or MouseButton.WheelUp => 1, MouseButton.Right or MouseButton.WheelDown => -1, _ => 0 };
                if (step != 0)
                {
                    _screen.Click(slot, column, step);
                    Refresh();
                    cell.AcceptEvent();
                }
            }
        };
        AddChild(cell);
        _refresh.Add(() => amount.Texture = Ui.Text(_screen.Amount(slot, column).ToString(CultureInfo.InvariantCulture), UiAssets.BoldFont, new Color(1f, 0.92f, 0.65f)));
    }

    private void Refresh()
    {
        foreach (Action a in _refresh)
        {
            a();
        }
    }
}
