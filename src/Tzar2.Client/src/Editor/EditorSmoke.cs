using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Tzar2.Content.Compiled;
using Tzar2.Content.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// The editor's headless smoke run (<c>--editor-smoke</c>): drives the real editor screen through the controller the way
/// the mouse does (terrain strokes, placement, walls, trees, starting locations, undo/redo), validates, saves, reopens and
/// checks that the reopened map saves to the same bytes. Hooks registered by the other editor parts run as well.
/// </summary>
public static class EditorSmoke
{
    /// <summary>A legacy file the smoke run imports through the converter UI (<c>--editor-import</c>), or null.</summary>
    public static string? ImportPath { get; set; }

    /// <summary>Extra smoke steps of later editor parts; each returns a short "name=value" note.</summary>
    public static event Func<Node, EditorScreen, string, Task<string>>? Steps;

    /// <summary>Runs the smoke steps; returns the <c>EDITOR SMOKE OK …</c> line or throws.</summary>
    public static async Task<string> RunAsync(Node host, EditorScreen screen, string dir)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(screen);
        EditorController c = screen.Controller;
        Ruleset rules = c.Ruleset;
        screen.Open(NewMap.Empty(new NewMapOptions("Smoke", 48, screen.Document.TerrainSet), screen.RulesFor(screen.Document.TerrainSet), rules), null);
        await Frame(host);

        // A lake by brush strokes (one undo step per stroke).
        c.Tool = EditorTool.Terrain;
        c.TerrainValue = screen.Document.Rules.Palette.First(p => p.IsWater).Index;
        c.BrushSize = 3;
        c.PointerDown(new MapPoint(20, 20, 20, 20), EditorModifiers.None);
        for (int x = 21; x <= 28; x++)
        {
            c.PointerDrag(new MapPoint(x, 22, x, 22));
            await Frame(host);
        }

        c.PointerUp(new MapPoint(28, 22, 28, 22), EditorModifiers.None);
        Require(screen.Document.History.UndoLabel == "Paint terrain", "terrain stroke recorded");

        // A castle, a peasant, a wall by drag, trees, a starting location.
        c.Tool = EditorTool.Place;
        c.PlaceOwner = 0;
        c.PlaceKey = rules.EntityTypes.First(t => t.Key.EndsWith(".castle", StringComparison.Ordinal)).Key;
        c.PointerDown(new MapPoint(4, 4, 4, 4), EditorModifiers.None);
        c.PointerUp(new MapPoint(4, 4, 4, 4), EditorModifiers.None);
        c.PlaceKey = rules.EntityTypes.First(t => t.Key.EndsWith(".peon", StringComparison.Ordinal)).Key;
        c.PointerDown(new MapPoint(11, 5, 11, 5), EditorModifiers.None);
        c.PointerUp(new MapPoint(11, 5, 11, 5), EditorModifiers.None);
        if (rules.EntityTypes.FirstOrDefault(t => t.Traits.IsWall) is { } wall)
        {
            c.PlaceKey = wall.Key;
            c.PointerDown(new MapPoint(2, 14, 2, 14), EditorModifiers.None);
            c.PointerUp(new MapPoint(12, 14, 12, 14), EditorModifiers.None);
        }

        c.Tool = EditorTool.Trees;
        c.PointerDown(new MapPoint(38, 8, 38, 8), EditorModifiers.None);
        c.PointerUp(new MapPoint(38, 8, 38, 8), EditorModifiers.None);
        c.PointerHover(new MapPoint(6, 10, 6, 10));
        c.Key("1", EditorModifiers.None);
        await Frame(host);
        int objects = screen.Document.State.Objects.Count;
        Require(objects >= 3, "objects placed");
        Require(screen.Document.State.Scenario.Kingdom(1)?.StartTileX == 6, "starting location set");

        // Select by rubber band and move with Alt-drag.
        c.Tool = EditorTool.Select;
        c.PointerDown(new MapPoint(10, 4, 10, 4), EditorModifiers.None);
        c.PointerUp(new MapPoint(12, 6, 12, 6), EditorModifiers.None);
        Require(c.Selection.Length == 1, "rubber band selects the peasant");
        c.PointerDown(new MapPoint(11, 5, 11, 5), EditorModifiers.Alt);
        c.PointerDrag(new MapPoint(13, 5, 13, 5));
        c.PointerUp(new MapPoint(13, 5, 13, 5), EditorModifiers.Alt);
        Require(screen.Document.State.Find(c.Selection[0])!.TileX == 13, "Alt-drag moves");

        ValidationResult result = screen.Revalidate();
        await Shot(host, "1-map");

        // Save, reopen, save again: the same bytes.
        string path = Path.Combine(dir, "smoke.t2map");
        Require(screen.SaveTo(path), "saved");
        byte[] first = File.ReadAllBytes(path);
        Require(screen.OpenFile(path), "reopened");
        await Frame(host);
        Require(MapPackage.Write(screen.Document).AsSpan().SequenceEqual(first), "byte-identical round trip");

        // Undo and redo on the reopened map.
        screen.Document.Apply("Smoke edit", s => s.WithScenario(m => m with { Properties = m.Properties with { Title = "Smoke 2" } }));
        screen.Undo();
        Require(screen.Document.State.Scenario.Properties.Title != "Smoke 2", "undo");
        screen.Redo();
        Require(screen.Document.State.Scenario.Properties.Title == "Smoke 2", "redo");

