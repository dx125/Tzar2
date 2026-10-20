using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Game.Input;
using Tzar2.Sim.Commands;
using Xunit;

namespace Tzar2.Game.Tests;

public sealed class SelectionTests
{
    private const byte Me = 0;

    internal static EntityInfo Unit(int id, ushort type = 1, byte owner = Me, bool military = true, int state = 0, int health = 10, int max = 10, bool harvester = false)
        => new(EntityId.Create(id, 1), type, owner, EntityCategory.Unit, military, state, health, max, harvester, harvester, true, false, false, false, true);

    internal static EntityInfo Building(int id, ushort type = 9, byte owner = Me, bool site = false, bool producer = true)
        => new(EntityId.Create(id, 1), type, owner, EntityCategory.Building, false, 0, 100, 100, false, false, false, producer, false, site, false);

    internal static EntityInfo Tree(int id) => new(EntityId.Create(id, 1), 20, 0xFF, EntityCategory.Resource, false, 0, 0, 0, false, false, false, false, true, false, false);

    [Fact]
    public void Click_replaces_and_shift_click_toggles_own_units()
    {
        var s = new SelectionModel(Me);
        s.Click(Unit(1), shift: false);
        s.Click(Unit(2), shift: true);
        Assert.Equal([Id(1), Id(2)], s.Selected);
        s.Click(Unit(1), shift: true);
        Assert.Equal([Id(2)], s.Selected);
        s.Click(null, shift: false);
        Assert.Empty(s.Selected);
        Assert.False(s.IsOwn);
    }

    [Fact]
    public void Enemies_are_selected_alone_and_never_added()
    {
        var s = new SelectionModel(Me);
        s.Click(Unit(5, owner: 1), shift: false);
        Assert.Equal([Id(5)], s.Selected);
        Assert.False(s.IsOwn);
        s.Click(Unit(1), shift: true);
        Assert.Equal([Id(1)], s.Selected);
        s.Click(Unit(6, owner: 1), shift: true);
        Assert.Equal([Id(6)], s.Selected);
    }

    [Fact]
    public void A_drag_box_prefers_own_units_then_one_own_building()
    {
        var s = new SelectionModel(Me);
        s.Box([Building(9), Unit(1), Unit(2, owner: 1), Unit(3)], shift: false);
        Assert.Equal([Id(1), Id(3)], s.Selected);
        s.Box([Building(9), Unit(2, owner: 1)], shift: false);
        Assert.Equal([Id(9)], s.Selected);
        s.Box([Unit(2, owner: 1), Tree(7)], shift: false);
        Assert.Empty(s.Selected);
        s.Box([Unit(1)], shift: false);
        s.Box([Unit(3)], shift: true);
        Assert.Equal([Id(1), Id(3)], s.Selected);
    }

    [Fact]
    public void Double_click_variants_follow_the_original()
    {
        EntityInfo[] screen =
        [
            Unit(1, type: 1, state: 0), Unit(2, type: 1, state: 1), Unit(3, type: 2, state: 0),
            Unit(4, type: 3, military: false, state: 0, harvester: true), Unit(5, type: 1, owner: 1),
        ];
        var s = new SelectionModel(Me);
        s.DoubleClick(screen[0], screen, ctrl: false, alt: false);
        Assert.Equal([Id(1), Id(2)], s.Selected);
        s.DoubleClick(screen[0], screen, ctrl: true, alt: false);
        Assert.Equal([Id(1)], s.Selected);
        s.DoubleClick(screen[0], screen, ctrl: false, alt: true);
        Assert.Equal([Id(1), Id(2), Id(3)], s.Selected);
        s.DoubleClick(screen[0], screen, ctrl: true, alt: true);
        Assert.Equal([Id(1), Id(3)], s.Selected);
        s.DoubleClick(screen[3], screen, ctrl: false, alt: true);
        Assert.Equal([Id(4)], s.Selected);
    }

