using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Tzar2.Client.Editor;
using Tzar2.Content.Editing;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Editing;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// The editor mode of the client (TDD §18, DEC-11: one client, several modes; ADR 0150): the main menu's EDITOR entry,
/// <c>--editor [file.t2map]</c>, and the headless <c>--editor-smoke</c> run. The open document survives playtests: the
/// editor screen is rebuilt over the same document (and its undo history) when the player comes back.
/// </summary>
public partial class GameApp
{
    private MapDocument? _editorDocument;
    private ICampaignSource? _converted;
    private CampaignDocument? _editorCampaign;
    private DebuggerWindow? _debuggerWindow;
    private string? _editorPath;

    /// <summary>The editor screen, or null.</summary>
    public EditorScreen? EditorScreen => (_current as CanvasLayer)?.GetChildCount() > 0 ? (_current as CanvasLayer)!.GetChild(0) as EditorScreen : null;

    /// <summary>Opens the editor (with the document of the last editor session, if any).</summary>
    public void ShowEditor(string? file = null)
    {
        if (_rules is null)
        {
            GD.PrintErr("The editor needs a package whose classic ruleset compiles.");
            ShowMainMenu();
            return;
        }

        if (_debuggerWindow is not null)
        {
            _debuggerWindow.QueueFree();
            _debuggerWindow = null;
        }

        Debugger?.Dispose();
        Debugger = null;
        var screen = new EditorScreen(_store, _rules, _catalog, _editorDocument, _editorPath);
        screen.Closed += () =>
        {
            _editorDocument = null;
            _editorPath = null;
            _editorCampaign = null;
            ShowMainMenu();
        };
        screen.PlaytestRequested += (options, debug) => StartPlaytest(screen, options, debug);
        screen.CampaignPlayRequested += source =>
        {
            RememberEditor(screen);
            _converted = source;
            StartMission(source, null);
        };
        screen.CampaignPlaytestRequested += (source, map) =>
        {
            RememberEditor(screen);
            _editorCampaign = screen.OpenCampaignEditor().Campaign;
            _converted = source;
            StartMission(source, map);
        };
        if (_editorCampaign is { } campaign)
        {
            screen.Ready += () => screen.OpenCampaignEditor(campaign);
        }
        EditorOpened?.Invoke(screen);
        Replace(screen, music: null);
        if (file is not null)
        {
            screen.OpenFile(file);
        }
    }

    /// <summary>Raised when an editor screen is created, so the other editor parts (triggers, converter, campaign, playtest) attach to it.</summary>
    public event Action<EditorScreen>? EditorOpened;

    /// <summary>The trigger debugger of the running playtest, or null.</summary>
    public TriggerDebugger? Debugger { get; private set; }

    /// <summary>
    /// Plays the editor's map (TDD §18.3 playtest, ADR 0153): the document is built and started as a scenario match, as
    /// the chosen kingdom, optionally revealed, optionally with the trigger debugger; leaving the game returns to the
    /// editor with the same document and undo history.
    /// </summary>
    public GameSession? StartPlaytest(EditorScreen editor, PlaytestOptions options, bool debug)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (_rules is null)
        {
            return null;
        }

        RememberEditor(editor);
        GameSession session;
        try
        {
            session = Playtest.Start(editor.Document, _rules, options);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            editor.Status("The map cannot start: " + e.Message);
            return null;
        }

        var game = new GameScreen(_store, _ui!, session, editor.Document.TerrainSet ?? "grass", _args.SoundFont, _settings) { KeepSpeed = true };
        if (debug && session.Scenario is not null)
        {
            Debugger = new TriggerDebugger(session);
            _debuggerWindow = new DebuggerWindow(Debugger, session);
            AddChild(_debuggerWindow);
            _debuggerWindow.Show();
        }

        void Back() => ShowEditor();

        game.QuitRequested += Back;
        game.Finished += (_, _) => Back();
        game.MapRequested += notice => GD.Print("Playtest: the scenario asks for the next map '" + notice.Text + "'.");
        Replace(game, music: null);
        return session;
    }

    // Keeps the document while another screen (playtest) is shown.
    private void RememberEditor(EditorScreen screen)
    {
        _editorDocument = screen.Document;
        _editorPath = screen.FilePath;
    }

    private async Task EditorSmokeAsync()
    {
        int exit = 1;
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            EditorScreen screen = EditorScreen ?? throw new InvalidOperationException("No editor screen.");
            string dir = Path.Combine(Path.GetTempPath(), "tzar2-editor-smoke-" + System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            Tzar2.Client.Editor.EditorSmoke.ImportPath = _args.EditorImport;
            string line = await Tzar2.Client.Editor.EditorSmoke.RunAsync(this, screen, dir);
            GD.Print(line);
            exit = 0;
        }
        catch (Exception e)
        {
            GD.PrintErr("EDITOR SMOKE FAILED: " + e);
        }

        if (_current is not null)
        {
            RemoveChild(_current);
            _current.QueueFree();
            _current = null;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetTree().Quit(exit);
    }
}
