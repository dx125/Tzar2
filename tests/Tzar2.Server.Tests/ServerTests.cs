using System;
using System.IO;
using Xunit;

namespace Tzar2.Server.Tests;

public class ServerTests
{
    [Fact]
    public void Placeholder_reports_not_implemented()
    {
        var output = new StringWriter();
        Assert.Equal(Program.NotImplemented, Program.Run(output));
        Assert.Contains("not implemented (M6)", output.ToString(), StringComparison.Ordinal);
    }
}
