using System;
using System.IO;
using Tzar2.Tools.Cli.Commands;
using Xunit;

namespace Tzar2.Tools.Cli.Tests;

public class TickProfilerTests
{
    [Fact]
    public void Sections_nest_and_too_deep_sections_do_not_unbalance_the_stack()
    {
        var profiler = new TickProfiler(ticks: 2);
        for (int t = 0; t < 2; t++)
        {
            profiler.NextTick();
            profiler.Begin("outer");
            for (int d = 0; d < 20; d++)
            {
                profiler.Begin("deep"); // 16 levels are recorded, the rest are ignored
            }

            for (int d = 0; d < 20; d++)
            {
                profiler.End();
            }

            profiler.Begin("inner");
            profiler.End();
            profiler.End();
            profiler.Begin("second");
            profiler.End();
        }

        using var output = new StringWriter();
        profiler.Print(output, [1.0, 1.0]);
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // "inner" is a child of "outer" (indented once more) and "second" is a top-level section again.
        Assert.Contains(lines, l => l.StartsWith("section ms     inner ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("section ms   second ", StringComparison.Ordinal));
    }
}