        // Triggers: a rule with a quest, edited through the model, shown in the rule editor, then a debugged playtest.
        screen.Document.Apply("Smoke rule", s => s.WithScenario(m => m
            .AddAction(new Content.Compiled.Scenario.AddQuestAction("Smoke quest", "Hold on.", null), out int quest)
            .AddRule(new Content.Compiled.Scenario.RuleDef("smoke", ScenarioModel.Source("1"), new[] { quest }), out _)));
        TriggerWindow triggers = screen.OpenTriggers();
        await Frame(host);
        await Frame(host);
        await Shot(host, "2-triggers");
        triggers.QueueFree();
        string play = string.Empty;
        if (host is Game.GameApp app)
        {
            Tzar2.Game.Session.GameSession? session = app.StartPlaytest(screen, new Tzar2.Game.Editing.PlaytestOptions(RevealMap: true), debug: true);
            Require(session is not null, "playtest starts");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (session!.TicksRun < 40 && watch.Elapsed.TotalSeconds < 60 && session.Fault is null)
            {
                await Frame(host);
            }

            Require(session.Fault is null && session.TicksRun >= 40, "playtest runs");
            await Shot(host, "3-playtest");
            Require(app.Debugger?.Watch.Rules[0].LastValue == 1, "debugger sees the rule fire");
            play = $" playtest={session.TicksRun}";
            app.ShowEditor();
            await Frame(host);
            screen = app.EditorScreen ?? throw new InvalidOperationException("no editor after the playtest");
            Require(screen.Document.State.Scenario.Rules.Length == 1, "the document survives the playtest");
        }

        // Campaign editor: two maps, saved, reopened, byte-identical.
        CampaignWindow campaignWindow = screen.OpenCampaignEditor(CampaignDocument.New("Smoke saga"));
        campaignWindow.Campaign.AddMap(screen.Document, entry: true);
        campaignWindow.Campaign.AddMap(NewMap.Empty(new NewMapOptions("Second", 32, screen.Document.TerrainSet), screen.RulesFor(screen.Document.TerrainSet), rules));
        await Frame(host);
        await Frame(host);
        await Shot(host, "5-campaign");
        string campaignPath = Path.Combine(dir, "smoke.t2camp");
        Require(campaignWindow.SaveTo(campaignPath), "campaign saved");
        byte[] campaignBytes = File.ReadAllBytes(campaignPath);
        Require(campaignWindow.OpenFile(campaignPath), "campaign reopened");
        Require(CampaignPackage.Write(campaignWindow.Campaign).AsSpan().SequenceEqual(campaignBytes), "campaign round trip");
        campaignWindow.QueueFree();
        string notes = play + $" campaign={campaignBytes.Length}";
        if (ImportPath is { } legacy)
        {
            ImportWindow import = screen.OpenImport();
            await Frame(host);
            import.AddPaths([legacy]);
            import.Convert();
            await Frame(host);
            Require(import.Session.Items.Count == 1 && import.Session.Items[0].Succeeded, "legacy import converts");
            await Shot(host, "4-import");
            Tzar2.Import.Editing.ImportItem item = import.Session.Items[0];
            if (!item.IsCampaign)
            {
                screen.Open(item.OpenDocument(screen.RulesFor), null);
                await Frame(host);
                Require(screen.Document.State.Objects.Count > 0, "the converted map opens in the editor");
            }

            import.QueueFree();
            notes += $" import={item.OutputName}";
        }
        if (Steps is not null)
        {
            foreach (Func<Node, EditorScreen, string, Task<string>> step in Steps.GetInvocationList().Cast<Func<Node, EditorScreen, string, Task<string>>>())
            {
                notes += " " + await step(host, screen, dir);
            }
        }

        return $"EDITOR SMOKE OK objects={objects} bytes={first.Length} issues={result.Issues.Length} blocking={result.Count(ValidationSeverity.Blocking)}{notes}";
    }

    /// <summary>Saves a screenshot to <c>$TZAR2_EDITOR_SHOTS/&lt;name&gt;.png</c> when that variable is set (local visual checks).</summary>
    public static async Task Shot(Node host, string name)
    {
        ArgumentNullException.ThrowIfNull(host);
        string? dir = System.Environment.GetEnvironmentVariable("TZAR2_EDITOR_SHOTS");
        if (string.IsNullOrEmpty(dir) || DisplayServer.GetName() == "headless")
        {
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            await Frame(host);
        }

        Directory.CreateDirectory(dir);
        foreach (Window w in host.GetTree().Root.GetChildren(includeInternal: false).OfType<Window>().Prepend(host.GetTree().Root))
        {
            Image? image = w.GetTexture()?.GetImage();
            image?.SavePng(Path.Combine(dir, name + (w == host.GetTree().Root ? string.Empty : "-" + w.Name) + ".png"));
        }
    }

    /// <summary>Waits one frame.</summary>
    public static async Task Frame(Node host)
    {
        ArgumentNullException.ThrowIfNull(host);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>Throws when <paramref name="ok"/> is false.</summary>
    public static void Require(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException("editor smoke: " + what + " failed");
        }
    }
}