    [Fact]
    public void Control_groups_assign_select_clear_and_centre_on_the_second_press()
    {
        var s = new SelectionModel(Me);
        s.Box([Unit(1), Unit(2)], shift: false);
        s.AssignGroup(1);
        s.Box([Unit(2), Unit(3)], shift: false);
        s.AssignGroup(2);
        Assert.Equal([Id(1)], s.Group(1));
        Assert.Equal(2, s.GroupOf(Id(2)));
        Assert.False(s.SelectGroup(1, _ => true));
        Assert.Equal([Id(1)], s.Selected);
        Assert.True(s.SelectGroup(1, _ => true));
        Assert.False(s.SelectGroup(2, id => id != Id(3)));
        Assert.Equal([Id(2)], s.Selected);
        s.ClearGroup();
        Assert.Equal(0, s.GroupOf(Id(2)));
        s.SelectUngrouped([Unit(1), Unit(2), Unit(4), Unit(5, owner: 1)]);
        Assert.Equal([Id(2), Id(4)], s.Selected);
    }

    [Fact]
    public void Health_filters_split_at_the_average_or_half()
    {
        EntityInfo[] screen = [Unit(1, health: 2), Unit(2, health: 6), Unit(3, health: 10), Unit(4, owner: 1, health: 1)];
        var s = new SelectionModel(Me);
        s.SelectByHealth(screen, above: true);
        Assert.Equal([Id(2), Id(3)], s.Selected);
        s.Box([screen[0], screen[1], screen[2]], shift: false);
        s.SelectByHealth(screen, above: false);
        Assert.Equal([Id(1)], s.Selected);
    }

    [Fact]
    public void Tab_cycles_the_focus_and_prune_drops_the_dead()
    {
        var s = new SelectionModel(Me);
        s.Box([Unit(1), Unit(2), Unit(3)], shift: false);
        s.CycleFocus();
        s.CycleFocus();
        Assert.Equal(2, s.Focus);
        s.Prune(id => id != Id(3));
        Assert.Equal([Id(1), Id(2)], s.Selected);
        Assert.Equal(1, s.Focus);
        s.Prune(_ => false);
        Assert.False(s.IsOwn);
    }

    internal static EntityId Id(int index) => EntityId.Create(index, 1);
}

public sealed class OrderTests
{
    private const byte Me = 0;

    [Fact]
    public void Right_click_on_ground_moves_with_the_modifier_flags()
    {
        IReadOnlyList<PlannedCommand> c = OrderResolver.RightClick([SelectionTests.Unit(1), SelectionTests.Unit(2)], null, 100, 200, KeyMods.None, Me);
        PlannedCommand move = Assert.Single(c);
        Assert.Equal(CommandKind.Move, move.Kind);
        Assert.Equal((100, 200), (move.X, move.Y));
        Assert.Equal(CommandFlags.None, move.Flags);
        Assert.Equal(CommandFlags.Ctrl, Assert.Single(OrderResolver.RightClick([SelectionTests.Unit(1)], null, 0, 0, KeyMods.Ctrl, Me)).Flags);
        Assert.Equal(CommandFlags.Ctrl | CommandFlags.Queue, Assert.Single(OrderResolver.RightClick([SelectionTests.Unit(1)], null, 0, 0, KeyMods.Ctrl | KeyMods.Shift, Me)).Flags);
        Assert.Equal(CommandFlags.Alt, Assert.Single(OrderResolver.RightClick([SelectionTests.Unit(1)], null, 0, 0, KeyMods.Alt, Me)).Flags);
    }

    [Fact]
    public void Right_click_on_an_enemy_attacks_with_armed_units()
    {
        EntityInfo enemy = SelectionTests.Unit(9, owner: 1);
        PlannedCommand attack = Assert.Single(OrderResolver.RightClick([SelectionTests.Unit(1), SelectionTests.Unit(2)], enemy, 5, 5, KeyMods.None, Me));
        Assert.Equal(CommandKind.Attack, attack.Kind);
        Assert.Equal(enemy.Id, attack.Target);
        Assert.Equal(2, attack.Entities.Count);
    }

