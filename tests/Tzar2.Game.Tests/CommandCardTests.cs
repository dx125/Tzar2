using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Ids;
using Tzar2.Game.Input;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Xunit;

namespace Tzar2.Game.Tests;

public sealed class CommandCardTests : IDisposable
{
    private static readonly Lazy<Ruleset> Rules = new(() => SyntheticLegacyContent.CompileRuleset());

    private readonly GameSession _session = new(SessionTests.Setup() with { ComputerAi = false }, Rules.Value, fog: false);
    private readonly TestCards _cards = new(Rules.Value);

    public void Dispose() => _session.Dispose();

    [Fact]
    [Trait("Rule", "HUD-001")]
    public void A_button_shows_its_price_its_time_and_why_it_is_greyed_out()
    {
        EntityId castle = Own("euro.castle");
        _cards.Menus["e_castle"] = [Train("peon", 'P'), Train("knight", 'K')];
        var card = new CommandCard(Rules.Value, _cards, 0);

        IReadOnlyList<CardSlot?> slots = card.Build(Context([castle]));

        CardSlot peon = slots[0]!.Value;
        EntityTraits peonTraits = Rules.Value.EntityTypes[Rules.Value.EntityTypeId("euro.peon")].Traits;
        Assert.True(peon.Enabled);
        Assert.Contains(peon.Price!, p => p.Icon == "art/icons/resfood.t2img" && p.Value == peonTraits.Cost[0].ToString(System.Globalization.CultureInfo.InvariantCulture) && !p.Short);
        Assert.Equal($"{(peonTraits.BuildTimeMs + 500) / 1000} s", peon.Price!.Last().Value);
        CardSlot knight = slots[1]!.Value;
        Assert.False(knight.Enabled);
        Assert.StartsWith("Requires: ", knight.Detail, StringComparison.Ordinal);
        Assert.Equal("Create knight: " + knight.Detail, card.Press(Context([castle]), knight).Message);
    }

    [Fact]
    [Trait("Rule", "PROD-001")]
    [Trait("Rule", "PROD-003")]
    public void Queued_units_count_on_their_button_a_right_click_takes_one_off_and_shift_orders_five()
    {
        EntityId castle = Own("euro.castle");
        ushort peon = (ushort)Rules.Value.EntityTypeId("euro.peon");
        _cards.Menus["e_castle"] = [Train("peon", 'P')];
        var card = new CommandCard(Rules.Value, _cards, 0);
        var batch = new CommandList();
        batch.Add(CommandKind.Train, [castle], arg0: peon, arg1: 3);
        _session.Submit(batch);
        _session.Step(2);

        CardSlot slot = card.Build(Context([castle]))[0]!.Value;
        Assert.Equal(3, slot.Count);

        Command cancel = Single(card.Press(Context([castle]), slot, right: true));
        Assert.Equal((CommandKind.Cancel, -1, peon + 1), (cancel.Kind, cancel.Arg0, cancel.Arg1));
        Command five = Single(card.Press(Context([castle]), slot, shift: true));
        Assert.Equal((CommandKind.Train, (int)peon, 5), (five.Kind, five.Arg0, five.Arg1));
    }

    [Fact]
    [Trait("Rule", "HUD-019")]
    public void More_orders_than_slots_go_on_pages_of_seven_with_a_page_button_and_hotkeys_reach_them_all()
    {
        EntityId peasant = Own("euro.peon");
        _cards.Menus["e_peon"] = [.. Enumerable.Range(0, 9).Select(i => new CardButton($"o{i}", (char)('A' + i), CardAction.Stop, string.Empty, i, $"Order {i}"))];
        var card = new CommandCard(Rules.Value, _cards, 0);

        IReadOnlyList<CardSlot?> first = card.Build(Context([peasant]));
        Assert.Equal(CommandCard.Slots, first.Count);
        Assert.Equal(["o0", "o1", "o2", "o3", "o4", "o5", "o6"], first.Take(7).Select(s => s!.Value.Button.Order));
        Assert.Equal(CardAction.Page, first[7]!.Value.Button.Action);
        Assert.Equal(9, card.WholeCard.Count);

        Assert.Same(CardPress.Nothing, card.Press(Context([peasant]), first[7]!.Value));
        IReadOnlyList<CardSlot?> second = card.Build(Context([peasant]));
        Assert.Equal(["o7", "o8"], second.Take(2).Select(s => s!.Value.Button.Order));
        Assert.Null(second[2]);
        Assert.Equal("page 2 of 2", second[7]!.Value.Detail);
    }

    [Fact]
    public void A_submenu_opens_on_the_card_and_back_or_a_new_selection_returns_to_the_entitys_own()
    {
        EntityId peasant = Own("euro.peon");
        _cards.Menus["e_peon"] = [new CardButton("build", 'B', CardAction.Submenu, "e_build", 1, "Build")];
        _cards.Menus["e_build"] = [new CardButton("e_house", 'H', CardAction.Build, "house", 2, "Build House")];
        var card = new CommandCard(Rules.Value, _cards, 0);

        CardSlot build = card.Build(Context([peasant]))[0]!.Value;
        Assert.True(build.Enabled);
        card.Press(Context([peasant]), build);
        Assert.Equal("e_build", card.OpenMenu);
        CardSlot house = card.Build(Context([peasant]))[0]!.Value;
        Assert.Equal((ushort)Rules.Value.EntityTypeId("euro.house"), card.Press(Context([peasant]), house).Placing);

        Assert.True(card.Back());
        Assert.Null(card.OpenMenu);
        Assert.False(card.Back());
        card.Press(Context([peasant]), build);
        card.Reset();
        Assert.Equal("build", card.Build(Context([peasant]))[0]!.Value.Button.Order);
    }

