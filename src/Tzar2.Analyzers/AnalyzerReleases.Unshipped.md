; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
T2D001 | Determinism | Error | DeterminismAnalyzer, floating point
T2D002 | Determinism | Error | DeterminismAnalyzer, non-deterministic random source
T2D003 | Determinism | Error | DeterminismAnalyzer, wall-clock time
T2D004 | Determinism | Error | DeterminismAnalyzer, unordered collection iteration
T2D005 | Determinism | Error | DeterminismAnalyzer, runtime code generation
T2D006 | Determinism | Error | DeterminismAnalyzer, file or network I/O
T2C001 | Globalization | Error | CultureAnalyzer, interpolated string formatted with the current culture
