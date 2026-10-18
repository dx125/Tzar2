using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Rules;

/// <summary>Reads the <c>gameplay:</c> section (ruleset-wide constants and the compile-time conversion factors).</summary>
internal sealed class GameplayReader
{
    private static readonly string[] KnownFields =
    [
        "groupMoveSpeed", "populationLimit", "startingResources", "defaultStartingResources", "minimumDamage", "resistanceScale",
        "interactionReachPx", "meleeReachPx", "acquireIntervalMs", "nodeSearchRadiusTiles", "constructionRefundPercent",
        "productionRefundPercent", "queueCapacity", "speedPixelsPerSecondPerPoint", "attackCycleMs", "hitDelayPercent",
        "researchRefundPercent", "capturePointsPerSecond", "minimumCaptureResistance", "captureResetMs", "influenceRadiusTiles",
        "repairTimePercent", "burnThresholdPercent", "burnDamagePerMinute", "wallContinueRadiusTiles", "maxWallSegments",
        "gateOpenReachPx", "manaRegenPerMinute",
    ];

    private readonly RulesetCompiler.SectionSet _settings;
    private readonly List<string> _resources;

    public GameplayReader(RulesetCompiler.SectionSet settings, List<string> resources)
    {
        _settings = settings;
        _resources = resources;
        if (settings.Mapping("gameplay") is { } map)
        {
            foreach (KeyValuePair<string, YamlNode> f in map.Entries)
            {
                if (Array.IndexOf(KnownFields, f.Key) < 0)
                {
                    throw new RulesException(settings.FieldSource("gameplay", f.Key), f.Value.Line, $"gameplay.{f.Key}: unknown setting");
                }
            }
        }
    }

    /// <summary>Legacy <c>SPEED</c> point → px/s (UNK-12).</summary>
    public int SpeedPixelsPerSecondPerPoint => Positive("speedPixelsPerSecondPerPoint", 12);

    /// <summary>Attack cycle at <c>RATE</c> 1; the cooldown is this divided by RATE (UNK-07).</summary>
    public int AttackCycleMs => Positive("attackCycleMs", 6000);

    /// <summary>Hit frame as a share of the cooldown (UNK-07).</summary>
    public int HitDelayPercent => Range("hitDelayPercent", 50, 0, 100);

    /// <summary>Default production queue length (UNK-20).</summary>
    public int QueueCapacity => Range("queueCapacity", 10, 1, ProducerDefinition.MaxQueueCapacity);

    /// <summary>Default mana regeneration of a <c>ManaPool</c> without its own (UNK-26).</summary>
    public int ManaRegenPerMinute => Range("manaRegenPerMinute", 0, 0, 1_000_000);

    public GameplaySettings Build(LegacyTables? legacy, RulesetCompiler.SectionSet settings)
    {
        GameplaySettings d = GameplaySettings.Default;
        GroupMoveSpeed group = (settings.Scalar("gameplay", "groupMoveSpeed") ?? "slowest") switch
        {
            "slowest" => GroupMoveSpeed.Slowest,
            "own" => GroupMoveSpeed.Own,
            { } other => throw Error("groupMoveSpeed", $"'{other}' is not slowest/own"),
        };

        (ImmutableArray<StartingResources> levels, int defaultLevel) = StartingLevels(legacy, settings);
        return d with
        {
            GroupMoveSpeed = group,
            PopulationLimit = Range("populationLimit", d.PopulationLimit, 0, 100_000),
            StartingResources = levels,
            DefaultStartingResources = defaultLevel,
            MinimumDamage = Range("minimumDamage", d.MinimumDamage, 0, 1_000_000),
            ResistanceScale = Range("resistanceScale", d.ResistanceScale, 1, 1_000_000),
            InteractionReachUnits = Range("interactionReachPx", d.InteractionReachUnits / 64, 0, 256) * 64,
            MeleeReachUnits = Range("meleeReachPx", d.MeleeReachUnits / 64, 0, 256) * 64,
            AcquireIntervalMs = Range("acquireIntervalMs", d.AcquireIntervalMs, 1, 60_000),
            NodeSearchRadiusTiles = Range("nodeSearchRadiusTiles", d.NodeSearchRadiusTiles, 0, 64),
            ConstructionRefundBasisPoints = Range("constructionRefundPercent", d.ConstructionRefundBasisPoints / 100, 0, 100) * 100,
            ProductionRefundBasisPoints = Range("productionRefundPercent", d.ProductionRefundBasisPoints / 100, 0, 100) * 100,
            ResearchRefundBasisPoints = Range("researchRefundPercent", d.ResearchRefundBasisPoints / 100, 0, 100) * 100,
            CapturePointsPerSecond = Range("capturePointsPerSecond", d.CapturePointsPerSecond, 0, 1_000_000),
            MinimumCaptureResistance = Range("minimumCaptureResistance", d.MinimumCaptureResistance, 1, 1_000_000),
            CaptureResetMs = Range("captureResetMs", d.CaptureResetMs, 0, 3_600_000),
            InfluenceRadiusTiles = Range("influenceRadiusTiles", d.InfluenceRadiusTiles, 0, 128),
            RepairTimeBasisPoints = Range("repairTimePercent", d.RepairTimeBasisPoints / 100, 1, 10_000) * 100,
            BurnThresholdBasisPoints = Range("burnThresholdPercent", d.BurnThresholdBasisPoints / 100, 0, 100) * 100,
            BurnDamagePerMinute = Range("burnDamagePerMinute", d.BurnDamagePerMinute, 0, 1_000_000),
            WallContinueRadiusTiles = Range("wallContinueRadiusTiles", d.WallContinueRadiusTiles, 0, 64),
            MaxWallSegments = Range("maxWallSegments", d.MaxWallSegments, 1, 256),
            GateOpenReachUnits = Range("gateOpenReachPx", d.GateOpenReachUnits / 64, 0, 256) * 64,
        };
    }

