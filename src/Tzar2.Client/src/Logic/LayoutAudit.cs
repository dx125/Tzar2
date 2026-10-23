using System;
using System.Collections.Generic;

namespace Tzar2.Client.Logic;

/// <summary>What a control is, for the layout audit (ADR 0200).</summary>
public enum AuditKind
{
    /// <summary>A container or plain control: only its bounds are checked.</summary>
    Frame,

    /// <summary>A picture (background, icon): only its bounds are checked.</summary>
    Art,

    /// <summary>Text (a label, or a picture of text drawn with a bitmap font).</summary>
    Text,

    /// <summary>Something to press.</summary>
    Button,

    /// <summary>Something to type into or pick from (line edit, list, slider).</summary>
    Field,
}

/// <summary>
/// A visible control on the logical screen, in whole pixels.
/// </summary>
/// <param name="Id">Index of this box in the list.</param>
/// <param name="Parent">Index of the nearest audited ancestor, or −1.</param>
/// <param name="Path">Where it is (type names from the screen down), for the report.</param>
/// <param name="Kind">What it is.</param>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="Label">Its text, when it has one.</param>
/// <param name="Overflow">For text: how many pixels the text is wider than its box (a label that does not fit).</param>
/// <param name="Layer">The layer it is drawn on (a canvas layer or a window); controls of different layers may cover one
/// another (a dialog over the HUD).</param>
public readonly record struct AuditBox(int Id, int Parent, string Path, AuditKind Kind, int X, int Y, int Width, int Height, string Label = "", int Overflow = 0, int Layer = 0)
{
    /// <summary>Right edge (exclusive).</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge (exclusive).</summary>
    public int Bottom => Y + Height;

    /// <summary>A short name for the report: the label, else the last part of the path.</summary>
    public string Name => Label.Length > 0 ? $"'{Label}'" : Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>A layout problem the audit found.</summary>
public enum AuditProblem
{
    /// <summary>A control leaves the window (wholly or in part).</summary>
    OffScreen,

    /// <summary>Two texts, buttons or fields cover one another.</summary>
    Overlap,

    /// <summary>A text is wider than its box, or a button's label leaves its button.</summary>
    TextOverflow,
}

/// <summary>One finding of the audit.</summary>
/// <param name="Problem">What is wrong.</param>
/// <param name="Path">The control.</param>
/// <param name="Detail">The rectangles and names involved.</param>
public readonly record struct AuditIssue(AuditProblem Problem, string Path, string Detail)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Problem} {Path}: {Detail}";
}

/// <summary>
/// The layout audit of the UI smoke test (ADR 0200), engine-free: given the visible controls of one screen on a logical
/// screen, it reports controls that leave the window, texts, buttons and fields that cover one another, and texts too
/// wide for their box. A text wholly inside a button is that button's label; a control inside another (its ancestor),
/// or on another layer (a dialog over the HUD, a window over the editor), never counts as an overlap.
/// </summary>
public static class LayoutAudit
{
    /// <summary>Checks <paramref name="boxes"/> on a logical screen of <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static List<AuditIssue> Check(int width, int height, IReadOnlyList<AuditBox> boxes) => Check(width, height, (0, 0, width, height), boxes);

