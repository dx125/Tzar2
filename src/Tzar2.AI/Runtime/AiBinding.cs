using System;
using System.Collections.Immutable;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Legacy.Syntax;

namespace Tzar2.AI.Runtime;

/// <summary>What a script name means for a ruleset.</summary>
public enum AiSymbolKind : byte
{
    /// <summary>Nothing the ruleset knows: counts are 0, goals are ignored (ADR 0050).</summary>
    Unknown = 0,

    /// <summary>An object class (<see cref="AiSymbol.Id"/> = class id).</summary>
    Class,

    /// <summary>A resource (<see cref="AiSymbol.Id"/> = resource type id).</summary>
    Resource,

    /// <summary><c>units</c>: the number of own units.</summary>
    Units,

    /// <summary>A technology (<c>te_*</c>; <see cref="AiSymbol.Id"/> = the ruleset's technology id, or -1 if it has none).</summary>
    Technology,

    /// <summary><c>convert</c>: tavern conversion of food (GAME-14).</summary>
    Convert,

    /// <summary><c>needs_locked</c>: save the resources of the unmet resource goals before it (ADR 0050).</summary>
    NeedsLocked,

    /// <summary><c>resign</c>: the AI resigns.</summary>
    Resign,
}

/// <summary>A bound name.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Id">Class or resource id, or -1.</param>
/// <param name="OwnType">The type the player's civilization builds or trains for a class goal, or -1 (then the goal is ignored).</param>
public readonly record struct AiSymbol(AiSymbolKind Kind, int Id, int OwnType);

/// <summary>
/// An <see cref="AiProgram"/> bound to a ruleset and a civilization: every name resolved once at load (no string work
/// while thinking), with warnings for names the ruleset does not know (ADR 0050). Immutable; shared by the AI players
/// with the same script and civilization.
/// </summary>
public sealed class AiBinding
{
    private AiBinding(AiProgram program, AiRulesetModel model, string civilization, ImmutableArray<AiSymbol> symbols, ImmutableArray<bool> civilizations, ImmutableArray<AiDiagnostic> diagnostics)
    {
        Program = program;
        Model = model;
        Civilization = civilization;
        Symbols = symbols;
        CivilizationMatches = civilizations;
        Diagnostics = diagnostics;
    }

    /// <summary>The program.</summary>
    public AiProgram Program { get; }

    /// <summary>The ruleset model.</summary>
    public AiRulesetModel Model { get; }

    /// <summary>The ruleset civilization key (<c>euro</c>, <c>asia</c>, <c>arab</c>).</summary>
    public string Civilization { get; }

    /// <summary>Bound symbols, by program symbol index.</summary>
    public ImmutableArray<AiSymbol> Symbols { get; }

    /// <summary>Whether each <see cref="AiProgram.Civilizations"/> entry names this civilization.</summary>
    public ImmutableArray<bool> CivilizationMatches { get; }

    /// <summary>Binding messages (unknown and misused names).</summary>
    public ImmutableArray<AiDiagnostic> Diagnostics { get; }

    /// <summary>Binds <paramref name="program"/> for players of <paramref name="civilization"/>.</summary>
    /// <param name="program">The program.</param>
    /// <param name="model">The ruleset model.</param>
    /// <param name="civilization">A ruleset civilization key or a script civilization name.</param>
    public static AiBinding Bind(AiProgram program, AiRulesetModel model, string civilization)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(civilization);
        string civ = AiRulesetModel.CivilizationKey(civilization);
        var diagnostics = ImmutableArray.CreateBuilder<AiDiagnostic>();
        var symbols = ImmutableArray.CreateBuilder<AiSymbol>(program.Symbols.Length);
        for (int s = 0; s < program.Symbols.Length; s++)
        {
            string name = program.Symbols[s];
            AiSymbol symbol = Resolve(model, civ, name);
            if (symbol.Kind == AiSymbolKind.Unknown)
            {
                diagnostics.Add(new AiDiagnostic(AiDiagnosticSeverity.Warning, AiDiagnosticCode.UnknownName, program.SymbolLocations[s],
                    $"'{name}' is not an object, resource or technology of ruleset '{model.Ruleset.Name}': it counts as 0 and goals for it are ignored."));
            }
            else if (symbol.Kind == AiSymbolKind.Class && symbol.OwnType < 0)
            {
                diagnostics.Add(new AiDiagnostic(AiDiagnosticSeverity.Warning, AiDiagnosticCode.UnknownName, program.SymbolLocations[s],
                    $"Civilization '{civ}' has no '{name}' in ruleset '{model.Ruleset.Name}': goals for it are ignored."));
            }

            symbols.Add(symbol);
        }

