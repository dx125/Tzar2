using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Components;
using Tzar2.Sim.Ecs;

namespace Tzar2.Sim.Snapshots;

/// <summary>One difference between two worlds.</summary>
/// <param name="Section">What differs: <c>world</c>, <c>rng</c>, <c>player</c>, <c>entities</c>, <c>map</c>,
/// <c>kingdoms</c>, <c>store:&lt;key&gt;</c> or <c>extension:&lt;key&gt;</c>.</param>
/// <param name="Entity">The entity concerned, or <see cref="EntityId.None"/>.</param>
/// <param name="Detail">Human-readable detail (values on both sides).</param>
public sealed record SnapshotDifference(string Section, EntityId Entity, string Detail);

/// <summary>
/// A component-by-component diff of two worlds restored from snapshots (TDD §8.8 <c>t2 desync diff</c>, ADR 0165):
/// tick and settings, RNG streams by name, player states field by field, the entity registry, every hot store entity by
/// entity (with the byte range of the component that differs and both values), cold stores and extension blocks as
/// wholes, plus the map, decals, paths and kingdoms. Entities are named by type key and owner. Diagnostics only.
/// </summary>
public static class SnapshotDiff
{
    private const int RngHeaderBytes = 9;
    private const int RngStreamBytes = 32;

    /// <summary>Lists the differences between <paramref name="a"/> and <paramref name="b"/> (at most <paramref name="max"/>).</summary>
    public static IReadOnlyList<SnapshotDifference> Compare(World a, World b, int max = 500)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        var diff = new Collector(max);
        if (a.Tick != b.Tick)
        {
            diff.Add("world", EntityId.None, Invariant($"tick {a.Tick.Value} vs {b.Tick.Value}"));
        }

        if (a.Config != b.Config)
        {
            diff.Add("world", EntityId.None, "the match settings differ");
        }

