using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Tzar2.Architecture.Tests;

/// <summary>
/// ADR 0212 (code review 2026-10, item 16): the client's contract with the game is the session, its frames and the UI
/// models (<c>Tzar2.Game.Session</c>, <c>Tzar2.Game.Ui</c>). Their public surface names no type the client may not use
/// (<see cref="ClientTypeReferenceTests.IsAllowed"/>): not the simulation, its world or kingdom state, the AI, the
/// network, the replays or the trigger engine. Hosts that need those (the CLI, tests) reach them through internals.
/// A few plain values of those assemblies describe a match's settings and are part of the contract (<see cref="Values"/>):
/// enums and immutable records, never a handle to running state.
/// </summary>
public class ClientContractTests
{
    private static readonly string[] ContractNamespaces = ["Tzar2.Game.Session", "Tzar2.Game.Ui"];

    // Settings and results as plain values: a stance and what is shared (diplomacy), the game type and the victory
    // condition, the AI programs, trigger options and chat lines a match or a replay is set up with, a tick's hashes.
    private static readonly string[] Values =
    [
        "Tzar2.Sim.Kingdoms.Stance", "Tzar2.Sim.Kingdoms.Sharing", "Tzar2.Sim.GameType", "Tzar2.Sim.VictoryCondition",
        "Tzar2.Sim.TickResult", "Tzar2.Replay.ReplayAiProgram", "Tzar2.Replay.ReplayChat", "Tzar2.Replay.ReplayHeader",
        "Tzar2.Scripting.Legacy.TriggerOptions",
    ];

    [Fact]
    public void The_session_and_ui_models_expose_only_what_the_client_may_use()
    {
        Assembly game = typeof(Game.Session.GameSession).Assembly;
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Type type in game.GetExportedTypes().Where(t => ContractNamespaces.Contains(t.Namespace)))
        {
            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (Type used in Signature(member))
                {
                    foreach (Type t in Flatten(used))
                    {
                        if (t.Namespace is { } ns && !ClientTypeReferenceTests.IsAllowed(ns) && !Values.Contains(t.FullName))
                        {
                            found.Add($"{type.FullName}.{member.Name}: {t.FullName}");
                        }
                    }
                }
            }

            if (type.BaseType is { } baseType && baseType.Namespace is { } baseNs && !ClientTypeReferenceTests.IsAllowed(baseNs))
            {
                found.Add($"{type.FullName}: base {baseType.FullName}");
            }
        }

        Assert.True(found.Count == 0, "The client contract names types the client may not use (ADR 0212):\n" + string.Join("\n", found));
    }

    private static IEnumerable<Type> Signature(MemberInfo member) => member switch
    {
        FieldInfo f => [f.FieldType],
        PropertyInfo p => [p.PropertyType, .. p.GetIndexParameters().Select(i => i.ParameterType)],
        MethodInfo m when !m.IsSpecialName => [m.ReturnType, .. m.GetParameters().Select(i => i.ParameterType)],
        ConstructorInfo c => c.GetParameters().Select(i => i.ParameterType),
        EventInfo e when e.EventHandlerType is { } h => [h],
        _ => [],
    };

    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (Type t in Flatten(element))
            {
                yield return t;
            }

            yield break;
        }

        yield return type;
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type t in Flatten(argument))
                {
                    yield return t;
                }
            }
        }
    }
}
