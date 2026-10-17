using System.Reflection;
using Tzar2.Core;
using Xunit;

namespace Tzar2.Net.Tests;

public class NetAssemblyTests
{
    [Fact]
    public void Assembly_markers_match_its_role()
    {
        Assembly assembly = typeof(NetAssembly).Assembly;
        Assert.Equal(NetAssembly.Name, assembly.GetName().Name);
        Assert.Null(assembly.GetCustomAttribute<Tzar2DeterministicAttribute>());
        Assert.Null(assembly.GetCustomAttribute<Tzar2NoIOAttribute>());
    }
}
