using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Content.Editing;

/// <summary>One map of a campaign being edited.</summary>
/// <param name="Id">Stable id inside the campaign (the lower-cased map name by default).</param>
/// <param name="MapName">The name Load Map / Player Win / Player Lose actions use (matched ignoring case).</param>
/// <param name="Description">The map's description on the campaign screen.</param>
/// <param name="IsEntry">A starting (entry) map.</param>
/// <param name="Document">The map itself (its own undo history; the map's description is its "details").</param>
public sealed record CampaignEntry(string Id, string MapName, string Description, bool IsEntry, MapDocument Document)
{
    /// <summary>The package path of the map.</summary>
    public string FileName => "maps/" + Id + ".t2map";
}

/// <summary>The campaign-level state (undoable as a whole; the maps keep their own histories).</summary>
/// <param name="Id">Campaign id (profile keys).</param>
/// <param name="Name">Title.</param>
/// <param name="Description">Description on the campaign screen.</param>
/// <param name="Maps">Maps in list order.</param>
/// <param name="Positions">Graph node positions (map id → x, y), editor only.</param>
public sealed record CampaignState(string Id, string Name, string Description, ImmutableList<CampaignEntry> Maps, ImmutableSortedDictionary<string, (int X, int Y)> Positions);

/// <summary>A variable a map carries to the next map (<c>#name</c>, ADR 0133) and the maps that use it.</summary>
/// <param name="Name">The variable, with its <c>#</c>.</param>
/// <param name="Maps">Map ids that use it, in campaign order.</param>
public sealed record CarriedVariable(string Name, ImmutableArray<string> Maps);

/// <summary>
/// An editable campaign (TDD §18.2 "Campaign editor", §18.3 visual campaign graph, ADR 0155): its maps (each a full
/// <see cref="MapDocument"/>), descriptions and entry maps, the transitions rebuilt from the maps' Load Map / Player Win /
/// Player Lose actions, the persistence mapping and the carried variables, and campaign-level shared user resources.
/// </summary>
public sealed class CampaignDocument
{
    private readonly List<CampaignState> _undo = [];
    private readonly List<CampaignState> _redo = [];
    private int _savedDepth;

    /// <summary>Creates a campaign.</summary>
    public CampaignDocument(CampaignState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
    }

    /// <summary>Raised after every campaign-level change.</summary>
    public event Action? Changed;

    /// <summary>The current state.</summary>
    public CampaignState State { get; private set; }

    /// <summary>Resources stored once at campaign level (identical in every map that has them, REQ-EDT-09).</summary>
    public ImmutableSortedDictionary<string, ImmutableArray<byte>> SharedAssets { get; internal set; } = ImmutableSortedDictionary.Create<string, ImmutableArray<byte>>(StringComparer.Ordinal);

    /// <summary>Package entries the editor does not own (provenance, conversion report), kept byte for byte.</summary>
    public ImmutableSortedDictionary<string, ImmutableArray<byte>> Preserved { get; internal set; } = ImmutableSortedDictionary.Create<string, ImmutableArray<byte>>(StringComparer.Ordinal);

    /// <summary>The manifest the campaign was loaded with, or null.</summary>
    public Packages.PackageManifest? Manifest { get; set; }

