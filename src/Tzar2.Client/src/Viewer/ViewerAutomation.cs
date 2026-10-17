using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace Tzar2.Client.Viewer;

/// <summary>
/// Scripted runs of the viewer: <c>--smoke</c> (load every kind of asset once, exit code 0 on success; used by the
/// headless test) and <c>--capture</c> (the fixed screenshot set used to check the renderer by eye; the images show
/// original art, so they are written to a user-given folder and never committed).
/// </summary>
public static class ViewerAutomation
{
    /// <summary>Line printed by a successful smoke run.</summary>
    public const string SmokeOk = "VIEWER SMOKE OK";

    /// <summary>Line printed by a smoke run that ended on the first-run screen.</summary>
    public const string SmokeFirstRun = "VIEWER SMOKE FIRST-RUN";

    /// <summary>Exercises every page once and returns a summary line.</summary>
    public static async Task<string> SmokeAsync(Node host, AssetViewer viewer, string? select)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(viewer);
        await Frames(host, 2);
        int sprites = 0;
        var names = new System.Collections.Generic.List<string>();
        foreach (string name in viewer.Sprites.EntryNames)
        {
            if (sprites >= 6)
            {
                break;
            }

            if (!viewer.Sprites.Select(name))
            {
                throw new InvalidOperationException($"sprite entry {name} could not be selected");
            }

            sprites++;
            names.Add(name);
            await Frames(host, 1);
        }

        if (select is not null && !viewer.Sprites.Select(select))
        {
            throw new InvalidOperationException($"--select {select}: no such entry");
        }

        viewer.Sprites.SetPlayers(0, 1);
        await Frames(host, 2);

        viewer.ShowPage(1);
        int terrain = 0;
        foreach (Content.Terrain.TerrainSet set in viewer.Store.TerrainSets)
        {
            foreach (Content.Terrain.TerrainFile file in set.Files)
            {
                if (viewer.Terrain.Select(set.Id, file.Pair))
                {
                    terrain++;
                }
            }
        }

        await Frames(host, 2);
        viewer.ShowPage(2);
        await Frames(host, 2);
        viewer.ShowPage(3);
        int sounds = 0;
        for (int i = 0; i < Math.Min(3, viewer.Sounds.Count); i++)
        {
            using AudioStreamWav stream = viewer.Sounds.Load(i);
            sounds += stream.Data.Length > 0 ? 1 : 0;
        }

        viewer.ShowPage(4);
        string music;
        if (viewer.Store.Music.Count == 0)
        {
            music = "none";
        }
        else if (!viewer.Music.Player.HasSoundFont)
        {
            music = "disabled (no SoundFont)";
        }
        else
        {
            float peak = viewer.Music.Player.RenderOffline(viewer.Store.Read(viewer.Store.Music[0].Asset), 44100 * 10);
            music = string.Create(CultureInfo.InvariantCulture, $"rendered (peak {peak:0.000})");
        }

        await Frames(host, 2);
        viewer.ShowPage(0);
        await Frames(host, 1);
        return string.Create(CultureInfo.InvariantCulture,
            $"{SmokeOk}: sprites={sprites} [{string.Join(", ", names)}] terrain={terrain} fonts={viewer.Fonts.Count} sounds={sounds} music={music}");
    }

    /// <summary>Renders the screenshot set into <paramref name="dir"/>.</summary>
    public static async Task CaptureAsync(Node host, AssetViewer viewer, string dir, string? unit)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(viewer);
        Directory.CreateDirectory(dir);
        viewer.Paused = true;

        viewer.ShowPage(0);
        Require(viewer.Sprites.Select(unit ?? "e_knight"), unit ?? "e_knight");
        viewer.Sprites.SetLayout(SpriteLayout.AnimateDirections);
        viewer.Sprites.SetPlayers(0, 1);
        viewer.SetZoom(1);
        viewer.TimeMs = 300;
        await Save(host, dir, "01-unit-8-directions-players-blue-red.png");
        viewer.SetZoom(2);
        await Save(host, dir, "02-unit-8-directions-zoom2.png");
        viewer.Sprites.SetPlayers(4, 5);
        viewer.SetZoom(1);
        viewer.TimeMs = 900;
        await Save(host, dir, "03-unit-players-yellow-cyan-later-frame.png");

        viewer.Sprites.SetPlayers(0, 1, 2, 3, 4, 5, 6, 7);
        viewer.TimeMs = 300;
        await Save(host, dir, "03b-unit-all-8-players.png");
        Require(viewer.Sprites.Select("e_knight_attack"), "e_knight_attack");
        viewer.Sprites.SetLayout(SpriteLayout.AllFrames);
        viewer.Sprites.SetPlayers(2);
        await Save(host, dir, "03c-unit-attack-all-frames.png");
        viewer.Sprites.SetLayout(SpriteLayout.AnimateDirections);
        if (viewer.Sprites.Select("a_dragon"))
        {
            viewer.Sprites.SetPlayers(6);
            await Save(host, dir, "03d-dragon-with-shadow-sheet.png");
        }

        Require(viewer.Sprites.Select("e_barrack"), "e_barrack");
        viewer.Sprites.SetPlayers(0, 1, 2, 3);
        viewer.SetZoom(1);
        viewer.TimeMs = 0;
        await Save(host, dir, "04-building-c-overlay-shadow.png");
        viewer.SetZoom(3);
        await Save(host, dir, "05-building-c-overlay-zoom3.png");
        viewer.TimeMs = 500;
        await Save(host, dir, "06-building-c-overlay-flag-frame2-zoom3.png");

        viewer.ShowPage(1);
        viewer.SetZoom(1);
        Require(viewer.Terrain.Select("grass", "12"), "grass 12");
        viewer.Terrain.PinFrame(0);
        await Save(host, dir, "07-terrain-grass-12-transitions.png");
        viewer.SetZoom(2);
        await Save(host, dir, "08-terrain-grass-12-zoom2.png");
        viewer.SetZoom(1);
        Require(viewer.Terrain.Select("grass", "01"), "grass 01");
        for (int f = 0; f < 3; f++)
        {
            viewer.Terrain.PinFrame(f);
            await Save(host, dir, string.Create(CultureInfo.InvariantCulture, $"09-water-shore-frame{f}.png"));
        }

        viewer.SetZoom(3);
        viewer.Terrain.PinFrame(0);
        await Save(host, dir, "10-water-shore-zoom3-frame0.png");
        viewer.Terrain.PinFrame(1);
        await Save(host, dir, "10-water-shore-zoom3-frame1.png");
        viewer.Terrain.PinFrame(-1);

        viewer.ShowPage(2);
        viewer.SetZoom(1);
        await Save(host, dir, "11-fonts.png");
        viewer.SetZoom(2);
        await Save(host, dir, "12-fonts-zoom2.png");
    }

    private static void Require(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException($"capture: {what} not found in the package");
        }
    }

    private static async Task Save(Node host, string dir, string name)
    {
        await Frames(host, 3);
        await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = host.GetViewport().GetTexture().GetImage();
        string path = Path.Combine(dir, name);
        Error error = image.SavePng(path);
        GD.Print(error == Error.Ok ? $"captured {path}" : $"capture failed: {path}: {error}");
    }

    private static async Task Frames(Node host, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
