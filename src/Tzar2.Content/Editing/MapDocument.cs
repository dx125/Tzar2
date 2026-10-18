using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Content.Editing;

/// <summary>Why a document changed.</summary>
public enum ChangeKind : byte
{
    /// <summary>An edit (live during a transaction, final at commit).</summary>
    Edit,

    /// <summary>An undo.</summary>
    Undo,

    /// <summary>A redo.</summary>
    Redo,

    /// <summary>An edit was rolled back.</summary>
    Rollback,
}

/// <summary>What changed, so views update incrementally.</summary>
/// <param name="Kind">Why.</param>
/// <param name="Label">The step's label.</param>
/// <param name="TerrainChanged">Some terrain cell changed.</param>
/// <param name="TileLeft">Left tile of the changed terrain (inclusive), valid when <paramref name="TerrainChanged"/>.</param>
/// <param name="TileTop">Top tile.</param>
/// <param name="TileRight">Right tile (inclusive).</param>
/// <param name="TileBottom">Bottom tile (inclusive).</param>
/// <param name="StateChanged">Objects, scenario, decorations, assets or extras changed.</param>
public readonly record struct DocumentChange(ChangeKind Kind, string Label, bool TerrainChanged, int TileLeft, int TileTop, int TileRight, int TileBottom, bool StateChanged);

/// <summary>
/// An editable map (TDD §18, REQ-EDT-02, ADR 0150): terrain layers, placed objects, decorations, user resources and the
/// scenario, with an unlimited undo history. Engine-agnostic: the Godot editor, the CLI and the tests use the same
/// document. Every change goes through an <see cref="EditTransaction"/>; <see cref="BuildMap"/> and
/// <see cref="BuildScenario"/> produce the compiled contract the game, the trigger engine and the package writer consume.
/// </summary>
public sealed class MapDocument
{
    /// <summary>The seven classic map sides (Appendix C.1).</summary>
    public static readonly ImmutableArray<int> ClassicSizes = [64, 96, 128, 192, 256, 384, 512];

    private readonly byte[] _painted;
    private readonly byte[] _terrain;
    private readonly byte[] _heights;
    private readonly byte[] _cliffs;
    private EditTransaction? _open;
    private long _savedVersion;

    private MapDocument(int width, int height, TerrainRules rules, byte[] painted, byte[] terrain, byte[] heights, byte[] cliffs, DocumentState state)
    {
        Width = width;
        Height = height;
        Rules = rules;
        _painted = painted;
        _terrain = terrain;
        _heights = heights;
        _cliffs = cliffs;
        State = state;
        History = new EditHistory();
    }

    /// <summary>Raised after every change (on the thread that made it).</summary>
    public event Action<DocumentChange>? Changed;

    /// <summary>Width in tiles (fixed after creation, Appendix C.1).</summary>
    public int Width { get; }

    /// <summary>Height in tiles.</summary>
    public int Height { get; }

    /// <summary>Terrain set id, or null (fixed after creation).</summary>
    public string? TerrainSet => Rules.SetId;

    /// <summary>The painting rules.</summary>
    public TerrainRules Rules { get; }

    /// <summary>The current immutable state.</summary>
    public DocumentState State { get; private set; }

    /// <summary>The undo history.</summary>
    public EditHistory History { get; }

    /// <summary>Package entries the editor does not own, kept byte for byte on save (provenance, conversion report, placeholders, translations).</summary>
    public ImmutableSortedDictionary<string, ImmutableArray<byte>> Preserved { get; internal set; } = ImmutableSortedDictionary.Create<string, ImmutableArray<byte>>(StringComparer.Ordinal);

    /// <summary>The manifest the document was loaded with (kept across saves; the name follows the map title), or null for a new map.</summary>
    public Packages.PackageManifest? Manifest { get; set; }

    /// <summary>Whether the document changed since it was created, loaded or last <see cref="MarkSaved">saved</see>.</summary>
    public bool IsDirty => History.Version != _savedVersion;

