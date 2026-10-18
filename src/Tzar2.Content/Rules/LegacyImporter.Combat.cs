using System;
using System.Collections.Generic;

namespace Tzar2.Content.Rules;

/// <content>
/// GAME-07…11 from the object INIs and command cards (ADRs 0090-0095): <c>[damage]</c> shares and <c>[accept]</c> lists of
/// holders with <c>max_obj_in_holder</c>; the inventory size of other units (<c>max_obj_in_holder</c> without
/// <c>[accept]</c>); <c>min_attack_range</c> and <c>max_damage</c>; the spells on a unit's command card (<c>ACTION = spell
/// x</c>, <c>raisedead</c>); one spell entry per spell order of <c>ORDERS.INI</c>; the item classes of
/// <c>legacy.items</c> with their <c>[enter]</c> lists; the extra objects of <c>legacy.objects</c> (crusade anchor, fog
/// revealer) with their <c>cost</c> and <c>fog_range</c>.
/// </content>
internal sealed partial class LegacyImporter
{
    private static readonly string[] DamageKeys = ["sword", "arrow", "cannon"];

    private void AddCombatObjectTraits(Body body, LegacyIni? ini, LegacyIniSection? section, string source, string key, bool building)
    {
        if (ini is null)
        {
            return;
        }

        var accepts = new List<string>();
        foreach (KeyValuePair<string?, string> e in ini.Section("accept")?.Entries ?? [])
        {
            string spec = (e.Key ?? e.Value).Trim();
            if (spec.Length > 0)
            {
                accepts.Add(spec.ToLowerInvariant());
            }
        }

        string? capacity = section?.Get("max_obj_in_holder");
        if (capacity is { Length: > 0 } && accepts.Count > 0)
        {
            var fields = new List<(string, string)> { ("capacity", Num(ParseInt(capacity, source, key + ".max_obj_in_holder"))), ("accepts", "[" + string.Join(", ", accepts) + "]") };
            body.Trait("Holder", [.. fields]);
            var shares = new List<KeyValuePair<string, YamlNode>>();
            foreach (KeyValuePair<string?, string> e in ini.Section("damage")?.Entries ?? [])
            {
                // "00 = 90,10": damage class 00 sword / 01 arrow / 02 cannon -> % to take, % to distribute.
                if (e.Key is null || !int.TryParse(e.Key.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int cls)
                    || cls is < 0 or > 2)
                {
                    continue;
                }

                string[] parts = e.Value.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                {
                    throw new RulesException(source, 0, $"{key}: cannot read [damage] {e.Key} = {e.Value}");
                }

                shares.Add(new KeyValuePair<string, YamlNode>(DamageKeys[cls], new YamlSequence([new YamlScalar(parts[0], quoted: true), new YamlScalar(parts[1], quoted: true)])));
            }

            if (shares.Count > 0)
            {
                body.Raw("Holder", new YamlMapping([new KeyValuePair<string, YamlNode>("damage", new YamlMapping(shares))]));
            }
        }
        else if (capacity is { Length: > 0 } && !building && accepts.Count == 0)
        {
            // Soldiers' max_obj_in_holder (8) is the item inventory (manual ch. 4 §5: inventory pages).
            body.Trait("Inventory", ("slots", Num(Math.Min(8, ParseInt(capacity, source, key + ".max_obj_in_holder")))));
        }

        if (section?.Get("min_attack_range") is { Length: > 0 } minRange)
        {
            body.Trait("Armament", ("minRange", Num(ParseInt(minRange, source, key + ".min_attack_range"))));
        }

        if (section?.Get("max_damage") is { Length: > 0 } maxDamage)
        {
            body.Trait("Armament", ("maxAttack", Num(ParseInt(maxDamage, source, key + ".max_damage"))));
        }

        // The spells of the command card (orders ACTION = spell x / raisedead).
        var spells = new List<string>();
        if (section?.Get("menu") is { Length: > 0 } menu)
        {
            CollectSpells(menu, spells, []);
        }

        if (spells.Count > 0)
        {
            body.Trait("Spellcaster", ("spells", "[" + string.Join(", ", spells) + "]"));
        }
    }

