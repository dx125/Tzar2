using Microsoft.CodeAnalysis;

namespace Tzar2.Analyzers;

/// <summary>Diagnostic descriptors of the determinism rules (TDD §8.8; IDs T2D001-T2D020 are reserved).</summary>
public static class Descriptors
{
    private const string Category = "Determinism";

    /// <summary>T2D001: floating point (REQ-SIM-04).</summary>
    public static readonly DiagnosticDescriptor FloatingPoint = new(
        id: "T2D001",
        title: "Floating point is not allowed in deterministic code",
        messageFormat: "Floating-point type '{0}' is not allowed in a [Tzar2Deterministic] assembly; use Tzar2.Core.Numerics.Fixed or integers",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "float, double, decimal, Half, MathF and System.Numerics vector types give results that can differ between platforms, compilers and JIT modes (REQ-SIM-04).");

    /// <summary>T2D002: non-deterministic random source (REQ-SIM-05/06).</summary>
    public static readonly DiagnosticDescriptor RandomSource = new(
        id: "T2D002",
        title: "Non-deterministic random source",
        messageFormat: "'{0}' is not allowed in a [Tzar2Deterministic] assembly; use the match's Tzar2.Core.Rng.RngStreams",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "System.Random, RandomNumberGenerator, Guid.NewGuid and Path.GetRandomFileName are not reproducible from the match seed; System.HashCode, string hash codes and RuntimeHelpers.GetHashCode are randomized per process (REQ-SIM-05, REQ-SIM-06).");

    /// <summary>T2D003: wall-clock time (REQ-SIM-05).</summary>
    public static readonly DiagnosticDescriptor WallClock = new(
        id: "T2D003",
        title: "Wall-clock time in deterministic code",
        messageFormat: "'{0}' reads wall-clock time and is not allowed in a [Tzar2Deterministic] assembly; use the sim tick",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "DateTime.Now/UtcNow/Today, DateTimeOffset.Now/UtcNow, Stopwatch, TimeProvider and Environment.TickCount differ between peers (REQ-SIM-05).");

    /// <summary>T2D004: iteration over an unordered collection (REQ-SIM-05).</summary>
    public static readonly DiagnosticDescriptor UnorderedIteration = new(
        id: "T2D004",
        title: "Iteration over an unordered collection",
        messageFormat: "foreach over '{0}' has no defined order; iterate a sorted or insertion-ordered collection instead",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Dictionary and HashSet enumeration order (including their Keys and Values, and through casts) depends on hash codes and the history of the collection (REQ-SIM-05).");

    /// <summary>T2D005: runtime code generation (REQ-ARCH-02).</summary>
    public static readonly DiagnosticDescriptor CodeGeneration = new(
        id: "T2D005",
        title: "Runtime code generation",
        messageFormat: "'{0}' generates code at runtime and is not allowed (NativeAOT/trimming, REQ-ARCH-02)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "System.Reflection.Emit and compiling expression trees do not work under NativeAOT (REQ-ARCH-02).");

    /// <summary>T2D006: file or network I/O (REQ-ARCH-01).</summary>
    public static readonly DiagnosticDescriptor InputOutput = new(
        id: "T2D006",
        title: "File or network I/O in simulation code",
        messageFormat: "'{0}' performs file or network I/O, which is not allowed in a [Tzar2NoIO] assembly (REQ-ARCH-01)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Sim, Scripting and AI consume only compiled Ruleset and MapData objects and never perform I/O (REQ-ARCH-01).");
}
