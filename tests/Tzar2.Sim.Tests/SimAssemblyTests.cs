using System.Reflection;
using Tzar2.Core;
using Xunit;

namespace Tzar2.Sim.Tests;

public class SimAssemblyTests
{
    [Fact]
    public void Assembly_markers_match_its_role()
    {
        Assembly assembly = typeof(SimAssembly).Assembly;
        Assert.Equal(SimAssembly.Name, assembly.GetName().Name);
        Assert.NotNull(assembly.GetCustomAttribute<Tzar2DeterministicAttribute>());
        Assert.NotNull(assembly.GetCustomAttribute<Tzar2NoIOAttribute>());
    }
}
