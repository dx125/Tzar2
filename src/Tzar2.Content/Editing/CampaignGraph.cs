using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>A node of the reconstructed campaign graph (REQ-EDT-08).</summary>
/// <param name="Id">Map id.</param>
/// <param name="Name">Map name actions use.</param>
/// <param name="IsEntry">A starting map.</param>
/// <param name="Reachable">Reachable from a starting map.</param>
public sealed record CampaignNode(string Id, string Name, bool IsEntry, bool Reachable);

/// <summary>The reconstructed graph of a campaign with its problems and the persistent-unit preview (REQ-EDT-08).</summary>
/// <param name="Nodes">Maps.</param>
/// <param name="Edges">Transitions (dangling ones have no target id).</param>
/// <param name="Dangling">Next-map names no map has.</param>
/// <param name="Persistent">Persistent unit names → the maps that have a unit of that name, in campaign order.</param>
public sealed record CampaignGraph(ImmutableArray<CampaignNode> Nodes, ImmutableArray<CampaignEdge> Edges, ImmutableArray<string> Dangling, ImmutableSortedDictionary<string, ImmutableArray<string>> Persistent)
{
    /// <summary>Builds the graph of a campaign from its maps' scenarios.</summary>
    /// <param name="campaign">The campaign.</param>
    /// <param name="scenarioOf">The scenario of a map id (null when unknown).</param>
    public static CampaignGraph Build(CampaignData campaign, Func<string, ScenarioData?> scenarioOf)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(scenarioOf);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (CampaignMap m in campaign.Maps)
        {
            if (m.IsEntry && reachable.Add(m.Id))
            {
                queue.Enqueue(m.Id);
            }
        }

        while (queue.Count > 0)
        {
            string from = queue.Dequeue();
            foreach (CampaignEdge e in campaign.Edges)
            {
                if (e.FromMapId == from && e.ToMapId is { } to && reachable.Add(to))
                {
                    queue.Enqueue(to);
                }
            }
        }

        var persistent = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (CampaignMap m in campaign.Maps)
        {
            if (scenarioOf(m.Id) is not { } s)
            {
                continue;
            }

            for (int i = 0; i < s.ObjectCount; i++)
            {
                ObjectInfo info = s.ObjectAt(i);
                if ((info.Flags & ObjectFlags.Persistent) != 0 && info.Name.Length > 0)
                {
                    if (!persistent.TryGetValue(info.Name, out List<string>? maps))
                    {
                        persistent[info.Name] = maps = [];
                    }

                    if (!maps.Contains(m.Id))
                    {
                        maps.Add(m.Id);
                    }
                }
            }
        }

        return new CampaignGraph(
            campaign.Maps.Items.Select(m => new CampaignNode(m.Id, m.MapName, m.IsEntry, reachable.Contains(m.Id))).ToImmutableArray(),
            campaign.Edges.Items,
            campaign.Edges.Items.Where(e => e.ToMapId is null).Select(e => e.ToMapName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToImmutableArray(),
            persistent.ToImmutableSortedDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Maps no starting map leads to.</summary>
    public IEnumerable<CampaignNode> Unreachable => Nodes.Where(n => !n.Reachable);
}
