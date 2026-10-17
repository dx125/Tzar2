using Godot;
using Tzar2.Game;

namespace Tzar2.Client;

/// <summary>M0 placeholder scene: proves the Godot project loads and references the engine-agnostic layers.</summary>
public partial class Main : Node2D
{
    public override void _Ready()
    {
        var label = new Label
        {
            Text = $"{GameInfo.Codename} {GameInfo.Version} - M0 skeleton",
            Position = new Vector2(16, 16),
        };
        AddChild(label);
        GD.Print(label.Text);
    }
}
