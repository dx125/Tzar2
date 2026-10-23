using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tzar2.Analyzers;

/// <summary>
/// T2C001: an interpolated string formats a culture-sensitive value with the machine's culture. CA1305 covers
/// <c>ToString</c>, <c>string.Format</c> and the other calls that take an <see cref="System.IFormatProvider"/>, but not
/// interpolation: <c>$"{seconds:0.##} s"</c> prints "37,5 s" under a German or a C.UTF-8 locale. Reported for holes of
/// type <c>float</c>, <c>double</c>, <c>decimal</c>, <c>DateTime</c> and <c>DateTimeOffset</c>, and for any numeric hole
/// with a format string (group separators), when the string is built without a format provider: converted straight to
/// a string, or through a handler (<c>StringBuilder.Append</c>, <c>TextWriter.Write</c>) that takes none. Also string
/// concatenation with such a value (<c>"x" + 1.5</c> calls its <c>ToString()</c>). Use
/// <c>string.Create(CultureInfo.InvariantCulture, $"...")</c>, <c>FormattableString.Invariant</c> or the provider
/// overloads.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CultureAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Descriptors.CultureFormatting);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.InterpolatedString);
        context.RegisterOperationAction(AnalyzeConcatenation, OperationKind.Binary);
    }

    // "x = " + 1.5: string concatenation calls the value's ToString() with the current culture.
    private static void AnalyzeConcatenation(OperationAnalysisContext context)
    {
        var binary = (IBinaryOperation)context.Operation;
        if (binary.OperatorKind != BinaryOperatorKind.Add || binary.Type?.SpecialType != SpecialType.System_String)
        {
            return;
        }

        foreach (IOperation operand in new[] { binary.LeftOperand, binary.RightOperand })
        {
            IOperation value = operand is IConversionOperation { IsImplicit: true } boxed ? boxed.Operand : operand;
            if (value.Type is { } type && IsCultureSensitiveType(type))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.CultureFormatting, value.Syntax.GetLocation(), type.ToDisplayString()));
            }
        }
    }

    private static bool IsCultureSensitiveType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        return type.SpecialType is SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal or SpecialType.System_DateTime
            || type.ToDisplayString() == "System.DateTimeOffset";
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var text = (IInterpolatedStringOperation)context.Operation;
        if (HasProvider(text))
        {
            return;
        }

        // One report per string, at the string: the fix wraps the whole string.
        foreach (IInterpolatedStringContentOperation part in text.Parts)
        {
            (IOperation? value, IOperation? format) = part switch
            {
                IInterpolationOperation hole => (hole.Expression, hole.FormatString),
                // Handler-built strings: the hole is an AppendFormatted(value[, alignment][, format]) call.
                IInterpolatedStringAppendOperation { AppendCall: IInvocationOperation call } when call.Arguments.Length > 0
                    => (Unwrap(call.Arguments[0].Value), call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "format")?.Value),
                _ => (null, null),
            };
            if (value is not null && IsCultureSensitive(value, format))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.CultureFormatting, text.Syntax.GetLocation(), value.Type?.ToDisplayString() ?? "?"));
                return;
            }
        }
    }

    // The string is formatted with a provider: FormattableString (Invariant, FormattableStringFactory) or a handler built
    // with an IFormatProvider argument (string.Create(provider, ...), StringBuilder.Append(provider, ...)).
    private static bool HasProvider(IInterpolatedStringOperation text)
    {
        IOperation? parent = text.Parent;
        while (parent is IParenthesizedOperation)
        {
            parent = parent.Parent;
        }

        if (parent is IConversionOperation conversion && conversion.Type is { } target
            && (target.ToDisplayString() == "System.FormattableString" || target.ToDisplayString() == "System.IFormattable"))
        {
            return true;
        }

        if (parent is IInterpolatedStringHandlerCreationOperation creation)
        {
            return creation.HandlerCreation is IObjectCreationOperation { Constructor: { } ctor }
                && ctor.Parameters.Any(p => p.Type is { Name: "IFormatProvider", ContainingNamespace.Name: "System" });
        }

        return false;
    }

    private static IOperation Unwrap(IOperation value) => value is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : value;

    private static bool IsCultureSensitive(IOperation value, IOperation? format)
    {
        ITypeSymbol? type = value.Type;
        if (type is null)
        {
            return false;
        }

        if (IsCultureSensitiveType(type))
        {
            return true;
        }

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                return format is { ConstantValue.HasValue: true, ConstantValue.Value: string } && !IsCultureFree(format);
            default:
                return false;
        }
    }

    // Integer formats that print no separator or sign symbol of the culture: hexadecimal, zero padding (D, 0000).
    private static bool IsCultureFree(IOperation format)
        => format.ConstantValue is { HasValue: true, Value: string f }
            && f.Length > 0
            && (f[0] is 'X' or 'x' or 'D' or 'd' || f.All(c => c == '0'));
}
