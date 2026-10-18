using System;
using System.Collections.Immutable;
using System.Globalization;

namespace Tzar2.Content.Editing;

/// <summary>How bad a validation finding is (TDD §18.3 validation panel).</summary>
public enum ValidationSeverity : byte
{
    /// <summary>Worth knowing.</summary>
    Info,

    /// <summary>Probably a mistake; the map plays.</summary>
    Warning,

    /// <summary>A mistake; the map still saves and stays editable.</summary>
    Error,

    /// <summary>The map cannot be played (or built) until this is fixed.</summary>
    Blocking,
}

/// <summary>What a finding points at (click to locate).</summary>
public enum LocationKind : byte
{
    /// <summary>The map as a whole.</summary>
    Map,

    /// <summary>A tile (<see cref="EditorLocation.X"/>, <see cref="EditorLocation.Y"/>).</summary>
    Tile,

    /// <summary>An object (<see cref="EditorLocation.Index"/> = object id).</summary>
    Object,

    /// <summary>A kingdom (<see cref="EditorLocation.Index"/> = number 1–8).</summary>
    Kingdom,

    /// <summary>An event (index).</summary>
    Event,

    /// <summary>An action (index).</summary>
    Action,

    /// <summary>A rule (index).</summary>
    Rule,

    /// <summary>A conversation (index).</summary>
    Conversation,

    /// <summary>A phrase (<see cref="EditorLocation.Index"/> = conversation, <see cref="EditorLocation.SubIndex"/> = phrase).</summary>
    Phrase,

    /// <summary>A custom sound (index).</summary>
    Sound,

    /// <summary>A user resource (<see cref="EditorLocation.Name"/> = path under <c>assets/</c>).</summary>
    Asset,

    /// <summary>A variable (index of the declared variables, or −1 with <see cref="EditorLocation.Name"/>).</summary>
    Variable,

    /// <summary>A campaign map (<see cref="EditorLocation.Name"/> = map id).</summary>
    CampaignMap,

    /// <summary>The map properties.</summary>
    Properties,
}

/// <summary>Where a finding is.</summary>
/// <param name="Kind">What it points at.</param>
/// <param name="Index">Index or id (see <see cref="LocationKind"/>).</param>
/// <param name="SubIndex">Secondary index, or −1.</param>
/// <param name="X">Tile column, or −1.</param>
/// <param name="Y">Tile row, or −1.</param>
/// <param name="Name">A name (asset path, campaign map id), or null.</param>
public readonly record struct EditorLocation(LocationKind Kind, int Index = -1, int SubIndex = -1, int X = -1, int Y = -1, string? Name = null)
{
    /// <summary>The map as a whole.</summary>
    public static EditorLocation Map => new(LocationKind.Map);

    /// <summary>A tile.</summary>
    public static EditorLocation Tile(int x, int y) => new(LocationKind.Tile, X: x, Y: y);

    /// <summary>An object at its tile.</summary>
    public static EditorLocation Object(EditorObject o) => new(LocationKind.Object, o.Id, X: o.TileX, Y: o.TileY);

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        LocationKind.Tile => string.Create(CultureInfo.InvariantCulture, $"tile ({X}, {Y})"),
        LocationKind.Object => string.Create(CultureInfo.InvariantCulture, $"object #{Index} at ({X}, {Y})"),
        LocationKind.Phrase => string.Create(CultureInfo.InvariantCulture, $"conversation {Index} phrase {SubIndex}"),
        LocationKind.Asset or LocationKind.CampaignMap => $"{Kind.ToString().ToLowerInvariant()} '{Name}'",
        LocationKind.Map or LocationKind.Properties => Kind.ToString().ToLowerInvariant(),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Kind.ToString().ToLowerInvariant()} {Index}"),
    };
}

/// <summary>One finding of the validator.</summary>
/// <param name="Severity">Severity.</param>
/// <param name="Code">Stable code, e.g. <c>object.type</c>.</param>
/// <param name="Message">Human-readable text.</param>
/// <param name="Location">Where.</param>
public sealed record ValidationIssue(ValidationSeverity Severity, string Code, string Message, EditorLocation Location);

/// <summary>The findings of one validation pass, in a fixed order.</summary>
/// <param name="Issues">Findings.</param>
public sealed record ValidationResult(ImmutableArray<ValidationIssue> Issues)
{
    /// <summary>Whether any finding blocks playing.</summary>
    public bool BlocksPlay
    {
        get
        {
            foreach (ValidationIssue i in Issues)
            {
                if (i.Severity == ValidationSeverity.Blocking)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Number of findings of <paramref name="severity"/>.</summary>
    public int Count(ValidationSeverity severity)
    {
        int n = 0;
        foreach (ValidationIssue i in Issues)
        {
            n += i.Severity == severity ? 1 : 0;
        }

        return n;
    }

    /// <summary>Findings with a code that starts with <paramref name="prefix"/>.</summary>
    public ImmutableArray<ValidationIssue> WithCode(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var b = ImmutableArray.CreateBuilder<ValidationIssue>();
        foreach (ValidationIssue i in Issues)
        {
            if (i.Code.StartsWith(prefix, StringComparison.Ordinal))
            {
                b.Add(i);
            }
        }

        return b.ToImmutable();
    }
}
