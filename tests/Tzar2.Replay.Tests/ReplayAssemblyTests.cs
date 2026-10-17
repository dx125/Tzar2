using System.Reflection;
using Tzar2.Core;
using Xunit;

namespace Tzar2.Replay.Tests;

public class ReplayAssemblyTests
{
    [Fact]
    public void Assembly_markers_match_its_role()
    {
        Assembly assembly = typeof(ReplayAssembly).Assembly;
        Assert.Equal(ReplayAssembly.Name, assembly.GetName().Name);
        Assert.NotNull(assembly.GetCustomAttribute<Tzar2DeterministicAttribute>());
        Assert.Null(assembly.GetCustomAttribute<Tzar2NoIOAttribute>());
    }
}
