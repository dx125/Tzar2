using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// The object properties dialog as a dock (TDD §18.2 "Object properties", Appendix C.2): kingdom, name, title, the skill
/// overrides with "use default", icons, the flags No AI / Persistent / Not Appear, resource amounts and the per-type item
/// properties. With several objects selected every change applies to all of them (multi-edit).
/// </summary>
public partial class PropertiesPanel : VBoxContainer
{
    private static readonly string[] Owners = ["Kingdom 1", "Kingdom 2", "Kingdom 3", "Kingdom 4", "Kingdom 5", "Kingdom 6", "Kingdom 7", "Kingdom 8", "Nature"];
    private readonly EditorController _c;
    private readonly VBoxContainer _body;

    /// <summary>Creates the panel.</summary>
    public PropertiesPanel(EditorController controller)
    {
        _c = controller;
        Name = "Properties";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        _body = Forms.Scroll(this);
        Refresh();
    }

    /// <summary>Rebuilds the form for the current selection.</summary>
    public void Refresh()
    {
        Forms.Clear(_body);
        ImmutableArray<int> ids = _c.Selection;
        DocumentState s = _c.Document.State;
        if (ids.IsEmpty || s.Find(ids[0]) is not { } first)
        {
            Forms.Label(_body, "Nothing selected. Click an object, or drag a rectangle (Shift adds).");
            return;
        }

        bool many = ids.Length > 1;
        Forms.Heading(_body, many ? $"{ids.Length} objects" : $"{first.TypeKey} (#{first.Id})");
        EntityTypeDefinition? type = _c.Ruleset.EntityTypeId(first.TypeKey) is >= 0 and var t ? _c.Ruleset.EntityTypes[t] : null;
        GridContainer g = Forms.Grid(_body);
        Forms.Choice(g, "Kingdom", Owners, first.Owner == MapObject.Nature ? 8 : Math.Min(8, (int)first.Owner), i =>
            Edit("Change owner", o => o with { Placement = o.Placement with { Owner = i == 8 ? MapObject.Nature : (byte)i } }));
        if (!many)
        {
            Forms.Text(g, "Name", first.Info.Name, v => Edit("Rename", o => o with { Info = o.Info with { Name = v } }));
            Forms.Text(g, "Title", first.Info.Title, v => Edit("Retitle", o => o with { Info = o.Info with { Title = v } }));
        }

        int maxHealth = type?.MaxHealth ?? 100;
        Forms.Optional(g, "Health", first.Info.Health, maxHealth, 1, 1_000_000, v => Edit("Health", o => o with { Info = o.Info with { Health = v } }));
        Forms.Optional(g, "Max health", first.Info.MaxHealth, maxHealth, 1, 1_000_000, v => Edit("Max health", o => o with { Info = o.Info with { MaxHealth = v } }));
        if (type?.Traits.Category == EntityCategory.Building)
        {
            Forms.Optional(g, "Resistance", first.Info.Resistance, type.Traits.Resistance, 0, 1_000_000, v => Edit("Resistance", o => o with { Info = o.Info with { Resistance = v } }));
        }
        else
        {
            Forms.Optional(g, "Attack", first.Info.Attack, type?.Traits.Armament?.Attack ?? 0, 0, 100_000, v => Edit("Attack", o => o with { Info = o.Info with { Attack = v } }));
            Forms.Optional(g, "Defence", first.Info.Defence, type?.Traits.Defence ?? 0, 0, 100_000, v => Edit("Defence", o => o with { Info = o.Info with { Defence = v } }));
            Forms.Optional(g, "Mana", first.Info.Mana, type?.Traits.ManaPool?.Max ?? 0, 0, 100_000, v => Edit("Mana", o => o with { Info = o.Info with { Mana = v } }));
            Forms.Optional(g, "Level", first.Info.Level, 1, 1, 100, v => Edit("Level", o => o with { Info = o.Info with { Level = v } }));
            if (!many)
            {
                Forms.Text(g, "Icon 44×44", first.Info.Icon?.Path ?? string.Empty, v => Edit("Icon", o => o with { Info = o.Info with { Icon = v.Length > 0 ? new ResourceRef(v) : null } }));
                Forms.Text(g, "Big icon 96×96", first.Info.BigIcon?.Path ?? string.Empty, v => Edit("Big icon", o => o with { Info = o.Info with { BigIcon = v.Length > 0 ? new ResourceRef(v) : null } }));
            }

            Forms.Number(g, "Facing", first.Placement.Facing, 0, 7, v => Edit("Facing", o => o with { Placement = o.Placement with { Facing = (byte)v } }));
        }

        Flag(g, "Not Appear", ObjectFlags.NotAppear, first);
        Flag(g, "Persistent", ObjectFlags.Persistent, first);
        Flag(g, "No AI", ObjectFlags.NoAi, first);

        if (type?.Traits.ResourceNode is { } node)
        {
            Forms.Number(g, "Amount (0 = default " + node.Amount.ToString(CultureInfo.InvariantCulture) + ")", first.Placement.Amount, 0, 10_000_000, v =>
                _c.Document.Apply("Resource amount", st => st.SetAmount(_c.Selection, v)));
            Forms.Button(_body, "Restore default amount", () => _c.Document.Apply("Default amount", st => st.SetAmount(_c.Selection, 0)));
        }

        if (type?.Traits.Gate is not null || type?.Traits.IsBridge == true)
        {
            Forms.Check(g, "Turned", (first.Placement.Flags & MapObjectFlags.Rotated) != 0, on => PlacementFlag(MapObjectFlags.Rotated, on));
            if (type.Traits.Gate is not null)
            {
                Forms.Check(g, "Open", (first.Placement.Flags & MapObjectFlags.Open) != 0, on => PlacementFlag(MapObjectFlags.Open, on));
                Forms.Check(g, "Other side", (first.Placement.Flags & MapObjectFlags.Mirrored) != 0, on => PlacementFlag(MapObjectFlags.Mirrored, on));
            }
        }

        if (type?.Traits.Item is not null)
        {
            ItemForm(first, many);
        }

        if (!many)
        {
            ImmutableArray<ObjectReference> refs = s.ReferencesTo(first.Id);
            Forms.Heading(_body, refs.IsEmpty ? "Not referenced by triggers" : $"Referenced {refs.Length} time(s) by triggers");
            foreach (ObjectReference r in refs)
            {
                string owner = r.Owner switch
                {
                    ReferenceOwner.Event => "event " + s.Scenario.Events[r.Index].Name,
                    ReferenceOwner.Action => "action " + s.Scenario.Actions[r.Index].Name,
                    ReferenceOwner.Phrase => $"conversation {s.Scenario.Conversations[r.Index].Name}, phrase {s.Scenario.Conversations[r.Index].Phrases[r.SubIndex].Name}",
                    _ => "sound " + s.Scenario.Sounds[r.Index].Name,
                };
                Forms.Label(_body, "• " + owner);
            }
        }

        var actions = new HBoxContainer();
        Forms.Button(actions, "Delete", _c.DeleteSelection);
        Forms.Button(actions, "Turn ↻", () => _c.Document.Apply("Turn", st => Transforms.Apply(st, _c.Selection, Transforms.Kind.RotateClockwise, _c.Document.Width, _c.Document.Height)));
        Forms.Button(actions, "Mirror ↔", () => _c.Document.Apply("Mirror", st => Transforms.Apply(st, _c.Selection, Transforms.Kind.MirrorHorizontal, _c.Document.Width, _c.Document.Height)));
        Forms.Button(actions, "Mirror ↕", () => _c.Document.Apply("Mirror", st => Transforms.Apply(st, _c.Selection, Transforms.Kind.MirrorVertical, _c.Document.Width, _c.Document.Height)));
        _body.AddChild(actions);
    }

