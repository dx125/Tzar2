using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Game.Session;
using Tzar2.Net.Protocol;
using Tzar2.Net.Relay;
using Tzar2.Sim;
using Tzar2.Sim.Snapshots;

namespace Tzar2.Game.Multiplayer;

/// <summary>A lobby slot as a match description needs it.</summary>
internal readonly record struct LobbySlotData(SlotState State, string Name, byte[] Choice);

/// <summary>How a lobby becomes a match: its occupied slots, in slot order, are the kingdoms (ADR 0162).</summary>
internal static class LobbyDescription
{
    /// <summary>
    /// The description of the match, and the relay slot of each kingdom and back (255 = none). On a custom map with
    /// <paramref name="customKingdoms"/> kingdoms (ADR 0233), lobby slot k plays kingdom k; a kingdom whose slot is open
    /// or closed stays on the map, played by nobody.
    /// </summary>
    /// <exception cref="InvalidDataException">The lobby does not fit the saved game being resumed or the custom map, or the ruleset has no castle.</exception>
    public static (MatchDescriptor Descriptor, byte[] SimSlotOf, byte[] LobbySlotOf) Build(
        MatchSettings settings, IReadOnlyList<LobbySlotData> slots, Ruleset ruleset, MultiplayerSave? save, int customKingdoms = 0)
    {
        var players = ImmutableArray.CreateBuilder<MatchPlayer>();
        byte[] simOf = Enumerable.Repeat((byte)255, Limits.MaxSlots).ToArray();
        var lobbyOf = new List<byte>();
        for (int i = 0; i < slots.Count && i < Limits.MaxSlots; i++)
        {
            LobbySlotData s = slots[i];
            bool occupied = s.State is SlotState.Human or SlotState.Computer;
            if (customKingdoms > 0 && i >= customKingdoms && occupied)
            {
                throw new InvalidDataException($"The map has {customKingdoms} kingdoms; slot {i + 1} is not one of them.");
            }

            if (customKingdoms > 0 && i < customKingdoms && !occupied)
            {
                players.Add(new MatchPlayer((byte)players.Count, string.Empty, string.Empty, ControllerKind.Passive, Difficulty.Normal, 0));
                lobbyOf.Add(255);
                continue;
            }

            if (!occupied)
            {
                continue;
            }

            byte sim = (byte)players.Count;
            simOf[i] = sim;
            lobbyOf.Add((byte)i);
            SlotChoice choice = SlotChoice.Parse(s.Choice);
            if (save is not null)
            {
                MatchPlayer old = sim < save.Descriptor.Players.Length ? save.Descriptor.Players[sim]
                    : throw new InvalidDataException("The lobby has more kingdoms than the saved game.");
                choice = new SlotChoice(old.Civilization, old.Team, old.Difficulty, old.AiScript, old.Colour);
            }

            string civ = ruleset.CanFoundKingdom(choice.Civilization) ? choice.Civilization : FirstCivilization(ruleset);
            ControllerKind controller = s.State == SlotState.Human ? ControllerKind.Human : ControllerKind.Computer;
            players.Add(new MatchPlayer(sim, s.State == SlotState.Human ? s.Name : string.Empty, civ, controller, choice.Difficulty, choice.Team, choice.AiScript, choice.Colour));
        }

        for (int k = players.Count; k < customKingdoms; k++)
        {
            players.Add(new MatchPlayer((byte)k, string.Empty, string.Empty, ControllerKind.Passive, Difficulty.Normal, 0));
            lobbyOf.Add(255);
        }

        // Colours (SHELL-016): wanted ones first, in slot order (the first slot keeps a colour two slots want), then
        // the rest take the free ones in order, so a lobby without wishes keeps the kingdom-index colours.
        byte[] colours = Colours(players.Select(p => p.Colour).ToArray());
        for (int k = 0; k < players.Count; k++)
        {
            players[k] = players[k] with { Colour = colours[k] == k ? (byte)255 : colours[k] };
        }

        if (players.Count == 0)
        {
            throw new InvalidDataException("The lobby has no kingdoms.");
        }

        if (save is not null && players.Count != save.Descriptor.Players.Length)
        {
            throw new InvalidDataException($"The saved game has {save.Descriptor.Players.Length} kingdoms, the lobby {players.Count}.");
        }

        MatchSettings effective = save is null
            ? settings
            : save.Descriptor.Settings with { ResumeSnapshotHash = settings.ResumeSnapshotHash, SpeedPercent = settings.SpeedPercent };
        return (new MatchDescriptor(effective, players.ToImmutable()), simOf, [.. lobbyOf]);
    }

