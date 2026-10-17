using System;
using System.IO;

namespace Tzar2.Server;

internal static class Program
{
    /// <summary>Exit code for a service that is not implemented yet.</summary>
    internal const int NotImplemented = 3;

    private static int Main() => Run(Console.Error);

    internal static int Run(TextWriter output)
    {
        output.WriteLine("Tzar2 relay/lobby server: not implemented (M6)");
        return NotImplemented;
    }
}
