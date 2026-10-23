using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Tzar2.Content.MapGen;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;
using Tzar2.Sim;

namespace Tzar2.Game.Ui;

/// <summary>One slot of the multiplayer setup (SHELL-016).</summary>
/// <param name="Slot">The lobby slot.</param>
/// <param name="Kind">Open, closed, player or computer.</param>
/// <param name="Colour">The colour the kingdom plays with (0-7), or −1 for a slot without a kingdom.</param>
/// <param name="Colours">The colours this slot can pick: its own and those no other kingdom has.</param>
/// <param name="Script">The index of its computer player's strategy in <see cref="LobbyScreenModel.Scripts"/>.</param>
/// <param name="CanChoose">Whether this machine may change the slot's civilization, team, colour and strategy:
/// the player's own slot while not ready, or a computer's as host (SHELL-016, SHELL-017).</param>
public sealed record LobbyRow(byte Slot, SlotKind Kind, int Colour, IReadOnlyList<int> Colours, int Script, bool CanChoose);

/// <summary>A setting of the multiplayer settings (SHELL-018).</summary>
public enum LobbySettingKey
{
    /// <summary>Random map type.</summary>
    MapType,

    /// <summary>Random map size.</summary>
    MapSize,

    /// <summary>Terrain set.</summary>
    Terrain,

    /// <summary>The random map's resources.</summary>
    MapResources,

    /// <summary>A guarded town and towers.</summary>
    GuardedTown,

    /// <summary>Animals.</summary>
    Animals,

    /// <summary>Gateways.</summary>
    Gateways,

    /// <summary>Magic items.</summary>
    MagicItems,

    /// <summary>Cliffs.</summary>
    Cliffs,

    /// <summary>Standard or deathmatch.</summary>
    GameType,

    /// <summary>The victory condition.</summary>
    Victory,

    /// <summary>The starting resources.</summary>
    StartingResources,

    /// <summary>The population limit.</summary>
    PopulationLimit,

    /// <summary>No fog.</summary>
    NoFog,

    /// <summary>The map starts explored.</summary>
    Explore,

    /// <summary>Day and night.</summary>
    DayNight,

    /// <summary>Weather.</summary>
    Weather,

    /// <summary>Game speed.</summary>
    Speed,

    /// <summary>AI assistance.</summary>
    AiAssist,

    /// <summary>Cheat codes allowed.</summary>
    Cheats,

    /// <summary>A ranked match.</summary>
    Ranked,
}

/// <summary>A setting with its choices as the settings panel shows it.</summary>
/// <param name="Key">Which setting.</param>
/// <param name="Caption">Its caption.</param>
/// <param name="Items">The choices (a check box has "off" and "on").</param>
/// <param name="Selected">The current choice.</param>
/// <param name="Enabled">Whether this machine may change it (the host; cheats never in a ranked match).</param>
/// <param name="IsCheck">Whether it is a check box.</param>
public sealed record LobbySetting(LobbySettingKey Key, string Caption, IReadOnlyList<string> Items, int Selected, bool Enabled, bool IsCheck);

/// <summary>
/// The multiplayer setup and settings screens (SHELL-016, SHELL-017, SHELL-018; manual "Multiplayer Setup Screen" and
/// "Multiplayer Settings Screen"): each slot's colour, strategy, civilization and team, who may change them, the host's
/// match settings and what each choice does to them (engine-free model, ADR 0182). The relay enforces who may change
/// what (ADR 0160); the model only offers it.
/// </summary>
public sealed class LobbyScreenModel
{
    /// <summary>Population limits offered (as single player's random map screen).</summary>
    public static readonly ImmutableArray<int> PopulationLimits = [50, 100, 150, 200, 300, 500, 1000];

    /// <summary>Game speeds offered, in percent.</summary>
    public static readonly ImmutableArray<int> Speeds = [50, 75, 100, 150, 200, 300];

    private static readonly string[] OffOn = ["off", "on"];
    private static readonly RandomMapType[] MapTypes = Enum.GetValues<RandomMapType>();
    private static readonly RandomMapSize[] MapSizes = Enum.GetValues<RandomMapSize>();
    private static readonly MapTerrainSet[] Terrains = Enum.GetValues<MapTerrainSet>();
    private static readonly VictoryCondition[] Victories = [VictoryCondition.Standard, VictoryCondition.DestroyCastles, VictoryCondition.TimeLimit, VictoryCondition.ScoreLimit];

    private readonly LobbyView _lobby;