    [Fact]
    [Trait("Rule", "CTRL-029")]
    [Trait("Rule", "HUD-034")]
    public void In_the_build_menu_the_letters_of_its_categories_buildings_work_and_each_category_lists_them()
    {
        // MENU.INI marks the build menu "capturekeys": its categories (guilds, common, rare) have no letter of their own,
        // and the letters of their buildings work from the build menu. The owner's fourth play-test asked to see them.
        EntityId peasant = Own("euro.peon");
        _cards.Menus["e_peon"] = [new CardButton("build", 'B', CardAction.Submenu, "e_build", 1, "Build")];
        _cards.Menus["e_build"] = [new CardButton("e_common", '\0', CardAction.Submenu, "e_common", 2, "Common buildings"), new CardButton("e_barrack", 'B', CardAction.Build, "barrack", 3, "Build Barracks")];
        _cards.Menus["e_common"] = [new CardButton("e_house", 'H', CardAction.Build, "house", 4, "Build House")];
        _cards.Capturing.Add("e_build");
        var card = new CommandCard(Rules.Value, _cards, 0);

        card.Press(Context([peasant]), card.Build(Context([peasant]))[0]!.Value);
        CardSlot common = card.Build(Context([peasant]))[0]!.Value;
        Assert.Equal("Keys: H Build House.", common.Description);
        CardSlot house = Assert.IsType<CardSlot>(card.SlotOfKey(Context([peasant]), 'H'));
        Assert.Equal((ushort)Rules.Value.EntityTypeId("euro.house"), card.Press(Context([peasant]), house).Placing);
        Assert.Equal("e_barrack", card.SlotOfKey(Context([peasant]), 'B')!.Value.Button.Order);
        Assert.Null(card.SlotOfKey(Context([peasant]), 'Q'));

        _cards.Capturing.Clear(); // a menu that does not capture: only its own letters
        card.Build(Context([peasant]));
        Assert.Null(card.SlotOfKey(Context([peasant]), 'H'));
    }

    [Fact]
    public void An_order_that_needs_a_target_waits_for_it_and_is_highlighted()
    {
        EntityId peasant = Own("euro.peon");
        _cards.Menus["e_peon"] = [new CardButton("move", 'M', CardAction.Move, string.Empty, 1, "Move"), new CardButton("stop", 'S', CardAction.Stop, string.Empty, 2, "Stop")];
        var card = new CommandCard(Rules.Value, _cards, 0);

        CardSlot move = card.Build(Context([peasant]))[0]!.Value;
        Assert.Equal(OrderMode.Move, card.Press(Context([peasant]), move).Mode);
        Assert.True(card.Build(Context([peasant]) with { Mode = OrderMode.Move })[0]!.Value.Active);
        Command stop = Single(card.Press(Context([peasant]), card.Build(Context([peasant]))[1]!.Value));
        Assert.Equal(CommandKind.Stop, stop.Kind);
    }

    [Fact]
    public void Another_kingdoms_selection_and_placing_a_building_show_no_card()
    {
        _cards.Menus["e_castle"] = [Train("peon", 'P')];
        var card = new CommandCard(Rules.Value, _cards, 0);

        Assert.Empty(card.Build(Context([Own("euro.castle", owner: 1)]) with { IsOwn = false }));
        Assert.Empty(card.Build(Context([Own("euro.castle")]) with { Placing = true }));
        Assert.Empty(card.Build(Context([])));
    }

    private static CardButton Train(string cls, char key) => new($"e_{cls}", key, CardAction.Train, cls, 1, $"Create {cls}");

    private static Command Single(CardPress press)
    {
        Assert.NotNull(press.Commands);
        Assert.Equal(1, press.Commands!.Count);
        return press.Commands[0];
    }

    private CardContext Context(EntityId[] selection) => new(_session.AcquireFrame(out _) ?? Step(), selection);

    private GameFrame Step()
    {
        _session.Step(1);
        return _session.AcquireFrame(out _)!;
    }

    private EntityId Own(string key, byte owner = 0)
    {
        World world = _session.Simulation.World;
        return world.Identities.Entities.ToArray().First(e => world.Identities.Get(e).Owner == owner && world.TypeOf(e).Key == key);
    }

    // The card's source for the tests: menus given by each test, names and civilizations from the ruleset.
    private sealed class TestCards(Ruleset rules) : ICardSource
    {
        public Dictionary<string, List<CardButton>> Menus { get; } = [];

        public HashSet<string> Capturing { get; } = [];

        public IReadOnlyList<CardButton> Card(string? menu) => menu is not null && Menus.TryGetValue(menu, out List<CardButton>? b) ? b : [];

        public bool CapturesKeys(string? menu) => menu is not null && Capturing.Contains(menu);

        public string? Menu(ushort typeId)
        {
            string key = rules.EntityTypes[typeId].Key;
            return key.StartsWith("euro.", StringComparison.Ordinal) ? "e_" + key[5..] : null;
        }

        public string Civilization(ushort typeId) => rules.EntityTypes[typeId].Traits.Civilization;

        public string Name(ushort typeId) => rules.EntityTypes[typeId].Key;

        public int TypeOfClass(string civilization, string cls)
            => rules.EntityTypeId(civilization + "." + cls) is >= 0 and var id ? id : rules.EntityTypeId("comm." + cls);

        public int TrainImage(ushort typeId) => 1;

        public string Text(string section, string key, string fallback) => fallback;
    }
}
