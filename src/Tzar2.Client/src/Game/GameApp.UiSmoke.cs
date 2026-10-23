using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// <c>--ui-smoke</c> (ADR 0200): opens every screen and dialog of the shell, the game and the editor at every display of
/// the UI review (<see cref="DisplayCases"/>) and audits its layout (<see cref="LayoutAudit"/>): nothing may leave the
/// window, and no texts, buttons or fields may cover one another. Prints a <c>UI ISSUE</c> line per finding and
/// <c>UI SMOKE DONE</c> with the counts, then quits with 0 (the test decides which findings are known). With
/// <c>--ui-shots</c> it also saves a screenshot of each screen at its logical size (needs a renderer).
/// </summary>
public partial class GameApp
{
    // The display the UI smoke test sets instead of the window's (null: the window decides).
    private DisplayCase? _display;
    private int _uiAudits;
    private int _uiIssues;

    private async Task UiSmokeAsync()
    {
        int exit = 1;
        try
        {
            List<DisplayCase> displays = DisplayCases.All();
            await Frames(2);

            // The shell: menus and dialogs of the main menu, each built fresh at each display.
            _customMaps ??= OriginalMaps();
            (string Name, Func<bool> Show)[] shell =
            [
                ("main-menu", () => Show(ShowMainMenu)),
                ("settings", () => Show(ShowSettings)),
                ("credits", () => Show(ShowCredits)),
                ("single-player", () => Show(ShowSinglePlayer)),
                ("choose-map", () => Show(ShowCustomMaps)),
                ("map-setup", () => _customMaps.Count > 0 && Show(() => ShowMapSetup(_customMaps[0]))),
                ("random-map", () => Show(ShowSetup)),
                ("campaigns", () => Show(ShowCampaigns)),
                ("load-game", () => Show(ShowLoadGame)),
                ("multiplayer", () => Show(ShowMultiplayer)),
                ("replays", () => Show(ShowReplays)),
                ("loading", () => Show(() => ShowLoading(LoadingStage.NewGame, "Bridge too far"))),
                ("victory", () => Show(() => ShowResults(MatchOutcome.Victory, SampleStats()))),
                ("defeat", () => Show(() => ShowResults(MatchOutcome.Defeat, SampleStats()))),
            ];
            foreach ((string name, Func<bool> show) in shell)
            {
                foreach (DisplayCase d in displays.Where(d => Wanted(d, name)))
                {
                    await SetDisplay(d);
                    if (!show())
                    {
                        GD.Print($"UI SKIP {name}: not available with this package");
                        break;
                    }

                    if (name != "loading")
                    {
                        await Until(() => Current is not LoadingScreen, 120, name); // campaigns load behind the loading screen
                    }

                    await AuditAsync(name, d, _current);
                }
            }

            if (displays.Any(d => Wanted(d, "lobby")))
            {
                await LobbyAuditAsync(displays);
            }

            // The game: a skirmish on the test map, the HUD with nothing, one building and many units selected, then
            // each in-game dialog over it.
            if (_rules is not null && displays.Any(d => GameScreen.AuditDialogs.Append("hud").Any(n => Wanted(d, n))))
            {
                await GameAuditAsync(displays);
            }

            // The editor and its windows.
            if (_rules is not null && displays.Any(d => Wanted(d, "editor")))
            {
                await EditorAuditAsync(displays);
            }

            GD.Print($"UI SMOKE DONE audits={_uiAudits} issues={_uiIssues} displays={displays.Count}");
            exit = 0;
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or NullReferenceException or System.Net.Sockets.SocketException)
        {
            GD.PrintErr("UI SMOKE FAILED: " + e);
        }

        LeaveNetwork();
        if (_current is not null)
        {
            RemoveChild(_current);
            _current.QueueFree();
            _current = null;
        }

        await Frames(2);
        GetTree().Quit(exit);
    }

    private bool Wanted(DisplayCase d, string screen) => _args.UiOnly is not { } only || d.Name.Contains(only, StringComparison.Ordinal) || screen.Contains(only, StringComparison.Ordinal);

    private static bool Show(Action show)
    {
        show();
        return true;
    }

    private static MatchStats SampleStats() => new() { Seconds = 4321, Trained = 123, Built = 45, Gathered = 98765, Kills = 678, Losses = 90 };