    [Fact]
    public void Nature_creatures_and_buildings_are_attacked_but_resources_are_not()
    {
        EntityInfo bear = SelectionTests.Unit(9, owner: 0xFF);
        Assert.Equal(CommandKind.Attack, Assert.Single(OrderResolver.RightClick([SelectionTests.Unit(1)], bear, 0, 0, KeyMods.None, Me)).Kind);
        EntityInfo tower = SelectionTests.Building(8, owner: 0xFF);
        Assert.Equal(CommandKind.Attack, Assert.Single(OrderResolver.RightClick([SelectionTests.Unit(1)], tower, 0, 0, KeyMods.None, Me)).Kind);
        Assert.Equal(CommandKind.Move, Assert.Single(OrderResolver.RightClick([SelectionTests.Unit(1)], SelectionTests.Tree(7), 0, 0, KeyMods.None, Me)).Kind);
    }

    [Fact]
    public void Right_click_on_a_resource_sends_harvesters_and_moves_the_rest()
    {
        IReadOnlyList<PlannedCommand> c = OrderResolver.RightClick([SelectionTests.Unit(1, harvester: true), SelectionTests.Unit(2)], SelectionTests.Tree(7), 5, 5, KeyMods.None, Me);
        Assert.Equal(2, c.Count);
        Assert.Equal(CommandKind.Harvest, c[0].Kind);
        Assert.Equal([SelectionTests.Id(1)], c[0].Entities);
        Assert.Equal(CommandKind.Move, c[1].Kind);
        Assert.Equal([SelectionTests.Id(2)], c[1].Entities);
    }

    [Fact]
    public void Right_click_on_an_own_site_sends_builders()
    {
        PlannedCommand help = OrderResolver.RightClick([SelectionTests.Unit(1, harvester: true)], SelectionTests.Building(4, site: true), 0, 0, KeyMods.None, Me)[0];
        Assert.Equal(CommandKind.Build, help.Kind);
        Assert.Equal(SelectionTests.Id(4), help.Target);
    }

    [Fact]
    public void Right_click_on_an_own_damaged_building_sends_builders_to_repair_it()
    {
        EntityInfo damaged = SelectionTests.Building(4) with { Health = 60, IsRepairable = true };
        IReadOnlyList<PlannedCommand> c = OrderResolver.RightClick([SelectionTests.Unit(1, harvester: true), SelectionTests.Unit(2)], damaged, 0, 0, KeyMods.None, Me);
        Assert.Equal((CommandKind.Repair, damaged.Id), (c[0].Kind, c[0].Target));
        Assert.Equal([SelectionTests.Id(1)], c[0].Entities);
        Assert.Equal(CommandKind.Move, c[1].Kind);

        // Full health or not repairable: builders just walk there.
        Assert.Equal(CommandKind.Move, Assert.Single(OrderResolver.RightClick([SelectionTests.Unit(1, harvester: true)], damaged with { Health = 100 }, 0, 0, KeyMods.None, Me)).Kind);
        Assert.Equal(CommandKind.Move, Assert.Single(OrderResolver.RightClick([SelectionTests.Unit(1, harvester: true)], damaged with { IsRepairable = false }, 0, 0, KeyMods.None, Me)).Kind);

        // The card's Repair order: only builders go, only to an own damaged object.
        PlannedCommand card = Assert.Single(OrderResolver.WithMode(OrderMode.Repair, [SelectionTests.Unit(1, harvester: true), SelectionTests.Unit(2)], damaged, 0, 0, KeyMods.None, Me));
        Assert.Equal([SelectionTests.Id(1)], card.Entities);
        Assert.Empty(OrderResolver.WithMode(OrderMode.Repair, [SelectionTests.Unit(1, harvester: true)], damaged with { Owner = 1 }, 0, 0, KeyMods.None, Me));
    }

    [Fact]
    public void Buildings_get_a_rally_point_and_enemies_get_nothing()
    {
        PlannedCommand rally = Assert.Single(OrderResolver.RightClick([SelectionTests.Building(3)], null, 7, 8, KeyMods.Ctrl, Me));
        Assert.Equal(CommandKind.SetRally, rally.Kind);
        Assert.Empty(OrderResolver.RightClick([SelectionTests.Building(3, site: true)], null, 7, 8, KeyMods.None, Me));
        Assert.Empty(OrderResolver.RightClick([SelectionTests.Unit(5, owner: 1)], null, 7, 8, KeyMods.None, Me));
    }

