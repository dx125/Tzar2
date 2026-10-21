using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Tzar2.Client.Game;

/// <summary>
/// A small dialog in the command card's place, on the original's 242 × 123 panels (<c>D_TRADE</c>, <c>D_TRADE1</c>,
/// <c>D_GAMBLE</c> <c>back_h</c>): cells of 42 × 42 (or 93 × 42) inside their frames, measured on the art. What each cell
/// does is ours (the original's dialog code is not decoded; ⚠ UNVERIFIED layout semantics).
/// </summary>
public abstract partial class CardDialog : Control
{
    /// <summary>The panel's size.</summary>
    public static readonly Vector2I PanelSize = new(242, 123);

    /// <summary>Where the panel's first cell frame starts inside it: aligned with the card's first slot.</summary>
    public static readonly Vector2I FirstCell = new(13, 9);

    /// <summary>The resources a dialog offers (icons <c>art/icons/res*.t2img</c>).</summary>
    public static readonly string[] Resources = ["gold", "food", "wood", "stone"];

    private static readonly Color Gold = new(1f, 0.92f, 0.65f);

    /// <summary>Creates the panel with the background of <paramref name="folder"/>.</summary>
    protected CardDialog(UiAssets ui, string folder)
    {
        ArgumentNullException.ThrowIfNull(ui);
        Ui = ui;
        Folder = folder;
        Size = PanelSize;
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(new TextureRect { Texture = ui.Texture($"art/screens/{folder}/back_h.t2img"), MouseFilter = MouseFilterEnum.Ignore });
    }

    /// <summary>Closed (cancel, Escape, or the selection changed).</summary>
    public event Action? Closed;

    /// <summary>The UI assets.</summary>
    protected UiAssets Ui { get; }

    /// <summary>The art folder.</summary>
    protected string Folder { get; }

    /// <summary>Closes the dialog.</summary>
    public void Close() => Closed?.Invoke();

    /// <summary>A clickable cell over <paramref name="interior"/>; left click and wheel up step +1, right click and wheel down −1.</summary>
    protected TextureRect Cell(Rect2I interior, Action<int>? step)
    {
        var picture = new TextureRect
        {
            Position = interior.Position,
            Size = interior.Size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            MouseFilter = step is null ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop,
        };
        if (step is not null)
        {
            picture.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true } b)
                {
                    int s = b.ButtonIndex switch
                    {
                        MouseButton.Left or MouseButton.WheelUp => 1,
                        MouseButton.Right or MouseButton.WheelDown => -1,
                        _ => 0,
                    };
                    if (s != 0)
                    {
                        step(s);
                        picture.AcceptEvent();
                    }
                }
            };
        }

        AddChild(picture);
        return picture;
    }

    /// <summary>A text label centred in <paramref name="interior"/>.</summary>
    protected TextureRect Label(Rect2I interior)
    {
        var label = new TextureRect { Position = interior.Position, Size = interior.Size, StretchMode = TextureRect.StretchModeEnum.KeepCentered, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(label);
        return label;
    }

    /// <summary>Sets a label's text.</summary>
    protected void SetText(TextureRect label, string text, string font = UiAssets.BoldFont)
        => label.Texture = text.Length == 0 ? null : Ui.Text(text, font, Gold);

    /// <summary>A 48 × 48 button of the folder (<c>trade</c>, <c>gamble</c>, <c>cancel</c>) over the cell whose interior starts at <paramref name="interior"/>.</summary>
    protected TextureButton Button(Vector2I interior, string image, Action pressed)
    {
        var button = new TextureButton { Position = interior - new Vector2I(3, 3), Size = new Vector2(48, 48), TextureNormal = Ui.Texture($"art/screens/{Folder}/{image}.t2img"), IgnoreTextureSize = true, StretchMode = TextureButton.StretchModeEnum.Keep };
        button.Pressed += pressed;
        AddChild(button);
        return button;
    }

    /// <summary>The big icon of resource <paramref name="index"/> (the 20 × 20 icon doubled).</summary>
    protected ImageTexture? ResourceIcon(int index)
    {
        if (Ui.Image($"art/icons/res{Resources[index]}.t2img") is not { } small)
        {
            return null;
        }

        Image big = (Image)small.Duplicate();
        big.Resize(small.GetWidth() * 2, small.GetHeight() * 2, Image.Interpolation.Nearest);
        return ImageTexture.CreateFromImage(big);
    }

    /// <summary>Steps <paramref name="value"/> through <paramref name="count"/> values, wrapping.</summary>
    protected static int Wrap(int value, int count) => ((value % count) + count) % count;

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton)
        {
            AcceptEvent(); // clicks on the panel never reach the world below
        }
    }
}