    /// <summary>
    /// Checks <paramref name="boxes"/> on a logical screen of <paramref name="width"/> × <paramref name="height"/> of which
    /// the window shows <paramref name="visible"/>. A control covering the whole logical screen (a backdrop, a screen's
    /// root) may be cut by the window; what is on it may not.
    /// </summary>
    public static List<AuditIssue> Check(int width, int height, (int Left, int Top, int Right, int Bottom) visible, IReadOnlyList<AuditBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        var issues = new List<AuditIssue>();
        foreach (AuditBox b in boxes)
        {
            if (b.Width <= 0 || b.Height <= 0)
            {
                continue;
            }

            bool whole = b.X <= 0 && b.Y <= 0 && b.Right >= width && b.Bottom >= height;
            if (!whole && (b.X < visible.Left || b.Y < visible.Top || b.Right > visible.Right || b.Bottom > visible.Bottom))
            {
                issues.Add(new AuditIssue(AuditProblem.OffScreen, b.Path, $"{b.Name} {Rect(b)} outside the window's ({visible.Left},{visible.Top})-({visible.Right},{visible.Bottom})"));
            }

            if (b.Kind == AuditKind.Text && b.Overflow > 0)
            {
                issues.Add(new AuditIssue(AuditProblem.TextOverflow, b.Path, $"{b.Name} is {b.Overflow} px wider than its box {Rect(b)}"));
            }
            else if (b.Kind == AuditKind.Text && Button(boxes, b) is { } owner && !Inside(b, owner))
            {
                issues.Add(new AuditIssue(AuditProblem.TextOverflow, b.Path, $"{b.Name} {Rect(b)} leaves its button {Rect(owner)}"));
            }
            else if (b.Kind == AuditKind.Text && Panel(boxes, b, width, height) is { } panel && !Inside(b, panel))
            {
                issues.Add(new AuditIssue(AuditProblem.TextOverflow, b.Path, $"{b.Name} {Rect(b)} runs out of the panel it starts on, {panel.Name} {Rect(panel)}"));
            }
        }

        for (int i = 0; i < boxes.Count; i++)
        {
            AuditBox a = boxes[i];
            if (!Solid(a))
            {
                continue;
            }

            for (int j = i + 1; j < boxes.Count; j++)
            {
                AuditBox b = boxes[j];
                if (!Solid(b) || a.Layer != b.Layer || !Intersect(a, b) || Ancestor(boxes, a, b) || Ancestor(boxes, b, a) || Label(a, b) || Label(b, a))
                {
                    continue;
                }

                issues.Add(new AuditIssue(AuditProblem.Overlap, a.Path, $"{a.Name} {Rect(a)} and {b.Name} {Rect(b)} ({b.Path})"));
            }
        }

        return issues;
    }

    private static bool Solid(AuditBox b) => b.Width > 0 && b.Height > 0 && b.Kind is AuditKind.Text or AuditKind.Button or AuditKind.Field;

    private static bool Intersect(AuditBox a, AuditBox b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static bool Inside(AuditBox inner, AuditBox outer)
        => inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    // A text wholly inside a button or field is its label (drawn over it, as the original's buttons do).
    private static bool Label(AuditBox text, AuditBox owner) => text.Kind == AuditKind.Text && owner.Kind != AuditKind.Text && Inside(text, owner);

    // The smallest picture of the same layer, smaller than the screen, on which a text starts (a dialog's background, a
    // box drawn for it): the text must end on it too.
    private static AuditBox? Panel(IReadOnlyList<AuditBox> boxes, AuditBox text, int width, int height)
    {
        AuditBox? best = null;
        foreach (AuditBox p in boxes)
        {
            if (p.Kind != AuditKind.Art || p.Layer != text.Layer || p.Width >= width || p.Height >= height
                || text.X < p.X || text.X >= p.Right || text.Y < p.Y || text.Y >= p.Bottom || Ancestor(boxes, text, p))
            {
                continue;
            }

            if (best is not { } b || (long)p.Width * p.Height < (long)b.Width * b.Height)
            {
                best = p;
            }
        }

        return best;
    }

    // The nearest button above a text in the tree (a label made a child of its button).
    private static AuditBox? Button(IReadOnlyList<AuditBox> boxes, AuditBox b)
    {
        for (int p = b.Parent; p >= 0; p = boxes[p].Parent)
        {
            if (boxes[p].Kind == AuditKind.Button)
            {
                return boxes[p];
            }
        }

        return null;
    }

    private static bool Ancestor(IReadOnlyList<AuditBox> boxes, AuditBox ancestor, AuditBox b)
    {
        for (int p = b.Parent; p >= 0; p = boxes[p].Parent)
        {
            if (p == ancestor.Id)
            {
                return true;
            }
        }

        return false;
    }

    private static string Rect(AuditBox b) => $"({b.X},{b.Y} {b.Width}x{b.Height})";
}
