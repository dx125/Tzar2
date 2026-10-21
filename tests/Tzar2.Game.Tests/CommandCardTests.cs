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
using Tzar2.Sim.Components;
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
    [Trait("Rule", "ITEM-008")]
    public void The_magic_workshop_makes_an_item_of_the_kind_and_gold_its_dialog_asks_for()
    {
        World world = _session.Simulation.World;
        EntityId workshop = world.PlaceStructure((ushort)Rules.Value.EntityTypeId("arab.magicworkshop"), 0, 30, 30);
        _session.Step(1);
        _cards.Menus["arab_magicworkshop"] = [new CardButton("build_item", 'I', CardAction.Craft, string.Empty, 249, "Build item")];
        _cards.MenuOverride[(ushort)Rules.Value.EntityTypeId("arab.magicworkshop")] = "arab_magicworkshop";
        var card = new CommandCard(Rules.Value, _cards, 0);

        CardSlot build = card.Build(Context([workshop]))[0]!.Value;
        Assert.True(build.Enabled);
        Assert.Equal(CardDialogKind.Craft, card.Press(Context([workshop]), build).Dialog);
        ushort book = (ushort)Rules.Value.EntityTypeId("comm.item_magic");
        Assert.Contains(book, card.CraftableItems(Context([workshop])));
        Assert.Equal(4 * card.CraftPricePerPower((ushort)Rules.Value.EntityTypeId("comm.item")), card.CraftPricePerPower(book));

        CommandList craft = card.Craft(Context([workshop]), book, 400)!;
        Assert.Equal((CommandKind.CraftItem, (int)book, 400), (craft[0].Kind, craft[0].Arg0, craft[0].Arg1));
        _session.Submit(craft);
        _session.Step(2);
        CardSlot busy = card.Build(Context([workshop]))[0]!.Value;
        Assert.Equal((false, "making an item"), (busy.Enabled, busy.Detail));
        Assert.Null(card.Craft(Context([workshop]), book, 400));
    }

    [Fact]
    [Trait("Rule", "ORD-015")]
    [Trait("Rule", "HUD-016")]
    public void A_unit_picked_in_the_castle_list_emerges_alone_at_the_clicked_place_and_emerge_alone_lets_all_out()
    {
        World world = _session.Simulation.World;
        EntityId castle = Own("euro.castle");
        EntityId[] peasants = [.. world.Identities.Entities.ToArray().Where(e => world.Identities.Get(e).Owner == 0 && world.TypeOf(e).Key == "euro.peon").Take(2)];
        var board = new CommandList();
        board.Add(CommandKind.Board, peasants, target: castle);
        _session.Submit(board);
        for (int i = 0; i < 600 && world.Holders.Get(castle).Count < peasants.Length; i++)
        {
            _session.Step(1);
        }

        _cards.Menus["e_castle"] = [new CardButton("emerge", 'E', CardAction.Unload, string.Empty, 8, "Emerge")];
        var card = new CommandCard(Rules.Value, _cards, 0);
        Assert.Equal(peasants.Order(), card.Inside(Context([castle])).Select(h => h.Unit).Order());
        GameFrame frame = Context([castle]).Frame;
        FrameEntity held = frame.Entities.ToArray().Single(e => e.Id == castle);
        Assert.Equal(2, held.Inside);
        Assert.True(held.InsideMax >= 2);
        CardSlot emerge = card.Build(Context([castle]))[0]!.Value;
        Assert.True(emerge.Enabled);

        // Picked in the list: Emerge waits for the place, and only that unit comes out there.
        CardPress press = card.Press(Context([castle]) with { Picked = peasants[1] }, emerge);
        Assert.Equal(OrderMode.Unload, press.Mode);
        Assert.True(card.Build(Context([castle]) with { Mode = OrderMode.Unload })[0]!.Value.Active);
        EntityInfo info = CommandCard.Info(Rules.Value, frame, held, 0);
        var batch = new CommandList();
        foreach (PlannedCommand c in OrderResolver.WithMode(OrderMode.Unload, [info], null, held.X + 4096, held.Y, KeyMods.None, 0, press.ModeArgument))
        {
            c.AddTo(batch);
        }

        _session.Submit(batch);
        _session.Step(2);
        Assert.False(world.IsHeld(peasants[1]));
        Assert.True(world.IsHeld(peasants[0]));

        // Nothing picked: everyone comes out at once; then the order is greyed out.
        Command all = Single(card.Press(Context([castle]), emerge));
        Assert.Equal((CommandKind.Unload, EntityId.None), (all.Kind, all.Target));
        _session.Submit(card.Press(Context([castle]), emerge).Commands!);
        _session.Step(2);
        Assert.False(world.IsHeld(peasants[0]));
        CardSlot empty = card.Build(Context([castle]))[0]!.Value;
        Assert.Equal((false, "nobody inside"), (empty.Enabled, empty.Detail));
        Assert.Empty(card.Inside(Context([castle])));
    }

    [Fact]
    [Trait("Rule", "AI-004")]
    [Trait("Rule", "HUD-033")]
    public void With_nothing_selected_the_kingdom_card_shows_and_AI_assistance_is_asked_for_and_lit()
    {
        _cards.Menus["kingdom"] = [new CardButton("nextidle", 'N', CardAction.NextIdle, string.Empty, 82, "Next idle peasant")];
        _cards.Menus["kingdom_ai"] = [.. _cards.Menus["kingdom"], new CardButton("view_ai", 'A', CardAction.Submenu, "aiassist", 250, "AI Assistance")];
        _cards.Menus["aiassist"] = [new CardButton("set_ai_none", 'N', CardAction.AiAssist, "0", 268, "NO Assistance"), new CardButton("set_ai_eco", 'E', CardAction.AiAssist, "1", 267, "ECONOMY Assistance")];
        var card = new CommandCard(Rules.Value, _cards, 0);
        CardContext none = Context([]) with { IsOwn = false };

        Assert.Equal(["nextidle"], card.Build(none).Select(s => s!.Value.Button.Order));
        Assert.True(card.Press(none, card.Build(none)[0]!.Value).NextIdlePeasant);
        CardContext assist = none with { AiAssist = true };
        CardSlot view = card.Build(assist)[1]!.Value;
        Assert.Equal("view_ai", view.Button.Order);
        card.Press(assist, view);
        IReadOnlyList<CardSlot?> modes = card.Build(assist);
        Assert.True(modes[0]!.Value.Active); // no assistance yet

        Command eco = Single(card.Press(assist, modes[1]!.Value));
        Assert.Equal((CommandKind.SetAiAssist, 1), (eco.Kind, eco.Arg0));
        _session.Submit(card.Press(assist, modes[1]!.Value).Commands!);
        _session.Step(2);
        Assert.True((_session.Simulation.World.Players[0].Flags & PlayerFlags.AssistEconomy) != 0);
        CardContext after = Context([]) with { IsOwn = false, AiAssist = true };
        Assert.Equal((false, true), (card.Build(after)[0]!.Value.Active, card.Build(after)[1]!.Value.Active));
    }

    [Fact]
    [Trait("Rule", "HUD-033")]
    public void The_kingdom_lists_its_carried_items_and_a_press_selects_the_carrier_and_an_empty_list_is_greyed_out()
    {
        using var session = new GameSession(SessionTests.Setup() with { ComputerAi = false, CheatsAllowed = true }, Rules.Value, fog: false);
        World world = session.Simulation.World;
        EntityId footman = world.Identities.Entities.ToArray().First(e => world.Identities.Get(e).Owner == 0 && world.TypeOf(e).Key == "euro.footman");
        Position at = world.Positions.Get(footman);
        ushort potion = (ushort)Rules.Value.EntityTypeId("comm.item_health");
        var spawn = new CommandList();
        spawn.Add(CommandKind.DebugSpawn, x: at.X + 1024, y: at.Y, arg0: potion, arg1: 1);
        session.Submit(spawn);
        session.Step(1);
        EntityId item = world.Identities.Entities.ToArray().First(e => world.Identities.Get(e).TypeId == potion);
        var pick = new CommandList();
        pick.Add(CommandKind.PickItem, [footman], target: item);
        session.Submit(pick);
        for (int i = 0; i < 400 && !world.IsHeld(item); i++)
        {
            session.Step(1);
        }

        session.Step(1);
        _cards.Menus["kingdom"] = [new CardButton("view_spys", 'S', CardAction.KingdomList, "spies", 107, "Spies"), new CardButton("view_items", 'I', CardAction.KingdomList, "items", 120, "Items")];
        var card = new CommandCard(Rules.Value, _cards, 0);
        CardContext none = new(session.AcquireFrame(out _)!, [], IsOwn: false);
        IReadOnlyList<CardSlot?> kingdom = card.Build(none);
        Assert.Equal((false, "none yet"), (kingdom[0]!.Value.Enabled, kingdom[0]!.Value.Detail));
        Assert.True(kingdom[1]!.Value.Enabled);

        card.Press(none, kingdom[1]!.Value);
        CardSlot entry = Assert.Single(card.Build(none), s => s is not null)!.Value;
        Assert.Contains(Rules.Value.EntityTypes[potion].Key, entry.Button.Tooltip, StringComparison.Ordinal);
        Assert.Equal(footman, card.Press(none, entry).Select);
    }

    [Fact]
    [Trait("Rule", "MAG-012")]
    [Trait("Rule", "HUD-022")]
    public void A_spy_impersonates_a_clicked_enemy_others_see_the_disguise_its_owner_its_true_kingdom_and_it_reveals_itself()
    {
        Ruleset rules = SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions { OverrideTexts = [new("test.yaml", "euro.footman: { Impersonator: {} }")] });
        const int Tile = Tzar2.Sim.Map.MapState.UnitsPerTile;
        MapData map = Tzar2.Sim.Scenarios.ScenarioMaps.WithObjects(Tzar2.Sim.Scenarios.ScenarioMaps.Build("spy", 24, 12, new byte[24 * 12], []),
            [new MapObject("euro.footman", 0, (4 * Tile) + (Tile / 2), (4 * Tile) + (Tile / 2)), new MapObject("euro.footman", 1, (8 * Tile) + (Tile / 2), (4 * Tile) + (Tile / 2))]);
        using var sim = new Simulation(rules, map, new SimulationConfig { PlayerCount = 2, MatchSeed = 5 });
        World world = sim.World;
        EntityId spy = world.Identities.Entities.ToArray().First(e => world.Identities.Get(e).Owner == 0);
        EntityId look = world.Identities.Entities.ToArray().First(e => world.Identities.Get(e).Owner == 1);
        var stances = new CommandList();
        stances.Add(CommandKind.SetStance, [spy], arg0: (int)CombatStance.HoldFire);
        stances.Add(CommandKind.SetStance, [look], arg0: (int)CombatStance.HoldFire, player: 1);
        sim.Tick(stances);
        _cards.Menus["e_footman"] = [new CardButton("stop", 'S', CardAction.Stop, string.Empty, 1, "Stop")];
        _cards.Menus["spy_add"] = [new CardButton("impersonate", 'I', CardAction.Impersonate, string.Empty, 237, "Impersonate"), new CardButton("reveal_self", 'V', CardAction.Reveal, string.Empty, 236, "Reveal")];
        var card = new CommandCard(rules, _cards, 0);
        GameFrame Frame(byte viewer)
        {
            var frame = new GameFrame();
            FrameBuilder.Fill(frame, world, viewer, MatchOutcome.None);
            return frame;
        }

        // The spy's card adds impersonate (reveal waits for a disguise); a click on the enemy footman orders it.
        CardContext context = new(Frame(0), [spy]);
        Assert.Equal(["stop", "impersonate"], card.Build(context).Select(s => s!.Value.Button.Order));
        Assert.Equal(OrderMode.Impersonate, card.Press(context, card.Build(context)[1]!.Value).Mode);
        FrameEntity target = Frame(0).Entities.ToArray().Single(e => e.Id == look);
        var order = new CommandList();
        foreach (PlannedCommand c in OrderResolver.WithMode(OrderMode.Impersonate, [CommandCard.Info(rules, context.Frame, context.Frame.Entities.ToArray().Single(e => e.Id == spy), 0)],
            CommandCard.Info(rules, context.Frame, target, 0), target.X, target.Y, KeyMods.None, 0))
        {
            c.AddTo(order);
        }

        sim.Tick(order);
        for (int i = 0; i < 400 && !world.Disguises.Has(spy); i++)
        {
            sim.Tick(new CommandList());
        }

        // The enemy sees one of its own footmen; its owner sees the footman's look in its own colour (1.05).
        FrameEntity seen = Frame(1).Entities.ToArray().Single(e => e.Id == spy);
        Assert.Equal(((byte)1, world.Identities.Get(look).TypeId, -1), (seen.Owner, seen.TypeId, seen.LooksLike));
        FrameEntity own = Frame(0).Entities.ToArray().Single(e => e.Id == spy);
        Assert.Equal(((byte)0, (int)world.Identities.Get(look).TypeId), (own.Owner, own.LooksLike));

        // Disguised: reveal instead of impersonate, and it works.
        context = new(Frame(0), [spy]);
        Assert.Equal(["stop", "reveal_self"], card.Build(context).Select(s => s!.Value.Button.Order));
        sim.Tick(card.Press(context, card.Build(context)[1]!.Value).Commands!);
        Assert.False(world.Disguises.Has(spy));
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

        public Dictionary<ushort, string> MenuOverride { get; } = [];

        public string? KingdomMenu(bool aiAssist) => aiAssist ? "kingdom_ai" : "kingdom";

        public IReadOnlyList<CardButton> AbilityButtons(string ability) => ability == "impersonate" ? Card("spy_add") : [];

        public string? MenuVariant(string? menu, string state) => menu is not null && Menus.ContainsKey(menu + "_" + state) ? menu + "_" + state : null;

        public IReadOnlyList<CardButton> Card(string? menu) => menu is not null && Menus.TryGetValue(menu, out List<CardButton>? b) ? b : [];

        public bool CapturesKeys(string? menu) => menu is not null && Capturing.Contains(menu);

        public string? Menu(ushort typeId)
        {
            if (MenuOverride.TryGetValue(typeId, out string? menu))
            {
                return menu;
            }

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
