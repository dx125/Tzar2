using System.Reflection;
using Tzar2.Core;
using Xunit;

namespace Tzar2.Content.Tests;

public class ContentAssemblyTests
{
    [Fact]
    public void Assembly_markers_match_its_role()
    {
        Assembly assembly = typeof(ContentAssembly).Assembly;
        Assert.Equal(ContentAssembly.Name, assembly.GetName().Name);
        Assert.Null(assembly.GetCustomAttribute<Tzar2DeterministicAttribute>());
        Assert.Null(assembly.GetCustomAttribute<Tzar2NoIOAttribute>());
    }
}
