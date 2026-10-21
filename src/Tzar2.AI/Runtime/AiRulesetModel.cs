using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Tzar2.Content.Compiled;

namespace Tzar2.AI.Runtime;

/// <summary>
/// What the AI knows about a compiled ruleset (REQ-ARCH-01: the AI consumes the compiled <see cref="Ruleset"/>; ADR
/// 0050): the original object classes the scripts name (<c>footman</c>, <c>barrack</c>…) and, per entity type, its
/// class, producers, builders and role. Built once per ruleset; immutable.
/// </summary>
public sealed class AiRulesetModel
{
    private readonly int[] _classOfType;
    private readonly ushort[][] _typesOfClass;
    private readonly ushort[][] _producersOf;
    private readonly ushort[][] _buildersOf;
    private readonly Dictionary<string, int> _classIds = new(StringComparer.Ordinal);
    private readonly bool[] _classIsBuilding;
    private readonly bool[] _isArmy;
    private readonly bool[] _isWorker;
    private readonly bool[] _isMilitary;

    /// <summary>Builds the model.</summary>
    public AiRulesetModel(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        Ruleset = ruleset;
        int n = ruleset.EntityTypes.Count;
        _classOfType = new int[n];
        var names = new List<string>();
        var members = new List<List<ushort>>();
        var producers = new List<ushort>[n];
        var builders = new List<ushort>[n];
        _isArmy = new bool[n];
        _isWorker = new bool[n];
        for (int t = 0; t < n; t++)
        {
            producers[t] = [];
            builders[t] = [];
        }

        for (int t = 0; t < n; t++)
        {
            EntityTypeDefinition type = ruleset.EntityTypes[t];
            EntityTraits traits = type.Traits;
            string key = traits.LegacyClass.ToLowerInvariant();
            _classOfType[t] = -1;
            if (key.Length > 0 && traits.Category is EntityCategory.Unit or EntityCategory.Building)
            {
                if (!_classIds.TryGetValue(key, out int id))
                {
                    id = names.Count;
                    _classIds[key] = id;
                    names.Add(key);
                    members.Add([]);
                }

                _classOfType[t] = id;
                members[id].Add((ushort)t);
            }

            _isWorker[t] = traits.Harvester is not null || traits.IsBuilder;
            _isArmy[t] = type.IsMobile && traits.Armament is not null && !_isWorker[t] && traits.Category == EntityCategory.Unit;
            if (traits.Producer is { } producer)
            {
                foreach (ushort produced in producer.Produces)
                {
                    producers[produced].Add((ushort)t);
                }
            }

            foreach (ushort built in traits.Builds)
            {
                builders[built].Add((ushort)t);
            }
        }

        ClassNames = [.. names];
        _typesOfClass = new ushort[names.Count][];
        _classIsBuilding = new bool[names.Count];
        for (int c = 0; c < names.Count; c++)
        {
            _typesOfClass[c] = [.. members[c]];
            _classIsBuilding[c] = ruleset.EntityTypes[members[c][0]].Traits.Category == EntityCategory.Building;
        }

        _producersOf = new ushort[n][];
        _buildersOf = new ushort[n][];
        for (int t = 0; t < n; t++)
        {
            _producersOf[t] = [.. producers[t]];
            _buildersOf[t] = [.. builders[t]];
        }

        // What the war half of the AI makes (AI assistance, ADR 0183; the owner: troops, military buildings and their
        // training): units that neither work nor trade, towers, walls, gates and buildings that train only such units.
        _isMilitary = new bool[n];
        for (int t = 0; t < n; t++)
        {
            EntityTraits traits = ruleset.EntityTypes[t].Traits;
            _isMilitary[t] = traits.Category == EntityCategory.Unit && !_isWorker[t] && traits.World.Trader is null;
        }

        for (int t = 0; t < n; t++)
        {
            EntityTraits traits = ruleset.EntityTypes[t].Traits;
            if (traits.Category == EntityCategory.Building)
            {
                bool trainsOnlyTroops = traits.Producer is { Produces.Length: > 0 } p && p.Produces.All(u => _isMilitary[u]);
                _isMilitary[t] = traits.Armament is not null || traits.IsWall || traits.Gate is not null || trainsOnlyTroops;
            }
        }

        ResourceCount = ruleset.ResourceTypes.Count;
    }

