using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Client.Editor;

/// <summary>One map node of the visual campaign graph.</summary>
/// <param name="Id">Map id.</param>
/// <param name="Title">Shown title.</param>
/// <param name="IsEntry">A starting map (green).</param>
/// <param name="Problem">Unreachable or otherwise flagged (red), or null.</param>
/// <param name="Position">Saved node position, or null for the automatic layout.</param>
public sealed record GraphMap(string Id, string Title, bool IsEntry, string? Problem, Vector2? Position);

/// <summary>
/// The visual campaign graph (TDD §18.3, REQ-EDT-08): nodes = maps, edges = Load Map / Player Win / Player Lose
/// transitions (coloured by kind), entry maps green, unreachable maps red, dangling transitions listed on their node.
/// Nodes can be dragged; selecting one raises <see cref="MapSelected"/>.
/// </summary>
public partial class CampaignGraphView : GraphEdit
{
    private static readonly Color Win = new(0.4f, 0.9f, 0.4f);
    private static readonly Color Lose = new(0.95f, 0.45f, 0.4f);
    private static readonly Color Load = new(0.55f, 0.75f, 1f);

    /// <summary>Creates the view.</summary>
    public CampaignGraphView()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(400, 300);
        RightDisconnects = false;
        NodeSelected += node => MapSelected?.Invoke(node.Name.ToString());
    }

    /// <summary>Raised when a map node is selected.</summary>
    public event Action<string>? MapSelected;

    /// <summary>Raised when a node was dragged: map id, new position.</summary>
    public event Action<string, Vector2>? MapMoved;

    /// <summary>Shows a graph.</summary>
    public void Show(IReadOnlyList<GraphMap> maps, IReadOnlyList<CampaignEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(edges);
        ClearConnections();
        foreach (Node child in GetChildren().Where(c => c is GraphNode).ToList())
        {
            RemoveChild(child);
            child.QueueFree();
        }

        // Automatic layout: columns by distance from the entry maps.
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (GraphMap m in maps.Where(m => m.IsEntry))
        {
            depth[m.Id] = 0;
            queue.Enqueue(m.Id);
        }

        while (queue.Count > 0)
        {
            string from = queue.Dequeue();
            foreach (CampaignEdge e in edges.Where(e => e.FromMapId == from && e.ToMapId is not null))
            {
                if (!depth.ContainsKey(e.ToMapId!))
                {
                    depth[e.ToMapId!] = depth[from] + 1;
                    queue.Enqueue(e.ToMapId!);
                }
            }
        }

        var rows = new Dictionary<int, int>();
        foreach (GraphMap m in maps)
        {
            int column = depth.TryGetValue(m.Id, out int d) ? d : -1;
            int row = rows.TryGetValue(column, out int r) ? r : 0;
            rows[column] = row + 1;
            var node = new GraphNode { Name = m.Id, Title = m.Title, PositionOffset = m.Position ?? new Vector2(40 + ((column + 1) * 260), 40 + (row * 140)), Resizable = false };
            IEnumerable<string> lines = edges.Where(e => e.FromMapId == m.Id).Select(e => e.ToMapId is null
                ? $"⚠ {Kind(e.Kind)} → '{e.ToMapName}' (no such map)"
                : $"{Kind(e.Kind)} → {e.ToMapName}");
            var body = new Label { Text = string.Join("\n", (m.IsEntry ? ["▶ entry map"] : Array.Empty<string>()).Concat(m.Problem is null ? [] : [m.Problem]).Concat(lines)) };
            node.AddChild(body);
            node.SetSlot(0, true, 0, Colors.White, true, 0, Colors.White);
            if (m.Problem is not null)
            {
                node.SelfModulate = new Color(1f, 0.55f, 0.55f);
            }
            else if (m.IsEntry)
            {
                node.SelfModulate = new Color(0.6f, 1f, 0.6f);
            }

            string id = m.Id;
            node.PositionOffsetChanged += () => MapMoved?.Invoke(id, node.PositionOffset);
            AddChild(node);
        }

        foreach (CampaignEdge e in edges)
        {
            if (e.ToMapId is { } to && HasNode(e.FromMapId) && HasNode(to))
            {
                ConnectNode(e.FromMapId, 0, to, 0);
            }
        }
    }

    private static string Kind(CampaignEdgeKind kind) => kind switch { CampaignEdgeKind.Win => "win", CampaignEdgeKind.Lose => "lose", _ => "load map" };

    /// <summary>The colour of an edge kind (legend).</summary>
    public static Color ColourOf(CampaignEdgeKind kind) => kind switch { CampaignEdgeKind.Win => Win, CampaignEdgeKind.Lose => Lose, _ => Load };
}