/// <summary>
/// Gambling in the tavern (order <c>gamble</c>; GAME-14 <c>Gamble</c>): the resource staked, the stake (with the dice
/// rolling, then the original's win or lose picture), GAMBLE; the proportion (the original's 1:1, 1:2, 1:5, 1:10) and
/// CANCEL.
/// </summary>
public partial class GambleDialog : CardDialog
{
    private const int Step = 100;
    private const int DiceFrames = 15;
    private const int DiceMs = 900;

    private readonly int _maxStake;
    private readonly int _oddsCount;
    private readonly TextureRect _resource;
    private readonly TextureRect _stake;
    private readonly TextureRect _dice;
    private readonly TextureRect _odds;
    private int _resourceIndex;
    private int _amount = Step;
    private int _oddsIndex;
    private double _rollingMs = -1;
    private (bool Won, int Payout)? _result;

    /// <summary>Creates the dialog.</summary>
    public GambleDialog(UiAssets ui, int maxStake, int oddsCount)
        : base(ui, "d_gamble")
    {
        _maxStake = Math.Max(Step, maxStake);
        _oddsCount = Math.Max(1, Math.Min(oddsCount, 4));
        _resource = Cell(new Rect2I(16, 12, 42, 42), s =>
        {
            _resourceIndex = Wrap(_resourceIndex + s, Resources.Length);
            Refresh();
        });
        _dice = Cell(new Rect2I(74, 12, 93, 42), s =>
        {
            _amount = Math.Clamp(_amount + (s * Step), Step, _maxStake);
            _result = null;
            Refresh();
        });
        _stake = Label(new Rect2I(74, 12, 93, 42));
        Button(new Vector2I(183, 12), "gamble", () =>
        {
            Bet?.Invoke(Resources[_resourceIndex], _oddsIndex, _amount);
            _rollingMs = 0;
            _result = null;
            Refresh();
        });
        _odds = Cell(new Rect2I(74, 68, 93, 42), s =>
        {
            _oddsIndex = Wrap(_oddsIndex + s, _oddsCount);
            Refresh();
        });
        Button(new Vector2I(183, 68), "cancel", Close);
        Refresh();
    }

    /// <summary>GAMBLE: the resource key, the proportion index and the stake.</summary>
    public event Action<string, int, int>? Bet;

    /// <summary>The bet's outcome (the sim's <c>Gambled</c> event): shown when the dice stop.</summary>
    public void ShowResult(bool won, int payout)
    {
        _result = (won, payout);
        Refresh();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_rollingMs < 0)
        {
            return;
        }

        _rollingMs += delta * 1000;
        if (_rollingMs >= DiceMs && _result is not null)
        {
            _rollingMs = -1;
        }

        Refresh();
    }

    private void Refresh()
    {
        _resource.Texture = ResourceIcon(_resourceIndex);
        _odds.Texture = Ui.Region("art/screens/d_gamble/prop.t2img", new Rect2I(0, _oddsIndex * 48, 99, 48));
        if (_rollingMs >= 0)
        {
            int frame = Math.Min(DiceFrames - 1, (int)(_rollingMs * DiceFrames / DiceMs) % DiceFrames);
            _dice.Texture = Ui.Region("art/screens/d_gamble/zarove.t2img", new Rect2I(frame * 93, 0, 93, 42));
            SetText(_stake, string.Empty);
            return;
        }

        if (_result is { } r)
        {
            // winlose.bmp: dice, lost, won (99 × 48 each).
            _dice.Texture = Ui.Region("art/screens/d_gamble/winlose.t2img", new Rect2I(0, (r.Won ? 2 : 1) * 48, 99, 48));
            SetText(_stake, r.Won ? "+" + r.Payout.ToString(CultureInfo.InvariantCulture) : string.Empty);
            return;
        }

        _dice.Texture = null;
        SetText(_stake, _amount.ToString(CultureInfo.InvariantCulture), UiAssets.TitleFont);
    }
}