    /// <summary>
    /// Whether the war half of the AI makes this type (troops and military buildings); the economy half makes the
    /// rest (AI assistance, ADR 0183).
    /// </summary>
    public bool IsMilitary(int typeId) => _isMilitary[typeId];

    /// <summary>The ruleset.</summary>
    public Ruleset Ruleset { get; }

    /// <summary>Original class names (lower case), by class id.</summary>
    public ImmutableArray<string> ClassNames { get; }

    /// <summary>Number of classes.</summary>
    public int ClassCount => ClassNames.Length;

    /// <summary>Number of resource types.</summary>
    public int ResourceCount { get; }

    /// <summary>The class of a type, or -1 (types without an original class: scenery, test types).</summary>
    public int ClassOf(int typeId) => _classOfType[typeId];

    /// <summary>The types of a class over all civilizations (ascending ids).</summary>
    public ReadOnlySpan<ushort> TypesOf(int classId) => _typesOfClass[classId];

    /// <summary>Whether the class is a building class.</summary>
    public bool IsBuildingClass(int classId) => _classIsBuilding[classId];

    /// <summary>Producer types that train <paramref name="typeId"/>.</summary>
    public ReadOnlySpan<ushort> ProducersOf(int typeId) => _producersOf[typeId];

    /// <summary>Builder types that construct <paramref name="typeId"/>.</summary>
    public ReadOnlySpan<ushort> BuildersOf(int typeId) => _buildersOf[typeId];

    /// <summary>Whether entities of the type fight as soldiers (mobile, armed, neither harvester nor builder).</summary>
    public bool IsArmy(int typeId) => _isArmy[typeId];

    /// <summary>Whether entities of the type harvest or build.</summary>
    public bool IsWorker(int typeId) => _isWorker[typeId];

    /// <summary>
    /// The class a script name means: the exact class, else the name without a plural <c>s</c> or <c>es</c>, else
    /// <c>-men</c> read as <c>-man</c> (<c>barracks</c> → <c>barrack</c>, <c>knights</c> → <c>knight</c>,
    /// <c>footmen</c> → <c>footman</c>; UNVERIFIED plural rule, Appendix D.2), or -1.
    /// </summary>
    public int ClassId(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string key = name.ToLowerInvariant();
        if (_classIds.TryGetValue(key, out int id))
        {
            return id;
        }

        if (key.Length > 1 && key[^1] == 's' && _classIds.TryGetValue(key[..^1], out id))
        {
            return id;
        }

        if (key.Length > 2 && key.EndsWith("es", StringComparison.Ordinal) && _classIds.TryGetValue(key[..^2], out id))
        {
            return id;
        }

        return key.Length > 3 && key.EndsWith("men", StringComparison.Ordinal) && _classIds.TryGetValue(key[..^3] + "man", out id) ? id : -1;
    }

    /// <summary>
    /// The ruleset civilization key of a script civilization name (<c>european</c> → <c>euro</c>, <c>asian</c> →
    /// <c>asia</c>, <c>arabian</c> → <c>arab</c>); other names are taken as keys.
    /// </summary>
    public static string CivilizationKey(string scriptName)
    {
        ArgumentNullException.ThrowIfNull(scriptName);
        return scriptName.ToLowerInvariant() switch
        {
            "european" => "euro",
            "asian" => "asia",
            "arabian" => "arab",
            var other => other,
        };
    }

    /// <summary>Whether a type belongs to the civilization (its own types and the common ones, <c>comm</c> or none).</summary>
    public bool IsOfCivilization(int typeId, string civilizationKey)
    {
        string civ = Ruleset.EntityTypes[typeId].Traits.Civilization;
        return civ.Length == 0 || string.Equals(civ, "comm", StringComparison.Ordinal) || string.Equals(civ, civilizationKey, StringComparison.Ordinal);
    }
}
