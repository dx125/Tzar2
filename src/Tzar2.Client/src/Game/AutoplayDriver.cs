using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Content.Compiled;
using Tzar2.Game.Input;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;

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
            DisplayServer.WindowSetSize(GameApp.ClassicScreen);
            await Frames(30);
            GameScreen game = _app.Game ?? throw new InvalidOperationException("No game.");
            game.EdgeScroll = false;
            game.Scripted = true;
            await Shot("start");

            // 0. Real mouse events through the viewport (not the driver's shortcuts): a click on a peasant's head selects
            //    it (the hit box covers the drawn figure), a right-button drag moves the map and gives no order.
            FrameEntity first = Own(game, "peon");
            game.CenterOn(first.X, first.Y);
            await Frames(3);
            game.Scripted = false;
            await RealButton(game.ToScreen(first.X, first.Y - 1500), game.ToScreen(first.X, first.Y - 1500), MouseButton.Left);
            int picked = game.Selection.Selected.Count;
            Vector2I panBefore = game.Pan;
            await RealButton(game.ToScreen(first.X + 12000, first.Y + 6000), game.ToScreen(first.X + 12000, first.Y + 6000) + new Vector2(-120, -60), MouseButton.Right);
            Vector2I panAfter = game.Pan;
            game.Scripted = true;
            Log(game, $"real click on a peasant's head: {picked} selected; right drag moved the view by {panAfter - panBefore}");
            if (picked != 1 || panAfter - panBefore != new Vector2I(120, 60))
            {
                throw new InvalidOperationException("Real mouse input failed: a click on a unit's figure must select it and a right drag must move the map.");
            }

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
            Log(game, "peasant card: " + CardText(game));
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
            Log(game, "castle card: " + CardText(game));

            // 5. Drag-box the footmen, Ctrl+1, move-advance toward the map centre.
            // Random maps start with peasants only: then the peasants that are not chopping play the army.
            bool soldiers = game.Frame!.Entities.ToArray().Any(e => e.Owner == game.Session.LocalPlayer && game.Catalog.Type(e.TypeId).Class == "footman");
            FrameEntity foot = soldiers ? Own(game, "footman") : Own(game, "peon");
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
            // The enemy castle is in the fog (not in the frame): its start position comes from the map, as a player knows it.
            Tzar2.Content.Compiled.MapObject enemy = game.Session.Map.Objects.First(o => o.Owner != game.Session.LocalPlayer && o.Owner < 8 && o.TypeKey.EndsWith(".castle", StringComparison.Ordinal));
            game.CenterOn(enemy.X, enemy.Y);
            await Frames(3);
            await Shot("fog-enemy-base");

            // 7. Group 1 attacks the enemy castle (right click on it); watch the fight.
            game.Press(Key.Key1);
            await Frames(2);
            // The enemy base is off screen and in the fog: order through the minimap like a player would.
            // Ctrl+right click on the minimap: move-advance, fighting whatever comes into sight on the way.
            game.MinimapClick(new Vector2(enemy.X / (float)(game.Session.Map.Width * 2048), enemy.Y / (float)(game.Session.Map.Height * 2048)), right: true, KeyMods.Ctrl);
            Log(game, $"move-advance ordered with {game.Selection.Selected.Count}");

            // Castles take one point per melee hit under the classic placeholder resistance formula (UNK-08), so the
            // army is pointed at enemy units it can see (right click = attack), like a player would, until one dies.
            for (int i = 0; i < 30 && game.Stats.Kills == 0 && game.Selection.Selected.Count > 0; i++)
            {
                await Seconds(game, 10);
                if (VisibleEnemyUnit(game) is { } victim)
                {
                    game.CenterOn(victim.X, victim.Y);
                    await Frames(3);
                    game.Click(Screen(game, victim), MouseButton.Right);
                }
            }

            if (game.Selection.Selected.Count > 0 && game.Info(game.Selection.Selected[0]) is { } lead
                && game.Frame!.Entities.ToArray().FirstOrDefault(e => e.Id == lead.Id) is { MaxHealth: > 0 } f0)
            {
                game.CenterOn(f0.X, f0.Y);
            }

            await Frames(3);
            await Shot("battle");
            Log(game, $"battle: kills {game.Stats.Kills}, losses {game.Stats.Losses}, sounds {game.Mixer?.Played}");
            if (game.Stats.Kills == 0 && soldiers)
            {
                throw new InvalidOperationException("The battle ended without a single kill.");
            }

            game.CenterOn(castle.X, castle.Y);
            await Seconds(game, 300);
            await Shot("night");
            game.Session.SpeedIndex = 3;
            game.ForceRain = true;
            await Frames(20);
            await Shot("rain");
            game.ForceRain = false;
            game.Press(Key.F9);
            await Frames(3);
            await Shot("options");
            game.GetNodeOrNull("Options")?.QueueFree();
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

            // Single player: the menu, the original maps (converted behind the loading screen), a map's setup, its match.
            _app.ShowSinglePlayer();
            await Frames(5);
            await Shot("single-player");
            _app.ShowCustomMaps();
            for (int i = 0; i < 600 && _app.Current is not ChooseMapScreen; i++)
            {
                await Frames(1);
            }

            await Frames(5);
            await Shot("choose-map");
            Vector2 art = (Vector2)(GameApp.Screen - new Vector2I(800, 600)) / 2;
            Vector2 play = art + new Vector2(535 + 117, 544 + 12);
            await RealButton(play, play, MouseButton.Left);
            await Frames(5);
            if (_app.Current is MapSetupScreen)
            {
                await Shot("map-setup");
                Vector2 start = art + new Vector2(555 + 105, 499 + 13);
                await RealButton(start, start, MouseButton.Left);
                for (int i = 0; i < 900 && _app.Game is null; i++)
                {
                    await Frames(1);
                }

                if (_app.Game is { } custom)
                {
                    await Seconds(custom, 3);
                    await Shot("custom-map");
                    Log(custom, "custom map");
                }
            }
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

    private static string CardText(GameScreen game)
        => string.Join(" ", game.Card.Select(c => c is { } s ? $"{s.Button.Hotkey}:{s.Button.Order}{(s.Enabled ? string.Empty : "(off)")}" : "-"));

    private static FrameEntity? VisibleEnemyUnit(GameScreen game)
    {
        GameFrame frame = game.Frame!;
        int width = game.Session.Map.Width;
        FrameEntity? best = null;
        long bestD = long.MaxValue;
        FrameEntity[] all = frame.Entities.ToArray();
        FrameEntity? lead = game.Selection.Selected.Count > 0 ? all.FirstOrDefault(e => e.Id == game.Selection.Selected[0]) : null;
        foreach (FrameEntity e in all)
        {
            bool mobile = game.Session.Ruleset.EntityTypes[e.TypeId].IsMobile;
            int tile = ((e.Y / 2048) * width) + (e.X / 2048);
            if (e.Owner == game.Session.LocalPlayer || e.Owner >= 8 || !mobile || e.Health <= 0 || (frame.Visibility.Length > 0 && frame.Visibility[tile] != 2))
            {
                continue;
            }

            long dx = e.X - (lead?.X ?? e.X), dy = e.Y - (lead?.Y ?? e.Y), d = (dx * dx) + (dy * dy);
            if (d < bestD)
            {
                bestD = d;
                best = e;
            }
        }

        return best;
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

    // A press at `from`, the pointer moved in steps to `to`, a release there, as window events through Input (the
    // viewport turns them into the game's logical coordinates).
    private async Task RealButton(Vector2 from, Vector2 to, MouseButton button)
    {
        Transform2D stretch = _app.GetViewport().GetFinalTransform();
        Vector2 a = stretch * from, b = stretch * to;
        MouseButtonMask mask = button == MouseButton.Left ? MouseButtonMask.Left : button == MouseButton.Right ? MouseButtonMask.Right : MouseButtonMask.Middle;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = a, GlobalPosition = a });
        await Frames(1);
        Input.ParseInputEvent(new InputEventMouseButton { Position = a, GlobalPosition = a, ButtonIndex = button, Pressed = true, ButtonMask = mask });
        await Frames(1);
        for (int i = 1; i <= 6 && a != b; i++)
        {
            Vector2 p = a + ((b - a) * (i / 6f));
            Input.ParseInputEvent(new InputEventMouseMotion { Position = p, GlobalPosition = p, ButtonMask = mask });
            await Frames(1);
        }

        Input.ParseInputEvent(new InputEventMouseButton { Position = b, GlobalPosition = b, ButtonIndex = button, Pressed = false });
        await Frames(3);
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
