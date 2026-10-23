using System;
using System.Collections.Generic;
using Tzar2.Game.Session;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Kingdoms;

namespace Tzar2.Game.Ui;

/// <summary>One other kingdom on the diplomacy screen (HUD-012).</summary>
/// <param name="Slot">The kingdom's slot.</param>
/// <param name="Civilization">Its civilization key (the race icon).</param>
/// <param name="Defeated">Whether it is out of the match (its row cannot change).</param>
/// <param name="Theirs">Its stance towards the player.</param>
/// <param name="TheirOffers">What it offers the player.</param>
public sealed record DiplomacyRow(int Slot, string Civilization, bool Defeated, Stance Theirs, Sharing TheirOffers)
{
    /// <summary>The player's stance towards it, as chosen on the screen.</summary>
    public Stance Mine { get; internal set; }

    /// <summary>What the player offers it, as chosen on the screen (without <see cref="Sharing.Victory"/>).</summary>
    public Sharing MyOffers { get; internal set; }
}

/// <summary>
/// The diplomacy screen (DIPLOMACY, F5; HUD-012, manual "Diplomacy Screen"): the player's stance towards each other
/// kingdom (ally, enemy, neutral), the sharing it offers (vision, resources, unit control) and shared victory. A stance
/// button is lit in its lower left when the player chose it and in its upper right when the other kingdom chose it
/// towards the player; a sharing button glows green when the other kingdom offers it too. Nothing changes until ACCEPT
/// (engine-free model, ADR 0182).
/// </summary>
public sealed class DiplomacyScreen
{
    private readonly int _me;
    private readonly List<DiplomacyRow> _rows = [];
    private readonly Dictionary<int, (Stance Stance, Sharing Offers)> _was = [];
    private readonly bool _victoryWas;

    /// <summary>Creates the screen from <paramref name="frame"/> for <paramref name="me"/>.</summary>
    public DiplomacyScreen(GameFrame frame, int me)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _me = me;
        bool victory = false;
        for (int p = 0; p < frame.Players.Length; p++)
        {
            if (p == me)
            {
                continue;
            }

            Sharing mine = frame.OffersOf(me, p);
            victory |= (mine & Sharing.Victory) != 0;
            var row = new DiplomacyRow(p, frame.Players[p].Civilization ?? string.Empty, frame.Players[p].Defeated, frame.StanceOf(p, me), frame.OffersOf(p, me) & ~Sharing.Victory)
            {
                Mine = frame.StanceOf(me, p),
                MyOffers = mine & ~Sharing.Victory,
            };
            _rows.Add(row);
            _was[p] = (row.Mine, mine);
        }

        _victoryWas = victory;
        AlliedVictory = victory;
    }

    /// <summary>The other kingdoms, by slot.</summary>
    public IReadOnlyList<DiplomacyRow> Rows => _rows;

    /// <summary>Shared victory (the check box): offered to every kingdom; it holds between two allies that both offer it.</summary>
    public bool AlliedVictory { get; set; }

    /// <summary>The frame of the shared victory check (COMMON <c>chk</c>) when offered: the green one, on top.</summary>
    public const int CheckOnFrame = 0;

    /// <summary>The frame of the shared victory check when not offered: the red one, under the green.</summary>
    public const int CheckOffFrame = 1;

    /// <summary>The frame of the shared victory check.</summary>
    public int AlliedVictoryFrame => AlliedVictory ? CheckOnFrame : CheckOffFrame;

    /// <summary>Chooses the stance towards the kingdom of <paramref name="row"/>.</summary>
    public void Choose(DiplomacyRow row, Stance stance)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.Defeated)
        {
            row.Mine = stance;
        }
    }

    /// <summary>Turns one sharing feature (vision, resources, unit control) towards the kingdom of <paramref name="row"/> on or off.</summary>
    public void Toggle(DiplomacyRow row, Sharing feature)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.Defeated && feature is Sharing.Vision or Sharing.Resources or Sharing.UnitControl)
        {
            row.MyOffers ^= feature;
        }
    }

    /// <summary>The stances in the screen's column order: ALLIES, NEUTRAL, ENEMIES (the client draws them by index).</summary>
    public static IReadOnlyList<Stance> StanceColumns { get; } = [Stance.Ally, Stance.Neutral, Stance.Enemy];

    /// <summary>The sharing features in the screen's column order: vision, resources, unit control.</summary>
    public static IReadOnlyList<Sharing> SharingColumns { get; } = [Sharing.Vision, Sharing.Resources, Sharing.UnitControl];

    /// <summary>Chooses the stance of column <paramref name="column"/> (<see cref="StanceColumns"/>).</summary>
    public void ChooseColumn(DiplomacyRow row, int column) => Choose(row, StanceColumns[column]);

    /// <summary>Toggles the sharing of column <paramref name="column"/> (<see cref="SharingColumns"/>).</summary>
    public void ToggleColumn(DiplomacyRow row, int column) => Toggle(row, SharingColumns[column]);

    /// <summary>The frame of the stance button of column <paramref name="column"/>.</summary>
    public static int StanceColumnFrame(DiplomacyRow row, int column) => StanceFrame(row, StanceColumns[column]);

    /// <summary>The frame of the sharing button of column <paramref name="column"/>.</summary>
    public static int SharingColumnFrame(DiplomacyRow row, int column) => SharingFrame(row, SharingColumns[column]);

    /// <summary>The frame of a stance button (<c>allies</c>, <c>enemies</c>, <c>neutral</c>): 1 when the player chose it, +2 when the other kingdom did.</summary>
    public static int StanceFrame(DiplomacyRow row, Stance stance)
    {
        ArgumentNullException.ThrowIfNull(row);
        return (row.Mine == stance ? 1 : 0) + (row.Theirs == stance ? 2 : 0);
    }

    /// <summary>The frame of a sharing button (<c>sh_vision</c>, <c>sh_res</c>, <c>cmd_units</c>): 0 on, 1 off, +2 with the other kingdom's green glow.</summary>
    public static int SharingFrame(DiplomacyRow row, Sharing feature)
    {
        ArgumentNullException.ThrowIfNull(row);
        return ((row.MyOffers & feature) != 0 ? 0 : 1) + ((row.TheirOffers & feature) != 0 ? 2 : 0);
    }

    /// <summary>ACCEPT: one diplomacy order per kingdom whose stance or offers changed; null when nothing did.</summary>
    public CommandList? Accept()
    {
        var list = new CommandList();
        foreach (DiplomacyRow row in _rows)
        {
            Sharing offers = row.MyOffers | (AlliedVictory ? Sharing.Victory : Sharing.None);
            if (row.Defeated || (_was[row.Slot].Stance == row.Mine && _was[row.Slot].Offers == offers))
            {
                continue;
            }

            list.Add(CommandKind.SetDiplomacy, x: row.Slot, arg0: (int)row.Mine, arg1: (int)offers);
        }

        return list.Count > 0 ? list : null;
    }

    /// <summary>Whether the screen holds changes not yet accepted.</summary>
    public bool Changed => Accept() is not null || AlliedVictory != _victoryWas && _rows.Exists(r => !r.Defeated);

    /// <summary>The player's slot.</summary>
    public int Player => _me;
}