    /// <summary>Whether a campaign-level undo is possible (map edits undo in their map).</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether a campaign-level redo is possible.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Whether the campaign or one of its maps changed since it was created, loaded or saved.</summary>
    public bool IsDirty
    {
        get
        {
            if (_undo.Count != _savedDepth)
            {
                return true;
            }

            foreach (CampaignEntry m in State.Maps)
            {
                if (m.Document.IsDirty)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A new, empty campaign.</summary>
    public static CampaignDocument New(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new CampaignDocument(new CampaignState(MapPackage.Slug(name), name, string.Empty, ImmutableList<CampaignEntry>.Empty, ImmutableSortedDictionary.Create<string, (int, int)>(StringComparer.Ordinal)));
    }

    /// <summary>Applies a campaign-level change as one undoable step.</summary>
    public void Apply(Func<CampaignState, CampaignState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        CampaignState next = change(State);
        if (ReferenceEquals(next, State))
        {
            return;
        }

        _undo.Add(State);
        _redo.Clear();
        State = next;
        Changed?.Invoke();
    }

    /// <summary>Undoes the last campaign-level step.</summary>
    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Add(State);
        State = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Changed?.Invoke();
    }

    /// <summary>Redoes the last undone step.</summary>
    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Add(State);
        State = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Changed?.Invoke();
    }

    /// <summary>Records that the campaign and its maps are saved.</summary>
    public void MarkSaved()
    {
        _savedDepth = _undo.Count;
        foreach (CampaignEntry m in State.Maps)
        {
            m.Document.MarkSaved();
        }
    }

    /// <summary>Adds a map (imported, converted or new); the id is made unique from the map name.</summary>
    /// <returns>The new map's id.</returns>
    public string AddMap(MapDocument document, string? mapName = null, bool entry = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        string name = mapName ?? document.State.Name;
        string id = UniqueId(name);
        Apply(s => s with { Maps = s.Maps.Add(new CampaignEntry(id, name, document.State.Scenario.Properties.Description, entry || s.Maps.IsEmpty, document)) });
        return id;
    }

    /// <summary>Removes a map (transitions to it become dangling and are flagged).</summary>
    public void RemoveMap(string id) => Apply(s => s with { Maps = s.Maps.RemoveAll(m => m.Id == id), Positions = s.Positions.Remove(id) });

    /// <summary>Replaces map <paramref name="id"/>'s entry through <paramref name="change"/> (name, description, entry flag).</summary>
    public void UpdateMap(string id, Func<CampaignEntry, CampaignEntry> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Apply(s =>
        {
            int i = s.Maps.FindIndex(m => m.Id == id);
            return i < 0 ? s : s with { Maps = s.Maps.SetItem(i, change(s.Maps[i]) with { Id = id }) };
        });
    }

    /// <summary>Moves a map in the list (the campaign screen's order).</summary>
    public void MoveMap(string id, int to) => Apply(s =>
    {
        int i = s.Maps.FindIndex(m => m.Id == id);
        if (i < 0)
        {
            return s;
        }

        CampaignEntry e = s.Maps[i];
        return s with { Maps = s.Maps.RemoveAt(i).Insert(Math.Clamp(to, 0, s.Maps.Count - 1), e) };
    });

    /// <summary>The map with id <paramref name="id"/>, or null.</summary>
    public CampaignEntry? Find(string id)
    {
        foreach (CampaignEntry m in State.Maps)
        {
            if (string.Equals(m.Id, id, StringComparison.Ordinal))
            {
                return m;
            }
        }

        return null;
    }

    /// <summary>
    /// The campaign graph as the game reads it (<see cref="CampaignData"/>): maps in list order and the transitions
    /// rebuilt from each map's actions in index order (Load Map, Player Win and Player Lose with a next map), names matched
    /// ignoring case (Appendix C.9).
    /// </summary>
    /// <exception cref="ArgumentException">Map ids repeat (the validator reports it first).</exception>
    public CampaignData Build()
    {
        CampaignState s = State;
        var maps = new List<CampaignMap>(s.Maps.Count);
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (CampaignEntry m in s.Maps)
        {
            maps.Add(new CampaignMap(m.Id, m.MapName, m.FileName, m.Description, m.IsEntry));
            lookup.TryAdd(m.MapName, m.Id);
        }

        var edges = new List<CampaignEdge>();
        foreach (CampaignEntry m in s.Maps)
        {
            EquatableArray<ActionDef> actions = m.Document.State.Scenario.Actions;
            for (int a = 0; a < actions.Length; a++)
            {
                (CampaignEdgeKind kind, string? target) = actions[a] switch
                {
                    LoadMapAction x => (CampaignEdgeKind.LoadMap, x.Map),
                    PlayerWinAction x => (CampaignEdgeKind.Win, x.Map),
                    PlayerLoseAction x => (CampaignEdgeKind.Lose, x.Map),
                    _ => (CampaignEdgeKind.LoadMap, null),
                };
                if (!string.IsNullOrEmpty(target))
                {
                    edges.Add(new CampaignEdge(m.Id, target, lookup.TryGetValue(target, out string? to) ? to : null, kind, a));
                }
            }
        }

        return new CampaignData(s.Id, s.Name, s.Description, EquatableArray<CampaignMap>.From(maps), EquatableArray<CampaignEdge>.From(edges));
    }

    /// <summary>The visual graph: entries, reachability, dangling names, persistent units.</summary>
    public CampaignGraph Graph()
    {
        CampaignData data = Build();
        var scenarios = new Dictionary<string, ScenarioData?>(StringComparer.Ordinal);
        return CampaignGraph.Build(data, id =>
        {
            if (!scenarios.TryGetValue(id, out ScenarioData? s))
            {
                try
                {
                    s = Find(id)?.Document.BuildScenario();
                }
                catch (ArgumentException)
                {
                    s = null;
                }

                scenarios[id] = s;
            }

            return s;
        });
    }

    /// <summary>The <c>#</c> variables the maps use (carried to the next map, ADR 0133), sorted by name.</summary>
    public ImmutableArray<CarriedVariable> CarriedVariables()
    {
        var uses = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (CampaignEntry m in State.Maps)
        {
            ScenarioModel model = m.Document.State.Scenario;
            void Scan(ExpressionDef e)
            {
                string src = e.Source;
                for (int i = src.IndexOf('#', StringComparison.Ordinal); i >= 0; i = src.IndexOf('#', i + 1))
                {
                    int end = i + 1;
                    while (end < src.Length && (char.IsAsciiLetterOrDigit(src[end]) || src[end] == '_'))
                    {
                        end++;
                    }

                    if (end > i + 1)
                    {
                        string name = src[i..end];
                        if (!uses.TryGetValue(name, out List<string>? maps))
                        {
                            uses[name] = maps = [];
                        }

                        if (!maps.Contains(m.Id))
                        {
                            maps.Add(m.Id);
                        }
                    }
                }
            }

            ExpressionHelp.MapExpressions(model, e =>
            {
                Scan(e);
                return e;
            });
        }

        var b = ImmutableArray.CreateBuilder<CarriedVariable>();
        foreach (KeyValuePair<string, List<string>> u in uses)
        {
            b.Add(new CarriedVariable(u.Key, [.. u.Value]));
        }

        return b.ToImmutable();
    }

    /// <summary>
    /// Validation of the campaign (TDD §18.3): an entry map is required (blocking, Appendix C.9), map names must be unique,
    /// dangling transitions and unreachable maps are warnings, Persistent units without a namesake in a later map are
    /// notes; plus every map's own findings (with the campaign's shared resources available).
    /// </summary>
    public ValidationResult Validate(ValidationOptions? options = null)
    {
        options ??= new ValidationOptions();
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        CampaignState s = State;
        if (s.Maps.Count == 0)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Blocking, "campaign.maps", "The campaign has no maps.", EditorLocation.Map));
        }
        else if (!s.Maps.Exists(m => m.IsEntry))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Blocking, "campaign.entry", "No starting map: the original rejects such a campaign.", EditorLocation.Map));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CampaignEntry m in s.Maps)
        {
            if (!names.Add(m.MapName))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "campaign.name", $"Two maps are named '{m.MapName}'; transitions find the first.", new EditorLocation(LocationKind.CampaignMap, Name: m.Id)));
            }
        }

        CampaignGraph graph;
        try
        {
            graph = Graph();
        }
        catch (ArgumentException e)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Blocking, "campaign.build", e.Message, EditorLocation.Map));
            return new ValidationResult(issues.ToImmutable());
        }

        foreach (CampaignEdge e in graph.Edges)
        {
            if (e.ToMapId is null)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, "campaign.edge", $"'{e.ToMapName}' is not a map of this campaign.", new EditorLocation(LocationKind.CampaignMap, e.ActionIndex, Name: e.FromMapId)));
            }
        }

        foreach (CampaignNode n in graph.Unreachable)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "campaign.unreachable", $"Map '{n.Name}' cannot be reached from a starting map.", new EditorLocation(LocationKind.CampaignMap, Name: n.Id)));
        }

        foreach (KeyValuePair<string, ImmutableArray<string>> p in graph.Persistent)
        {
            if (p.Value.Length == 1)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Info, "campaign.persistent", $"Persistent unit '{p.Key}' appears in one map only; nothing carries it.", new EditorLocation(LocationKind.CampaignMap, Name: p.Value[0])));
            }
        }

        Func<string, bool> shared = path => SharedAssets.ContainsKey(path) || options.ExternalAsset?.Invoke(path) == true;
        foreach (CampaignEntry m in s.Maps)
        {
            foreach (ValidationIssue i in MapValidator.Validate(m.Document, options with { ExternalAsset = shared }).Issues)
            {
                if (i.Severity >= ValidationSeverity.Warning)
                {
                    issues.Add(i with { Message = $"{m.MapName}: {i.Message}", MapId = m.Id });
                }
            }
        }

        return new ValidationResult(issues.ToImmutable());
    }

    private string UniqueId(string name)
    {
        string stem = MapPackage.Slug(name);
        string id = stem;
        for (int i = 2; Find(id) is not null; i++)
        {
            id = stem + "-" + i.ToString(CultureInfo.InvariantCulture);
        }

        return id;
    }

    /// <summary>A short description of the campaign for status lines.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(State.Name);
        sb.Append(" (").Append(State.Maps.Count.ToString(CultureInfo.InvariantCulture)).Append(" maps)");
        return sb.ToString();
    }
}