    private (ImmutableArray<StartingResources> Levels, int Default) StartingLevels(LegacyTables? legacy, RulesetCompiler.SectionSet settings)
    {
        YamlNode? node = settings.Field("gameplay", "startingResources");
        if (node is null)
        {
            return ([], 0);
        }

        string source = settings.FieldSource("gameplay", "startingResources");
        var levels = ImmutableArray.CreateBuilder<StartingResources>();
        if (node is YamlScalar { Value: "legacy" })
        {
            // RES.INI [Low]/[Medium]/[High]: the lobby's STARTING RESOURCES (manual §3).
            YamlMapping map = settings.Field("legacy", "startingResources") is { } m
                ? RulesetCompiler.Map(m, settings.FieldSource("legacy", "startingResources"), "legacy.startingResources")
                : throw new RulesException(source, node.Line, "gameplay.startingResources: legacy needs legacy.startingResources");
            if (legacy is null || !legacy.Has("data/res.ini"))
            {
                throw new RulesException(source, node.Line, "gameplay.startingResources: RES.INI is not in the imported package");
            }

            LegacyIni res = legacy.Ini("data/res.ini");
            foreach (KeyValuePair<string, YamlNode> level in map.Entries)
            {
                string sectionName = RulesetCompiler.Str(level.Value, source, $"legacy.startingResources.{level.Key}");
                LegacyIniSection section = res.Section(sectionName) ?? throw new RulesException("legacy:data/res.ini", 0, $"no section [{sectionName}]");
                int[] amounts = new int[_resources.Count];
                for (int r = 0; r < amounts.Length; r++)
                {
                    string? text = section.Get(_resources[r]);
                    amounts[r] = text is null ? 0 : RulesetCompiler.Int(new YamlScalar(text, false), "legacy:data/res.ini", $"[{sectionName}] {_resources[r]}");
                }

                levels.Add(new StartingResources(level.Key, [.. amounts]));
            }
        }
        else
        {
            foreach (KeyValuePair<string, YamlNode> level in RulesetCompiler.Map(node, source, "gameplay.startingResources").Entries)
            {
                int[] amounts = new int[_resources.Count];
                foreach (KeyValuePair<string, YamlNode> a in RulesetCompiler.Map(level.Value, source, $"gameplay.startingResources.{level.Key}").Entries)
                {
                    int r = _resources.IndexOf(a.Key);
                    if (r < 0)
                    {
                        throw new RulesException(source, a.Value.Line, $"gameplay.startingResources.{level.Key}: unknown resource '{a.Key}'");
                    }

                    amounts[r] = RulesetCompiler.Int(a.Value, source, $"gameplay.startingResources.{level.Key}.{a.Key}");
                }

                levels.Add(new StartingResources(level.Key, [.. amounts]));
            }
        }

        int index = 0;
        if (settings.Scalar("gameplay", "defaultStartingResources") is { } wanted)
        {
            index = -1;
            for (int i = 0; i < levels.Count; i++)
            {
                if (levels[i].Key == wanted)
                {
                    index = i;
                }
            }

            if (index < 0)
            {
                throw Error("defaultStartingResources", $"no starting-resource level '{wanted}'");
            }
        }

        return (levels.ToImmutable(), index);
    }

    private int Positive(string field, int fallback) => Range(field, fallback, 1, int.MaxValue);

    private int Range(string field, int fallback, int min, int max)
    {
        int value = _settings.Int("gameplay", field) ?? fallback;
        return value < min || value > max ? throw Error(field, $"{value} is outside {min}..{max}") : value;
    }

    private RulesException Error(string field, string message)
        => new(_settings.FieldSource("gameplay", field), _settings.Field("gameplay", field)?.Line ?? 0, $"gameplay.{field}: {message}");
}
