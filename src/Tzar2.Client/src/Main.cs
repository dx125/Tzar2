using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Client.Viewer;
using Tzar2.Game;

namespace Tzar2.Client;

/// <summary>
/// Client entry point. In M1 the only mode is the asset viewer (TDD §25 M1); without a configured package the
/// first-run screen explains how to import one. Command line: see <see cref="ViewerArgs.Usage"/>.
/// </summary>
public partial class Main : Node
{
    private ViewerArgs _args = new();
    private ViewerSettings? _settings;
    private ContentStore? _store;
    private Node? _screen;

    /// <inheritdoc/>
    public override void _Ready()
    {
        GD.Print($"{GameInfo.Codename} {GameInfo.Version} - asset viewer (M1)");
        _args = ViewerArgs.Parse(OS.GetCmdlineUserArgs());
        if (_args.Help || _args.Errors.Count > 0)
        {
            foreach (string error in _args.Errors)
            {
                GD.PrintErr(error);
            }

            GD.Print(ViewerArgs.Usage);
            GetTree().Quit(_args.Help ? 0 : 2);
            return;
        }

        _settings = new ViewerSettings(_args.Config);
        string? package = _args.Package ?? (_settings.Package.Length > 0 ? _settings.Package : null);
        if (package is null)
        {
            ShowFirstRun(null);
        }
        else
        {
            Open(package, remember: _args.Package is null);
        }

        if (_args.Smoke || _args.CaptureDir is not null)
        {
            _ = RunAutomationAsync();
        }
    }

    /// <inheritdoc/>
    public override void _ExitTree() => _store?.Dispose();

    private void Open(string path, bool remember)
    {
        ContentStore store;
        try
        {
            store = ContentStore.Open(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.PrintErr($"Cannot open package '{path}': {e.Message}");
            ShowFirstRun($"{path}\n{e.Message}");
            return;
        }

        if (remember)
        {
            _settings!.Package = path;
        }

        ReplaceScreen(null);
        _store?.Dispose();
        _store = store;
        var viewer = new AssetViewer(store, _settings!, _args.SoundFont);
        viewer.OpenPackageRequested += () => ShowFirstRun(null);
        ReplaceScreen(viewer);
        if (_args.Select is { } select && !_args.Smoke)
        {
            viewer.Sprites.Select(select);
        }

        GD.Print($"Opened {path}");
    }

    private void ShowFirstRun(string? error)
    {
        var screen = new FirstRunScreen(error);
        screen.PackageChosen += path =>
        {
            _settings!.Package = path;
            Open(path, remember: true);
        };
        var layer = new CanvasLayer { Layer = 2 };
        layer.AddChild(screen);
        ReplaceScreen(layer);
    }

    private void ReplaceScreen(Node? screen)
    {
        if (_screen is not null)
        {
            RemoveChild(_screen);
            _screen.QueueFree();
        }

        _screen = screen;
        if (screen is not null)
        {
            AddChild(screen);
        }
    }

    private async Task RunAutomationAsync()
    {
        int exit = 0;
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_screen is not AssetViewer viewer)
            {
                GD.Print(ViewerAutomation.SmokeFirstRun);
                exit = _args.CaptureDir is null ? 0 : 1;
            }
            else if (_args.CaptureDir is { } dir)
            {
                await ViewerAutomation.CaptureAsync(this, viewer, dir, _args.Select);
            }
            else
            {
                GD.Print(await ViewerAutomation.SmokeAsync(this, viewer, _args.Select));
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"VIEWER AUTOMATION FAILED: {e}");
            exit = 1;
        }

        // Free the screen (and its RenderingServer resources) before quitting, so the exit is clean.
        ReplaceScreen(null);
        _store?.Dispose();
        _store = null;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetTree().Quit(exit);
    }
}