/// <summary>
/// Exchange in the tavern (order <c>trade1</c>; GAME-14 <c>Convert</c>, one for ten): what is given, what is received,
/// how much is given; the amount received, TRADE and CANCEL.
/// </summary>
public partial class ExchangeDialog : CardDialog
{
    private static readonly int[] Amounts = [100, 200, 500, 1000];
    private readonly int _ratio;
    private readonly TextureRect _give;
    private readonly TextureRect _get;
    private readonly TextureRect _amount;
    private readonly TextureRect _result;
    private int _giveIndex = 1;
    private int _getIndex;
    private int _amountIndex;

    /// <summary>Creates the dialog; <paramref name="ratio"/> units are given for one received.</summary>
    public ExchangeDialog(UiAssets ui, int ratio)
        : base(ui, "d_trade1")
    {
        _ratio = Math.Max(1, ratio);
        _give = Cell(new Rect2I(73, 12, 42, 42), s =>
        {
            _giveIndex = Wrap(_giveIndex + s, Resources.Length);
            Refresh();
        });
        _get = Cell(new Rect2I(128, 12, 42, 42), s =>
        {
            _getIndex = Wrap(_getIndex + s, Resources.Length);
            Refresh();
        });
        _amount = Cell(new Rect2I(184, 12, 39, 42), s =>
        {
            _amountIndex = Wrap(_amountIndex + s, Amounts.Length);
            Refresh();
        });
        _result = Label(new Rect2I(72, 68, 42, 42));
        Button(new Vector2I(128, 68), "trade", () =>
        {
            if (_giveIndex != _getIndex)
            {
                Exchange?.Invoke(Resources[_giveIndex], Resources[_getIndex], Amounts[_amountIndex]);
            }
        });
        Button(new Vector2I(184, 68), "cancel", Close);
        Refresh();
    }

    /// <summary>TRADE: the resource given, the resource received, the amount given.</summary>
    public event Action<string, string, int>? Exchange;

    private void Refresh()
    {
        _give.Texture = ResourceIcon(_giveIndex);
        _get.Texture = ResourceIcon(_getIndex);
        SetText(_amount, Amounts[_amountIndex].ToString(CultureInfo.InvariantCulture));
        SetText(_result, _giveIndex == _getIndex ? "-" : "+" + (Amounts[_amountIndex] / _ratio).ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// A trade route (order <c>trade</c> of caravans, merchant ships and Asian peasants; GAME-14 <c>Trade</c>): the goods
/// loaded at the near market, the goods brought back from the far one, the load per trip ("full" = the trader's
/// capacity); TRADE asks for the far market, CANCEL.
/// </summary>
public partial class TradeDialog : CardDialog
{
    private static readonly int[] Loads = [0, 10, 20, 50, 100];
    private readonly TextureRect _here;
    private readonly TextureRect _there;
    private readonly TextureRect _load;
    private int _hereIndex = 2;
    private int _thereIndex;
    private int _loadIndex;

    /// <summary>Creates the dialog.</summary>
    public TradeDialog(UiAssets ui)
        : base(ui, "d_trade")
    {
        _here = Cell(new Rect2I(16, 12, 42, 42), s =>
        {
            _hereIndex = Wrap(_hereIndex + s, Resources.Length);
            Refresh();
        });
        _there = Cell(new Rect2I(73, 12, 42, 42), s =>
        {
            _thereIndex = Wrap(_thereIndex + s, Resources.Length);
            Refresh();
        });
        _load = Cell(new Rect2I(130, 12, 93, 42), s =>
        {
            _loadIndex = Wrap(_loadIndex + s, Loads.Length);
            Refresh();
        });
        Button(new Vector2I(128, 68), "trade", () => Route?.Invoke(Resources[_hereIndex], Resources[_thereIndex], Loads[_loadIndex]));
        Button(new Vector2I(184, 68), "cancel", Close);
        Refresh();
    }

    /// <summary>TRADE: the goods loaded here, the goods loaded there, the load per trip (0 = full).</summary>
    public event Action<string, string, int>? Route;

    private void Refresh()
    {
        _here.Texture = ResourceIcon(_hereIndex);
        _there.Texture = ResourceIcon(_thereIndex);
        SetText(_load, Loads[_loadIndex] == 0 ? "full load" : Loads[_loadIndex].ToString(CultureInfo.InvariantCulture) + " per trip");
    }
}