        CompareRng(a, b, diff);
        ComparePlayers(a, b, diff);
        CompareBytes("entities", a, b, (w, x) => w.Entities.WriteTo(x), diff);
        CompareBytes("map", a, b, (w, x) => w.Map.WriteTo(x), diff);
        CompareBytes("decals", a, b, (w, x) => w.Decals.WriteTo(x), diff);
        CompareBytes("paths", a, b, (w, x) =>
        {
            x.WriteVarUInt64((ulong)w.NextGroupId);
            w.Paths.WriteTo(x);
            w.PathQueue.WriteTo(x);
        }, diff);
        CompareBytes("kingdoms", a, b, (w, x) => w.WriteKingdomsTo(x), diff);
        CompareStores(a, b, diff);
        CompareExtensions(a, b, diff);
        return diff.Items;
    }

    /// <summary>Formats differences as text lines.</summary>
    public static string Format(IReadOnlyList<SnapshotDifference> differences)
    {
        ArgumentNullException.ThrowIfNull(differences);
        var text = new StringBuilder();
        foreach (SnapshotDifference d in differences)
        {
            text.Append(d.Section);
            if (d.Entity != EntityId.None)
            {
                text.Append(CultureInfo.InvariantCulture, $" entity {d.Entity.Index}:{d.Entity.Generation}");
            }

            text.Append(": ").Append(d.Detail).Append('\n');
        }

        return text.ToString();
    }

    private static void CompareRng(World a, World b, Collector diff)
    {
        byte[] x = Bytes(a, (w, o) => w.Rng.WriteTo(o));
        byte[] y = Bytes(b, (w, o) => w.Rng.WriteTo(o));
        int streams = Math.Min(x.Length, y.Length) - RngHeaderBytes;
        for (int i = 0; i * RngStreamBytes < streams; i++)
        {
            ReadOnlySpan<byte> sx = x.AsSpan(RngHeaderBytes + (i * RngStreamBytes), RngStreamBytes);
            ReadOnlySpan<byte> sy = y.AsSpan(RngHeaderBytes + (i * RngStreamBytes), RngStreamBytes);
            if (!sx.SequenceEqual(sy))
            {
                string name = i switch { 0 => "combat", 1 => "mapgen", 2 => "scripts", 3 => "misc", _ => Invariant($"ai[{i - 4}]") };
                diff.Add("rng", EntityId.None, $"stream {name} differs ({Convert.ToHexString(sx[..8])}... vs {Convert.ToHexString(sy[..8])}...)");
            }
        }

        if (x.Length != y.Length)
        {
            diff.Add("rng", EntityId.None, "different numbers of streams");
        }
    }

    private static void ComparePlayers(World a, World b, Collector diff)
    {
        int n = Math.Min(a.Players.Length, b.Players.Length);
        for (int p = 0; p < n; p++)
        {
            PlayerState x = a.Players[p];
            PlayerState y = b.Players[p];
            void Field(string name, long vx, long vy)
            {
                if (vx != vy)
                {
                    diff.Add("player", EntityId.None, Invariant($"player {p} {name}: {vx} vs {vy}"));
                }
            }

            if (x.Flags != y.Flags)
            {
                diff.Add("player", EntityId.None, Invariant($"player {p} flags: {x.Flags} vs {y.Flags}"));
            }

            for (int r = 0; r < a.Ruleset.ResourceTypes.Count; r++)
            {
                Field("resource " + a.Ruleset.ResourceTypes[r], x.Resources[r], y.Resources[r]);
            }

            Field("population", x.Population, y.Population);
            Field("population cap", x.PopulationCap, y.PopulationCap);
            Field("population limit", x.PopulationLimit, y.PopulationLimit);
            Field("civilization", x.Civilization, y.Civilization);
            for (int t = 0; t < Content.Compiled.Ruleset.MaxTechnologies; t++)
            {
                if (x.HasTechnology(t) != y.HasTechnology(t))
                {
                    string name = t < a.Ruleset.Technologies.Count ? a.Ruleset.Technologies[t].Key : Invariant($"#{t}");
                    diff.Add("player", EntityId.None, Invariant($"player {p} technology {name}: {x.HasTechnology(t)} vs {y.HasTechnology(t)}"));
                }
            }
        }
    }

    private static void CompareStores(World a, World b, Collector diff)
    {
        var buffer = new BinaryBufferWriter(64 * 1024);
        foreach (IComponentStore sa in a.Components.All)
        {
            string section = "store:" + sa.Key;
            IComponentStore? sb = b.Components.FindByKey(sa.Key);
            if (sb is null)
            {
                diff.Add(section, EntityId.None, "missing on the right");
                continue;
            }

            CompareEntitySets(section, a, b, sa.Entities, sb.Entities, diff);
            if (sa.Tier == ComponentTier.Cold)
            {
                if (!Bytes(sa, buffer).AsSpan().SequenceEqual(Bytes(sb, buffer)))
                {
                    diff.Add(section, EntityId.None, "cold component values differ");
                }

                continue;
            }

            (EntityId[] ea, byte[] da, int size) = Hot(sa, buffer);
            (EntityId[] eb, byte[] db, int sizeB) = Hot(sb, buffer);
            if (size != sizeB)
            {
                diff.Add(section, EntityId.None, Invariant($"element size {size} vs {sizeB}"));
                continue;
            }

            int j = 0;
            for (int i = 0; i < ea.Length && !diff.Full; i++)
            {
                while (j < eb.Length && eb[j].Value < ea[i].Value)
                {
                    j++;
                }

                if (j >= eb.Length || eb[j] != ea[i])
                {
                    continue;
                }

                ReadOnlySpan<byte> va = da.AsSpan(i * size, size);
                ReadOnlySpan<byte> vb = db.AsSpan(j * size, size);
                if (!va.SequenceEqual(vb))
                {
                    int first = 0;
                    while (va[first] == vb[first])
                    {
                        first++;
                    }

                    int last = size - 1;
                    while (va[last] == vb[last])
                    {
                        last--;
                    }

                    diff.Add(section, ea[i], Invariant($"{Describe(a, ea[i])}: bytes {first}-{last} of {size}: {Convert.ToHexString(va)} vs {Convert.ToHexString(vb)}"));
                }
            }
        }

        foreach (IComponentStore sb in b.Components.All)
        {
            if (a.Components.FindByKey(sb.Key) is null)
            {
                diff.Add("store:" + sb.Key, EntityId.None, "missing on the left");
            }
        }
    }

    private static void CompareEntitySets(string section, World a, World b, ReadOnlySpan<EntityId> x, ReadOnlySpan<EntityId> y, Collector diff)
    {
        int i = 0;
        int j = 0;
        while ((i < x.Length || j < y.Length) && !diff.Full)
        {
            if (j >= y.Length || (i < x.Length && x[i].Value < y[j].Value))
            {
                diff.Add(section, x[i], $"{Describe(a, x[i])}: only on the left");
                i++;
            }
            else if (i >= x.Length || y[j].Value < x[i].Value)
            {
                diff.Add(section, y[j], $"{Describe(b, y[j])}: only on the right");
                j++;
            }
            else
            {
                i++;
                j++;
            }
        }
    }

    private static void CompareExtensions(World a, World b, Collector diff)
    {
        Dictionary<string, byte[]> x = Extensions(a);
        Dictionary<string, byte[]> y = Extensions(b);
        var keys = new SortedSet<string>(x.Keys, StringComparer.Ordinal);
        keys.UnionWith(y.Keys);
        foreach (string key in keys)
        {
            bool inA = x.TryGetValue(key, out byte[]? va);
            bool inB = y.TryGetValue(key, out byte[]? vb);
            if (!inA || !inB)
            {
                diff.Add("extension:" + key, EntityId.None, inA ? "missing on the right" : "missing on the left");
            }
            else if (!va.AsSpan().SequenceEqual(vb))
            {
                diff.Add("extension:" + key, EntityId.None, Invariant($"state differs ({va!.Length} vs {vb!.Length} bytes)"));
            }
        }
    }

    // Splits the extension blocks (count, then key, length, bytes per block).
    private static Dictionary<string, byte[]> Extensions(World world)
    {
        byte[] bytes = Bytes(world, (w, o) => w.Extensions.WriteTo(o));
        var r = new BinaryBufferReader(bytes);
        var blocks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        ulong count = r.ReadVarUInt64();
        for (ulong i = 0; i < count; i++)
        {
            string key = r.ReadString();
            int length = (int)r.ReadVarUInt64();
            blocks[key] = r.ReadBytes(length).ToArray();
        }

        return blocks;
    }

    private static (EntityId[] Entities, byte[] Data, int Size) Hot(IComponentStore store, BinaryBufferWriter buffer)
    {
        byte[] bytes = Bytes(store, buffer);
        var r = new BinaryBufferReader(bytes);
        int count = (int)r.ReadVarUInt64();
        int size = (int)r.ReadVarUInt64();
        var entities = new EntityId[count];
        for (int i = 0; i < count; i++)
        {
            entities[i] = EntityId.FromValue(r.ReadUInt32());
        }

        return (entities, r.ReadBytes(count * size).ToArray(), size);
    }

    private static void CompareBytes(string section, World a, World b, Action<World, BinaryBufferWriter> write, Collector diff)
    {
        byte[] x = Bytes(a, write);
        byte[] y = Bytes(b, write);
        if (!x.AsSpan().SequenceEqual(y))
        {
            int first = 0;
            while (first < x.Length && first < y.Length && x[first] == y[first])
            {
                first++;
            }

            diff.Add(section, EntityId.None, Invariant($"differs from byte {first} ({x.Length} vs {y.Length} bytes)"));
        }
    }

    private static byte[] Bytes(World world, Action<World, BinaryBufferWriter> write)
    {
        var writer = new BinaryBufferWriter(4096);
        write(world, writer);
        return writer.ToArray();
    }

    private static byte[] Bytes(IComponentStore store, BinaryBufferWriter buffer)
    {
        buffer.Clear();
        store.WriteTo(buffer);
        return buffer.ToArray();
    }

    private static string Describe(World world, EntityId entity)
    {
        if (!world.Entities.IsAlive(entity) || !world.Identities.Has(entity))
        {
            return "(no identity)";
        }

        Identity id = world.Identities.Get(entity);
        string type = id.TypeId < world.Ruleset.EntityTypes.Count ? world.Ruleset.EntityTypes[id.TypeId].Key : Invariant($"type #{id.TypeId}");
        return Invariant($"{type} of player {id.Owner}");
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private sealed class Collector(int max)
    {
        public List<SnapshotDifference> Items { get; } = [];

        public bool Full => Items.Count >= max;

        public void Add(string section, EntityId entity, string detail)
        {
            if (Items.Count < max)
            {
                Items.Add(new SnapshotDifference(section, entity, detail));
            }
            else if (Items.Count == max)
            {
                Items.Add(new SnapshotDifference("...", EntityId.None, "more differences omitted"));
            }
        }
    }
}
