using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tzar2.Analyzers;

/// <summary>
/// Enforces the determinism rules of TDD §8.8 in assemblies marked <c>[assembly: Tzar2.Core.Tzar2Deterministic]</c>
/// (T2D001-T2D005) and the no-I/O rule of REQ-ARCH-01 in assemblies marked <c>[assembly: Tzar2.Core.Tzar2NoIO]</c> (T2D006).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeterminismAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Full name of the determinism opt-in attribute.</summary>
    public const string DeterministicAttributeName = "Tzar2.Core.Tzar2DeterministicAttribute";

    /// <summary>Full name of the no-I/O opt-in attribute.</summary>
    public const string NoIOAttributeName = "Tzar2.Core.Tzar2NoIOAttribute";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.FloatingPoint,
        Descriptors.RandomSource,
        Descriptors.WallClock,
        Descriptors.UnorderedIteration,
        Descriptors.CodeGeneration,
        Descriptors.InputOutput);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            bool deterministic = HasAttribute(start.Compilation.Assembly, DeterministicAttributeName);
            bool noIO = HasAttribute(start.Compilation.Assembly, NoIOAttributeName);
            if (!deterministic && !noIO)
            {
                return;
            }

            var rules = new Rules(start.Compilation, deterministic, noIO);
            start.RegisterSymbolAction(rules.AnalyzeSymbol, SymbolKind.Field, SymbolKind.Property, SymbolKind.Method, SymbolKind.Event);
            start.RegisterOperationAction(
                rules.AnalyzeExpression,
                OperationKind.Literal,
                OperationKind.Conversion,
                OperationKind.Invocation,
                OperationKind.ObjectCreation,
                OperationKind.ArrayCreation,
                OperationKind.FieldReference,
                OperationKind.PropertyReference,
                OperationKind.EventReference,
                OperationKind.MethodReference,
                OperationKind.BinaryOperator,
                OperationKind.UnaryOperator,
                OperationKind.TypeOf);
            start.RegisterOperationAction(rules.AnalyzeDeclarator, OperationKind.VariableDeclarator);
            if (deterministic)
            {
                start.RegisterOperationAction(rules.AnalyzeLoop, OperationKind.Loop);
            }
        });
    }

    private static bool HasAttribute(IAssemblySymbol assembly, string fullName)
        => assembly.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == fullName);

    private sealed class Rules
    {
        private readonly bool _deterministic;
        private readonly bool _noIO;

        private readonly ImmutableHashSet<INamedTypeSymbol> _floatingTypes;
        private readonly ImmutableHashSet<INamedTypeSymbol> _randomTypes;
        private readonly ImmutableHashSet<INamedTypeSymbol> _clockTypes;
        private readonly ImmutableHashSet<INamedTypeSymbol> _unorderedCollections;
        private readonly ImmutableHashSet<INamedTypeSymbol> _fileSystemTypes;
        private readonly ImmutableHashSet<INamedTypeSymbol> _fileCapableReaders;
        private readonly INamedTypeSymbol? _guid;
        private readonly INamedTypeSymbol? _dateTime;
        private readonly INamedTypeSymbol? _dateTimeOffset;
        private readonly INamedTypeSymbol? _environment;
        private readonly INamedTypeSymbol? _lambdaExpression;
        private readonly INamedTypeSymbol? _genericExpression;
        private readonly INamedTypeSymbol? _stream;

        public Rules(Compilation compilation, bool deterministic, bool noIO)
        {
            _deterministic = deterministic;
            _noIO = noIO;
            _floatingTypes = Types(
                compilation,
                "System.Single",
                "System.Double",
                "System.Decimal",
                "System.Half",
                "System.MathF",
                "System.Runtime.InteropServices.NFloat",
                "System.Numerics.Vector2",
                "System.Numerics.Vector3",
                "System.Numerics.Vector4",
                "System.Numerics.Matrix3x2",
                "System.Numerics.Matrix4x4",
                "System.Numerics.Quaternion",
                "System.Numerics.Plane",
                "System.Numerics.Complex");
            _randomTypes = Types(compilation, "System.Random", "System.Security.Cryptography.RandomNumberGenerator");
            _clockTypes = Types(compilation, "System.Diagnostics.Stopwatch", "System.TimeProvider");
            _unorderedCollections = Types(
                compilation,
                "System.Collections.Generic.Dictionary`2",
                "System.Collections.Generic.Dictionary`2+KeyCollection",
                "System.Collections.Generic.Dictionary`2+ValueCollection",
                "System.Collections.Generic.HashSet`1",
                "System.Collections.Generic.IDictionary`2",
                "System.Collections.Generic.IReadOnlyDictionary`2",
                "System.Collections.Generic.ISet`1",
                "System.Collections.Generic.IReadOnlySet`1",
                "System.Collections.Concurrent.ConcurrentDictionary`2",
                "System.Collections.Concurrent.ConcurrentBag`1",
                "System.Collections.Immutable.ImmutableDictionary`2",
                "System.Collections.Immutable.ImmutableHashSet`1",
                "System.Collections.Frozen.FrozenDictionary`2",
                "System.Collections.Frozen.FrozenSet`1",
                "System.Collections.Hashtable");
            _fileSystemTypes = Types(
                compilation,
                "System.IO.File",
                "System.IO.Directory",
                "System.IO.FileInfo",
                "System.IO.DirectoryInfo",
                "System.IO.FileSystemInfo",
                "System.IO.DriveInfo",
                "System.IO.FileSystemWatcher",
                "System.IO.RandomAccess");
            _fileCapableReaders = Types(compilation, "System.IO.StreamReader", "System.IO.StreamWriter", "System.IO.BinaryReader", "System.IO.BinaryWriter");
            _guid = compilation.GetTypeByMetadataName("System.Guid");
            _dateTime = compilation.GetTypeByMetadataName("System.DateTime");
            _dateTimeOffset = compilation.GetTypeByMetadataName("System.DateTimeOffset");
            _environment = compilation.GetTypeByMetadataName("System.Environment");
            _lambdaExpression = compilation.GetTypeByMetadataName("System.Linq.Expressions.LambdaExpression");
            _genericExpression = compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression`1");
            _stream = compilation.GetTypeByMetadataName("System.IO.Stream");
        }

        public void AnalyzeSymbol(SymbolAnalysisContext context)
        {
            ISymbol symbol = context.Symbol;
            if (symbol.IsImplicitlyDeclared || symbol.Locations.Length == 0 || !symbol.Locations[0].IsInSource)
            {
                return;
            }

            Location location = symbol.Locations[0];
            switch (symbol)
            {
                case IFieldSymbol field:
                    CheckDeclaredType(context.ReportDiagnostic, field.Type, location);
                    break;
                case IPropertySymbol property:
                    CheckDeclaredType(context.ReportDiagnostic, property.Type, location);
                    foreach (IParameterSymbol parameter in property.Parameters)
                    {
                        CheckDeclaredType(context.ReportDiagnostic, parameter.Type, ParameterLocation(parameter, location));
                    }

                    break;
                case IEventSymbol @event:
                    CheckDeclaredType(context.ReportDiagnostic, @event.Type, location);
                    break;
                case IMethodSymbol method when method.AssociatedSymbol is null:
                    CheckDeclaredType(context.ReportDiagnostic, method.ReturnType, location);
                    foreach (IParameterSymbol parameter in method.Parameters)
                    {
                        CheckDeclaredType(context.ReportDiagnostic, parameter.Type, ParameterLocation(parameter, location));
                    }

                    break;
            }
        }

        public void AnalyzeDeclarator(OperationAnalysisContext context)
        {
            var declarator = (IVariableDeclaratorOperation)context.Operation;
            CheckDeclaredType(context.ReportDiagnostic, declarator.Symbol.Type, declarator.Syntax.GetLocation());
        }

        public void AnalyzeExpression(OperationAnalysisContext context)
        {
            IOperation operation = context.Operation;
            Location location = operation.Syntax.GetLocation();

            if (_deterministic)
            {
                // Report the outermost floating-point expression only, to avoid one error per sub-expression.
                if (IsFloating(operation.Type) && !IsFloating(NearestTypedAncestor(operation)?.Type))
                {
                    Report(context.ReportDiagnostic, Descriptors.FloatingPoint, location, operation.Type!);
                }

                if (operation is ITypeOfOperation typeOf && IsFloating(typeOf.TypeOperand))
                {
                    Report(context.ReportDiagnostic, Descriptors.FloatingPoint, location, typeOf.TypeOperand);
                }
            }

            switch (operation)
            {
                case IObjectCreationOperation creation when creation.Type is not null:
                    CheckUsedType(context.ReportDiagnostic, creation.Type, location);
                    if (_noIO && (DerivesFrom(creation.Type, _stream) || Contains(_fileCapableReaders, creation.Type)))
                    {
                        Report(context.ReportDiagnostic, Descriptors.InputOutput, location, creation.Type);
                    }

                    break;
                case IInvocationOperation invocation:
                    CheckMember(context.ReportDiagnostic, invocation.TargetMethod, location);
                    break;
                case IMemberReferenceOperation reference:
                    CheckMember(context.ReportDiagnostic, reference.Member, location);
                    break;
            }
        }

        public void AnalyzeLoop(OperationAnalysisContext context)
        {
            if (context.Operation is not IForEachLoopOperation loop)
            {
                return;
            }

            IOperation collection = loop.Collection;
            while (collection is IConversionOperation { IsImplicit: true } conversion)
            {
                collection = conversion.Operand;
            }

            if (collection.Type is ITypeSymbol type && IsUnordered(type))
            {
                Report(context.ReportDiagnostic, Descriptors.UnorderedIteration, loop.Collection.Syntax.GetLocation(), type);
            }
        }

        private void CheckDeclaredType(System.Action<Diagnostic> report, ITypeSymbol type, Location location)
        {
            if (_deterministic && IsFloating(type))
            {
                Report(report, Descriptors.FloatingPoint, location, type);
            }

            CheckUsedType(report, type, location);
        }

        // Types whose mere use is banned (declared, constructed or accessed through a member).
        private void CheckUsedType(System.Action<Diagnostic> report, ITypeSymbol type, Location location)
        {
            foreach (ITypeSymbol part in Flatten(type))
            {
                if (_deterministic)
                {
                    if (Contains(_randomTypes, part))
                    {
                        Report(report, Descriptors.RandomSource, location, part);
                    }
                    else if (Contains(_clockTypes, part))
                    {
                        Report(report, Descriptors.WallClock, location, part);
                    }
                    else if (IsInNamespace(part, "System.Reflection.Emit"))
                    {
                        Report(report, Descriptors.CodeGeneration, location, part);
                    }
                }

                if (_noIO && (Contains(_fileSystemTypes, part) || IsInNamespace(part, "System.Net")))
                {
                    Report(report, Descriptors.InputOutput, location, part);
                }
            }
        }

        private void CheckMember(System.Action<Diagnostic> report, ISymbol member, Location location)
        {
            INamedTypeSymbol? containingType = member.ContainingType;
            if (containingType is null)
            {
                return;
            }

            // Static members are checked through their containing type (e.g. Stopwatch.StartNew, File.ReadAllText).
            if (member.IsStatic)
            {
                CheckUsedType(report, containingType, location);
            }

            if (!_deterministic)
            {
                return;
            }

            string name = member.Name;
            if (Same(containingType, _dateTime) && name is "Now" or "UtcNow" or "Today")
            {
                Report(report, Descriptors.WallClock, location, member);
            }
            else if (Same(containingType, _dateTimeOffset) && name is "Now" or "UtcNow")
            {
                Report(report, Descriptors.WallClock, location, member);
            }
            else if (Same(containingType, _environment) && name is "TickCount" or "TickCount64")
            {
                Report(report, Descriptors.WallClock, location, member);
            }
            else if (Same(containingType, _guid) && name == "NewGuid")
            {
                Report(report, Descriptors.RandomSource, location, member);
            }
            else if (name == "Compile" && (DerivesFrom(containingType, _lambdaExpression) || Same(containingType.OriginalDefinition, _genericExpression)))
            {
                Report(report, Descriptors.CodeGeneration, location, member);
            }
        }

        private bool IsFloating(ITypeSymbol? type)
        {
            if (type is null)
            {
                return false;
            }

            return Flatten(type).Any(part => Contains(_floatingTypes, part));
        }

        private bool IsUnordered(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named && Contains(_unorderedCollections, named.OriginalDefinition))
            {
                return true;
            }

            // A custom type deriving from Dictionary/HashSet is just as unordered.
            for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (Contains(_unorderedCollections, current.OriginalDefinition))
                {
                    return true;
                }
            }

            return false;
        }

        private static IOperation? NearestTypedAncestor(IOperation operation)
        {
            IOperation? current = operation.Parent;
            while (current is not null && current.Type is null)
            {
                current = current.Parent;
            }

            return current;
        }

        private static IEnumerable<ITypeSymbol> Flatten(ITypeSymbol type)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    foreach (ITypeSymbol part in Flatten(array.ElementType))
                    {
                        yield return part;
                    }

                    yield break;
                case IPointerTypeSymbol pointer:
                    foreach (ITypeSymbol part in Flatten(pointer.PointedAtType))
                    {
                        yield return part;
                    }

                    yield break;
                case INamedTypeSymbol named:
                    yield return named.OriginalDefinition;
                    foreach (ITypeSymbol argument in named.TypeArguments)
                    {
                        foreach (ITypeSymbol part in Flatten(argument))
                        {
                            yield return part;
                        }
                    }

                    yield break;
                default:
                    yield return type;
                    yield break;
            }
        }

        private static bool DerivesFrom(ITypeSymbol type, INamedTypeSymbol? baseType)
        {
            if (baseType is null)
            {
                return false;
            }

            for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInNamespace(ITypeSymbol type, string ns)
        {
            string? name = type.ContainingNamespace?.ToDisplayString();
            return name is not null && (name == ns || name.StartsWith(ns + ".", System.StringComparison.Ordinal));
        }

        private static bool Contains(ImmutableHashSet<INamedTypeSymbol> set, ITypeSymbol type)
            => type is INamedTypeSymbol named && set.Contains(named.OriginalDefinition);

        private static bool Same(ITypeSymbol? a, ITypeSymbol? b) => b is not null && SymbolEqualityComparer.Default.Equals(a, b);

        private static Location ParameterLocation(IParameterSymbol parameter, Location fallback)
            => parameter.Locations.Length > 0 && parameter.Locations[0].IsInSource ? parameter.Locations[0] : fallback;

        private static void Report(System.Action<Diagnostic> report, DiagnosticDescriptor descriptor, Location location, ISymbol subject)
            => report(Diagnostic.Create(descriptor, location, subject.ToDisplayString()));

        private static ImmutableHashSet<INamedTypeSymbol> Types(Compilation compilation, params string[] metadataNames)
        {
            ImmutableHashSet<INamedTypeSymbol>.Builder builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (string metadataName in metadataNames)
            {
                INamedTypeSymbol? type = compilation.GetTypeByMetadataName(metadataName);
                if (type is not null)
                {
                    builder.Add(type);
                }
            }

            return builder.ToImmutable();
        }
    }
}