    /// <summary>
    /// The colour of each of <paramref name="wanted"/>'s kingdoms (0-7; <see cref="SlotChoice.AnyColour"/> = no wish):
    /// a wish is granted unless an earlier kingdom has that colour; the others take the free colours in order.
    /// </summary>
    public static byte[] Colours(IReadOnlyList<byte> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var result = new byte[wanted.Count];
        var taken = new bool[Math.Max(8, wanted.Count)];
        for (int k = 0; k < wanted.Count; k++)
        {
            result[k] = SlotChoice.AnyColour;
            if (wanted[k] < 8 && !taken[wanted[k]])
            {
                result[k] = wanted[k];
                taken[wanted[k]] = true;
            }
        }

        int next = 0;
        for (int k = 0; k < wanted.Count; k++)
        {
            if (result[k] == SlotChoice.AnyColour)
            {
                while (taken[next])
                {
                    next++;
                }

                result[k] = (byte)next;
                taken[next] = true;
            }
        }

        return result;
    }

    private static string FirstCivilization(Ruleset ruleset)
        => ruleset.FirstKingdomCivilization ?? throw new InvalidDataException("The ruleset has no civilization with a seat.");
}

/// <summary>
/// Reads desync reports (REQ-NET-04; ADR 0165): rebuilds the match from the report's lobby settings and slots, restores
/// two peers' snapshots and diffs them component by component (<see cref="SnapshotDiff"/>).
/// </summary>
public static class DesyncReports
{
    /// <summary>Reads a <c>.t2desync</c> file's bytes.</summary>
    /// <exception cref="InvalidDataException">Malformed.</exception>
    public static DesyncBundle Read(ReadOnlySpan<byte> bytes) => DesyncBundle.Parse(bytes);

    /// <summary>The match setup of the report (to restore its snapshots).</summary>
    /// <exception cref="InvalidDataException">The report has no lobby description.</exception>
    public static MatchSetup Setup(DesyncBundle bundle, Ruleset ruleset, SkirmishAi ai)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(ruleset);
        if (bundle.Settings.Length == 0 || bundle.LobbySlots.Length == 0)
        {
            throw new InvalidDataException("The report does not describe its lobby.");
        }

        MatchSettings settings = MatchSettings.Parse(bundle.Settings);
        (MatchDescriptor descriptor, _, _) = LobbyDescription.Build(
            settings with { ResumeSnapshotHash = 0 }, bundle.LobbySlots.Select(s => new LobbySlotData(s.State, s.Name, s.Choice)).ToArray(), ruleset, save: null);
        return descriptor.ToSetup(ai, multiplayer: true, localSlot: null);
    }

    /// <summary>The differences between the current snapshots of lobby slots <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static IReadOnlyList<SnapshotDifference> Diff(DesyncBundle bundle, Ruleset ruleset, SkirmishAi ai, byte a, byte b, int max = 500)
        => Compare(bundle, ruleset, ai, a, b, before: false, max);

    /// <summary>The differences between the "before" keyframes of lobby slots <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static IReadOnlyList<SnapshotDifference> DiffBefore(DesyncBundle bundle, Ruleset ruleset, SkirmishAi ai, byte a, byte b, int max = 500)
        => Compare(bundle, ruleset, ai, a, b, before: true, max);

    /// <summary>The differences between two snapshots of the same match.</summary>
    public static IReadOnlyList<SnapshotDifference> Diff(MatchSetup setup, Ruleset ruleset, byte[] left, byte[] right, int max = 500)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(ruleset);
        MapData map = setup.Map.Create(ruleset, setup);
        using Simulation x = Simulation.Restore(left, ruleset, map);
        using Simulation y = Simulation.Restore(right, ruleset, map);
        return SnapshotDiff.Compare(x.World, y.World, max);
    }

    private static IReadOnlyList<SnapshotDifference> Compare(DesyncBundle bundle, Ruleset ruleset, SkirmishAi ai, byte a, byte b, bool before, int max)
    {
        DesyncSlotReport ra = bundle.Slots.FirstOrDefault(s => s.Slot == a) ?? throw new InvalidDataException($"The report has no snapshot of slot {a}.");
        DesyncSlotReport rb = bundle.Slots.FirstOrDefault(s => s.Slot == b) ?? throw new InvalidDataException($"The report has no snapshot of slot {b}.");
        byte[] left = before ? ra.Before : ra.After;
        byte[] right = before ? rb.Before : rb.After;
        if (left.Length == 0 || right.Length == 0)
        {
            throw new InvalidDataException("A snapshot is missing from the report.");
        }

        return Diff(Setup(bundle, ruleset, ai), ruleset, left, right, max);
    }
}
