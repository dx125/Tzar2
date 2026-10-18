using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Content.Compiled;
using Tzar2.Game.Input;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// In-game test driver (<c>--autoplay &lt;dir&gt;</c>): plays a short skirmish through the same input paths as a player
/// (clicks, drag boxes, right-clicks, command-card hotkeys, placement, hotkeys), takes screenshots into the folder and
/// prints what happened. Needs a window (screenshots); it is a development tool, not part of CI.
/// </summary>
public sealed class AutoplayDriver
{
    private readonly GameApp _app;
    private readonly string _dir;
    private int _shot;

    /// <summary>Creates the driver.</summary>
    public AutoplayDriver(GameApp app, string dir)
    {
        _app = app;
        _dir = dir;
    }

    /// <summary>Runs the script and quits the client.</summary>
    public async Task RunAsync()
    {
        int exit = 0;
        try
        {
            Directory.CreateDirectory(_dir);
            DisplayServer.WindowSetSize(GameApp.Screen);
            await Frames(30);
            GameScreen game = _app.Game ?? throw new InvalidOperationException("No game.");
            game.EdgeScroll = false;
            game.Scripted = true;
            await Shot("start");

            // 1. Double-click a peasant: every peasant on screen is selected.
            FrameEntity peon = Own(game, "peon");
            game.Click(Screen(game, peon), MouseButton.Left, doubleClick: true);
            await Frames(3);
            Log(game, $"double-click peasant: {game.Selection.Selected.Count} selected");

            // 2. Right-click a tree: harvest.
            FrameEntity tree = Nearest(game, peon, e => game.Catalog.Type(e.TypeId).Key == "comm.tree");
            game.CenterOn(tree.X, tree.Y);
            await Frames(3);
            game.Click(Screen(game, tree) + new Vector2(0, -20), MouseButton.Right);
            await Seconds(game, 6);
            await Shot("harvest");
            Log(game, "harvesting: " + string.Join(",", game.Frame!.Entities.ToArray().Where(e => e.Owner == game.Session.LocalPlayer && game.Catalog.Type(e.TypeId).Class == "peon").Select(e => e.Activity)));

            // 3. One peasant builds a house: B (build menu), the house is in a submenu.
            game.CenterOn(peon.X, peon.Y);
            await Frames(3);
            game.Click(Screen(game, Own(game, "peon")), MouseButton.Left);
            await Frames(2);
            game.Press(Key.B);
            await Frames(2);
            OpenHouse(game);
            await Frames(2);
            FrameEntity castle = Own(game, "castle");
            Vector2 spot = FindLegalSpot(game, castle);
            game.Click(spot, MouseButton.Right);
            game.Press(Key.B);
            await Frames(2);
            OpenHouse(game);
            await MoveMouse(game, spot);
            await Shot("placement-ghost");
            game.Click(spot, MouseButton.Left);
            await Seconds(game, 4);
            await Shot("construction");
            Log(game, $"placing={game.Placing}, sites={game.Frame!.Entities.ToArray().Count(e => e.ConstructionPermille >= 0)}");

            // 4. Ctrl+C selects the castle; P trains a peasant.
            game.Press(Key.C, KeyMods.Ctrl);
            await Frames(2);
            game.Press(Key.P);
            game.Press(Key.P);
            await Seconds(game, 3);
            await Shot("castle-training");
            Log(game, "castle card: " + string.Join(" ", game.Card.Select(c => c is { } s ? $"{s.Button.Hotkey}:{s.Button.Order}{(s.Enabled ? string.Empty : "(off)")}" : "-")));

            // 5. Drag-box the footmen, Ctrl+1, move-advance toward the map centre.
            FrameEntity foot = Own(game, "footman");
            game.CenterOn(foot.X, foot.Y);
            await Frames(3);
            Vector2 f = Screen(game, foot);
            game.Drag(f - new Vector2(60, 60), f + new Vector2(60, 60));
            await Frames(2);
            game.Press(Key.Key1, KeyMods.Ctrl);
            Log(game, $"box: {game.Selection.Selected.Count} selected");
            await Shot("army-selected");
            game.Press(Key.Quoteleft, KeyMods.Ctrl);
            game.Click(f + new Vector2(200, 120), MouseButton.Right, KeyMods.Ctrl);
            await Seconds(game, 3);
            await Shot("move-advance-bars");
            game.Press(Key.Quoteleft, KeyMods.Ctrl);

            // 6. Fast forward, then look at an enemy base through the fog and at night.
            game.Session.SpeedIndex = GameSession.SpeedSteps.Length - 1;
            await Seconds(game, 120);
            Log(game, "after fast forward");
            await Shot("economy");
            FrameEntity enemy = game.Frame!.Entities.ToArray().First(e => e.Owner != game.Session.LocalPlayer && e.Owner < 8 && game.Catalog.Type(e.TypeId).Class == "castle");
            game.CenterOn(enemy.X, enemy.Y);
            await Frames(3);
            await Shot("fog-enemy-base");
            game.CenterOn(castle.X, castle.Y);
            await Seconds(game, 360);
            await Shot("night");
            game.Session.SpeedIndex = 3;
            game.Rain = true;
            await Frames(20);
            await Shot("rain");
            game.Rain = false;
            game.Press(Key.F1);
            await Frames(3);
            await Shot("game-menu");
            game.Press(Key.Escape);
            game.GetNodeOrNull("GameMenu")?.QueueFree();
            game.Session.Paused = false;
            await Frames(3);
            Log(game, "final");
            _app.ShowResultsForTest(game.Frame!.Outcome == MatchOutcome.None ? MatchOutcome.Victory : game.Frame.Outcome, game.Stats);
            await Frames(5);
            await Shot("results");
            _app.ShowMainMenu();
            await Frames(5);
            await Shot("main-menu");
            _app.ShowSetup();
            await Frames(5);
            await Shot("skirmish-setup");
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or IOException or ArgumentException)
        {
            GD.PrintErr("AUTOPLAY FAILED: " + e);
            exit = 1;
        }

        GD.Print($"AUTOPLAY DONE {_shot} screenshots in {_dir}");
        _app.GetTree().Quit(exit);
    }

