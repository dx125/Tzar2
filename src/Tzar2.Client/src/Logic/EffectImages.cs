using System;
using System.Collections.Generic;

namespace Tzar2.Client.Logic;

/// <summary>
/// The original's pictures of what flies and burns (IMAGES.INI <c>anims\*</c> entries): what each shot looks like by its
/// kind and its shooter, how it arcs and what it leaves (PRES-002, ReTzar's reading of the original's missiles), and the
/// flames of a burning building.
/// </summary>
public static class EffectImages
{
    /// <summary>Flames on a large burning building.</summary>
    public const string FireBig = "fire_big";

    /// <summary>Flames on a small burning building.</summary>
    public const string FireSmall = "fire_small";

    /// <summary>The red cross where a move order was given (IMAGES.INI <c>cursor</c>: ten frames, hotspot 24, 24).</summary>
    public const string OrderCross = "cursor";

    /// <summary>The flag on a waypoint of the selection while Shift is held (IMAGES.INI <c>wp_flag</c>; CTRL-025).</summary>
    public const string Waypoint = "wp_flag";

    /// <summary>The yellow stars floating above a unit that carries a spell book (IMAGES.INI <c>magic_star</c>; 1.09, ITEM-012).</summary>
    public const string BookStars = "magic_star";

    /// <summary>Every picture a match may need.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "arrow", "arrow_big", "arrow_ballista", "arrow_ballista_shadow", "cb_stone", "cb_metal", "cb_fire", "cb_metal_shadow",
        "arrow_rocket", "magic_star", "flame_bolt", "exorcism", "freeze_ball_trail", "flame_ball_trail", "explosion_small",
        "flame_bolt_expl", "freeze_bolt_expl", Hole, Splash, Blast, FireBig, FireSmall, OrderCross, Waypoint,
    ];

    /// <summary>
    /// How a shot looks (PRES-002), by the projectile kind of the rules and the original class of its shooter, as ReTzar
    /// reads the original's missiles (0x431340 and its classes; 0x4dc100, 0x4ed680, 0x4ed800, 0x432dd9, 0x432350):
    /// <list type="bullet">
    /// <item>arrows turn through 24 directions; the longbowman's, the xebec's and the stone tower's are <c>arrow_big</c>;</item>
    /// <item>the mage's and the messiah's bolt is <c>flame_bolt</c>, the priest's <c>exorcism</c>, both bursting in
    /// <c>flame_bolt_expl</c>;</item>
    /// <item>catapult stones (<c>cb_stone</c>) arc from 48 px to a peak of 192, the galleon's and the hulk's shells
    /// (<c>cb_metal</c>) from 24 to 64, the cannon tower's (<c>cb_fire</c>) from 32 to 64, all with
    /// <c>cb_metal_shadow</c>; the rocket tower's rocket turns;</item>
    /// <item>the ballista's bolt turns and arcs from 16 to 64 with its shadow;</item>
    /// <item>freeze and flame balls show only their trails, the freeze bursting in <c>freeze_bolt_expl</c>.</item>
    /// </list>
    /// Shells, bolts and rockets leave a hole (<c>expl_small_hole</c>), a splash on water (<c>expl_small_water</c>) or,
    /// shells on a building, a blast (<c>expl64</c>; 0x43392a).
    /// </summary>
    public static ShotLook? Look(string kind, string? shooterClass) => (kind, shooterClass) switch
    {
        (_, "mage" or "messiah") when kind is "magic" or "arrow" => new("flame_bolt", null, 0, 0, Turns: false, Glows: true, Impact: "flame_bolt_expl"),
        (_, "priest") when kind is "magic" or "arrow" => new("exorcism", null, 0, 0, Turns: false, Glows: true, Impact: "flame_bolt_expl"),
        ("arrow", "longbowman" or "xebec" or "tower_stone") => new("arrow_big", null, 0, 0, Turns: true),
        ("arrow", _) => new("arrow", null, 0, 0, Turns: true),
        ("stone", _) => new("cb_stone", "cb_metal_shadow", 192, 48, Turns: false, Impact: Hole, Shell: true),
        ("cannon", "tower_stone2") => new("cb_fire", "cb_metal_shadow", 64, 32, Turns: false, Impact: Hole, Shell: true),
        ("cannon", _) => new("cb_metal", "cb_metal_shadow", 64, 24, Turns: false, Impact: Hole, Shell: true, Muzzle: shooterClass is "galleon" or "hulk"),
        ("rocket_arrow", _) => new("arrow_rocket", null, 0, 0, Turns: true, Impact: Hole, Shell: true),
        ("bolt", _) => new("arrow_ballista", "arrow_ballista_shadow", 64, 16, Turns: true, Impact: Hole),
        ("freeze_bolt", _) => new("freeze_ball_trail", null, 0, 0, Turns: false, Glows: true, Impact: "freeze_bolt_expl"),
        ("flame_ball" or "flame_bolt", _) => new("flame_ball_trail", null, 0, 0, Turns: false, Glows: true, Impact: "flame_bolt_expl"),
        ("magic", _) => new("magic_star", null, 0, 0, Turns: false, Glows: true),
        _ => null,
    };

    /// <summary>The hole a shell, bolt or rocket leaves on land.</summary>
    public const string Hole = "expl_small_hole";

    /// <summary>How long a hole stays on the ground (0x43392a, ReTzar's reading).</summary>
    public const int HoleMs = 10_000;

    /// <summary>The splash where one falls in water.</summary>
    public const string Splash = "expl_small_water";

    /// <summary>The blast of a shell on a building.</summary>
    public const string Blast = "expl64";

    /// <summary>
    /// The height in pixels of an arcing shot <paramref name="flown"/> pixels into a flight of <paramref name="span"/>
    /// (both Chebyshev distances): h = (a·s + b)·s + h0, starting at <paramref name="start"/> and landing at 0
    /// (0x4335e7, ReTzar's reading).
    /// </summary>
    public static int ArcHeight(int peak, int start, double span, double flown)
    {
        if (span <= 0 || (peak == 0 && start == 0))
        {
            return 0;
        }

        double s = Math.Clamp(flown, 0, span);
        double a = ((2.0 * start) - (2.0 * peak)) / (span * span);
        double b = ((2.0 * peak) - (3.0 * start)) / span;
        return (int)Math.Truncate((((a * s) + b) * s) + start);
    }

    /// <summary>
    /// The frame of a picture that turns with the flight: the screen direction (<paramref name="dx"/>, <paramref name="dy"/>;
    /// y down) as one of <paramref name="frames"/> steps clockwise from north.
    /// </summary>
    public static int TurnFrame(double dx, double dy, int frames)
    {
        if (frames <= 1 || (dx == 0 && dy == 0))
        {
            return 0;
        }

        double angle = Math.Atan2(dx, -dy);
        if (angle < 0)
        {
            angle += 2 * Math.PI;
        }

        return (int)Math.Round(angle / (2 * Math.PI / frames)) % frames;
    }
}

