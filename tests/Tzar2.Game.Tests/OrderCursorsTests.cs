using Tzar2.Game.Input;
using Tzar2.Game.Ui;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>
/// The pointers over the map are the original's, as ReTzar reads them (the owner's fifth play-test: Tzar2's own saw and
/// pickaxe were not the original's): one shovel for every gathering, the order's pointer while the card's order waits.
/// </summary>
public class OrderCursorsTests
{
    [Fact]
    [Trait("Rule", "CTRL-012")]
    [Trait("Rule", "CTRL-013")]
    public void Over_an_object_the_pointer_is_that_of_the_right_click_and_the_arrow_where_it_would_only_walk()
    {
        // 0x42da60: chop, dig, mine and build show the shovel; repair its own; capture the spell; picking up the hand.
        Assert.Equal(OrderCursors.Harvest, OrderCursors.ForHover(DefaultAction.Harvest, dug: true)); // a tree, a rock, a field
        Assert.Equal(OrderCursors.Arrow, OrderCursors.ForHover(DefaultAction.Harvest, dug: false)); // milking, fishing, meat
        Assert.Equal(OrderCursors.Harvest, OrderCursors.ForHover(DefaultAction.Build));
        Assert.Equal(OrderCursors.Repair, OrderCursors.ForHover(DefaultAction.Repair));
        Assert.Equal(OrderCursors.MagicHarm, OrderCursors.ForHover(DefaultAction.Capture));
        Assert.Equal(OrderCursors.Pick, OrderCursors.ForHover(DefaultAction.PickItem));
        Assert.Equal(OrderCursors.Move, OrderCursors.ForHover(DefaultAction.Board));
        Assert.Equal(OrderCursors.Arrow, OrderCursors.ForHover(DefaultAction.Follow));
        Assert.Equal(OrderCursors.Arrow, OrderCursors.ForHover(DefaultAction.Move));
        Assert.Equal(OrderCursors.Attack, OrderCursors.ForHover(DefaultAction.Attack));
        Assert.Equal(OrderCursors.AttackType, OrderCursors.ForHover(DefaultAction.Attack, alt: true));
        Assert.Equal(OrderCursors.AttackSet, OrderCursors.ForHover(DefaultAction.Attack, shift: true)); // CTRL-026, Tzar2's
    }

    [Fact]
    [Trait("Rule", "CTRL-012")]
    [Trait("Rule", "ORD-014")]
    public void An_order_of_the_card_waits_for_its_target_under_the_originals_pointer_for_it()
    {
        // The table at 0x43a8c1.
        Assert.Equal(OrderCursors.Attack, OrderCursors.ForMode(OrderMode.Attack));
        Assert.Equal(OrderCursors.AttackSet, OrderCursors.ForMode(OrderMode.AttackGround));
        Assert.Equal(OrderCursors.Move, OrderCursors.ForMode(OrderMode.MoveAdvance));
        Assert.Equal(OrderCursors.Move, OrderCursors.ForMode(OrderMode.MoveSneak));
        Assert.Equal(OrderCursors.Harvest, OrderCursors.ForMode(OrderMode.Harvest));
        Assert.Equal(OrderCursors.Repair, OrderCursors.ForMode(OrderMode.Repair));
        Assert.Equal(OrderCursors.Repair, OrderCursors.ForMode(OrderMode.Capture));
        Assert.Equal(OrderCursors.Guard, OrderCursors.ForMode(OrderMode.Guard));
        Assert.Equal(OrderCursors.Patrol, OrderCursors.ForMode(OrderMode.Patrol));
        Assert.Equal(OrderCursors.Patrol, OrderCursors.ForMode(OrderMode.Explore));
        Assert.Equal(OrderCursors.Pick, OrderCursors.ForMode(OrderMode.PickItem));
        Assert.Equal(OrderCursors.Spell, OrderCursors.ForMode(OrderMode.Spell));
        Assert.Equal(OrderCursors.Spell, OrderCursors.ForMode(OrderMode.UseItem));
        Assert.Equal(OrderCursors.Arrow, OrderCursors.ForMode(OrderMode.Rally));
        Assert.Equal(OrderCursors.Move, OrderCursors.ForMode(OrderMode.Unload));
        Assert.Equal(OrderCursors.None, OrderCursors.ForMode(OrderMode.Unload, canLand: false)); // no landing there
    }
}
