using Tzar2.Content.Compiled;

namespace Tzar2.Content.Rules;

internal sealed partial class EntityCompiler
{
    // The world traits (ADRs 0101-0104): Trader, TradeDock, Tavern, ClaimOnTouch, Animal, Detector.
    private WorldTraits ReadWorldTraits(ResolvedEntry entry, string key)
    {
        WorldTraits w = WorldTraits.None;
        if (entry.Find("Trader") is { } trader)
        {
            int capacity = trader.Get("capacity") is { } c ? RulesetCompiler.Int(c.Node, c.Source, $"{key}.Trader.capacity") : 100;
            if (capacity < 1)
            {
                throw Error(trader.Get("capacity")!.Value, $"{key}.Trader.capacity must be at least 1");
            }

            int tech = -1;
            if (trader.Get("technology") is { } t)
            {
                string name = RulesetCompiler.Str(t.Node, t.Source, $"{key}.Trader.technology");
                tech = _research.Technology(name) ?? throw Error(t, $"{key}.Trader.technology: unknown '{name}'");
            }

            w = w with { Trader = new TraderDefinition(capacity, Access(trader.Get("access"), $"{key}.Trader.access"), tech) };
        }

        if (entry.Find("TradeDock") is { } dock)
        {
            w = w with { TradeDock = Access(dock.Get("access"), $"{key}.TradeDock.access") };
        }

        if (entry.Find("ClaimOnTouch") is { } claim)
        {
            string by = claim.Get("by") is { } b ? RulesetCompiler.Str(b.Node, b.Source, $"{key}.ClaimOnTouch.by") : "any";
            w = w with
            {
                Claim = by switch
                {
                    "any" => ClaimBy.AnyUnit,
                    "military" => ClaimBy.Military,
                    _ => throw Error(claim.Get("by")!.Value, $"{key}.ClaimOnTouch.by: '{by}' is not any/military"),
                },
            };
        }

        if (entry.Find("Animal") is { } animal)
        {
            w = w with
            {
                Wanders = animal.Get("wanders") is not { } wa || RulesetCompiler.Bool(wa.Node, wa.Source, $"{key}.Animal.wanders"),
                Guards = animal.Get("guards") is { } g && RulesetCompiler.Bool(g.Node, g.Source, $"{key}.Animal.guards"),
                IsAmbient = animal.Get("ambient") is { } am && RulesetCompiler.Bool(am.Node, am.Source, $"{key}.Animal.ambient"),
            };
        }

        if (entry.Find("Detector") is { } detector)
        {
            int tiles = detector.Get("radius") is { } r ? RulesetCompiler.Int(r.Node, r.Source, $"{key}.Detector.radius") : 4;
            if (tiles is < 0 or > 64)
            {
                throw Error(detector.Get("radius")!.Value, $"{key}.Detector.radius must be 0..64 tiles");
            }

            w = w with { DetectUnits = tiles * UnitsPerTile };
        }

        return w with { IsTavern = entry.Find("Tavern") is not null };

        static DropOffAccess Access(RuleValue? value, string what)
        {
            if (value is null)
            {
                return DropOffAccess.Land;
            }

            return RulesetCompiler.Str(value.Value.Node, value.Value.Source, what) switch
            {
                "land" => DropOffAccess.Land,
                "water" => DropOffAccess.Water,
                { } other => throw Error(value.Value, $"{what}: '{other}' is not land/water"),
            };
        }
    }
}
