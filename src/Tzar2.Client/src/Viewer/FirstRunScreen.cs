using System;
using Godot;

namespace Tzar2.Client.Viewer;

/// <summary>
/// First-run hook (TDD §12.2; the full import wizard is a later milestone): shown when no imported package is
/// configured or it cannot be opened. Explains how to produce one with <c>t2 import</c> and lets the user pick it.
/// </summary>
public partial class FirstRunScreen : Control
{
    /// <summary>The explanation shown on the screen.</summary>
    public const string Explanation =
        "Tzar2 does not ship any of the original game's art, sound or data. They are imported from your own copy of "
        + "TZAR: The Burden of the Crown (GOG, Steam or CD).\n\n"
        + "1. Import your installation with the command-line tool (the import wizard comes in a later milestone):\n\n"
        + "      t2 import <path to the Tzar install> <output folder>\n\n"
        + "   From a source checkout:\n\n"
        + "      dotnet run --project src/Tzar2.Tools.Cli -- import <path to the Tzar install> <output folder>\n\n"
        + "2. Choose the produced base.original.t2pkg below, or start the client with --package <path>.";

    private readonly string? _error;
    private FileDialog? _dialog;

    /// <summary>Creates the screen; <paramref name="error"/> explains why the configured package could not be opened.</summary>
    public FirstRunScreen(string? error) => _error = error;

    /// <summary>Parameterless constructor required by Godot; not used.</summary>
    public FirstRunScreen()
        : this(null)
    {
    }

    /// <summary>Raised with the chosen package path.</summary>
    public event Action<string>? PackageChosen;

    /// <inheritdoc/>
    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) };
        center.AddChild(box);
        box.AddChild(new Label { Text = "No imported game content", ThemeTypeVariation = "HeaderLarge" });
        box.AddChild(new Label { Text = Explanation, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        if (!string.IsNullOrEmpty(_error))
        {
            box.AddChild(new Label { Text = "Could not open the configured package:\n" + _error, Modulate = Color.Color8(255, 128, 128), AutowrapMode = TextServer.AutowrapMode.WordSmart });
        }

        var choose = new Button { Text = "Choose package…", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        box.AddChild(choose);
        _dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = ["*.t2pkg ; Tzar2 package"],
            Title = "Choose an imported package",
            UseNativeDialog = false,
        };
        _dialog.FileSelected += path => PackageChosen?.Invoke(path);
        AddChild(_dialog);
        choose.Pressed += () => _dialog.PopupCentered(new Vector2I(900, 600));
    }
}
