using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Game.Session;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Persistence;
using Tzar2.Sim.Systems;
using ScenarioStance = Tzar2.Content.Compiled.Scenario.Stance;
using ScenarioVictory = Tzar2.Content.Compiled.Scenario.VictoryCondition;
using SimStance = Tzar2.Sim.Kingdoms.Stance;

namespace Tzar2.Game.Campaigns;

/// <summary>One playable map: the terrain and objects plus its scenario (ADR 0120).</summary>
/// <param name="Map">The map.</param>
/// <param name="Scenario">Its scenario (objects, kingdoms, triggers).</param>
public sealed record MissionContent(MapData Map, ScenarioData Scenario);

/// <summary>Where a campaign's maps come from: a converted <c>.wcm</c>, a <c>.t2camp</c> package, or code (tests, demo).</summary>
public interface ICampaignSource
{
    /// <summary>The campaign graph.</summary>
    CampaignData Campaign { get; }

    /// <summary>Loads the map with campaign id <paramref name="mapId"/>.</summary>
    MissionContent Load(string mapId);
}

/// <summary>A campaign whose maps are already in memory.</summary>
public sealed class InMemoryCampaignSource : ICampaignSource
{
    private readonly Dictionary<string, MissionContent> _maps;

    /// <summary>Creates the source.</summary>
    public InMemoryCampaignSource(CampaignData campaign, IReadOnlyDictionary<string, MissionContent> maps)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(maps);
        Campaign = campaign;
        _maps = new Dictionary<string, MissionContent>(maps, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public CampaignData Campaign { get; }

    /// <inheritdoc />
    public MissionContent Load(string mapId)
        => _maps.TryGetValue(mapId, out MissionContent? m) ? m : throw new KeyNotFoundException($"The campaign has no map '{mapId}'.");
}

/// <summary>
/// Builds the match of a scenario map (ADR 0133): kingdoms from the scenario's kingdom table (the first active
/// Human-or-AI kingdom is the local player, the others computers), civilizations, victory condition and population
/// limit from the map properties, a seed derived from the map, and the match-start preparation (starting stockpiles,
/// diplomacy, campaign carry-over).
/// </summary>
public static class ScenarioMatch
{
    /// <summary>The setup of <paramref name="mission"/>.</summary>
    /// <param name="mission">The map and scenario.</param>
    /// <param name="ruleset">The ruleset (civilizations must exist).</param>
    /// <param name="carry">Campaign carry-over (persistent units, variables), or null.</param>
    /// <param name="computerAi">Whether computer kingdoms run the legacy AI.</param>
    /// <param name="cheats">Whether cheats are allowed (the headless solver).</param>
    public static MatchSetup Setup(MissionContent mission, Ruleset ruleset, CampaignProgress? carry = null, bool computerAi = true, bool cheats = false)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(ruleset);
        ScenarioData s = mission.Scenario;
        int players = 1;
        foreach (MapObject o in mission.Map.Objects)
        {
            if (o.Owner != MapObject.Nature)
            {
                players = Math.Max(players, o.Owner + 1);
            }
        }

        foreach (KingdomDef k in s.Kingdoms)
        {
            if (k.Active && k.Number is >= 1 and <= 8)
            {
                players = Math.Max(players, k.Number);
            }
        }

        players = Math.Min(players, ruleset.MaxPlayers);

        // The legacy Rescue pseudo-kingdom (objects flagged Rescue, owned by Nature in the IR) becomes one more,
        // passive kingdom: Nature may not own working buildings in the sim. Rescuing itself (its units joining whoever
        // reaches them) is not implemented yet (UNVERIFIED, UNK-66).
        MapData map = mission.Map;
        int rescue = -1;
        for (int i = 0; i < map.Objects.Length && rescue < 0; i++)
        {
            if (Passive(s, ruleset, map.Objects, i))
            {
                // A free slot, else an inactive kingdom that owns nothing.
                rescue = players < ruleset.MaxPlayers ? players++ : FreeInactive(s, map, players);
                break;
            }
        }

