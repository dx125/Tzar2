using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Formats.Text;

namespace Tzar2.Import.Maps;

/// <summary>Options of the legacy converter (REQ-EDT-04/07/10).</summary>
public sealed class LegacyConversionOptions
{
    /// <summary>The target ruleset: type keys and terrain ids resolve against it. Null forms keys by name.</summary>
    public Ruleset? Ruleset { get; init; }

    /// <summary>User type mapping (legacy class or <c>civ.class</c> → type key).</summary>
    public IReadOnlyDictionary<string, string>? Mapping { get; init; }

    /// <summary>Forces a code page instead of detecting it.</summary>
    public CodePage? CodePage { get; init; }

    /// <summary>Convert BMP user images to PNG (REQ-EDT-09). On by default.</summary>
    public bool ConvertImages { get; init; } = true;

    /// <summary>
    /// ⚠ UNVERIFIED candidate for UNK-81 (ADR 0220), off by default: a vertical run of cliff (piece shape 6) blocks only
    /// its right-hand column, as if the rock of a 2 × 2 piece filled one half of it. Every other cliff tile blocks, as
    /// before. Only for measuring the campaign map <c>rajba</c>, whose mage plateau no unit can reach while every cliff
    /// tile blocks; an in-game check of the original settles it.
    /// </summary>
    public bool Unk81HalfCliffs { get; init; }
}

/// <summary>A user resource of a converted map (image, sound, music, AI script).</summary>
/// <param name="Path">Package path under <c>assets/</c>, normalised (e.g. <c>user/hero.png</c>).</param>
/// <param name="SourcePath">Path inside the legacy container.</param>
/// <param name="Data">Content (converted to PNG for BMP images).</param>
public sealed record UserResource(string Path, string SourcePath, byte[] Data);

/// <summary>A decoration (ground patch/object, ruin, animation): drawn, not simulated.</summary>
/// <param name="ClassName">Legacy class.</param>
/// <param name="Image">Image/draw definition name.</param>
/// <param name="TileX">Tile column.</param>
/// <param name="TileY">Tile row.</param>
/// <param name="PixelX">Pixel x (or −1).</param>
/// <param name="PixelY">Pixel y (or −1).</param>
public sealed record Decoration(string ClassName, string Image, int TileX, int TileY, int PixelX, int PixelY);

/// <summary>An object whose class has no entity type in the target ruleset (REQ-EDT-10): kept, flagged, not placed.</summary>
/// <param name="LegacyIndex">Index in the legacy object list.</param>
/// <param name="ClassName">Legacy class.</param>
/// <param name="Race">Legacy race.</param>
/// <param name="Owner">Legacy owner.</param>
/// <param name="TileX">Tile column.</param>
/// <param name="TileY">Tile row.</param>
/// <param name="Id">Legacy object id.</param>
public sealed record Placeholder(int LegacyIndex, string ClassName, int Race, int Owner, int TileX, int TileY, int Id);

/// <summary>A translatable string of the map (exported as <c>strings/&lt;lang&gt;.po</c>).</summary>
/// <param name="Context">Stable context, e.g. <c>action/Mission briefing/text</c>.</param>
/// <param name="Text">The text.</param>
public sealed record MapString(string Context, string Text);

/// <summary>The result of converting one legacy map.</summary>
public sealed class MapConversion
{
    /// <summary>The map's internal legacy name (also the campaign's <c>maps.ini</c> name).</summary>
    public required string MapName { get; init; }

    /// <summary>Terrain and placed objects, or null when the map could not be converted.</summary>
    public MapData? Map { get; init; }

    /// <summary>The scenario, or null when the map could not be converted.</summary>
    public ScenarioData? Scenario { get; init; }

    /// <summary>The report.</summary>
    public required ConversionReport Report { get; init; }

    /// <summary>User resources.</summary>
    public IReadOnlyList<UserResource> Resources { get; init; } = [];

    /// <summary>Decorations.</summary>
    public IReadOnlyList<Decoration> Decorations { get; init; } = [];

    /// <summary>Placeholders for unresolved object types.</summary>
    public IReadOnlyList<Placeholder> Placeholders { get; init; } = [];

    /// <summary>Translatable strings.</summary>
    public IReadOnlyList<MapString> Strings { get; init; } = [];

    /// <summary>Next-map names used by Load Map / Player Win / Player Lose actions: (action index, kind, name).</summary>
    public IReadOnlyList<(int ActionIndex, CampaignEdgeKind Kind, string MapName)> Transitions { get; init; } = [];

    /// <summary>Legacy object index (the <c>object #N</c> of report locations) → index in <see cref="MapData.Objects"/>, for click-to-locate.</summary>
    public IReadOnlyDictionary<int, int> LegacyObjectIndices { get; init; } = new Dictionary<int, int>();

    /// <summary>Whether conversion succeeded.</summary>
    public bool Succeeded => Map is not null && Scenario is not null && Report.Succeeded;
}

/// <summary>The result of converting a legacy campaign.</summary>
public sealed class CampaignConversion
{
    /// <summary>The campaign graph, or null when the container could not be read.</summary>
    public CampaignData? Campaign { get; init; }

    /// <summary>The maps, in the order of <see cref="CampaignData.Maps"/>.</summary>
    public IReadOnlyList<MapConversion> Maps { get; init; } = [];

    /// <summary>The report (the maps' reports are its children).</summary>
    public required ConversionReport Report { get; init; }

    /// <summary>Whether everything converted.</summary>
    public bool Succeeded => Campaign is not null && Report.Succeeded;
}