    private async Task LobbyAuditAsync(List<DisplayCase> displays)
    {
        ShowMultiplayer();
        _lanHost = LanHost.Start(relayPort: 0, discoveryPort: 0, desyncDirectory: UserPath("desync"));
        OpenLobby(() => NetworkMatch.Create(NetworkOptions("127.0.0.1", _lanHost.Port), "UI review", 4, isPublic: true));
        NetworkMatch match = _network ?? throw new InvalidOperationException("The lobby did not open.");
        await Until(() => match.Lobby is not null, 10, "lobby");
        match.SetSlot(1, SlotKind.Computer, new SlotChoice("euro", 0, Difficulty.Hard));
        await Until(() => match.Lobby!.Slots[1].Kind == SlotKind.Computer, 10, "computer slot");
        foreach (DisplayCase d in displays.Where(d => Wanted(d, "lobby")))
        {
            await SetDisplay(d);
            await AuditAsync("lobby", d, _current);
        }

        LeaveNetwork();
    }

    private async Task GameAuditAsync(List<DisplayCase> displays)
    {
        await SetDisplay(displays[0]);
        StartSkirmish(new SkirmishOptions { TestMapSize = 48, Players = 3 });
        await Until(() => Game?.Frame is not null && Game.Hud is not null, 60, "the game screen");
        GameScreen game = Game!;
        game.Session.Paused = true;
        byte me = game.Session.LocalPlayer;
        FrameEntity[] own = game.Frame!.Entities.ToArray().Where(e => e.Owner == me).ToArray();
        FrameEntity? castle = own.FirstOrDefault(e => game.Catalog.Type(e.TypeId).Class == "castle");
        foreach (DisplayCase d in displays)
        {
            await SetDisplay(d);
            if (Wanted(d, "hud"))
            {
                game.Selection.Clear();
                await AuditAsync("hud", d, game);
                if (castle is { } c)
                {
                    game.Selection.SelectOwn([c.Id]);
                    await AuditAsync("hud-building", d, game);
                }

                game.Selection.SelectOwn([.. own.Select(e => e.Id)]);
                await AuditAsync("hud-many", d, game);
                game.Selection.Clear();
            }

            foreach (string dialog in GameScreen.AuditDialogs.Where(n => Wanted(d, n)))
            {
                if (dialog is "gamble" or "exchange" or "trade" or "craft" && castle is { } c2)
                {
                    game.Selection.SelectOwn([c2.Id]); // the card's dialogs open in the card's place, for a selection
                }

                if (game.OpenForAudit(dialog))
                {
                    await AuditAsync(dialog, d, game);
                }
                else if (d == displays[0])
                {
                    GD.Print($"UI SKIP {dialog}: not available in this match");
                }

                game.CloseAuditDialogs();
                game.Selection.Clear();
                await Frames(1);
            }
        }

        game.Session.Stop();
    }

    private async Task EditorAuditAsync(List<DisplayCase> displays)
    {
        foreach (DisplayCase d in displays.Where(d => Wanted(d, "editor")))
        {
            await SetDisplay(d);
            ShowEditor();
            await Frames(3);
            Editor.EditorScreen editor = EditorScreen ?? throw new InvalidOperationException("No editor screen.");
            await AuditAsync("editor", d, _current);
            (string Name, Func<Window> Open)[] windows =
            [
                ("editor-triggers", editor.OpenTriggers),
                ("editor-import", editor.OpenImport),
                ("editor-campaign", () => editor.OpenCampaignEditor()),
            ];
            foreach ((string name, Func<Window> open) in windows)
            {
                Window window = open();
                await Frames(3);
                await AuditAsync(name, d, _current);
                window.Hide();
            }
        }
    }

    // Sets the display (and, for screenshots, a window of the logical size, so that a shot is 1:1) and lets it settle.
    private async Task SetDisplay(DisplayCase d)
    {
        _display = d;
        if (_args.UiShots is not null)
        {
            GetTree().Root.Size = new Vector2I(d.Width, d.Height);
        }

        Relayout();
        await Frames(2);
    }

    private async Task AuditAsync(string screen, DisplayCase d, Node? root)
    {
        await Frames(2); // containers sort and sizes settle a frame after a change
        if (root is null)
        {
            throw new InvalidOperationException($"Nothing shown for {screen}.");
        }

        List<AuditIssue> issues = LayoutAudit.Check(d.Width, d.Height, d.Visible, UiAudit.Collect(root));
        _uiAudits++;
        _uiIssues += issues.Count;
        foreach (AuditIssue issue in issues)
        {
            GD.Print($"UI ISSUE {d.Name} {screen} {issue}");
        }

        if (_args.UiShots is { } dir)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Image image = GetViewport().GetTexture().GetImage();
            Directory.CreateDirectory(dir);
            if (image.GetSize() == new Vector2I(d.Width, d.Height))
            {
                image.SavePng(Path.Combine(dir, $"{screen}-{d.Name}.png"));
            }
        }
    }

    private async Task Frames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