        if (rescue >= 0)
        {
            var objects = new MapObject[map.Objects.Length];
            for (int i = 0; i < objects.Length; i++)
            {
                MapObject o = map.Objects[i];
                objects[i] = Passive(s, ruleset, map.Objects, i) ? o with { Owner = (byte)rescue } : o;
            }

            map = new MapData(map.Name, map.Width, map.Height, map.TerrainCorners, map.HeightCorners, map.CliffTiles, objects, map.TerrainSet, map.SetTerrainCorners);
        }

        int human = -1;
        foreach (KingdomDef k in s.Kingdoms)
        {
            if (k.Active && k.PlayerType == PlayerType.HumanOrAi && k.Number is >= 1 and <= 8 && k.Number <= players && (human < 0 || k.Number - 1 < human))
            {
                human = k.Number - 1;
            }
        }

        human = Math.Max(0, human);
        var list = new PlayerSetup[players];
        for (int p = 0; p < players; p++)
        {
            KingdomDef? k = Kingdom(s, p + 1);
            string civ = (k?.Civilization ?? Civilization.European) switch
            {
                Civilization.Arabian => "arab",
                Civilization.Asian => "asia",
                _ => "euro",
            };
            if (ruleset.EntityTypeId(civ + ".castle") < 0)
            {
                civ = "euro";
            }

            list[p] = new PlayerSetup((byte)p, civ, p == human ? ControllerKind.Human : p == rescue ? ControllerKind.Passive : ControllerKind.Computer, Difficulty.Normal);
        }

        var hasher = new StateHasher();
        hasher.Add(map.ContentHash).Add(s.ContentHash);
        return new MatchSetup
        {
            Map = new FixedMapSource(s.Properties.Title.Length > 0 ? s.Properties.Title : map.Name, map),
            Players = list,
            Seed = hasher.GetCurrentHash(),
            Victory = s.Properties.Victory switch
            {
                ScenarioVictory.Standard => Sim.VictoryCondition.Standard,
                ScenarioVictory.DestroyCastle => Sim.VictoryCondition.DestroyCastles,
                ScenarioVictory.ScoreLimit => Sim.VictoryCondition.ScoreLimit,
                ScenarioVictory.TimeLimit => Sim.VictoryCondition.TimeLimit,
                _ => Sim.VictoryCondition.NotSpecified,
            },
            PopulationLimit = s.Properties.PopulationLimit > 0 ? s.Properties.PopulationLimit : -1,
            NoFog = s.Properties.NoFog,
            Explored = s.Properties.Explore,
            Scenario = s,
            ComputerAi = computerAi,
            CheatsAllowed = cheats,
            Preparation = new Preparation(s, carry, (byte)human, rescue),
        };
    }

    private static int FreeInactive(ScenarioData s, MapData map, int players)
    {
        for (int p = players - 1; p >= 0; p--)
        {
            if (Kingdom(s, p + 1) is { Active: false })
            {
                bool owns = false;
                foreach (MapObject o in map.Objects)
                {
                    owns |= o.Owner == p;
                }

                if (!owns)
                {
                    return p;
                }
            }
        }

        return -1;
    }

    // Objects of the passive kingdom: those of the Rescue pseudo-kingdom, and owner-less buildings and units that work
    // for a kingdom (the sim gives Nature no workers, producers or drop-offs; legacy owner 0 "none").
    private static bool Passive(ScenarioData s, Ruleset ruleset, System.Collections.Immutable.ImmutableArray<MapObject> objects, int i)
    {
        if (i < s.ObjectCount && (s.ObjectAt(i).Flags & ObjectFlags.Rescue) != 0)
        {
            return true;
        }

        MapObject o = objects[i];
        if (o.Owner != MapObject.Nature || ruleset.EntityTypeId(o.TypeKey) is not (>= 0 and var type))
        {
            return false;
        }

        EntityTraits t = ruleset.EntityTypes[type].Traits;
        return t.Harvester is not null || t.IsBuilder || t.IsCapturer || (t.Producer is { Produces.IsEmpty: false } && t.World.Claim is null) || t.DropOffMask != 0;
    }

