namespace Tzar2.Tools.Cli;

/// <summary>Process exit codes of <c>t2</c>.</summary>
internal static class ExitCodes
{
    public const int Success = 0;

    /// <summary>The command ran and found a mismatch (e.g. <c>verify-original</c> found modified or missing files).</summary>
    public const int Mismatch = 1;

    /// <summary>Invalid arguments, or input that cannot be read.</summary>
    public const int UsageError = 2;

    /// <summary>The command exists in the TDD but is not implemented yet.</summary>
    public const int NotImplemented = 3;
}
