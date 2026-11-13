using System.IO;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Serialization;
using Tzar2.Scripting.Legacy;
using Tzar2.Tests.Scenario;
using Xunit;

namespace Tzar2.Scripting.Tests;

/// <summary>The trigger state of Tzar2's own actions: patrols and waves (ADR 0293).</summary>
public class OwnActionStateTests
{
    private static byte[] Written(TriggerState state)
    {
        var w = new BinaryBufferWriter(256);
        state.WriteTo(w);
        return w.ToArray();
    }

    [Fact]
    public void Patrols_and_waves_write_version_5_and_read_back_and_bad_ones_are_refused()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        b.Rule("1", b.Action(new ExpressionAction("noop", b.Expr("$x = 1"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(2, Written(d.State)[0]); // without them, the older format

        d.State.Patrols.Add(new PatrolState(5, 0, 1));
        d.State.Waves.Add(new WaveState(0, 3, 4_000));
        byte[] bytes = Written(d.State);
        Assert.Equal(5, bytes[0]);
        TriggerState back = TriggerState.ReadFrom(bytes, d.State.Shape);
        Assert.Equal([new PatrolState(5, 0, 1)], back.Patrols);
        Assert.Equal([new WaveState(0, 3, 4_000)], back.Waves);
        Assert.Equal(bytes, Written(back));

        // An action index the scenario does not have is refused (snapshots are untrusted).
        d.State.Patrols[0] = new PatrolState(5, 7, 0);
        Assert.Throws<InvalidDataException>(() => TriggerState.ReadFrom(Written(d.State), d.State.Shape));
        d.State.Patrols.Clear();
        d.State.Waves[0] = new WaveState(0, 0, 10);
        Assert.Throws<InvalidDataException>(() => TriggerState.ReadFrom(Written(d.State), d.State.Shape));
    }
}