        ImmutableArray<AiSymbol> bound = symbols.MoveToImmutable();
        CheckUsage(program, bound, diagnostics);
        var civilizations = ImmutableArray.CreateBuilder<bool>(program.Civilizations.Length);
        foreach (string name in program.Civilizations)
        {
            civilizations.Add(string.Equals(AiRulesetModel.CivilizationKey(name), civ, StringComparison.Ordinal));
        }

        return new AiBinding(program, model, civ, bound, civilizations.MoveToImmutable(), diagnostics.ToImmutable());
    }

    private static AiSymbol Resolve(AiRulesetModel model, string civ, string name)
    {
        switch (name)
        {
            case "units": return new AiSymbol(AiSymbolKind.Units, -1, -1);
            case "convert": return new AiSymbol(AiSymbolKind.Convert, -1, -1);
            case "needs_locked": return new AiSymbol(AiSymbolKind.NeedsLocked, -1, -1);
            case "resign": return new AiSymbol(AiSymbolKind.Resign, -1, -1);
        }

        int resource = model.Ruleset.ResourceId(name);
        if (resource >= 0)
        {
            return new AiSymbol(AiSymbolKind.Resource, resource, -1);
        }

        if (name.StartsWith("te_", StringComparison.Ordinal))
        {
            return new AiSymbol(AiSymbolKind.Technology, model.Ruleset.TechnologyId(name), -1);
        }

        int classId = model.ClassId(name);
        if (classId < 0)
        {
            return new AiSymbol(AiSymbolKind.Unknown, -1, -1);
        }

        // The civilization's own type that someone can make; else any own type (it may exist from the map).
        int own = -1;
        foreach (ushort t in model.TypesOf(classId))
        {
            if (!model.IsOfCivilization(t, civ))
            {
                continue;
            }

            if (!model.ProducersOf(t).IsEmpty || !model.BuildersOf(t).IsEmpty)
            {
                own = t;
                break;
            }

            own = own < 0 ? t : own;
        }

        return new AiSymbol(AiSymbolKind.Class, classId, own);
    }

    // Names whose kind makes no sense where they are used: a warning, and the documented fallback (ADR 0050).
    private static void CheckUsage(AiProgram program, ImmutableArray<AiSymbol> symbols, ImmutableArray<AiDiagnostic>.Builder diagnostics)
    {
        var reported = new bool[symbols.Length];
        foreach (AiConditionCode c in program.Conditions)
        {
            if (c.Op is AiConditionOp.Count or AiConditionOp.EnemyCount && c.Operand >= 0 && !reported[c.Operand]
                && symbols[c.Operand].Kind is AiSymbolKind.Convert or AiSymbolKind.NeedsLocked or AiSymbolKind.Resign)
            {
                reported[c.Operand] = true;
                diagnostics.Add(new AiDiagnostic(AiDiagnosticSeverity.Warning, AiDiagnosticCode.MisusedName, program.SymbolLocations[c.Operand],
                    $"'{program.Symbols[c.Operand]}' is a goal, not something to count; the condition is false."));
            }
        }

        foreach (AiActionCode a in program.Actions)
        {
            if (a.Op == AiActionOp.Goal && !reported[a.Operand] && symbols[a.Operand].Kind == AiSymbolKind.Units)
            {
                reported[a.Operand] = true;
                diagnostics.Add(new AiDiagnostic(AiDiagnosticSeverity.Warning, AiDiagnosticCode.MisusedName, program.SymbolLocations[a.Operand],
                    "'units' cannot be a goal (say which units); the goal is ignored."));
            }
        }
    }
}
