using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Tzar2.Architecture.Tests;

/// <summary>
/// Code review 2026-10 (item 16): no public static state anyone can change. A public static field that is not
/// <c>readonly</c>, or a <c>readonly</c> one that holds an array or a mutable collection, is global state: one caller
/// (a match, a test, a fork's mod) changes it for every other one, across matches and threads. Use a <c>const</c>,
/// an <c>ImmutableArray</c>, a <c>ReadOnlySpan</c> property or a frozen collection instead.
/// </summary>
public class SharedStateTests
{
    private static readonly Assembly[] Core =
    [
        typeof(Core.Tzar2DeterministicAttribute).Assembly,
        typeof(Formats.FormatsAssembly).Assembly,
        typeof(Content.ContentAssembly).Assembly,
        typeof(Import.Editions.InstallVerifier).Assembly,
        typeof(Sim.SimAssembly).Assembly,
        typeof(Scripting.ScriptingAssembly).Assembly,
        typeof(AI.AIAssembly).Assembly,
        typeof(Net.NetAssembly).Assembly,
        typeof(Replay.ReplayAssembly).Assembly,
        typeof(Game.Session.GameSession).Assembly,
    ];

    // Settable public statics that are deliberate, each with its reason.
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        // The overflow policy's switch for tests and tools (ADR 0014), documented on the property.
        "Tzar2.Core.Numerics.OverflowPolicy.Saturated",
    };

    [Fact]
    public void Public_statics_cannot_be_changed()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Type type in Core.SelectMany(a => a.GetExportedTypes()))
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (field.IsLiteral)
                {
                    continue;
                }

                string name = $"{type.FullName}.{field.Name}";
                if (!field.IsInitOnly || IsMutableContainer(field.FieldType))
                {
                    found.Add(name);
                }
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (property.SetMethod is { IsPublic: true })
                {
                    found.Add($"{type.FullName}.{property.Name}");
                }
            }
        }

        found.ExceptWith(Allowed);
        Assert.True(found.Count == 0, "Public static state that callers can change:\n" + string.Join("\n", found));
    }

    private static bool IsMutableContainer(Type type)
    {
        if (type.IsArray)
        {
            return true;
        }

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) || definition == typeof(Dictionary<,>) || definition == typeof(HashSet<>)
                || definition == typeof(SortedDictionary<,>) || definition == typeof(SortedSet<>) || definition == typeof(Queue<>)
                || definition == typeof(Stack<>))
            {
                return true;
            }
        }

        return typeof(IList).IsAssignableFrom(type) && !type.IsValueType && !type.FullName!.StartsWith("System.Collections.Immutable", StringComparison.Ordinal)
            && !type.FullName.StartsWith("System.Collections.Frozen", StringComparison.Ordinal) && !type.FullName.StartsWith("System.Collections.ObjectModel.ReadOnly", StringComparison.Ordinal);
    }
}