    /// <summary>Whether an edit is open.</summary>
    public bool InTransaction => _open is not null;

    /// <summary>Corners per row.</summary>
    public int Stride => Width + 1;

    /// <summary>Painted values per corner, (W+1)×(H+1).</summary>
    public ReadOnlySpan<byte> PaintedCorners => _painted;

    /// <summary>Ruleset terrain ids per corner.</summary>
    public ReadOnlySpan<byte> TerrainCorners => _terrain;

    /// <summary>Heights per corner.</summary>
    public ReadOnlySpan<byte> HeightCorners => _heights;

    /// <summary>Cliff flags per tile, W×H.</summary>
    public ReadOnlySpan<byte> CliffTiles => _cliffs;

    /// <summary>Creates an empty map filled with <paramref name="fill"/> (a painted value of <paramref name="rules"/>).</summary>
    /// <exception cref="ArgumentException">The size is out of range or the fill is not paintable.</exception>
    public static MapDocument CreateEmpty(string name, int width, int height, TerrainRules rules, int? fill = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, MapData.MinSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MapData.MaxSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, MapData.MinSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MapData.MaxSize);
        int value = fill ?? rules.DefaultLand;
        if (!rules.IsPaintable(value))
        {
            throw new ArgumentException($"Terrain {value} is not in the palette.", nameof(fill));
        }

