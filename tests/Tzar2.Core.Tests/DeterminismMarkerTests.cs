using System.Reflection;
using Xunit;

namespace Tzar2.Core.Tests;

public class DeterminismMarkerTests
{
    [Fact]
    public void Core_is_marked_deterministic_but_may_do_io()
    {
        Assembly core = typeof(Tzar2DeterministicAttribute).Assembly;
        Assert.NotNull(core.GetCustomAttribute<Tzar2DeterministicAttribute>());
        Assert.Null(core.GetCustomAttribute<Tzar2NoIOAttribute>());
    }
}
