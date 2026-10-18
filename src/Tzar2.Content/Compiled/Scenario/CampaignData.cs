using System;
using System.Collections.Generic;
using System.Text;
using Tzar2.Core.Hashing;

namespace Tzar2.Content.Compiled.Scenario;

/// <summary>How one campaign map leads to another (TDD §18.3 campaign graph, REQ-EDT-08).</summary>
public enum CampaignEdgeKind : byte
{
    /// <summary>A Player Win action names the next map.</summary>
    Win,

    /// <summary>A Player Lose action names the next map.</summary>
    Lose,

    /// <summary>A Load Map action names the next map.</summary>
    LoadMap,
}

/// <summary>One map of a campaign.</summary>
/// <param name="Id">Stable id inside the campaign (the lower-cased map name).</param>
/// <param name="MapName">The name actions refer to (matched case-insensitively).</param>
/// <param name="FileName">The map file inside the campaign package, e.g. <c>maps/saa3.t2map</c>.</param>
/// <param name="Description">The map's description.</param>
/// <param name="IsEntry">A starting map.</param>
public sealed record CampaignMap(string Id, string MapName, string FileName, string Description, bool IsEntry);

/// <summary>A transition of the campaign graph.</summary>
/// <param name="FromMapId">The map holding the action.</param>
/// <param name="ToMapName">The next-map name as written.</param>
/// <param name="ToMapId">The resolved target map id, or null when no map has that name (dangling).</param>
/// <param name="Kind">Which action.</param>
/// <param name="ActionIndex">Index of the action in the source map's <see cref="ScenarioData.Actions"/>.</param>
public sealed record CampaignEdge(string FromMapId, string ToMapName, string? ToMapId, CampaignEdgeKind Kind, int ActionIndex);

/// <summary>
/// A campaign (Appendix C.9, ADR 0120): its maps, entry maps and the transitions rebuilt from the maps' Player Win,
/// Player Lose and Load Map actions. Map order lives in the maps themselves, so this is derived data plus the
/// campaign description.
/// </summary>
public sealed class CampaignData
{
    /// <summary>Creates a campaign and checks its references.</summary>
    /// <param name="id">Stable id (profile keys, persistence).</param>
    /// <param name="name">Campaign title.</param>
    /// <param name="description">Campaign description.</param>
    /// <param name="maps">The maps (unique ids).</param>
    /// <param name="edges">Transitions.</param>
    /// <exception cref="ArgumentException">An id repeats or an edge refers to an unknown map.</exception>
    public CampaignData(string id, string name, string description, EquatableArray<CampaignMap> maps, EquatableArray<CampaignEdge> edges)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (CampaignMap m in maps)
        {
            if (string.IsNullOrEmpty(m.Id) || !ids.Add(m.Id))
            {
                throw new ArgumentException($"Campaign map id '{m.Id}' is empty or repeated.", nameof(maps));
            }
        }

        foreach (CampaignEdge e in edges)
        {
            if (!ids.Contains(e.FromMapId) || (e.ToMapId is not null && !ids.Contains(e.ToMapId)))
            {
                throw new ArgumentException($"Campaign edge {e.FromMapId} → {e.ToMapName} refers to an unknown map.", nameof(edges));
            }
        }

        Id = id;
        Name = name;
        Description = description;
        Maps = maps;
        Edges = edges;
        var hasher = new StateHasher(ScenarioData.HashVersion);
        hasher.Add(Encoding.UTF8.GetBytes(id)).Add(Encoding.UTF8.GetBytes(name)).Add(Encoding.UTF8.GetBytes(description)).Add(maps.Length);
        foreach (CampaignMap m in maps)
        {
            hasher.Add(Encoding.UTF8.GetBytes(m.Id)).Add(Encoding.UTF8.GetBytes(m.MapName)).Add(Encoding.UTF8.GetBytes(m.FileName))
                .Add(Encoding.UTF8.GetBytes(m.Description)).Add(m.IsEntry);
        }

        hasher.Add(edges.Length);
        foreach (CampaignEdge e in edges)
        {
            hasher.Add(Encoding.UTF8.GetBytes(e.FromMapId)).Add(Encoding.UTF8.GetBytes(e.ToMapName))
                .Add(Encoding.UTF8.GetBytes(e.ToMapId ?? string.Empty)).Add((byte)e.Kind).Add(e.ActionIndex);
        }

        ContentHash = hasher.GetCurrentHash();
    }

    /// <summary>Stable id.</summary>
    public string Id { get; }

    /// <summary>Title.</summary>
    public string Name { get; }

    /// <summary>Description shown on the campaign screen.</summary>
    public string Description { get; }

    /// <summary>Maps, in the order of the campaign's map list.</summary>
    public EquatableArray<CampaignMap> Maps { get; }

    /// <summary>Transitions.</summary>
    public EquatableArray<CampaignEdge> Edges { get; }

    /// <summary>xxHash64 of the campaign (profile keys, saves).</summary>
    public ulong ContentHash { get; }

    /// <summary>The map whose <see cref="CampaignMap.MapName"/> equals <paramref name="mapName"/> ignoring case, or null.</summary>
    public CampaignMap? FindMap(string mapName)
    {
        foreach (CampaignMap m in Maps)
        {
            if (string.Equals(m.MapName, mapName, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }

        return null;
    }
}
