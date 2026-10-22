using System;
using System.Collections.Generic;

namespace Tzar2.Client.Logic;

/// <summary>
/// The original's pictures of what flies and burns (IMAGES.INI <c>anims\*</c> entries): the sprite of each projectile kind
/// of the rules, the explosion it leaves, how high it arcs, and the flames of a burning building. The pairing of kinds and
/// pictures is ours (the data names the pictures only; ⚠ UNVERIFIED which unit shoots which picture).
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
        "arrow", "arrow_ballista", "cb_stone", "cb_metal", "arrow_rocket", "magic_star", "flame_bolt", "freeze_bolt",
        "explosion_small", "flame_bolt_expl", "freeze_bolt_expl", FireBig, FireSmall, OrderCross, Waypoint,
    ];

    /// <summary>The picture of projectile kind <paramref name="key"/> (rules <c>projectiles</c>), or null.</summary>
    public static string? Projectile(string key) => key switch
    {
        "arrow" => "arrow",
        "bolt" => "arrow_ballista",
        "stone" => "cb_stone",
        "cannon" => "cb_metal",
        "rocket_arrow" => "arrow_rocket",
        "magic" => "magic_star",
        "flame_ball" or "flame_bolt" => "flame_bolt",
        "freeze_bolt" => "freeze_bolt",
        _ => null,
    };

    /// <summary>Whether the projectile's picture glows (painted on black: drawn additively).</summary>
    public static bool Glows(string key) => key is "magic" or "flame_ball" or "flame_bolt" or "freeze_bolt";

    /// <summary>Whether the picture's frames are its flight directions (24 strips, north first, clockwise).</summary>
    public static bool Turns(string key) => key is "arrow" or "bolt" or "rocket_arrow";

    /// <summary>The explosion a projectile of kind <paramref name="key"/> leaves where it lands, or null.</summary>
    public static string? Impact(string key) => key switch
    {
        "stone" or "cannon" or "rocket_arrow" => "explosion_small",
        "flame_ball" or "flame_bolt" => "flame_bolt_expl",
        "freeze_bolt" => "freeze_bolt_expl",
        _ => null,
    };

    /// <summary>The top of the flight arc, per mille of the flight's length (0: straight).</summary>
    public static int ArcPermille(string key) => key switch
    {
        "stone" => 300,
        "cannon" => 150,
        "arrow" or "bolt" => 120,
        _ => 0,
    };

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

    /// <summary>The height (pixels) of a projectile of kind <paramref name="key"/> at <paramref name="progress"/> (0-1) of a flight of <paramref name="length"/> pixels.</summary>
    public static int ArcHeight(string key, double length, double progress)
    {
        double p = Math.Clamp(progress, 0, 1);
        double top = Math.Min(length * ArcPermille(key) / 1000.0, 96);
        return (int)Math.Round(top * 4 * p * (1 - p));
    }
}
