using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Events;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Helpers of the GAME-07…11 tests (synthetic classic; ADRs 0090-0096). Synthetic numbers used: catapult range 15,
/// minimum range 3, attack 11, swing 2 s, stone 200 px/s with a 32 px splash of 50%; cannon tower attack 39 rolling up
/// to 45; mage 90 mana, priest 95 mana; castle holds 45 (95/5, 95/5, 85/15), outpost 40, galleon 20, carpet 6; peasants
/// carry 6 items, soldiers 7.
/// </summary>
internal static class CombatMagicFixtures
{
    /// <summary>Gives a player a technology between ticks (the modifier tables follow).</summary>
    public static void Grant(World world, int player, string technology)
    {
        int id = world.Ruleset.TechnologyId(technology);
        world.Players[player].AddTechnology(id);
        world.Stats.Rebuild(player, world.Players[player]);
    }

    public static ushort Spell(Ruleset ruleset, string key) => (ushort)ruleset.SpellId(key);

    public static IEnumerable<SimEvent> Of(SimDriver d, SimEventKind kind) => d.Events.Where(e => e.Event.Kind == kind).Select(e => e.Event);

    public static IEnumerable<(int Tick, SimEvent Event)> TimedOf(SimDriver d, SimEventKind kind) => d.Events.Where(e => e.Event.Kind == kind);

    public static int Hp(SimDriver d, EntityId e) => d.World.Healths.Get(e).Current;
}
