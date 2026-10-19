using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Campaigns;

namespace Tzar2.Game.Editing;

/// <summary>
/// Plays a campaign being edited (TDD §18.3 playtest, §18.6): the campaign document becomes a campaign source whose maps
/// are built from their documents when a mission starts, so the campaign flow, carry-over and saves of ADR 0133 run on it
/// unchanged. A playtest is a snapshot: each map is built once, when its mission first starts; edits made to a map while
/// the campaign plays take effect in the next playtest.
/// </summary>
public sealed class CampaignPlaytest : ICampaignSource
{
    private readonly CampaignDocument _campaign;
    private readonly Dictionary<string, MissionContent> _built = new(StringComparer.Ordinal);

    /// <summary>Creates the source; the graph is built now, maps on first use.</summary>
    /// <exception cref="ArgumentException">The campaign does not build.</exception>
    public CampaignPlaytest(CampaignDocument campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        _campaign = campaign;
        Campaign = campaign.Build();
    }

    /// <inheritdoc/>
    public CampaignData Campaign { get; }

    /// <inheritdoc/>
    public MissionContent Load(string mapId)
    {
        if (!_built.TryGetValue(mapId, out MissionContent? mission))
        {
            CampaignEntry entry = _campaign.Find(mapId) ?? throw new KeyNotFoundException($"The campaign has no map '{mapId}'.");
            mission = Playtest.Mission(entry.Document);
            _built[mapId] = mission;
        }

        return mission;
    }
}