/// <summary>
/// The tribute screen (TRIBUTE, F6; HUD-013, manual "The Tribute Screen"): an amount of gold, food, wood and stone for
/// each other kingdom; a left click raises an amount by <see cref="Step"/>, a right click lowers it (the step is ours,
/// ⚠ UNVERIFIED); SEND sends them all. The amounts of one resource never add up to more than the stockpile.
/// </summary>
public sealed class TributeScreen
{
    private readonly int _me;
    private readonly int[] _stock;
    private readonly int[] _resources;
    private readonly List<int> _recipients = [];
    private readonly Dictionary<(int Slot, int Column), int> _amounts = [];

    /// <summary>Creates the screen.</summary>
    /// <param name="frame">The newest frame.</param>
    /// <param name="me">The player's slot.</param>
    /// <param name="resources">The resource type ids of the columns (gold, food, wood, stone).</param>
    /// <param name="step">How much a click adds or takes.</param>
    public TributeScreen(GameFrame frame, int me, IReadOnlyList<int> resources, int step = 100)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(resources);
        _me = me;
        Step = Math.Max(1, step);
        _resources = [.. resources];
        _stock = new int[_resources.Length];
        for (int c = 0; c < _resources.Length; c++)
        {
            _stock[c] = (uint)me < (uint)frame.Players.Length && (uint)_resources[c] < 8 ? frame.Players[me].Resources[_resources[c]] : 0;
        }

        for (int p = 0; p < frame.Players.Length; p++)
        {
            if (p != me && !frame.Players[p].Defeated)
            {
                _recipients.Add(p);
            }
        }
    }

    /// <summary>The kingdoms that can receive a tribute, by slot.</summary>
    public IReadOnlyList<int> Recipients => _recipients;

    /// <summary>The resource type ids of the columns.</summary>
    public IReadOnlyList<int> Resources => _resources;

    /// <summary>How much a click adds or takes.</summary>
    public int Step { get; }

    /// <summary>The amount of column <paramref name="column"/> for kingdom <paramref name="slot"/>.</summary>
    public int Amount(int slot, int column) => _amounts.TryGetValue((slot, column), out int a) ? a : 0;

    /// <summary>A click on an amount: <paramref name="direction"/> +1 raises it, -1 lowers it, within what is left of the stockpile.</summary>
    public void Click(int slot, int column, int direction)
    {
        if (!_recipients.Contains(slot) || (uint)column >= (uint)_resources.Length || direction == 0)
        {
            return;
        }

        int others = 0;
        foreach (int r in _recipients)
        {
            others += r == slot ? 0 : Amount(r, column);
        }

        int room = Math.Max(0, _stock[column] - others);
        int next = Math.Clamp(Amount(slot, column) + (Math.Sign(direction) * Step), 0, room);
        _amounts[(slot, column)] = next;
    }

    /// <summary>SEND: a tribute order for every amount above zero; null when there is none.</summary>
    public CommandList? Send()
    {
        var list = new CommandList();
        foreach (int slot in _recipients)
        {
            for (int c = 0; c < _resources.Length; c++)
            {
                if (Amount(slot, c) > 0)
                {
                    list.Add(CommandKind.Tribute, x: slot, arg0: _resources[c], arg1: Amount(slot, c));
                }
            }
        }

        return list.Count > 0 ? list : null;
    }

    /// <summary>The player's slot.</summary>
    public int Player => _me;
}
