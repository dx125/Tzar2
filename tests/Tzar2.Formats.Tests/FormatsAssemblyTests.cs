using System.Reflection;
using Tzar2.Core;
using Xunit;

namespace Tzar2.Formats.Tests;

public class FormatsAssemblyTests
{
    [Fact]
    public void Assembly_markers_match_its_role()
    {
        Assembly assembly = typeof(FormatsAssembly).Assembly;
        Assert.Equal(FormatsAssembly.Name, assembly.GetName().Name);
        Assert.Null(assembly.GetCustomAttribute<Tzar2DeterministicAttribute>());
        Assert.Null(assembly.GetCustomAttribute<Tzar2NoIOAttribute>());
    }
}
