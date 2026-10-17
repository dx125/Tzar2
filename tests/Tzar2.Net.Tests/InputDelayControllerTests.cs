using Tzar2.Net.Lockstep;
using Xunit;

namespace Tzar2.Net.Tests;

public class InputDelayControllerTests
{
    private const long TickMicroseconds = 50_000;

    [Fact]
    public void Rtt_estimator_follows_rfc6298()
    {
        var rtt = new RttEstimator();
        rtt.AddSample(100_000);
        Assert.Equal(100_000, rtt.SmoothedMicroseconds);
        Assert.Equal(50_000, rtt.VariationMicroseconds);

        rtt.AddSample(200_000);
        Assert.Equal(112_500, rtt.SmoothedMicroseconds); // (7 * 100 + 200) / 8
        Assert.Equal(62_500, rtt.VariationMicroseconds); // (3 * 50 + 100) / 4
        Assert.Equal(2, rtt.SampleCount);
    }

    [Theory]
    [InlineData(0, 2)] // LAN: 1 + ceil(5 ms / 50 ms)
    [InlineData(100_000, 3)] // 1 + ceil(55 ms / 50 ms)
    [InlineData(250_000, 4)] // 1 + ceil(130 ms / 50 ms)
    [InlineData(2_000_000, 10)] // clamped
    public void Target_covers_one_way_trip_plus_margin(long steadyRtt, int expected)
    {
        RttEstimator rtt = Steady(steadyRtt);
        var controller = new InputDelayController(TickMicroseconds);
        Assert.Equal(expected, controller.Target(rtt));
    }

    [Fact]
    public void Delay_rises_at_once_and_falls_only_after_the_hold_period()
    {
        var controller = new InputDelayController(TickMicroseconds, decreaseHoldTicks: 3);
        Assert.Equal(4, controller.Update(Steady(250_000)));

        RttEstimator lan = Steady(0);
        Assert.Equal(4, controller.Update(lan));
        Assert.Equal(4, controller.Update(lan));
        Assert.Equal(3, controller.Update(lan));
        Assert.Equal(3, controller.Update(lan));
        Assert.Equal(3, controller.Update(lan));
        Assert.Equal(2, controller.Update(lan));
    }

    [Fact]
    public void Late_batches_raise_delay_once_per_cooldown_up_to_the_maximum()
    {
        var controller = new InputDelayController(TickMicroseconds, maxDelay: 4, lateCooldownTicks: 2);
        RttEstimator lan = Steady(0);
        controller.OnLateBatch();
        controller.OnLateBatch(); // same burst: ignored
        Assert.Equal(3, controller.Delay);

        controller.Update(lan);
        controller.Update(lan);
        controller.OnLateBatch();
        Assert.Equal(4, controller.Delay);

        controller.Update(lan);
        controller.Update(lan);
        controller.OnLateBatch();
        Assert.Equal(4, controller.Delay); // clamped
    }

    private static RttEstimator Steady(long rttMicroseconds)
    {
        var rtt = new RttEstimator();
        for (int i = 0; i < 64; i++)
        {
            rtt.AddSample(rttMicroseconds);
        }

        return rtt;
    }
}
