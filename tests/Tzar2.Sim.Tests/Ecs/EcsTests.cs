using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FsCheck.Xunit;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Ecs;
using Tzar2.Sim.Events;
using Tzar2.Sim.Snapshots;
using Xunit;

namespace Tzar2.Sim.Tests.Ecs;

public class EcsTests
{
    [Fact]
    public void Registry_hands_out_ascending_fresh_slots_and_recycles_fifo_after_commit()
    {
        var registry = new EntityRegistry();
        EntityId a = registry.Create();
        EntityId b = registry.Create();
        EntityId c = registry.Create();
        Assert.Equal([0, 1, 2], new[] { a.Index, b.Index, c.Index });
        Assert.All(new[] { a, b, c }, e => Assert.Equal(1, e.Generation));

        Assert.True(registry.Destroy(b));
        Assert.True(registry.Destroy(a));
        Assert.False(registry.IsAlive(b));
        Assert.False(registry.Destroy(b));

        // Not recycled within the same tick.
        Assert.Equal(3, registry.Create().Index);

        registry.CommitDestroyed();
        EntityId reusedB = registry.Create();
        EntityId reusedA = registry.Create();
        Assert.Equal(EntityId.Create(1, 2), reusedB);
        Assert.Equal(EntityId.Create(0, 2), reusedA);
        Assert.False(registry.IsAlive(a));
        Assert.True(registry.IsAlive(reusedA));
        Assert.Equal(4, registry.AliveCount);
    }

    [Fact]
    public void Stores_stay_sorted_by_entity_index_whatever_the_insertion_order()
    {
        World world = TestWorlds.NewWorld();
        EntityId[] ids = Enumerable.Range(0, 10).Select(_ => world.Entities.Create()).ToArray();
        foreach (int i in new[] { 7, 2, 9, 0, 5, 3, 8, 1, 6, 4 })
        {
            world.Set(ids[i], new Position(i, -i));
        }

        Assert.Equal(ids, world.Positions.Entities.ToArray());
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.Equal(i, world.Positions.Get(ids[i]).X);
        }