    [Fact]
    public void Card_orders_target_the_clicked_object_or_point()
    {
        EntityInfo[] sel = [SelectionTests.Unit(1)];
        Assert.Equal(CommandKind.Attack, OrderResolver.WithMode(OrderMode.Attack, sel, SelectionTests.Unit(9, owner: 1), 0, 0, KeyMods.None, Me)[0].Kind);
        PlannedCommand advance = OrderResolver.WithMode(OrderMode.Attack, sel, null, 3, 4, KeyMods.None, Me)[0];
        Assert.Equal((CommandKind.Move, CommandFlags.Ctrl), (advance.Kind, advance.Flags));
        Assert.Equal(CommandKind.Patrol, OrderResolver.WithMode(OrderMode.Patrol, sel, null, 3, 4, KeyMods.None, Me)[0].Kind);
        Assert.Equal(CommandKind.Follow, OrderResolver.WithMode(OrderMode.Guard, sel, SelectionTests.Unit(2), 3, 4, KeyMods.None, Me)[0].Kind);
        Assert.Empty(OrderResolver.WithMode(OrderMode.Harvest, sel, null, 3, 4, KeyMods.None, Me));
        Assert.Equal((CommandKind.Move, CommandFlags.Ctrl), OrderResolver.WithMode(OrderMode.MoveAdvance, sel, null, 3, 4, KeyMods.None, Me)[0] is var a ? (a.Kind, a.Flags) : default);
        Assert.Equal((CommandKind.Move, CommandFlags.Alt), OrderResolver.WithMode(OrderMode.MoveSneak, sel, null, 3, 4, KeyMods.None, Me)[0] is var n ? (n.Kind, n.Flags) : default);
        Assert.Equal(CommandKind.AttackGround, OrderResolver.WithMode(OrderMode.AttackGround, sel, null, 3, 4, KeyMods.None, Me)[0].Kind);
        Assert.Equal(CommandKind.Capture, OrderResolver.WithMode(OrderMode.Capture, sel, SelectionTests.Building(8, owner: 1), 3, 4, KeyMods.None, Me)[0].Kind);
        Assert.Empty(OrderResolver.WithMode(OrderMode.Capture, sel, SelectionTests.Building(8), 3, 4, KeyMods.None, Me));
        Assert.Empty(OrderResolver.WithMode(OrderMode.Capture, sel, SelectionTests.Unit(9, owner: 1), 3, 4, KeyMods.None, Me));

        // Spells (the mode's argument is the spell), bribery, items, and the cathedral's crusade.
        PlannedCommand cast = OrderResolver.WithMode(OrderMode.Spell, sel, SelectionTests.Unit(9, owner: 1), 3, 4, KeyMods.None, Me, argument: 7)[0];
        Assert.Equal((CommandKind.CastSpell, SelectionTests.Id(9), 7), (cast.Kind, cast.Target, cast.Arg0));
        PlannedCommand ground = OrderResolver.WithMode(OrderMode.Spell, sel, null, 3, 4, KeyMods.None, Me, argument: 7)[0];
        Assert.Equal((CommandKind.CastSpell, Core.Ids.EntityId.None, 3, 4), (ground.Kind, ground.Target, ground.X, ground.Y));
        Assert.Equal(CommandKind.Bribe, OrderResolver.WithMode(OrderMode.Bribe, sel, SelectionTests.Unit(9, owner: 1), 3, 4, KeyMods.None, Me)[0].Kind);
        Assert.Empty(OrderResolver.WithMode(OrderMode.Bribe, sel, SelectionTests.Unit(2), 3, 4, KeyMods.None, Me));
        Assert.Equal(CommandKind.PickItem, OrderResolver.WithMode(OrderMode.PickItem, sel, SelectionTests.Unit(9, owner: 1), 3, 4, KeyMods.None, Me)[0].Kind);
        Assert.Equal((CommandKind.UseItem, -1), OrderResolver.WithMode(OrderMode.UseItem, sel, null, 3, 4, KeyMods.None, Me)[0] is var use ? (use.Kind, use.Arg0) : default);
        PlannedCommand crusade = Assert.Single(OrderResolver.WithMode(OrderMode.Crusade, [SelectionTests.Building(3)], null, 3, 4, KeyMods.None, Me));
        Assert.Equal((CommandKind.PreachCrusade, SelectionTests.Id(3)), (crusade.Kind, crusade.Entities[0]));
    }

