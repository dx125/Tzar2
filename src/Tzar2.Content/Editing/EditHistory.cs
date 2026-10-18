using System;
using System.Collections.Generic;

namespace Tzar2.Content.Editing;

/// <summary>Which terrain layer a cell change touches.</summary>
public enum TerrainLayer : byte
{
    /// <summary>Painted corner values (terrain-set indices, or ruleset ids without a set).</summary>
    Painted,

    /// <summary>Ruleset terrain id per corner (derived from the painted value).</summary>
    Terrain,

    /// <summary>Height per corner.</summary>
    Height,

    /// <summary>Cliff flag per tile.</summary>
    Cliff,
}

/// <summary>The terrain cells one edit changed, with their values before and after (sorted by layer and index).</summary>
public sealed class TerrainPatch
{
    internal TerrainPatch(TerrainLayer[] layers, int[] indices, byte[] before, byte[] after)
    {
        Layers = layers;
        Indices = indices;
        Before = before;
        After = after;
    }

    /// <summary>Layer per changed cell.</summary>
    public IReadOnlyList<TerrainLayer> Layers { get; }

    /// <summary>Cell index per changed cell (corner index, or tile index for cliffs).</summary>
    public IReadOnlyList<int> Indices { get; }

    /// <summary>Value before the edit.</summary>
    public IReadOnlyList<byte> Before { get; }

    /// <summary>Value after the edit.</summary>
    public IReadOnlyList<byte> After { get; }

    /// <summary>Number of changed cells.</summary>
    public int Count => Indices.Count;
}

/// <summary>One undoable step (TDD REQ-EDT-02: command pattern, unlimited history).</summary>
/// <param name="Serial">Unique, increasing number of the step in this session.</param>
/// <param name="Label">What the step did ("Paint terrain", "Delete 3 objects"…).</param>
/// <param name="Before">The state before.</param>
/// <param name="After">The state after.</param>
/// <param name="Terrain">The terrain cells it changed, or null.</param>
public sealed record EditStep(long Serial, string Label, DocumentState Before, DocumentState After, TerrainPatch? Terrain);

/// <summary>
/// The undo and redo stacks of a document. Unlimited; saving does not clear them, so a session can undo past a save
/// (REQ-EDT-02 "survives save within a session").
/// </summary>
public sealed class EditHistory
{
    private readonly List<EditStep> _undo = [];
    private readonly List<EditStep> _redo = [];
    private long _serial;

    /// <summary>Whether there is a step to undo.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether there is a step to redo.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Label of the next undo, or null.</summary>
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    /// <summary>Label of the next redo, or null.</summary>
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>Steps that can be undone, oldest first.</summary>
    public IReadOnlyList<EditStep> UndoSteps => _undo;

    /// <summary>Steps that can be redone, next redo last.</summary>
    public IReadOnlyList<EditStep> RedoSteps => _redo;

    /// <summary>Serial of the step the document is at (0 = the loaded or created state).</summary>
    public long Version => _undo.Count > 0 ? _undo[^1].Serial : 0;

    internal EditStep Push(string label, DocumentState before, DocumentState after, TerrainPatch? terrain)
    {
        var step = new EditStep(++_serial, label, before, after, terrain);
        _undo.Add(step);
        _redo.Clear();
        return step;
    }

    // A freshly created document starts without history (its creation steps are not undoable).
    internal void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    internal EditStep PopUndo()
    {
        if (_undo.Count == 0)
        {
            throw new InvalidOperationException("Nothing to undo.");
        }

        EditStep step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(step);
        return step;
    }

    internal EditStep PopRedo()
    {
        if (_redo.Count == 0)
        {
            throw new InvalidOperationException("Nothing to redo.");
        }

        EditStep step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(step);
        return step;
    }
}
