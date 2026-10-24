using Tzar2.Game.Input;

namespace Tzar2.Game.Ui;

/// <summary>
/// Which of the original's pointers (its IMAGES.INI <c>cs_*</c> entries) shows over the map, as ReTzar reads the original
/// (the owner's fifth play-test: Tzar2's own saw and pickaxe were not the original's). While an order of the card waits
/// for its target, the order's pointer (the table at 0x43a8c1); otherwise the pointer of what a right click would do
/// there (0x4f45b0 → 0x42da60), and the arrow where it would only walk, follow, milk, fish or carry goods home.
/// </summary>
public static class OrderCursors
{
    /// <summary>The default pointer, the golden spearhead (<c>s_def</c>; CTRL-015).</summary>
    public const string Arrow = "cs_arrow";

    /// <summary>Attack (<c>s_attack</c>).</summary>
    public const string Attack = "cs_attack";

    /// <summary>Attack in turn, Shift (<c>cs_attackset</c>); the original's attack on the ground.</summary>
    public const string AttackSet = "cs_attackset";

    /// <summary>Attack every object of the type, Alt (<c>cs_attacktype</c>).</summary>
    public const string AttackType = "cs_attacktype";

    /// <summary>Guard (<c>s_guard</c>).</summary>
    public const string Guard = "cs_guard";

    /// <summary>Gather and build: the shovel (<c>s_harvest</c>).</summary>
    public const string Harvest = "cs_harvest";

    /// <summary>Capture under the pointer (<c>s_spell</c> with its own hotspot).</summary>
    public const string MagicHarm = "cs_magicharm";

    /// <summary>Walk or board (<c>s_move</c>).</summary>
    public const string Move = "cs_move";

    /// <summary>Not there (<c>s_none</c>; ORD-014's landing).</summary>
    public const string None = "cs_none";

    /// <summary>Patrol and explore (<c>s_patrol2</c>; the original loads no <c>cs_patrol</c>).</summary>
    public const string Patrol = "cs_patrol_ex";

    /// <summary>Pick up an item, the hand (<c>s_hand</c>; CTRL-013).</summary>
    public const string Pick = "cs_pick";

    /// <summary>Repair, and the card's capture and build orders (<c>s_repair</c>).</summary>
    public const string Repair = "cs_repair";

    /// <summary>A spell or an item used on a target (<c>s_spell</c>).</summary>
    public const string Spell = "cs_spell";

    /// <summary>
    /// The pointer of an order of the card waiting for its target. <paramref name="canLand"/> tells an unload where its
    /// units can step ashore (ORD-014); Shift and Alt pick the attack's variants (CTRL-026).
    /// </summary>
    public static string ForMode(OrderMode mode, bool shift = false, bool alt = false, bool canLand = true) => mode switch
    {
        OrderMode.Attack when alt => AttackType,
        OrderMode.Attack when shift => AttackSet,
        OrderMode.Attack => Attack,
        OrderMode.AttackGround => AttackSet,
        OrderMode.Move or OrderMode.MoveAdvance or OrderMode.MoveSneak => Move,
        OrderMode.Unload => canLand ? Move : None,
        OrderMode.Harvest => Harvest,
        OrderMode.Repair or OrderMode.Capture => Repair,
        OrderMode.Guard => Guard,
        OrderMode.Patrol or OrderMode.Explore => Patrol,
        OrderMode.PickItem => Pick,
        OrderMode.Spell or OrderMode.UseItem => Spell,
        _ => Arrow,
    };

    /// <summary>
    /// The pointer of a right click's default action on an object. <paramref name="dug"/>: the target is gathered where
    /// it stands (a tree, a rock, a field: the shovel), not a cow, a fish patch or meat (the arrow). Shift and Alt pick the
    /// attack's variants (CTRL-026).
    /// </summary>
    public static string ForHover(DefaultAction action, bool dug = false, bool shift = false, bool alt = false) => action switch
    {
        DefaultAction.Attack when alt => AttackType,
        DefaultAction.Attack when shift => AttackSet,
        DefaultAction.Attack => Attack,
        DefaultAction.Board => Move,
        DefaultAction.Harvest when dug => Harvest,
        DefaultAction.Build => Harvest, // building is one of the shovel's programs (Build, 0xd)
        DefaultAction.Repair => Repair,
        DefaultAction.Capture => MagicHarm,
        DefaultAction.PickItem => Pick,
        _ => Arrow,
    };
}
