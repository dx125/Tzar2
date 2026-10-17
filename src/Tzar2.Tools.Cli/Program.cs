using System;

namespace Tzar2.Tools.Cli;

internal static class Program
{
    private static int Main(string[] args) => CliApp.Run(args, Console.Out, Console.Error);
}