    private static KingdomDef? Kingdom(ScenarioData s, int number)
    {
        foreach (KingdomDef k in s.Kingdoms)
        {
            if (k.Number == number)
            {
                return k;
            }
        }

        return null;
    }

    /// <summary>Match start: stockpiles and diplomacy of the scenario's kingdoms, then the campaign carry-over.</summary>
    private sealed class Preparation(ScenarioData scenario, CampaignProgress? carry, byte human, int rescue) : IMatchPreparation
    {
        public void Prepare(Simulation simulation, LegacyTriggerSystem? triggers)
        {
            World world = simulation.World;
            int[] resources = [world.Ruleset.ResourceId("food"), world.Ruleset.ResourceId("wood"), world.Ruleset.ResourceId("gold"), world.Ruleset.ResourceId("stone")];
            foreach (KingdomDef k in scenario.Kingdoms)
            {
                int p = k.Number - 1;
                if (!world.HasPlayer(p))
                {
                    continue;
                }

                int[] amounts = [k.Food, k.Wood, k.Gold, k.Stone];
                for (int r = 0; r < 4; r++)
                {
                    if (resources[r] >= 0)
                    {
                        world.Players[p].Resources[resources[r]] = Math.Clamp(amounts[r], 0, 10_000_000);
                    }
                }
            }

            foreach (DiplomacyDef d in scenario.Diplomacy)
            {
                int a = d.From - 1;
                int b = d.To - 1;
                if (!world.HasPlayer(a) || !world.HasPlayer(b) || a == b)
                {
                    continue;
                }

                SimStance stance = d.Stance switch { ScenarioStance.Ally => SimStance.Ally, ScenarioStance.Neutral => SimStance.Neutral, _ => SimStance.Enemy };
                Sharing sharing = stance != SimStance.Ally ? Sharing.None
                    : (d.SharedVision ? Sharing.Vision : 0) | (d.SharedResources ? Sharing.Resources : 0) | (d.CommandUnits ? Sharing.UnitControl : 0)
                        | (Kingdom(scenario, d.From)?.SharedVictory == true ? Sharing.Victory : 0);
                ScenarioOperations.SetDiplomacy(world, a, b, stance, sharing);
            }

            // Runs after the loop above has set every pair's stance. A kingdom that lets an ally command its units (the legacy "Command Units" flag of the pair) is enough for
            // the sim's consented sharing, which needs both offers: the other side's offer is added (UNVERIFIED, UNK-69).
            foreach (DiplomacyDef d in scenario.Diplomacy)
            {
                int a = d.From - 1;
                int b = d.To - 1;
                if (d.CommandUnits && d.Stance == ScenarioStance.Ally && world.HasPlayer(a) && world.HasPlayer(b) && a != b
                    && world.Diplomacy.StanceOf(b, a) == SimStance.Ally)
                {
                    ScenarioOperations.SetDiplomacy(world, b, a, SimStance.Ally, world.Diplomacy.OffersOf(b, a) | Sharing.UnitControl);
                }
            }

            // The rescue kingdom is neutral to everyone (UNVERIFIED, UNK-66).
            if (world.HasPlayer(rescue))
            {
                for (int p = 0; p < world.Players.Length; p++)
                {
                    if (p != rescue)
                    {
                        ScenarioOperations.SetDiplomacy(world, rescue, p, SimStance.Neutral, Sharing.None);
                        ScenarioOperations.SetDiplomacy(world, p, rescue, SimStance.Neutral, Sharing.None);
                    }
                }
            }

            if (carry is not null && triggers is not null)
            {
                CampaignCarry.Apply(world, triggers, carry, human);
            }
        }
    }
}

/// <summary>
/// What crosses from one campaign map to the next (Appendix C.9; ADR 0133): units flagged Persistent, alive at the
/// end of a map, give their properties (level, experience, hero, potions, items) to the unit with the same name on a
/// later map; variables written <c>#Name</c> keep their value (UNVERIFIED campaign scope, UNK-74/UNK-68).
/// </summary>
public static class CampaignCarry
{
    /// <summary>The prefix of carried variables.</summary>
    public const string CarriedPrefix = "#";

