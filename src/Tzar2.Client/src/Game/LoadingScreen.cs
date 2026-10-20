using System;
using Godot;
using Tzar2.Client.Logic;

namespace Tzar2.Client.Game;

/// <summary>
/// Shown while a match is built (map, sprites, terrain): one of the original's wait screens (<c>IMAGES/SCREENS/WAITSCR</c>)
/// with a line saying what is loading, so the game answers the click at once (play-test of 2026-10-20: a random map
/// seemed to hang the game).
/// </summary>
public partial class LoadingScreen : ArtScreen
{
    /// <summary>The wait screens of the package (<c>screen.bmp</c>, <c>screen1.bmp</c>…<c>screen6.bmp</c>).</summary>
    public static readonly string[] Backgrounds =
    [
        "art/screens/waitscr/screen.t2img", "art/screens/waitscr/screen1.t2img", "art/screens/waitscr/screen2.t2img",
        "art/screens/waitscr/screen3.t2img", "art/screens/waitscr/screen4.t2img", "art/screens/waitscr/screen5.t2img",
        "art/screens/waitscr/screen6.t2img",
    ];

    /// <summary>Creates the screen with wait screen <paramref name="index"/> (any number; it wraps) and <paramref name="what"/>.</summary>
    public LoadingScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, int index, string what)
        : base(ui, screen, Backgrounds[(int)((uint)index % (uint)Backgrounds.Length)])
    {
        string loading = catalog?.Text("viewmsg", "loading", "Loading...") ?? "Loading...";
        // The background is centred: its size is the screen less the margins on both sides.
        int width = Math.Max(1, screen.X - (2 * Origin.X)), height = Math.Max(1, screen.Y - (2 * Origin.Y));
        Text(new Vector2I(0, Math.Max(0, height - 60)), $"{loading} {what}", UiAssets.BoldFont, null, width);
    }
}