/// <summary>How a shot looks: its picture, shadow, arc, whether it turns or glows, and what it leaves where it lands.</summary>
/// <param name="Body">The picture (IMAGES.INI).</param>
/// <param name="Shadow">Its shadow on the ground, or null.</param>
/// <param name="Peak">The arc's highest point in pixels (0 with <paramref name="Start"/> 0: flat).</param>
/// <param name="Start">The height it leaves at.</param>
/// <param name="Turns">Whether its frames are its 24 flight directions.</param>
/// <param name="Glows">Whether it is drawn additively (painted on black).</param>
/// <param name="Impact">What it leaves where it lands (a hole: <see cref="EffectImages.Hole"/>), or null.</param>
/// <param name="Shell">A shell or rocket: a blast on a building instead of a hole.</param>
/// <param name="Muzzle">A flash (<c>explosion_small</c>) 24 px above where it leaves: the galleon's and the hulk's guns (0x4e8dd3).</param>
public sealed record ShotLook(string Body, string? Shadow, int Peak, int Start, bool Turns, bool Glows = false, string? Impact = null, bool Shell = false, bool Muzzle = false)
{
    /// <summary>What it leaves on a tile: a splash on water instead of a hole, a blast when a shell hits a building.</summary>
    public string? ImpactOn(bool water, bool building)
        => Impact != EffectImages.Hole ? Impact : Shell && building ? EffectImages.Blast : water ? EffectImages.Splash : EffectImages.Hole;
}