    private void CollectSpells(string menu, List<string> spells, List<string> visited)
    {
        if (visited.Count >= 8 || visited.Contains(menu) || !_menus.TryGetValue(menu, out List<string>? orders))
        {
            return;
        }

        visited.Add(menu);
        foreach (string order in orders)
        {
            if (!_orders.TryGetValue(order, out (string Action, string ClassName) o))
            {
                continue;
            }

            if (SpellOrder(order, o.Action) is { } spell)
            {
                AddOnce(spells, spell);
            }
            else if (o.Action.StartsWith("SUBMENU ", StringComparison.Ordinal))
            {
                CollectSpells(o.Action["SUBMENU ".Length..].Trim(), spells, visited);
            }
        }
    }

    // "ACTION = spell spell_heal" names the spell; the special actions raisedead (necromancy), cloak and murder (ninjas)
    // are abilities cast like spells, named by their order.
    private static string? SpellOrder(string order, string action)
    {
        string a = action.Trim();
        if (a.StartsWith("spell ", StringComparison.OrdinalIgnoreCase) && a.Length > "spell ".Length)
        {
            return a["spell ".Length..].Trim().ToLowerInvariant();
        }

        return a.ToLowerInvariant() is "raisedead" or "cloak" or "murder" ? order.Trim().ToLowerInvariant() : null;
    }

    private void AddCombatEntries(RulesDocument document)
    {
        // Spells are entries of the rules YAML, keyed by their order (the data names them only; UNK-26).

        // Items: the classes of legacy.items (class -> kind), with the [enter] list of who may carry them.
        foreach (KeyValuePair<string, YamlNode> item in Mapping("items").Entries)
        {
            string cls = item.Key;
            string kind = RulesYaml.AsString(item.Value, $"legacy.items.{cls}");
            LegacyIni? ini = _tables.ObjectOf(cls);
            if (ini is null)
            {
                _diagnostics.Add($"legacy.items.{cls}: no object INI of that class");
                continue;
            }

            string key = "comm." + cls;
            var holders = new List<string>();
            foreach (KeyValuePair<string?, string> e in ini.Section("enter")?.Entries ?? [])
            {
                string spec = (e.Key ?? e.Value).Trim();
                if (spec.Length > 0)
                {
                    holders.Add(spec.ToLowerInvariant());
                }
            }

            var body = new Body();
            body.Inherits("^Item", "comm", cls, _templateExists);
            body.Trait("Legacy", ("class", cls), ("civ", "comm"));
            body.Trait("Item", ("kind", kind), ("holders", "[" + string.Join(", ", holders) + "]"));
            document.ApplyLayer(body.ToEntry(key), "legacy:" + ini.Path, declared: false);
        }

        // Other objects (crusade anchor, fog revealer): cost and fog_range per civilization section.
        YamlMapping suffixes = Mapping("costSuffixes");
        foreach (string cls in Names("objects"))
        {
            LegacyIni? ini = _tables.ObjectOf(cls);
            if (ini is null)
            {
                _diagnostics.Add($"legacy.objects: no object INI of class '{cls}'");
                continue;
            }

            foreach (LegacyIniSection section in ini.Sections)
            {
                string? civ = section.Name?.Trim().ToLowerInvariant();
                if (civ is null || civ is "general" or "accept" or "enter" or "damage")
                {
                    continue;
                }

                string lower = cls.ToLowerInvariant();
                string key = civ + "." + lower;
                string source = "legacy:" + ini.Path;
                var body = new Body();
                body.Inherits("^Object", civ, lower, _templateExists);
                body.Trait("Legacy", ("class", lower), ("civ", civ));
                if (section.Get("cost") is { Length: > 0 } cost)
                {
                    body.Trait("Cost", [.. CostFields(cost, suffixes, source, key)]);
                }

                if (section.Get("fog_range") is { Length: > 0 } fog)
                {
                    body.Trait("RevealsShroud", ("sight", Num(ParseInt(fog, source, key + ".fog_range"))));
                }

                document.ApplyLayer(body.ToEntry(key), source, declared: false);
            }
        }
    }

    private static List<(string, string)> CostFields(string cost, YamlMapping suffixes, string source, string key)
    {
        var fields = new List<(string, string)>();
        foreach (string part in cost.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int split = part.Length;
            while (split > 0 && !char.IsAsciiDigit(part[split - 1]))
            {
                split--;
            }

            string suffix = part[split..].ToLowerInvariant();
            if (split == 0 || suffixes.Get(suffix) is not YamlScalar { IsNull: false } resource)
            {
                throw new RulesException(source, 0, $"{key}: cannot read cost '{cost}'");
            }

            fields.Add((resource.Value, Num(ParseInt(part[..split], source, key + ".cost"))));
        }

        return fields;
    }
}
