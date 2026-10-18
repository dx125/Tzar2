using System;
using System.Collections.Generic;

namespace Tzar2.Content.Rules;

/// <summary>A rules error with its source (file and line), e.g. <c>rules/classic.yaml:42: euro.peon.Health.max: ...</c>.</summary>
public sealed class RulesException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RulesException(string source, int line, string message)
        : base(line > 0 ? $"{source}:{line}: {message}" : $"{source}: {message}")
    {
        RuleSource = source;
        SourceLine = line;
    }

    /// <summary>Creates the exception.</summary>
    public RulesException()
        : this("rules", 0, "rules error")
    {
    }

    /// <summary>Creates the exception.</summary>
    public RulesException(string message)
        : this("rules", 0, message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public RulesException(string message, Exception innerException)
        : base(message, innerException)
    {
        RuleSource = "rules";
    }

    /// <summary>The file (or <c>legacy:</c> table) the error is in.</summary>
    public string RuleSource { get; } = "rules";

    /// <summary>1-based line, 0 if unknown.</summary>
    public int SourceLine { get; }
}

/// <summary>How a field of a trait is changed by a patch (TDD §11.2: overrides and the list operations <c>+=</c>, <c>-=</c>).</summary>
internal enum FieldOp : byte
{
    /// <summary><c>field: value</c> replaces the inherited value.</summary>
    Set,

    /// <summary><c>field+=: [a, b]</c> appends to the inherited list.</summary>
    Append,

    /// <summary><c>field-=: [a]</c> removes items from the inherited list.</summary>
    Remove,

    /// <summary><c>field: ~</c> drops the field (back to the trait's default).</summary>
    Unset,
}

/// <summary>A located YAML value.</summary>
internal readonly record struct RuleValue(YamlNode Node, string Source);

internal sealed record FieldOperation(string Field, FieldOp Op, RuleValue Value);

/// <summary>One change of an entry: a trait patch (field operations) or the removal of a trait (<c>-Trait:</c>).</summary>
internal sealed record TraitChange(string Trait, bool RemoveTrait, List<FieldOperation> Operations, string Source, int Line);

/// <summary>
/// An unresolved rules entry: its parents (<c>inherits</c>) and its own changes, in the order the layers patched it
/// (ADR 0031). Keys starting with <c>^</c> are templates: they are inherited from but never compiled.
/// </summary>
internal sealed class RuleEntry(string key, string source, int line)
{
    public string Key { get; } = key;

    public string Source { get; } = source;

    public int Line { get; } = line;

    public List<string> Inherits { get; } = [];

    public List<TraitChange> Changes { get; } = [];

    public bool IsTemplate => Key.StartsWith('^');
}

/// <summary>An entry after inheritance: traits in order, each with its fields in order.</summary>
internal sealed class ResolvedEntry(string key, string source, int line)
{
    public string Key { get; } = key;

    public string Source { get; } = source;

    public int Line { get; } = line;

    public List<ResolvedTrait> Traits { get; } = [];

    /// <summary>
    /// Traits this entry (or a template it inherits) removed with <c>-Trait:</c>: an entry that inherits it after a parent
    /// that adds such a trait loses it too (a template's removal is part of what it passes on, ADR 0040).
    /// </summary>
    public List<string> Removed { get; } = [];

    public ResolvedTrait? Find(string trait)
    {
        foreach (ResolvedTrait t in Traits)
        {
            if (string.Equals(t.Name, trait, StringComparison.Ordinal))
            {
                return t;
            }
        }

        return null;
    }
}

/// <summary>A trait with its resolved fields.</summary>
internal sealed class ResolvedTrait(string name, string source, int line)
{
    public string Name { get; } = name;

    public string Source { get; set; } = source;

    public int Line { get; set; } = line;

    public List<KeyValuePair<string, RuleValue>> Fields { get; } = [];

    public RuleValue? Get(string field)
    {
        foreach (KeyValuePair<string, RuleValue> f in Fields)
        {
            if (string.Equals(f.Key, field, StringComparison.Ordinal))
            {
                return f.Value;
            }
        }

        return null;
    }

    public void Set(string field, RuleValue value)
    {
        for (int i = 0; i < Fields.Count; i++)
        {
            if (string.Equals(Fields[i].Key, field, StringComparison.Ordinal))
            {
                Fields[i] = new KeyValuePair<string, RuleValue>(field, value);
                return;
            }
        }

        Fields.Add(new KeyValuePair<string, RuleValue>(field, value));
    }

    public void Unset(string field) => Fields.RemoveAll(f => string.Equals(f.Key, field, StringComparison.Ordinal));

    public ResolvedTrait Clone()
    {
        var copy = new ResolvedTrait(Name, Source, Line);
        copy.Fields.AddRange(Fields);
        return copy;
    }
}