        int corners = (width + 1) * (height + 1);
        byte[] painted = new byte[corners];
        byte[] terrain = new byte[corners];
        Array.Fill(painted, (byte)value);
        Array.Fill(terrain, rules.SimTerrainOf(value));
        var state = new DocumentState(
            name,
            ImmutableList<EditorObject>.Empty,
            1,
            ScenarioModel.Empty(name),
            ImmutableList<MapDecoration>.Empty,
            ImmutableSortedDictionary.Create<string, ImmutableArray<byte>>(StringComparer.Ordinal),
            EditorExtras.Default);
        return new MapDocument(width, height, rules, painted, terrain, new byte[corners], new byte[width * height], state);
    }

    /// <summary>
    /// Opens a compiled map and its scenario for editing. Objects get the ids <paramref name="objectIds"/> (one per
    /// object, unique, ≥ 1) or index + 1; the scenario's object indices become those ids and its expressions source text.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="scenario">Its scenario, or null for a plain map.</param>
    /// <param name="rules">Painting rules; must match the map's terrain set (null id ⇔ no set).</param>
    /// <param name="objectIds">Stored object ids, or null.</param>
    /// <exception cref="ArgumentException">The parts do not fit together.</exception>
    public static MapDocument FromCompiled(MapData map, ScenarioData? scenario, TerrainRules rules, IReadOnlyList<int>? objectIds = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(rules);
        if (!string.Equals(map.TerrainSet, rules.SetId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The map uses terrain set '{map.TerrainSet}', the rules '{rules.SetId}'.", nameof(rules));
        }

        if (scenario is not null && scenario.ObjectCount != map.Objects.Length)
        {
            throw new ArgumentException($"The scenario describes {scenario.ObjectCount} objects, the map has {map.Objects.Length}.", nameof(scenario));
        }

        int n = map.Objects.Length;
        int[] ids = new int[n];
        var seen = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            ids[i] = objectIds is { } given && given.Count == n ? given[i] : i + 1;
            if (ids[i] < 1 || !seen.Add(ids[i]))
            {
                throw new ArgumentException($"Object id {ids[i]} is invalid or repeated.", nameof(objectIds));
            }
        }

        int next = 1;
        var objects = ImmutableList.CreateBuilder<EditorObject>();
        for (int i = 0; i < n; i++)
        {
            objects.Add(new EditorObject(ids[i], map.Objects[i], scenario?.ObjectAt(i) ?? ObjectInfo.Plain));
            next = Math.Max(next, ids[i] + 1);
        }

        ScenarioModel model = scenario is null ? ScenarioModel.Empty(map.Name) : ToModel(scenario, ids);
        var state = new DocumentState(
            map.Name,
            objects.ToImmutable(),
            next,
            model,
            ImmutableList<MapDecoration>.Empty,
            ImmutableSortedDictionary.Create<string, ImmutableArray<byte>>(StringComparer.Ordinal),
            EditorExtras.Default);
        byte[] painted = rules.HasSet ? map.SetTerrainCorners.ToArray() : map.TerrainCorners.ToArray();
        return new MapDocument(map.Width, map.Height, rules, painted, map.TerrainCorners.ToArray(), map.HeightCorners.ToArray(), map.CliffTiles.ToArray(), state);
    }

    /// <summary>Opens an edit. Only one may be open; dispose it without <see cref="EditTransaction.Commit"/> to roll back.</summary>
    /// <exception cref="InvalidOperationException">Another edit is open.</exception>
    public EditTransaction Begin(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (_open is not null)
        {
            throw new InvalidOperationException($"The edit '{_open.Label}' is still open.");
        }

        _open = new EditTransaction(this, label);
        return _open;
    }

    /// <summary>Applies one state change as a single undoable step.</summary>
    public void Apply(string label, Func<DocumentState, DocumentState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using EditTransaction tx = Begin(label);
        tx.State = change(tx.State);
        tx.Commit();
    }

    /// <summary>Undoes the last step.</summary>
    /// <exception cref="InvalidOperationException">An edit is open or there is nothing to undo.</exception>
    public void Undo()
    {
        RequireClosed();
        EditStep step = History.PopUndo();
        Restore(step, before: true, ChangeKind.Undo);
    }

    /// <summary>Redoes the last undone step.</summary>
    /// <exception cref="InvalidOperationException">An edit is open or there is nothing to redo.</exception>
    public void Redo()
    {
        RequireClosed();
        EditStep step = History.PopRedo();
        Restore(step, before: false, ChangeKind.Redo);
    }

    /// <summary>Records that the current version is saved (<see cref="IsDirty"/> becomes false); the history stays.</summary>
    public void MarkSaved() => _savedVersion = History.Version;

    /// <summary>Painted value at corner (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public byte PaintedAt(int x, int y) => _painted[(y * Stride) + x];

    /// <summary>Ruleset terrain id at corner (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public byte TerrainAt(int x, int y) => _terrain[(y * Stride) + x];

    /// <summary>Whether tile (<paramref name="x"/>, <paramref name="y"/>) is a cliff.</summary>
    public bool IsCliff(int x, int y) => _cliffs[(y * Width) + x] != 0;

    /// <summary>Whether corner (<paramref name="x"/>, <paramref name="y"/>) lies on the map.</summary>
    public bool ContainsCorner(int x, int y) => (uint)x <= (uint)Width && (uint)y <= (uint)Height;

    /// <summary>Whether tile (<paramref name="x"/>, <paramref name="y"/>) lies on the map.</summary>
    public bool ContainsTile(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    /// <summary>Whether all four corners of the tile are water.</summary>
    public bool IsWaterTile(int x, int y)
        => Rules.IsWater(PaintedAt(x, y)) && Rules.IsWater(PaintedAt(x + 1, y)) && Rules.IsWater(PaintedAt(x + 1, y + 1)) && Rules.IsWater(PaintedAt(x, y + 1));

    /// <summary>Whether no corner of the tile is water.</summary>
    public bool IsLandTile(int x, int y)
        => !Rules.IsWater(PaintedAt(x, y)) && !Rules.IsWater(PaintedAt(x + 1, y)) && !Rules.IsWater(PaintedAt(x + 1, y + 1)) && !Rules.IsWater(PaintedAt(x, y + 1));

    /// <summary>Whether the set can draw tile (<paramref name="x"/>, <paramref name="y"/>) (else it is shown red).</summary>
    public bool IsDrawableTile(int x, int y) => Rules.IsDrawable(PaintedAt(x, y), PaintedAt(x + 1, y), PaintedAt(x + 1, y + 1), PaintedAt(x, y + 1));

    /// <summary>The terrain and objects as the simulation consumes them (object order = <see cref="DocumentState.Objects"/>).</summary>
    /// <exception cref="ArgumentException">The map is not valid as compiled data (see the validator).</exception>
    public MapData BuildMap()
    {
        var objects = new List<MapObject>(State.Objects.Count);
        foreach (EditorObject o in State.Objects)
        {
            objects.Add(o.Placement);
        }

        return new MapData(State.Name, Width, Height, _terrain, _heights, _cliffs, objects, Rules.SetId, Rules.HasSet ? _painted : default);
    }

    /// <summary>
    /// The scenario as the trigger engine consumes it: object ids become indices of <see cref="BuildMap"/>'s objects, and
    /// every expression is parsed against the tables (undeclared <c>$variables</c> are appended to the variable table in
    /// a fixed order: events, actions, rules, phrases).
    /// </summary>
    /// <exception cref="ArgumentException">A reference is out of range (the validator reports it first).</exception>
    public ScenarioData BuildScenario()
    {
        ScenarioModel m = State.Scenario;
        var index = new Dictionary<int, int>(State.Objects.Count);
        for (int i = 0; i < State.Objects.Count; i++)
        {
            index[State.Objects[i].Id] = i;
        }

        ScenarioModel mapped = ScenarioRewriter.MapObjects(m, id => index.TryGetValue(id, out int i) ? i : null);
        var scope = new ScenarioModel.BuildScope(m, new List<string>(m.Variables.Items));
        ExpressionDef P(ExpressionDef e) => e.IsEmpty ? ExpressionDef.Empty : ExpressionParser.Parse(e.Source, scope);
        var events = new List<EventDef>(mapped.Events.Length);
        foreach (EventDef e in mapped.Events)
        {
            events.Add(e is ObjectExpressionEvent x ? x with { Expression = P(x.Expression) } : e);
        }

        var actions = new List<ActionDef>(mapped.Actions.Length);
        foreach (ActionDef a in mapped.Actions)
        {
            actions.Add(a switch
            {
                ExpressionAction x => x with { Expression = P(x.Expression) },
                SetObjectPropertiesAction x => x with { Expression = P(x.Expression) },
                SetTimerAction x => x with { Initial = P(x.Initial) },
                _ => a,
            });
        }

        var rules = new List<RuleDef>(mapped.Rules.Length);
        foreach (RuleDef r in mapped.Rules)
        {
            rules.Add(r with { Condition = P(r.Condition) });
        }

        var conversations = new List<ConversationDef>(mapped.Conversations.Length);
        foreach (ConversationDef c in mapped.Conversations)
        {
            var phrases = new List<PhraseDef>(c.Phrases.Length);
            foreach (PhraseDef p in c.Phrases)
            {
                phrases.Add(p with { Condition = P(p.Condition), Result = P(p.Result) });
            }

            conversations.Add(c with { Phrases = EquatableArray<PhraseDef>.From(phrases) });
        }

        var infos = new List<ObjectInfo>(State.Objects.Count);
        foreach (EditorObject o in State.Objects)
        {
            infos.Add(o.Info);
        }

        return new ScenarioData(
            m.Properties,
            State.Objects.Count,
            EquatableArray<ObjectInfo>.From(infos),
            m.Kingdoms,
            m.Diplomacy,
            EquatableArray<string>.From(scope.Variables),
            EquatableArray<EventDef>.From(events),
            EquatableArray<ActionDef>.From(actions),
            EquatableArray<RuleDef>.From(rules),
            EquatableArray<ConversationDef>.From(conversations),
            mapped.Sounds,
            m.Music);
    }

    // ----- used by EditTransaction ----------------------------------------------------------------------------------

    internal byte[] Layer(TerrainLayer layer) => layer switch
    {
        TerrainLayer.Painted => _painted,
        TerrainLayer.Terrain => _terrain,
        TerrainLayer.Height => _heights,
        _ => _cliffs,
    };

    internal void SetState(DocumentState state) => State = state;

    internal void Close(EditTransaction tx)
    {
        if (ReferenceEquals(_open, tx))
        {
            _open = null;
        }
    }

    internal void Raise(DocumentChange change) => Changed?.Invoke(change);

    internal (int Left, int Top, int Right, int Bottom) TilesOf(TerrainLayer layer, int index)
    {
        if (layer == TerrainLayer.Cliff)
        {
            int ty = index / Width, tx = index % Width;
            return (tx, ty, tx, ty);
        }

        int y = index / Stride, x = index % Stride;
        return (Math.Max(0, x - 1), Math.Max(0, y - 1), Math.Min(Width - 1, x), Math.Min(Height - 1, y));
    }

    private static ScenarioModel ToModel(ScenarioData s, int[] ids)
    {
        static ExpressionDef S(ExpressionDef e) => ScenarioModel.Source(e.Source);
        var model = new ScenarioModel(s.Properties, s.Kingdoms, s.Diplomacy, s.Variables, s.Events, s.Actions, s.Rules, s.Conversations, s.Sounds, s.Music);
        model = ScenarioRewriter.MapObjects(model, i => (uint)i < (uint)ids.Length ? ids[i] : null);
        var events = new List<EventDef>();
        foreach (EventDef e in model.Events)
        {
            events.Add(e is ObjectExpressionEvent x ? x with { Expression = S(x.Expression) } : e);
        }

        var actions = new List<ActionDef>();
        foreach (ActionDef a in model.Actions)
        {
            actions.Add(a switch
            {
                ExpressionAction x => x with { Expression = S(x.Expression) },
                SetObjectPropertiesAction x => x with { Expression = S(x.Expression) },
                SetTimerAction x => x with { Initial = S(x.Initial) },
                _ => a,
            });
        }

        var rules = new List<RuleDef>();
        foreach (RuleDef r in model.Rules)
        {
            rules.Add(r with { Condition = S(r.Condition) });
        }

        var conversations = new List<ConversationDef>();
        foreach (ConversationDef c in model.Conversations)
        {
            var phrases = new List<PhraseDef>();
            foreach (PhraseDef p in c.Phrases)
            {
                phrases.Add(p with { Condition = S(p.Condition), Result = S(p.Result) });
            }

            conversations.Add(c with { Phrases = EquatableArray<PhraseDef>.From(phrases) });
        }

        return model with
        {
            Events = EquatableArray<EventDef>.From(events),
            Actions = EquatableArray<ActionDef>.From(actions),
            Rules = EquatableArray<RuleDef>.From(rules),
            Conversations = EquatableArray<ConversationDef>.From(conversations),
        };
    }

    private void Restore(EditStep step, bool before, ChangeKind kind)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        if (step.Terrain is { } patch)
        {
            for (int i = 0; i < patch.Count; i++)
            {
                Layer(patch.Layers[i])[patch.Indices[i]] = before ? patch.Before[i] : patch.After[i];
                (int l, int t, int r, int b) = TilesOf(patch.Layers[i], patch.Indices[i]);
                left = Math.Min(left, l);
                top = Math.Min(top, t);
                right = Math.Max(right, r);
                bottom = Math.Max(bottom, b);
            }
        }

        DocumentState target = before ? step.Before : step.After;
        bool stateChanged = !ReferenceEquals(State, target);
        State = target;
        bool terrain = right >= 0;
        Raise(new DocumentChange(kind, step.Label, terrain, terrain ? left : 0, terrain ? top : 0, right, bottom, stateChanged));
    }

    private void RequireClosed()
    {
        if (_open is not null)
        {
            throw new InvalidOperationException($"The edit '{_open.Label}' is still open.");
        }
    }
}
