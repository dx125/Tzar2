using System.Reflection;
using Tzar2.Core;
using Xunit;

namespace Tzar2.Scripting.Tests;

public class ScriptingAssemblyTests
{
    [Fact]
    public void Assembly_markers_match_its_role()
    {
        Assembly assembly = typeof(ScriptingAssembly).Assembly;
        Assert.Equal(ScriptingAssembly.Name, assembly.GetName().Name);
        Assert.NotNull(assembly.GetCustomAttribute<Tzar2DeterministicAttribute>());
        Assert.NotNull(assembly.GetCustomAttribute<Tzar2NoIOAttribute>());
    }
}
