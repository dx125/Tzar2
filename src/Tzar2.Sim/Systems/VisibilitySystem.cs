using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Events;

namespace Tzar2.Sim.Systems;

/// <summary>Pipeline step 14 (GAME-12, ADR 0101): incremental per-kingdom visibility, exploration and last-seen records.</summary>
public sealed class VisibilitySystem : ISimSystem
{
    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Visibility;

    /// <inheritdoc />
    public string Name => "visibility";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.World.Visibility.Update(context.World, context.Workers);
    }
}

/// <summary>
/// Pipeline step 15 (GAME-16, ADR 0105): the day clock and the weather are pure functions of the tick (no state); this
/// step only reports their changes as events for the presentation (dusk, dawn, rain starting or stopping).
/// </summary>
public sealed class ClockSystem : ISimSystem
{
    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Clock;

    /// <inheritdoc />
    public string Name => "clock";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        int tick = world.Tick.Value;
        if (tick == 0)
        {
            return;
        }

        bool night = world.IsNightAt(world.MinuteAt(tick));
        if (night != world.IsNightAt(world.MinuteAt(tick - 1)))
        {
            world.Events.Emit(new SimEvent(SimEventKind.DayPhaseChanged, 0, EntityId.None, world.MinuteAt(tick), 0, night ? 1 : 0));
        }

        WeatherKind weather = world.WeatherAt(tick);
        if (weather != world.WeatherAt(tick - 1))
        {
            world.Events.Emit(new SimEvent(SimEventKind.WeatherChanged, 0, EntityId.None, 0, 0, (int)weather));
        }
    }
}