    /// <summary>Creates the model of <paramref name="lobby"/> with the strategies of <paramref name="scripts"/>.</summary>
    public LobbyScreenModel(LobbyView lobby, IReadOnlyList<AiScriptOption> scripts)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(scripts);
        _lobby = lobby;
        Scripts = scripts.Count > 0 ? scripts : [new AiScriptOption("Default", string.Empty)];
        Rows = BuildRows();
        Settings = BuildSettings();
    }

    /// <summary>The strategies a computer player can follow (the first is the default).</summary>
    public IReadOnlyList<AiScriptOption> Scripts { get; }

    /// <summary>Every slot.</summary>
    public IReadOnlyList<LobbyRow> Rows { get; }

    /// <summary>The match settings (SHELL-018).</summary>
    public IReadOnlyList<LobbySetting> Settings { get; }

    /// <summary>Whether START is active: this machine hosts and every player is ready (SHELL-016).</summary>
    public bool CanStart => _lobby.LocalIsHost && _lobby.Slots.All(s => s.Kind != SlotKind.Human || s.Ready || s.IsHost);

    /// <summary>The choice of <paramref name="slot"/> with colour <paramref name="colour"/>.</summary>
    public SlotChoice WithColour(byte slot, int colour) => _lobby.Slots[slot].Choice with { Colour = (byte)colour };

    /// <summary>The choice of <paramref name="slot"/> with the strategy at <paramref name="index"/> of <see cref="Scripts"/>.</summary>
    public SlotChoice WithScript(byte slot, int index) => _lobby.Slots[slot].Choice with { AiScript = Scripts[Math.Clamp(index, 0, Scripts.Count - 1)].Script };

    /// <summary>The settings after choosing item <paramref name="index"/> of <paramref name="key"/>.</summary>
    public MatchSettings Apply(LobbySettingKey key, int index)
    {
        MatchSettings s = _lobby.Settings;
        bool on = index != 0;
        return key switch
        {
            LobbySettingKey.MapType => s with { Map = MapSourceKind.RandomMap, MapType = MapTypes[Math.Clamp(index, 0, MapTypes.Length - 1)] },
            LobbySettingKey.MapSize => s with { MapSize = MapSizes[Math.Clamp(index, 0, MapSizes.Length - 1)] },
            LobbySettingKey.Terrain => s with { Terrain = Terrains[Math.Clamp(index, 0, Terrains.Length - 1)] },
            LobbySettingKey.MapResources => s with { Resources = (MapResourceLevel)Math.Clamp(index, 0, 2) },
            LobbySettingKey.GuardedTown => s with { Features = Feature(s.Features, MapFeatures.GuardedTowns, on) },
            LobbySettingKey.Animals => s with { Features = Feature(s.Features, MapFeatures.Animals, on) },
            LobbySettingKey.Gateways => s with { Features = Feature(s.Features, MapFeatures.Gateways, on) },
            LobbySettingKey.MagicItems => s with { Features = Feature(s.Features, MapFeatures.MagicItems, on) },
            LobbySettingKey.Cliffs => s with { Features = Feature(s.Features, MapFeatures.Cliffs, on) },
            LobbySettingKey.GameType => s with { GameType = on ? GameType.Deathmatch : GameType.Standard },
            LobbySettingKey.Victory => s with { Victory = Victories[Math.Clamp(index, 0, Victories.Length - 1)] },
            LobbySettingKey.StartingResources => s with { StartingResources = Math.Clamp(index, 0, 3) - 1 },
            LobbySettingKey.PopulationLimit => s with { PopulationLimit = index <= 0 ? -1 : PopulationLimits[Math.Min(index, PopulationLimits.Length) - 1] },
            LobbySettingKey.NoFog => s with { NoFog = on },
            LobbySettingKey.Explore => s with { Explored = on },
            LobbySettingKey.DayNight => s with { DayNight = on },
            LobbySettingKey.Weather => s with { Weather = on },
            LobbySettingKey.Speed => s with { SpeedPercent = Speeds[Math.Clamp(index, 0, Speeds.Length - 1)] },
            LobbySettingKey.AiAssist => s with { AiAssist = on },
            LobbySettingKey.Cheats => s with { CheatsAllowed = on && !s.Ranked },
            LobbySettingKey.Ranked => s with { Ranked = on, CheatsAllowed = false },
            _ => s,
        };
    }

    private static MapFeatures Feature(MapFeatures features, MapFeatures flag, bool on) => on ? features | flag : features & ~flag;

    private List<LobbyRow> BuildRows()
    {
        // The colours the kingdoms will play with, as the match description resolves them (LobbyDescription).
        var kingdoms = _lobby.Slots.Where(s => s.Kind is SlotKind.Human or SlotKind.Computer).ToList();
        byte[] colours = LobbyDescription.Colours(kingdoms.Select(s => s.Choice.Colour).ToArray());
        var colourOf = new Dictionary<byte, int>();
        for (int k = 0; k < kingdoms.Count; k++)
        {
            colourOf[kingdoms[k].Slot] = colours[k];
        }

        var rows = new List<LobbyRow>(_lobby.Slots.Count);
        foreach (LobbySlotView s in _lobby.Slots)
        {
            int colour = colourOf.TryGetValue(s.Slot, out int c) ? c : -1;
            int[] free = colour < 0 ? [] : [.. Enumerable.Range(0, 8).Where(x => x == colour || !colourOf.Values.Contains(x))];
            int script = Math.Max(0, Scripts.ToList().FindIndex(o => o.Script == s.Choice.AiScript));
            bool canChoose = (s.IsLocal && s.Kind == SlotKind.Human && !s.Ready) || (_lobby.LocalIsHost && s.Kind == SlotKind.Computer);
            rows.Add(new LobbyRow(s.Slot, s.Kind, colour, free, script, canChoose));
        }

        return rows;
    }

    private List<LobbySetting> BuildSettings()
    {
        MatchSettings s = _lobby.Settings;
        bool host = _lobby.LocalIsHost;
        var list = new List<LobbySetting>();
        void Option(LobbySettingKey key, string caption, IReadOnlyList<string> items, int selected, bool enabled = true) =>
            list.Add(new LobbySetting(key, caption, items, Math.Clamp(selected, 0, items.Count - 1), host && enabled, false));
        void Check(LobbySettingKey key, string caption, bool value, bool enabled = true) =>
            list.Add(new LobbySetting(key, caption, OffOn, value ? 1 : 0, host && enabled, true));

        Option(LobbySettingKey.MapType, "Map", [.. MapTypes.Select(t => t.ToString())], Array.IndexOf(MapTypes, s.MapType));
        Option(LobbySettingKey.MapSize, "Size", [.. MapSizes.Select(t => t.ToString())], Array.IndexOf(MapSizes, s.MapSize));
        Option(LobbySettingKey.Terrain, "Terrain", [.. Terrains.Select(t => t.ToString())], Array.IndexOf(Terrains, s.Terrain));
        Option(LobbySettingKey.MapResources, "Map resources", ["Low", "Normal", "High"], (int)s.Resources);
        Check(LobbySettingKey.GuardedTown, "Guarded town", s.Features.HasFlag(MapFeatures.GuardedTowns));
        Check(LobbySettingKey.Animals, "Animals", s.Features.HasFlag(MapFeatures.Animals));
        Check(LobbySettingKey.Gateways, "Gateways", s.Features.HasFlag(MapFeatures.Gateways));
        Check(LobbySettingKey.MagicItems, "Magic items", s.Features.HasFlag(MapFeatures.MagicItems));
        Check(LobbySettingKey.Cliffs, "Cliffs", s.Features.HasFlag(MapFeatures.Cliffs));
        Option(LobbySettingKey.GameType, "Game type", ["Standard", "Deathmatch"], s.GameType == GameType.Deathmatch ? 1 : 0);
        Option(LobbySettingKey.Victory, "Victory", ["Standard", "Destroy castles", "Time limit", "Score limit"], Array.IndexOf(Victories, s.Victory));
        Option(LobbySettingKey.StartingResources, "Starting resources", ["Default", "Low", "Normal", "High"], s.StartingResources + 1);
        Option(LobbySettingKey.PopulationLimit, "Population limit", ["Default", .. PopulationLimits.Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            s.PopulationLimit < 0 ? 0 : PopulationLimits.IndexOf(s.PopulationLimit) + 1);
        Check(LobbySettingKey.NoFog, "No fog", s.NoFog);
        Check(LobbySettingKey.Explore, "Explore", s.Explored);
        Check(LobbySettingKey.DayNight, "Day and night", s.DayNight);
        Check(LobbySettingKey.Weather, "Weather", s.Weather);
        Option(LobbySettingKey.Speed, "Speed", [.. Speeds.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture) + " %")], Speeds.IndexOf(s.SpeedPercent));
        Check(LobbySettingKey.AiAssist, "AI Assist", s.AiAssist);
        Check(LobbySettingKey.Cheats, "Cheats allowed", s.CheatsAllowed, enabled: !s.Ranked);
        Check(LobbySettingKey.Ranked, "Ranked (never cheats)", s.Ranked);
        return list;
    }
}
