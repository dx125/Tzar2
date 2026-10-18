using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Tzar2.Client.Editor;
using Tzar2.Content.Editing;

namespace Tzar2.Client.Game;

/// <summary>
/// The editor mode of the client (TDD §18, DEC-11: one client, several modes; ADR 0150): the main menu's EDITOR entry,
/// <c>--editor [file.t2map]</c>, and the headless <c>--editor-smoke</c> run. The open document survives playtests: the
/// editor screen is rebuilt over the same document (and its undo history) when the player comes back.
/// </summary>
public partial class GameApp
{
    private MapDocument? _editorDocument;
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

        var screen = new EditorScreen(_store, _rules, _catalog, _editorDocument, _editorPath);
        screen.Closed += () =>
        {
            _editorDocument = null;
            _editorPath = null;
            ShowMainMenu();
        };
        EditorOpened?.Invoke(screen);
        Replace(screen, music: null);
        if (file is not null)
        {
            screen.OpenFile(file);
        }
    }

    /// <summary>Raised when an editor screen is created, so the other editor parts (triggers, converter, campaign, playtest) attach to it.</summary>
    public event Action<EditorScreen>? EditorOpened;

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