    private static void OpenHouse(GameScreen game)
    {
        // The house sits in a submenu of the build menu; open the submenu that offers it.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int house = Index(game, b => b.Action == CardAction.Build && b.Argument == "house");
            if (house >= 0)
            {
                game.PressCard(house);
                return;
            }

            int sub = Index(game, b => b.Action == CardAction.Submenu && game.Catalog.Card(b.Argument).Any(c => c.Argument == "house"));
            if (sub < 0)
            {
                return;
            }

            game.PressCard(sub);
        }
    }

    private static int Index(GameScreen game, Func<CardButton, bool> match)
    {
        for (int i = 0; i < game.Card.Count; i++)
        {
            if (game.Card[i] is { } s && match(s.Button))
            {
                return i;
            }
        }

        return -1;
    }

    private static Vector2 FindLegalSpot(GameScreen game, FrameEntity castle)
    {
        GameFrame frame = game.Frame!;
        ushort house = game.Placing;
        if (house == ushort.MaxValue)
        {
            return Screen(game, castle);
        }

        EntityTypeDefinition type = game.Session.Ruleset.EntityTypes[house];
        int cx = castle.X / 2048, cy = castle.Y / 2048;
        for (int r = 5; r < 14; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int tx = cx + dx, ty = cy + dy;
                    if (Placement.IsLegal(frame.Buildable, game.Session.Map.Width, game.Session.Map.Height, type, tx, ty)
                        && Placement.IsLegal(frame.Buildable, game.Session.Map.Width, game.Session.Map.Height, type, tx - 1, ty - 1))
                    {
                        int x = (tx * 2048) + (type.FootprintWidth * 1024), y = (ty * 2048) + (type.FootprintHeight * 1024);
                        game.CenterOn(x, y);
                        return game.ToScreen(x, y);
                    }
                }
            }
        }

        return Screen(game, castle);
    }

    private static FrameEntity Own(GameScreen game, string cls)
        => game.Frame!.Entities.ToArray().First(e => e.Owner == game.Session.LocalPlayer && game.Catalog.Type(e.TypeId).Class == cls);

    private static FrameEntity Nearest(GameScreen game, FrameEntity from, Func<FrameEntity, bool> match)
        => game.Frame!.Entities.ToArray().Where(match).OrderBy(e => ((long)(e.X - from.X) * (e.X - from.X)) + ((long)(e.Y - from.Y) * (e.Y - from.Y))).First();

    private static Vector2 Screen(GameScreen game, FrameEntity e) => game.ToScreen(e.X, e.Y - 600);

    private static void Log(GameScreen game, string what)
    {
        GameFrame frame = game.Frame!;
        FramePlayer p = frame.Players[game.Session.LocalPlayer];
        GD.Print($"AUTOPLAY t={frame.Tick.Value} {what}; pop {p.Population}/{p.PopulationCap}; res {string.Join(",", Enumerable.Range(0, 5).Select(r => p.Resources[r]))}; stats trained {game.Stats.Trained} built {game.Stats.Built} gathered {game.Stats.Gathered}; sprites {game.World?.DrawnSprites}");
    }

    private async Task MoveMouse(GameScreen game, Vector2 at)
    {
        game.Hover(at);
        await Frames(3);
    }

    private async Task Shot(string name)
    {
        await Frames(2);
        Image image = _app.GetViewport().GetTexture().GetImage();
        string path = Path.Combine(_dir, $"{++_shot:00}-{name}.png");
        image.SavePng(path);
        GD.Print("AUTOPLAY screenshot " + path);
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++)
        {
            await _app.ToSignal(_app.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private async Task Seconds(GameScreen game, double gameSeconds)
    {
        int target = game.Session.TicksRun + (int)(gameSeconds * 1000 / 50);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (game.Session.TicksRun < target && watch.Elapsed.TotalSeconds < 240)
        {
            await Frames(1);
        }
    }
}
