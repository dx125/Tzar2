using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Import.Editions;
using Tzar2.Import.Pipeline;

namespace Tzar2.Client.Viewer;

/// <summary>
/// The first-run import (TDD §12.2, REQ-IMP; ADR 0241): shown when no imported package is configured or it cannot be
/// opened. Lists the original installs found on this machine, imports the one the player picks (or a folder chosen by
/// hand) into the user's data, and opens it; an already imported package can be chosen too.
/// </summary>
public partial class FirstRunScreen : Control
{
    /// <summary>The explanation shown on the screen.</summary>
    public const string Explanation =
        "Tzar2 does not ship any of the original game's art, sound or data. They are imported once from your own copy "
        + "of TZAR: The Burden of the Crown (GOG, Steam or CD). Your copy is only read, never changed.\n\n"
        + "Pick your installation below, or choose its folder (the one with Tzar.exe). The import takes a few minutes.";

    private readonly string? _error;
    private readonly string _userDir;
    private VBoxContainer? _box;
    private Label? _status;
    private readonly List<Button> _buttons = [];
    private bool _importing;

    /// <summary>
    /// Creates the screen; <paramref name="error"/> explains why the configured package could not be opened,
    /// <paramref name="userDir"/> is where an import goes.
    /// </summary>
    public FirstRunScreen(string? error, string userDir)
    {
        _error = error;
        _userDir = userDir;
    }

    /// <summary>Parameterless constructor required by Godot; not used.</summary>
    public FirstRunScreen()
        : this(null, ProjectSettings.GlobalizePath("user://"))
    {
    }

    /// <summary>Raised with the chosen or imported package path.</summary>
    public event Action<string>? PackageChosen;

    /// <inheritdoc/>
    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);
        _box = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) };
        center.AddChild(_box);
        _box.AddChild(new Label { Text = "Import the original game", ThemeTypeVariation = "HeaderLarge" });
        _box.AddChild(new Label { Text = Explanation, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        if (!string.IsNullOrEmpty(_error))
        {
            _box.AddChild(new Label { Text = "Could not open the configured package:\n" + _error, Modulate = Color.Color8(255, 128, 128), AutowrapMode = TextServer.AutowrapMode.WordSmart });
        }

        IReadOnlyList<FoundInstall> found = [];
        try
        {
            found = FirstRunImport.FindInstalls();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ClientLog.Warn("first run: searching for the original failed: " + e.Message);
        }

        ClientLog.Info($"first run: {found.Count} original install(s) found");
        foreach (FoundInstall install in found)
        {
            AddButton($"Import from {install.Path}  ({install.Source})", () => StartImport(install.Path));
        }

        if (found.Count == 0)
        {
            _box.AddChild(new Label { Text = "No installation was found automatically.", Modulate = new Color(0.85f, 0.85f, 0.75f) });
        }

        var folderDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Choose the folder of your TZAR installation",
            UseNativeDialog = true,
        };
        folderDialog.DirSelected += dir =>
        {
            if (InstallLocator.IsInstall(dir))
            {
                StartImport(dir);
            }
            else
            {
                SetStatus($"{dir} does not look like TZAR (no Tzar.exe in it).", error: true);
            }
        };
        AddChild(folderDialog);
        AddButton("Choose the game's folder…", () => folderDialog.PopupCentered(new Vector2I(900, 600)));

        var packageDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = ["*.t2pkg ; Tzar2 package"],
            Title = "Choose an imported package",
            UseNativeDialog = false,
        };
        packageDialog.FileSelected += path => PackageChosen?.Invoke(path);
        AddChild(packageDialog);
        AddButton("Choose an already imported package…", () => packageDialog.PopupCentered(new Vector2I(900, 600)));

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _box.AddChild(_status);
    }

    /// <summary>Imports <paramref name="origin"/> and raises <see cref="PackageChosen"/> when it succeeded.</summary>
    public async void StartImport(string origin)
    {
        if (_importing)
        {
            return;
        }

        _importing = true;
        foreach (Button button in _buttons)
        {
            button.Disabled = true;
        }

        ClientLog.Info("first run: importing " + origin);
        SetStatus("Importing " + origin + " …", error: false);
        try
        {
            ImportResult result = await FirstRunImport.RunAsync(origin, _userDir, line => Callable.From(() => SetStatus(line, error: false)).CallDeferred());
            string summary = FirstRunImport.Summary(result);
            ClientLog.Info("first run: " + summary);
            SetStatus(summary, !result.Report.Succeeded);
            if (result.Report.Succeeded)
            {
                PackageChosen?.Invoke(result.PackagePath);
                return;
            }
        }
        catch (Exception e)
        {
            // Any failure of the importer ends here, so that a bad install never takes the game down.
            ClientLog.Error("first run: the import failed: " + e);
            SetStatus("The import failed: " + e.Message, error: true);
        }

        _importing = false;
        foreach (Button button in _buttons)
        {
            button.Disabled = false;
        }
    }

    private void AddButton(string text, Action pressed)
    {
        var button = new Button { Text = text, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        button.Pressed += pressed;
        _box!.AddChild(button);
        _buttons.Add(button);
    }

    private void SetStatus(string text, bool error)
    {
        if (_status is not null && IsInstanceValid(_status))
        {
            _status.Text = text;
            _status.Modulate = error ? Color.Color8(255, 128, 128) : Colors.White;
        }
    }
}