    /// <summary>Applies carried units and variables at match setup.</summary>
    public static void Apply(World world, LegacyTriggerSystem triggers, CampaignProgress carry, byte human)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(carry);
        ScenarioData scenario = triggers.Scenario;
        for (int i = 0; i < scenario.ObjectCount; i++)
        {
            ObjectInfo info = scenario.ObjectAt(i);
            if ((info.Flags & ObjectFlags.Persistent) == 0 || info.Name.Length == 0 || triggers.State.MapEntities[i] == 0)
            {
                continue;
            }

            EntityId e = EntityId.FromValue(triggers.State.MapEntities[i]);
            if (!world.Entities.IsAlive(e) || Find(carry.Heroes, info.Name) is not { } hero)
            {
                continue;
            }

            PersistentHeroes.Apply(world, e, hero);
        }

        for (int v = 0; v < scenario.Variables.Length; v++)
        {
            string name = scenario.Variables[v];
            if (name.StartsWith(CarriedPrefix, StringComparison.Ordinal))
            {
                foreach (KeyValuePair<string, int> kv in carry.Variables)
                {
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        triggers.State.Variables[v] = kv.Value;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The carry-over at the end of a map: the alive Persistent units (by name; units of every kingdom, UNVERIFIED) merged
    /// over the previous records, and the carried variables.
    /// </summary>
    public static (ImmutableArray<PersistentHero> Heroes, ImmutableArray<KeyValuePair<string, int>> Variables) Capture(World world, LegacyTriggerSystem triggers, CampaignProgress previous)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(previous);
        ScenarioData scenario = triggers.Scenario;
        var heroes = new SortedDictionary<string, PersistentHero>(StringComparer.Ordinal);
        foreach (PersistentHero h in previous.Heroes)
        {
            heroes[h.Name] = h;
        }

        var persistentNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < scenario.ObjectCount; i++)
        {
            ObjectInfo info = scenario.ObjectAt(i);
            if ((info.Flags & ObjectFlags.Persistent) != 0 && info.Name.Length > 0)
            {
                persistentNames.Add(info.Name);
            }
        }

        for (int p = 0; p < world.Players.Length; p++)
        {
            foreach (PersistentHero h in PersistentHeroes.Capture(world, p))
            {
                if (persistentNames.Contains(h.Name))
                {
                    heroes[h.Name] = h;
                }
            }
        }

        var variables = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> kv in previous.Variables)
        {
            variables[kv.Key] = kv.Value;
        }

        for (int v = 0; v < scenario.Variables.Length; v++)
        {
            if (scenario.Variables[v].StartsWith(CarriedPrefix, StringComparison.Ordinal))
            {
                variables[scenario.Variables[v]] = triggers.State.Variables[v];
            }
        }

        return ([.. heroes.Values], [.. variables]);
    }

    private static PersistentHero? Find(ImmutableArray<PersistentHero> heroes, string name)
    {
        foreach (PersistentHero h in heroes)
        {
            if (string.Equals(h.Name, name, StringComparison.Ordinal))
            {
                return h;
            }
        }

        return null;
    }
}

/// <summary>What ended a mission and what comes next.</summary>
/// <param name="Result">Won, lost, or moved on by Load Map.</param>
/// <param name="NextMapId">The next map, or null (campaign finished, or retry after a loss).</param>
/// <param name="Progress">The updated progress.</param>
public sealed record MissionEnd(MissionResult Result, string? NextMapId, CampaignProgress Progress);