    private void ItemForm(EditorObject first, bool many)
    {
        Forms.Heading(_body, "Item");
        GridContainer g = Forms.Grid(_body);
        ItemInfo item = first.Info.Item ?? new ItemInfo(first.TypeKey);
        void Set(string label, Func<ItemInfo, ItemInfo> change) => Edit(label, o => o with { Info = o.Info with { Item = change(o.Info.Item ?? new ItemInfo(o.TypeKey)) } });
        if (!many)
        {
            Forms.Text(g, "Definition", item.Definition, v => Set("Item definition", i => i with { Definition = v }));
        }

        string cls = first.TypeKey;
        if (cls.Contains("resitem", StringComparison.Ordinal))
        {
            Forms.Optional(g, "Resource amount", item.Amount, 100, 0, 1_000_000, v => Set("Item amount", i => i with { Amount = v }));
        }
        else if (cls.Contains("scroll", StringComparison.Ordinal))
        {
            Forms.Multiline(g, "Scroll text", item.Text ?? string.Empty, v => Set("Scroll text", i => i with { Text = v }));
            Forms.Optional(g, "Scroll time (ms)", item.ScrollTimeMs, 10_000, 0, 600_000, v => Set("Scroll time", i => i with { ScrollTimeMs = v }));
            Forms.Check(g, "Auto close", item.AutoClose == true, v => Set("Auto close", i => i with { AutoClose = v }));
            Forms.Text(g, "Image", item.Image?.Path ?? string.Empty, v => Set("Scroll image", i => i with { Image = v.Length > 0 ? new ResourceRef(v) : null }));
            Forms.Text(g, "Sound", item.Sound?.Path ?? string.Empty, v => Set("Scroll sound", i => i with { Sound = v.Length > 0 ? new ResourceRef(v) : null }));
            Forms.Text(g, "Music", item.Music?.Path ?? string.Empty, v => Set("Scroll music", i => i with { Music = v.Length > 0 ? new ResourceRef(v) : null }));
        }
        else if (cls.Contains("magic", StringComparison.Ordinal))
        {
            Forms.Optional(g, "Magic type", item.MagicType, 0, 0, 64, v => Set("Magic type", i => i with { MagicType = v }));
            Forms.Check(g, "Needs mana", item.NeedsMana == true, v => Set("Needs mana", i => i with { NeedsMana = v }));
            Forms.Optional(g, "Time restriction (s)", item.CooldownSeconds, 0, 0, 3600, v => Set("Cooldown", i => i with { CooldownSeconds = v }));
            Forms.Optional(g, "Max charges", item.Charges, 1, 0, 1000, v => Set("Charges", i => i with { Charges = v }));
        }
        else
        {
            // Armor (defence), weapon (attack), potion (level / attack / defence / health / mana): the bonus value.
            Forms.Optional(g, "Value (defence / attack / potion)", item.Value, 1, -10_000, 10_000, v => Set("Item value", i => i with { Value = v }));
        }
    }

    private void Flag(GridContainer g, string label, ObjectFlags flag, EditorObject first)
        => Forms.Check(g, label, (first.Info.Flags & flag) != 0, on => Edit(label, o => o with { Info = o.Info with { Flags = on ? o.Info.Flags | flag : o.Info.Flags & ~flag } }));

    private void PlacementFlag(MapObjectFlags flag, bool on)
        => Edit("Placement", o => o with { Placement = o.Placement with { Flags = on ? o.Placement.Flags | flag : o.Placement.Flags & ~flag } });

    private void Edit(string label, Func<EditorObject, EditorObject> change)
    {
        ImmutableArray<int> ids = _c.Selection;
        if (ids.IsEmpty)
        {
            return;
        }

        _c.Document.Apply(label + (ids.Length > 1 ? $" ({ids.Length})" : string.Empty), s => s.UpdateAll(ids, change));
    }
}