/// <summary>
/// The layered rules of one compilation (TDD §11.2, ADR 0031): entries by key in first-definition order. Layers are
/// applied in order (imported legacy tables, then the ruleset files, then overrides); a later layer patches the
/// entries of earlier ones. Inheritance is resolved after all layers are in.
/// </summary>
internal sealed class RulesDocument
{
    private readonly List<RuleEntry> _entries = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal); // lookup only, never iterated (T2D004)

    public IReadOnlyList<RuleEntry> Entries => _entries;

    public RuleEntry? Find(string key) => _index.TryGetValue(key, out int i) ? _entries[i] : null;

    /// <summary>The entry <paramref name="key"/>, created if it does not exist.</summary>
    public RuleEntry GetOrAdd(string key, string source, int line)
    {
        if (_index.TryGetValue(key, out int i))
        {
            return _entries[i];
        }

        var entry = new RuleEntry(key, source, line);
        _index.Add(key, _entries.Count);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Applies a YAML mapping of entries (<c>key: { inherits, Trait: {...}, -Trait: }</c>) as a layer. With
    /// <paramref name="declared"/>, a layer may only patch entries that exist, unless it marks a new one with
    /// <c>new: true</c> (templates are exempt): a typo in a patch is an error instead of a silently created type.
    /// </summary>
    public void ApplyLayer(YamlMapping entries, string source, bool declared = true)
    {
        foreach (KeyValuePair<string, YamlNode> e in entries.Entries)
        {
            YamlMapping? body = e.Value as YamlMapping;
            if (body is null && e.Value is not YamlScalar { IsNull: true })
            {
                throw new RulesException(source, e.Value.Line, $"{e.Key}: an entry must be a mapping of traits");
            }

            bool isNew = body?.Get("new") is { } marker && RulesetCompiler.Bool(marker, source, $"{e.Key}.new");
            if (declared && !e.Key.StartsWith('^'))
            {
                bool exists = Find(e.Key) is not null;
                if (!exists && !isNew)
                {
                    throw new RulesException(source, e.Value.Line, $"{e.Key}: no such entry to patch (a typo? mark a new entry with 'new: true')");
                }

                if (exists && isNew)
                {
                    throw new RulesException(source, e.Value.Line, $"{e.Key}: 'new: true' but the entry already exists");
                }
            }

            RuleEntry entry = GetOrAdd(e.Key, source, e.Value.Line);
            if (body is not null)
            {
                ApplyPatch(entry, body, source);
            }
        }
    }

    public static void ApplyPatch(RuleEntry entry, YamlMapping body, string source)
    {
        foreach (KeyValuePair<string, YamlNode> kv in body.Entries)
        {
            string key = kv.Key;
            int line = kv.Value.Line;
            if (key == "new")
            {
                continue; // the declaration marker of ApplyLayer
            }

            if (key is "inherits" or "inherits+=")
            {
                if (key == "inherits")
                {
                    entry.Inherits.Clear();
                }

                foreach (string parent in StringList(kv.Value, source, $"{entry.Key}.{key}"))
                {
                    if (!parent.StartsWith('^'))
                    {
                        throw new RulesException(source, line, $"{entry.Key}: can only inherit templates (keys starting with '^'), not '{parent}'");
                    }

                    entry.Inherits.Add(parent);
                }

                continue;
            }

            if (key.StartsWith('-'))
            {
                entry.Changes.Add(new TraitChange(key[1..], RemoveTrait: true, [], source, line));
                continue;
            }

            var ops = new List<FieldOperation>();
            if (kv.Value is YamlMapping fields)
            {
                foreach (KeyValuePair<string, YamlNode> f in fields.Entries)
                {
                    (string name, FieldOp op) = f.Key.EndsWith("+=", StringComparison.Ordinal) ? (f.Key[..^2], FieldOp.Append)
                        : f.Key.EndsWith("-=", StringComparison.Ordinal) ? (f.Key[..^2], FieldOp.Remove)
                        : f.Value is YamlScalar { IsNull: true, Value: "~" or "null" } ? (f.Key, FieldOp.Unset)
                        : (f.Key, FieldOp.Set);
                    if (name.Length == 0)
                    {
                        throw new RulesException(source, f.Value.Line, $"{entry.Key}.{key}: empty field name");
                    }

                    if (op is FieldOp.Append or FieldOp.Remove && f.Value is not YamlSequence)
                    {
                        throw new RulesException(source, f.Value.Line, $"{entry.Key}.{key}.{f.Key}: list operations need a list");
                    }

                    ops.Add(new FieldOperation(name, op, new RuleValue(f.Value, source)));
                }
            }
            else if (kv.Value is not YamlScalar { IsNull: true })
            {
                throw new RulesException(source, line, $"{entry.Key}.{key}: a trait must be a mapping of fields (or empty)");
            }

            entry.Changes.Add(new TraitChange(key, RemoveTrait: false, ops, source, line));
        }
    }

    /// <summary>Resolves inheritance for every entry; templates are resolved but returned too (callers skip them).</summary>
    public List<ResolvedEntry> Resolve()
    {
        var resolved = new ResolvedEntry?[_entries.Count];
        var state = new byte[_entries.Count]; // 0 new, 1 resolving, 2 done
        var result = new List<ResolvedEntry>(_entries.Count);
        for (int i = 0; i < _entries.Count; i++)
        {
            result.Add(Resolve(i, resolved, state, 0));
        }

        return result;
    }

    /// <summary>Longest chain of templates an entry may inherit through (bounded recursion: rules are untrusted input).</summary>
    public const int MaxInheritanceDepth = 64;

    private ResolvedEntry Resolve(int index, ResolvedEntry?[] resolved, byte[] state, int depth)
    {
        if (state[index] == 2)
        {
            return resolved[index]!;
        }

        RuleEntry entry = _entries[index];
        if (depth > MaxInheritanceDepth)
        {
            throw new RulesException(entry.Source, entry.Line, $"{entry.Key}: templates inherit deeper than {MaxInheritanceDepth} levels");
        }

        if (state[index] == 1)
        {
            throw new RulesException(entry.Source, entry.Line, $"{entry.Key}: inheritance cycle");
        }

        state[index] = 1;
        var output = new ResolvedEntry(entry.Key, entry.Source, entry.Line);
        foreach (string parentKey in entry.Inherits)
        {
            if (!_index.TryGetValue(parentKey, out int parentIndex))
            {
                throw new RulesException(entry.Source, entry.Line, $"{entry.Key}: inherits unknown template '{parentKey}'");
            }

            ResolvedEntry parent = Resolve(parentIndex, resolved, state, depth + 1);
            foreach (string removed in parent.Removed)
            {
                output.Traits.RemoveAll(t => string.Equals(t.Name, removed, StringComparison.Ordinal));
                if (!output.Removed.Contains(removed))
                {
                    output.Removed.Add(removed);
                }
            }

            foreach (ResolvedTrait trait in parent.Traits)
            {
                output.Removed.Remove(trait.Name);
                ResolvedTrait? existing = output.Find(trait.Name);
                if (existing is null)
                {
                    output.Traits.Add(trait.Clone());
                }
                else
                {
                    // A later parent overrides fields of an earlier one.
                    foreach (KeyValuePair<string, RuleValue> f in trait.Fields)
                    {
                        existing.Set(f.Key, f.Value);
                    }
                }
            }
        }

        foreach (TraitChange change in entry.Changes)
        {
            ResolvedTrait? trait = output.Find(change.Trait);
            if (change.RemoveTrait)
            {
                if (trait is not null)
                {
                    output.Traits.Remove(trait);
                }

                if (!output.Removed.Contains(change.Trait))
                {
                    output.Removed.Add(change.Trait);
                }

                continue;
            }

            output.Removed.Remove(change.Trait);

            if (trait is null)
            {
                trait = new ResolvedTrait(change.Trait, change.Source, change.Line);
                output.Traits.Add(trait);
            }
            else
            {
                trait.Source = change.Source;
                trait.Line = change.Line;
            }

            foreach (FieldOperation op in change.Operations)
            {
                Apply(entry.Key, trait, op);
            }
        }

        resolved[index] = output;
        state[index] = 2;
        return output;
    }

    private static void Apply(string key, ResolvedTrait trait, FieldOperation op)
    {
        switch (op.Op)
        {
            case FieldOp.Set:
                trait.Set(op.Field, op.Value);
                break;
            case FieldOp.Unset:
                trait.Unset(op.Field);
                break;
            default:
            {
                var items = new List<YamlNode>();
                if (trait.Get(op.Field) is { } current)
                {
                    if (current.Node is not YamlSequence existing)
                    {
                        throw new RulesException(op.Value.Source, op.Value.Node.Line, $"{key}.{trait.Name}.{op.Field}: the inherited value is not a list");
                    }

                    items.AddRange(existing.Items);
                }

                foreach (YamlNode item in ((YamlSequence)op.Value.Node).Items)
                {
                    if (op.Op == FieldOp.Append)
                    {
                        items.Add(item);
                    }
                    else
                    {
                        string text = item is YamlScalar s ? s.Value : throw new RulesException(op.Value.Source, item.Line, $"{key}.{trait.Name}.{op.Field}: '-=' removes scalars only");
                        items.RemoveAll(i => i is YamlScalar x && string.Equals(x.Value, text, StringComparison.Ordinal));
                    }
                }

                trait.Set(op.Field, new RuleValue(new YamlSequence(items, op.Value.Node.Line), op.Value.Source));
                break;
            }
        }
    }

    internal static List<string> StringList(YamlNode node, string source, string what)
    {
        var list = new List<string>();
        switch (node)
        {
            case YamlScalar { IsNull: true }:
                break;
            case YamlScalar s:
                list.Add(s.Value);
                break;
            case YamlSequence seq:
                foreach (YamlNode item in seq.Items)
                {
                    list.Add(item is YamlScalar x && !x.IsNull ? x.Value : throw new RulesException(source, item.Line, $"{what}: expected a list of names"));
                }

                break;
            default:
                throw new RulesException(source, node.Line, $"{what}: expected a list of names");
        }

        return list;
    }
}