/// <summary>
/// The campaign state machine (ADR 0133; ORIG-08: transitions are explicit, with persistence snapshots): start at an
/// entry map or the saved current map, and at the end of a mission resolve the next map from the scenario's request
/// (Load Map / Player Win / Player Lose, names matched case-insensitively) or the campaign graph, record the result,
/// carry persistent units and variables over, and unlock the next map.
/// </summary>
public static class CampaignFlow
{
    /// <summary>Progress at the start of a campaign: the entry maps unlocked, the first one current.</summary>
    public static CampaignProgress Begin(CampaignData campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var entries = ImmutableArray.CreateBuilder<string>();
        foreach (CampaignMap m in campaign.Maps)
        {
            if (m.IsEntry)
            {
                entries.Add(m.Id);
            }
        }

        if (entries.Count == 0)
        {
            throw new ArgumentException($"Campaign '{campaign.Id}' has no entry map (Appendix C.9: it is invalid).", nameof(campaign));
        }

        return new CampaignProgress(campaign.Id, campaign.ContentHash, entries[0], entries.ToImmutable(), ImmutableArray<MissionRecord>.Empty, ImmutableArray<PersistentHero>.Empty, ImmutableArray<KeyValuePair<string, int>>.Empty, Completed: false);
    }

    /// <summary>The map id named <paramref name="mapName"/> (case-insensitive map name or id), or null.</summary>
    public static string? Resolve(CampaignData campaign, string? mapName)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return null;
        }

        string name = mapName.Trim();
        foreach (CampaignMap m in campaign.Maps)
        {
            if (string.Equals(m.MapName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Id, name, StringComparison.OrdinalIgnoreCase))
            {
                return m.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Ends a mission played in <paramref name="session"/> (stopping its sim thread): the local kingdom's outcome, or
    /// "moved" for a scenario transition without one, then <see cref="End"/> with the final world.
    /// </summary>
    public static MissionEnd Finish(CampaignData campaign, CampaignProgress progress, string mapId, GameSession session, string? requestedMap, bool transition)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Stop();
        PlayerFlags flags = session.Simulation.World.Players[session.LocalPlayer].Flags;
        MissionResult result = (flags & PlayerFlags.Defeated) != 0 ? MissionResult.Lost
            : (flags & PlayerFlags.Victorious) != 0 ? MissionResult.Won
            : transition ? MissionResult.Moved : MissionResult.Lost;
        return End(campaign, progress, mapId, result, transition ? requestedMap : null, session.Simulation.World.Tick.Value, session.Simulation.World, session.Scenario?.Triggers);
    }

    /// <summary>
    /// Ends the current mission. <paramref name="requestedMap"/> is the scenario's next-map name (null when the mission
    /// ended by the sim's own victory/defeat): without one, a win follows the map's single Win or Load Map edge, else the
    /// campaign is finished; a loss without one retries the mission (UNVERIFIED, UNK-68).
    /// </summary>
    public static MissionEnd End(CampaignData campaign, CampaignProgress progress, string mapId, MissionResult result, string? requestedMap, int ticks, World? world = null, LegacyTriggerSystem? triggers = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(progress);
        string? next = Resolve(campaign, requestedMap);
        if (next is null && requestedMap is null && result != MissionResult.Lost)
        {
            string? only = null;
            int count = 0;
            foreach (CampaignEdge e in campaign.Edges)
            {
                if (e.FromMapId == mapId && e.ToMapId is not null && e.Kind is CampaignEdgeKind.Win or CampaignEdgeKind.LoadMap && e.ToMapId != only)
                {
                    only = e.ToMapId;
                    count++;
                }
            }

            next = count == 1 ? only : null;
        }

        ImmutableArray<PersistentHero> heroes = progress.Heroes;
        ImmutableArray<KeyValuePair<string, int>> variables = progress.Variables;
        if (world is not null && triggers is not null && result != MissionResult.Lost)
        {
            (heroes, variables) = CampaignCarry.Capture(world, triggers, progress);
        }

        ImmutableArray<string> unlocked = progress.Unlocked;
        if (next is not null && !unlocked.Contains(next))
        {
            unlocked = unlocked.Add(next);
        }

        bool finished = next is null && result != MissionResult.Lost;
        string? current = next ?? (result == MissionResult.Lost ? mapId : null);
        CampaignProgress updated = progress with
        {
            Current = current,
            Unlocked = unlocked,
            History = progress.History.Add(new MissionRecord(mapId, result, ticks)),
            Heroes = heroes,
            Variables = variables,
            Completed = progress.Completed || finished,
        };
        return new MissionEnd(result, next, updated);
    }
}
