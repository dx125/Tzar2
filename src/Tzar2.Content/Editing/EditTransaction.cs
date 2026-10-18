using System;
using System.Collections.Generic;

namespace Tzar2.Content.Editing;

/// <summary>
/// One open edit of a <see cref="MapDocument"/>: terrain writes take effect at once (a brush stroke is visible while the
/// mouse moves) and are recorded with their first "before" value; the state is replaced as a whole. <see cref="Commit"/>
/// turns everything into one undoable step; disposing without a commit rolls it back.
/// </summary>
public sealed class EditTransaction : IDisposable
{
    private readonly MapDocument _doc;
    private readonly DocumentState _before;
    private readonly SortedDictionary<long, byte> _original = [];
    private int _left = int.MaxValue, _top = int.MaxValue, _right = -1, _bottom = -1;
    private int _flushLeft = int.MaxValue, _flushTop = int.MaxValue, _flushRight = -1, _flushBottom = -1;
    private bool _done;

    internal EditTransaction(MapDocument doc, string label)
    {
        _doc = doc;
        Label = label;
        _before = doc.State;
    }

    /// <summary>The step's label.</summary>
    public string Label { get; set; }

    /// <summary>The document.</summary>
    public MapDocument Document => _doc;

    /// <summary>The state as edited so far; set it to change objects, scenario, decorations, assets or extras.</summary>
    public DocumentState State
    {
        get => _doc.State;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            RequireOpen();
            _doc.SetState(value);
        }
    }

    /// <summary>Number of terrain cells changed so far.</summary>
    public int TerrainCells => _original.Count;

    /// <summary>Paints corner (<paramref name="x"/>, <paramref name="y"/>); the ruleset terrain follows. Off-map corners are ignored.</summary>
    /// <returns>Whether the corner changed.</returns>
    public bool Paint(int x, int y, int value)
    {
        if (!_doc.ContainsCorner(x, y))
        {
            return false;
        }

        int i = (y * _doc.Stride) + x;
        bool a = Write(TerrainLayer.Painted, i, (byte)value);
        bool b = Write(TerrainLayer.Terrain, i, _doc.Rules.SimTerrainOf(value));
        return a || b;
    }

    /// <summary>Sets the height of a corner.</summary>
    public bool SetHeight(int x, int y, byte height)
        => _doc.ContainsCorner(x, y) && Write(TerrainLayer.Height, (y * _doc.Stride) + x, height);

    /// <summary>Sets or clears the cliff flag of a tile.</summary>
    public bool SetCliff(int x, int y, bool cliff)
        => _doc.ContainsTile(x, y) && Write(TerrainLayer.Cliff, (y * _doc.Width) + x, cliff ? (byte)1 : (byte)0);

    /// <summary>Raises a live <see cref="MapDocument.Changed"/> for the terrain written since the last flush.</summary>
    public void Flush()
    {
        RequireOpen();
        if (_flushRight < 0)
        {
            return;
        }

        _doc.Raise(new DocumentChange(ChangeKind.Edit, Label, true, _flushLeft, _flushTop, _flushRight, _flushBottom, false));
        _flushLeft = _flushTop = int.MaxValue;
        _flushRight = _flushBottom = -1;
    }

    /// <summary>Ends the edit as one undoable step (nothing is recorded when nothing changed).</summary>
    /// <returns>Whether a step was recorded.</returns>
    public bool Commit()
    {
        RequireOpen();
        _done = true;
        _doc.Close(this);
        DocumentState after = _doc.State;
        TerrainPatch? patch = null;
        if (_original.Count > 0)
        {
            var layers = new List<TerrainLayer>();
            var indices = new List<int>();
            var before = new List<byte>();
            var now = new List<byte>();
            foreach (KeyValuePair<long, byte> cell in _original)
            {
                var layer = (TerrainLayer)(cell.Key >> 32);
                int index = (int)(cell.Key & 0xFFFFFFFF);
                byte current = _doc.Layer(layer)[index];
                if (current == cell.Value)
                {
                    continue;
                }

                layers.Add(layer);
                indices.Add(index);
                before.Add(cell.Value);
                now.Add(current);
            }

            if (indices.Count > 0)
            {
                patch = new TerrainPatch(layers.ToArray(), indices.ToArray(), before.ToArray(), now.ToArray());
            }
        }

        bool stateChanged = !ReferenceEquals(after, _before) && !after.Equals(_before);
        if (patch is null && !stateChanged)
        {
            _doc.SetState(_before);
            return false;
        }

        _doc.History.Push(Label, _before, after, patch);
        bool terrain = patch is not null;
        _doc.Raise(new DocumentChange(ChangeKind.Edit, Label, terrain, terrain ? _left : 0, terrain ? _top : 0, _right, _bottom, stateChanged));
        return true;
    }

    /// <summary>Rolls back an edit that was not committed.</summary>
    public void Dispose()
    {
        if (_done)
        {
            return;
        }

        _done = true;
        _doc.Close(this);
        foreach (KeyValuePair<long, byte> cell in _original)
        {
            _doc.Layer((TerrainLayer)(cell.Key >> 32))[(int)(cell.Key & 0xFFFFFFFF)] = cell.Value;
        }

        bool stateChanged = !ReferenceEquals(_doc.State, _before);
        _doc.SetState(_before);
        if (_original.Count > 0 || stateChanged)
        {
            bool terrain = _right >= 0;
            _doc.Raise(new DocumentChange(ChangeKind.Rollback, Label, terrain, terrain ? _left : 0, terrain ? _top : 0, _right, _bottom, stateChanged));
        }
    }

    private bool Write(TerrainLayer layer, int index, byte value)
    {
        RequireOpen();
        byte[] cells = _doc.Layer(layer);
        if (cells[index] == value)
        {
            return false;
        }

        _original.TryAdd(((long)layer << 32) | (uint)index, cells[index]);
        cells[index] = value;
        (int l, int t, int r, int b) = _doc.TilesOf(layer, index);
        _left = Math.Min(_left, l);
        _top = Math.Min(_top, t);
        _right = Math.Max(_right, r);
        _bottom = Math.Max(_bottom, b);
        _flushLeft = Math.Min(_flushLeft, l);
        _flushTop = Math.Min(_flushTop, t);
        _flushRight = Math.Max(_flushRight, r);
        _flushBottom = Math.Max(_flushBottom, b);
        return true;
    }

    private void RequireOpen()
    {
        if (_done)
        {
            throw new InvalidOperationException("The edit is closed.");
        }
    }
}
