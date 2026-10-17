using System.Reflection;
using Tzar2.Core;
using Xunit;

namespace Tzar2.AI.Tests;

public class AIAssemblyTests
{
    [Fact]
    public void Assembly_markers_match_its_role()
    {
        Assembly assembly = typeof(AIAssembly).Assembly;
        Assert.Equal(AIAssembly.Name, assembly.GetName().Name);
        Assert.NotNull(assembly.GetCustomAttribute<Tzar2DeterministicAttribute>());
        Assert.NotNull(assembly.GetCustomAttribute<Tzar2NoIOAttribute>());
    }
}
