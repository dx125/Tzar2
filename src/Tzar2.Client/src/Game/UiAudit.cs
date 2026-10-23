using System;
using System.Collections.Generic;
using Godot;
using Tzar2.Client.Logic;

namespace Tzar2.Client.Game;

/// <summary>
/// The Godot side of the layout audit (ADR 0200): collects the visible controls under a node as
/// <see cref="AuditBox"/>es in logical screen pixels, for <see cref="LayoutAudit"/>. Embedded windows (the editor's) add
/// their position; a clipping control (scroll areas) cuts what is under it to its own rectangle.
/// </summary>
public static class UiAudit
{
    /// <summary>The resource name prefix of text pictures drawn with a bitmap font (<see cref="UiAssets.Text"/>).</summary>
    public const string TextPrefix = "text:";

    /// <summary>The visible controls under <paramref name="root"/>.</summary>
    public static List<AuditBox> Collect(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var boxes = new List<AuditBox>();
        int layers = 0;
        Walk(root, -1, 0, ref layers, root.GetType().Name, Vector2.Zero, null, boxes);
        return boxes;
    }

    private static void Walk(Node node, int parent, int layer, ref int layers, string path, Vector2 offset, Rect2? clip, List<AuditBox> boxes)
    {
        foreach (Node child in node.GetChildren())
        {
            string childPath = path + "/" + Segment(child);
            switch (child)
            {
                case Window window:
                    if (window.Visible)
                    {
                        var rect = new Rect2(window.Position, window.Size);
                        int own = ++layers;
                        int id = Add(boxes, parent, own, childPath, AuditKind.Frame, rect, window.Title, 0);
                        Walk(window, id, own, ref layers, childPath, offset + window.Position, null, boxes);
                    }

                    break;
                case CanvasLayer canvas:
                    if (canvas.Visible)
                    {
                        Walk(canvas, parent, ++layers, ref layers, childPath, offset, clip, boxes);
                    }

                    break;
                case Control control:
                    if (!control.IsVisibleInTree())
                    {
                        break;
                    }

                    Rect2 r = control.GetGlobalRect();
                    r.Position += offset;
                    Rect2? cut = clip;
                    if (clip is { } c)
                    {
                        if (!c.Intersects(r))
                        {
                            break;
                        }

                        r = c.Intersection(r);
                    }

                    (AuditKind kind, string label, int overflow) = Describe(control);
                    int index = Add(boxes, parent, layer, childPath, kind, r, label, overflow);
                    if (control.ClipContents)
                    {
                        cut = r;
                    }

                    Walk(control, index, layer, ref layers, childPath, offset, cut, boxes);
                    break;
                case CanvasItem item when !item.Visible:
                    break;
                default:
                    Walk(child, parent, layer, ref layers, childPath, offset, clip, boxes);
                    break;
            }
        }
    }

    private static string Segment(Node node)
    {
        string type = node.GetType().Name;
        string name = node.Name.ToString();
        return name.StartsWith('@') || name == type ? type : $"{type}[{name}]";
    }

    private static int Add(List<AuditBox> boxes, int parent, int layer, string path, AuditKind kind, Rect2 r, string label, int overflow)
    {
        int x = (int)MathF.Round(r.Position.X), y = (int)MathF.Round(r.Position.Y);
        int w = (int)MathF.Round(r.End.X) - x, h = (int)MathF.Round(r.End.Y) - y;
        boxes.Add(new AuditBox(boxes.Count, parent, path, kind, x, y, w, h, label, overflow, layer));
        return boxes.Count - 1;
    }

    private static (AuditKind Kind, string Label, int Overflow) Describe(Control control)
    {
        switch (control)
        {
            case Button button:
                return (AuditKind.Button, button.Text, 0);
            case BaseButton:
                return (AuditKind.Button, string.Empty, 0);
            case Label label:
                int overflow = 0;
                if (label.AutowrapMode == TextServer.AutowrapMode.Off && !label.ClipText && label.TextOverrunBehavior == TextServer.OverrunBehavior.NoTrimming)
                {
                    float need = label.GetThemeFont("font").GetStringSize(label.Text, HorizontalAlignment.Left, -1, label.GetThemeFontSize("font_size")).X;
                    overflow = Math.Max(0, (int)MathF.Ceiling(need - label.Size.X));
                }

                return (AuditKind.Text, label.Text, overflow);
            case RichTextLabel rich:
                return (AuditKind.Text, rich.Text.Length > 40 ? rich.Text[..40] : rich.Text, 0);
            case LineEdit or TextEdit or ItemList or Tree or Godot.Range or GraphEdit:
                return (AuditKind.Field, string.Empty, 0);
            case TextureRect { Texture: { } texture } when texture.ResourceName.StartsWith(TextPrefix, StringComparison.Ordinal):
                return (AuditKind.Text, texture.ResourceName[TextPrefix.Length..], 0);
            case TextureRect or ColorRect or NinePatchRect or Panel:
                return (AuditKind.Art, string.Empty, 0);
            default:
                return (AuditKind.Frame, string.Empty, 0);
        }
    }
}
