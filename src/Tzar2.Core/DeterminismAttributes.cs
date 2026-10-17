using System;

namespace Tzar2.Core;

/// <summary>
/// Marks an assembly as determinism-critical (TDD §8.8). Tzar2.Analyzers then reports floating point,
/// wall-clock time, non-deterministic random sources, unordered hash-collection iteration and runtime
/// code generation as errors. Applied automatically by the <c>Tzar2Deterministic</c> MSBuild property.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class Tzar2DeterministicAttribute : Attribute
{
}

/// <summary>
/// Marks an assembly that must not perform file or network I/O (REQ-ARCH-01: Sim, Scripting, AI).
/// Applied automatically by the <c>Tzar2NoIO</c> MSBuild property.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class Tzar2NoIOAttribute : Attribute
{
}
