using System;
using System.Linq;
using Tzar2.Sim.Events;
using Xunit;

namespace Tzar2.Sim.Tests;

public class SimEventKindTests
{
    [Fact]
    public void Every_event_kind_has_a_number_of_its_own()
    {
        // Impersonated shared DiplomacyChanged's 40 until 2026-10-23: every reader of either took one for the other.
        var shared = Enum.GetNames<SimEventKind>().GroupBy(n => Enum.Parse<SimEventKind>(n)).Where(g => g.Count() > 1).Select(g => string.Join(" = ", g)).ToArray();
        Assert.Empty(shared);
    }
}