        world.Remove<Position>(ids[4]);
        world.Remove<Position>(ids[0]);
        Assert.Equal(ids.Where((_, i) => i is not 0 and not 4), world.Positions.Entities.ToArray());
        Assert.Equal(9, world.Positions.Get(ids[9]).X);
    }

    [Fact]
    public void Destroy_removes_every_component_and_stale_handles_do_not_resolve()
    {
        World world = TestWorlds.NewWorld();
        EntityId unit = world.Spawn(0, 1, 1000, 1000);
        world.SetCold(unit, new EntityName("Ivan"));
        Assert.True(world.Destroy(unit));
        foreach (IComponentStore store in world.Components.All)
        {
            Assert.Equal(0, store.Count);
        }

        Assert.Equal(0, world.Map.Occupancy.ToArray().Sum(c => c));
        world.Entities.CommitDestroyed();
        EntityId next = world.Spawn(0, 1, 1000, 1000);
        Assert.Equal(unit.Index, next.Index);
        Assert.False(world.Positions.Has(unit));
        Assert.Throws<InvalidOperationException>(() => world.Set(unit, new Position(1, 1)));
    }

    [Fact]
    public void Registration_is_closed_after_the_first_tick()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        sim.Tick(new CommandList());
        Assert.Throws<InvalidOperationException>(() => sim.World.Components.RegisterHot<Probe>("test.probe"));
        Assert.Throws<InvalidOperationException>(() => sim.Systems.Add(new Systems.CleanupSystem()));
    }

    public static TheoryData<Type> HashedStructs()
    {
        var data = new TheoryData<Type>();
        foreach (Type t in typeof(World).Assembly.GetTypes().Where(t => t.IsValueType && typeof(IComponent).IsAssignableFrom(t)))
        {
            data.Add(t);
        }

        data.Add(typeof(PlayerState));
        data.Add(typeof(EntityId));
        return data;
    }

    [Theory]
    [MemberData(nameof(HashedStructs))]
    public void Hashed_structs_have_no_padding(Type type)
    {
        // StateHasher.AddSpan and snapshots copy raw memory: padding bytes would make them irreproducible.
        int fieldBytes = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Sum(f => SizeOf(f.FieldType));
        int size = (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type).Invoke(null, null)!;
        Assert.Equal(size, fieldBytes);
        Assert.True(type.IsLayoutSequential || type.IsExplicitLayout);
    }

    [Fact]
    public void Wire_structs_have_expected_sizes()
    {
        Assert.Equal(32, Unsafe.SizeOf<Command>());
        Assert.Equal(20, Unsafe.SizeOf<SimEvent>());
        Assert.Equal(24, Unsafe.SizeOf<RenderEntity>());
    }

    /// <summary>
    /// Model-based property test (REQ-SIM-07/08): a random sequence of create/destroy/commit/add/remove operations on a
    /// world behaves like a simple dictionary model, the stores stay sorted, and stale handles never resolve.
    /// </summary>
    [Property(MaxTest = 300)]
    public bool Ecs_matches_a_dictionary_model(byte[] operations)
    {
        World world = TestWorlds.NewWorld();
        var alive = new List<EntityId>();
        var dead = new List<EntityId>();
        var positions = new SortedDictionary<int, (EntityId Id, int X)>();
        var healths = new SortedDictionary<int, (EntityId Id, int Hp)>();
        int counter = 0;
        foreach (byte op in operations ?? [])
        {
            int pick = op >> 3;
            switch (op & 7)
            {
                case 0:
                case 1:
                    alive.Add(world.Entities.Create());
                    break;
                case 2 when alive.Count > 0:
                {
                    EntityId e = alive[pick % alive.Count];
                    world.Destroy(e);
                    alive.Remove(e);
                    dead.Add(e);
                    positions.Remove(e.Index);
                    healths.Remove(e.Index);
                    break;
                }

                case 3:
                    world.Entities.CommitDestroyed();
                    break;
                case 4 when alive.Count > 0:
                {
                    EntityId e = alive[pick % alive.Count];
                    world.Set(e, new Position(++counter, 0));
                    positions[e.Index] = (e, counter);
                    break;
                }

                case 5 when alive.Count > 0:
                {
                    EntityId e = alive[pick % alive.Count];
                    world.Set(e, new Health { Current = ++counter, Max = 1 });
                    healths[e.Index] = (e, counter);
                    break;
                }

                case 6 when alive.Count > 0:
                {
                    EntityId e = alive[pick % alive.Count];
                    bool had = positions.Remove(e.Index);
                    if (world.Remove<Position>(e) != had)
                    {
                        return false;
                    }

                    break;
                }

                case 7 when alive.Count > 0:
                {
                    EntityId e = alive[pick % alive.Count];
                    healths.Remove(e.Index);
                    world.Remove<Health>(e);
                    break;
                }
            }
        }

        bool sorted = IsStrictlyAscending(world.Positions.Entities) && IsStrictlyAscending(world.Healths.Entities);
        bool matchesPositions = world.Positions.Entities.ToArray().SequenceEqual(positions.Values.Select(v => v.Id))
            && positions.Values.All(v => world.Positions.Get(v.Id).X == v.X);
        bool matchesHealths = world.Healths.Entities.ToArray().SequenceEqual(healths.Values.Select(v => v.Id))
            && healths.Values.All(v => world.Healths.Get(v.Id).Current == v.Hp);
        bool liveness = alive.All(world.Entities.IsAlive) && dead.All(e => !world.Entities.IsAlive(e))
            && dead.All(e => !world.Positions.Has(e) && !world.Healths.Has(e))
            && world.Entities.AliveCount == alive.Count;
        bool uniqueHandles = alive.Select(e => e.Index).Distinct().Count() == alive.Count;
        return sorted && matchesPositions && matchesHealths && liveness && uniqueHandles;
    }

    private static bool IsStrictlyAscending(ReadOnlySpan<EntityId> ids)
    {
        for (int i = 1; i < ids.Length; i++)
        {
            if (ids[i - 1].Index >= ids[i].Index)
            {
                return false;
            }
        }

        return true;
    }

    private static int SizeOf(Type type)
    {
        if (type.IsEnum)
        {
            return SizeOf(Enum.GetUnderlyingType(type));
        }

        if (type.IsPrimitive)
        {
            return Marshal.SizeOf(type);
        }

        return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Sum(f => SizeOf(f.FieldType));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Probe : IComponent
    {
        public int Value;
    }
}