    [Fact]
    public void Planned_commands_round_trip_into_a_command_list()
    {
        var list = new CommandList();
        new PlannedCommand(CommandKind.Move, CommandFlags.Ctrl, EntityId.None, 10, 20, 0, 0, [SelectionTests.Id(3), SelectionTests.Id(1)]).AddTo(list);
        Command c = list[0];
        Assert.Equal((CommandKind.Move, 10, 20, CommandFlags.Ctrl), (c.Kind, c.X, c.Y, c.Flags));
        Assert.Equal([SelectionTests.Id(1), SelectionTests.Id(3)], list.EntitiesOf(c).ToArray());
    }
}

public sealed class HotkeyTests
{
    [Fact]
    public void Every_chord_is_bound_once_and_every_action_has_a_key()
    {
        var table = new HotkeyTable();
        Assert.Equal(table.Bindings.Count, table.Bindings.Select(b => (b.Key, b.Mods)).Distinct().Count());
        Assert.Equal(table.Bindings.Count, table.Bindings.Select(b => b.Id).Distinct().Count());
        foreach (GameAction action in Enum.GetValues<GameAction>().Where(a => a != GameAction.None))
        {
            Assert.Contains(table.Bindings, b => b.Action == action);
        }
    }

    [Fact]
    public void Lookups_follow_the_original_keys()
    {
        var table = new HotkeyTable();
        Assert.Equal(GameAction.MenuScreen, table.Find(GameKey.F1, KeyMods.None)!.Action);
        Assert.Equal(GameAction.AssignGroup, table.Find(GameKey.D3, KeyMods.Ctrl)!.Action);
        Assert.Equal(3, table.Find(GameKey.D3, KeyMods.Ctrl)!.Argument);
        Assert.Equal(GameAction.SelectGroup, table.Find(GameKey.D3, KeyMods.None)!.Action);
        Assert.Equal(GameAction.ClearGroup, table.Find(GameKey.D0, KeyMods.Ctrl)!.Action);
        Assert.Equal(GameAction.LastLocation, table.Find(GameKey.Home, KeyMods.Shift)!.Action);
        Assert.Equal(GameAction.LastEventAndSelect, table.Find(GameKey.Home, KeyMods.Ctrl)!.Action);
        Assert.Equal(GameAction.HealthBarsToggle, table.Find(GameKey.Backquote, KeyMods.Ctrl)!.Action);
        Assert.Equal(GameAction.SelectBuildingClass, table.Find(GameKey.C, KeyMods.Ctrl)!.Action);
        Assert.Equal('C' - 'A', table.Find(GameKey.C, KeyMods.Ctrl)!.Argument);
        Assert.Equal(GameAction.SpeedUp, table.Find(GameKey.Plus, KeyMods.Shift)!.Action);
        Assert.Null(table.Find(GameKey.Q, KeyMods.None));
    }

    [Fact]
    public void Rebinding_moves_a_binding_and_frees_the_old_chord()
    {
        var table = new HotkeyTable();
        table.Rebind("pause", GameKey.P, KeyMods.Ctrl);
        Assert.Equal(GameAction.Pause, table.Find(GameKey.P, KeyMods.Ctrl)!.Action);
        Assert.Null(table.Find(GameKey.Pause, KeyMods.None));
        Assert.Throws<ArgumentException>(() => table.Rebind("nope", GameKey.A, KeyMods.None));
    }

    [Fact]
    public void The_parity_checklist_lists_every_binding()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Tzar2.sln")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Tzar2.sln not found");
        }

        string doc = File.ReadAllText(Path.Combine(root, "docs/mechanics/hotkey-parity.md"));
        foreach (HotkeyBinding b in new HotkeyTable().Bindings.Where(b => !b.Id.StartsWith("group-", StringComparison.Ordinal) && !b.Id.StartsWith("building-", StringComparison.Ordinal)))
        {
            Assert.Contains($"`{b.Id}`", doc, StringComparison.Ordinal);
        }

        Assert.Contains("`group-assign-1`", doc, StringComparison.Ordinal);
        Assert.Contains("`building-a`", doc, StringComparison.Ordinal);
    }
}
