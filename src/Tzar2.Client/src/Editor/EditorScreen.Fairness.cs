using System;
using System.Globalization;
using Godot;
using Tzar2.Content.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// The Map menu's multiplayer tools (ADR 0292): the fair-starts check (each start's walk to gold, stone and wood, and
/// which starts reach one another) and building the map from one half, mirrored or turned.
/// </summary>
public partial class EditorScreen
{
    private void FairnessMenu(PopupMenu map)
    {
        map.AddSeparator();
        Item(map, "Check fair starts (multiplayer)…", () => ShowFairStarts());
        Item(map, "Mirror the left half onto the right…", () => AskMirror(MirrorKind.LeftToRight));
        Item(map, "Mirror the top half onto the bottom…", () => AskMirror(MirrorKind.TopToBottom));
        Item(map, "Turn the left half round onto the right…", () => AskMirror(MirrorKind.LeftTurned));
    }

    /// <summary>Shows the fair-starts check of the open map; returns it.</summary>
    public FairnessReport ShowFairStarts()
    {
        FairnessReport report = FairStarts.Check(Document, _rules);
        Forms.Dialog(this, "Fair starts", new Vector2I(620, 480), body =>
        {
            GridContainer g = Forms.Grid(body, 6);
            foreach (string head in new[] { "Kingdom", "Start", "Gold", "Stone", "Wood", "Reaches" })
            {
                Forms.Label(g, head);
            }

            static string Tiles(int? d) => d is int v ? v.ToString(CultureInfo.InvariantCulture) : "none";
            foreach (FairStart s in report.Starts)
            {
                FairStart each = s;
                Forms.Button(g, "Kingdom " + s.Kingdom.ToString(CultureInfo.InvariantCulture), () => _view.CenterOnTile(each.TileX, each.TileY)).TooltipText = "Show the start";
                Forms.Label(g, $"({s.TileX}, {s.TileY})");
                Forms.Label(g, Tiles(s.Gold));
                Forms.Label(g, Tiles(s.Stone));
                Forms.Label(g, Tiles(s.Wood));
                Forms.Label(g, s.Reaches.IsEmpty ? "nobody" : string.Join(", ", s.Reaches));
            }

            Forms.Heading(body, report.Findings.IsEmpty ? "The starts look fair." : "Worth a look");
            foreach (string f in report.Findings)
            {
                Forms.Label(body, "• " + f).AutowrapMode = TextServer.AutowrapMode.WordSmart;
            }

            Forms.Label(body, "Distances are tiles walked by land from each start to the nearest resource.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        });
        return report;
    }

    private void AskMirror(MirrorKind kind)
    {
        var ask = new ConfirmationDialog
        {
            Title = "Build the map from one half",
            DialogText = "The other half's terrain, objects and decorations are replaced by a copy of this half; kingdoms 1 and 2, 3 and 4, 5 and 6, 7 and 8 change places in the copy. Ctrl+Z undoes it.",
            OkButtonText = "Copy the half",
        };
        ask.Confirmed += () =>
        {
            int copied = MapMirror.Apply(Document, _rules, kind);
            Status($"The half is copied: {copied} object(s) in the copy.");
            ask.QueueFree();
        };
        ask.Canceled += ask.QueueFree;
        AddChild(ask);
        Forms.ShowCentered(ask);
    }
}
