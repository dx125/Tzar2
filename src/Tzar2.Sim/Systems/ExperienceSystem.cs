using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 11 (TDD §8.5, GAME-08; ADR 0092): the experience earned this tick (combat damage against enemies,
/// training dummies, kills, spells, potions) counts after the owner's learning speed (Warfare, Tactics, Sun Tzu); heroes
/// with Leadership share it with the units around them; levels follow (each costs more; capped by Learning, Heroism);
/// a level adds attack, defence and — with Body Building — hit points; at the hero level a unit becomes a named hero.
/// </summary>
public sealed class ExperienceSystem : ISimSystem
{
    private readonly List<(EntityId Hero, int Gain)> _shares = [];
    private readonly List<byte> _tournaments = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Experience;

    /// <inheritdoc />
    public string Name => "experience";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        if (world.Experiences.Count == 0)
        {
            return;
        }

        CombatSettings settings = world.Ruleset.Combat;
        _shares.Clear();
        _tournaments.Clear();
        ReadOnlySpan<EntityId> ids = world.Experiences.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            ref Experience x = ref world.Experiences.At(i);
            if (x.PendingXp <= 0)
            {
                continue;
            }

            Identity identity = world.Identities.Get(ids[i]);
            x.PendingXp = world.Stats.Apply(identity.Owner, ModifierStat.ExperienceGain, identity.TypeId, x.PendingXp);
            if ((x.Flags & ExperienceFlags.Hero) != 0 && settings.LeadershipTechnology >= 0 && world.HasPlayer(identity.Owner)
                && world.Players[identity.Owner].HasTechnology(settings.LeadershipTechnology))
            {
                _shares.Add((ids[i], x.PendingXp));
            }

            Tournament(world, identity.Owner, x.PendingXp, settings);
        }

        foreach (byte kingdom in _tournaments)
        {
            Teach(world, kingdom, settings);
        }

        foreach ((EntityId hero, int gain) in _shares)
        {
            Share(world, hero, gain, settings);
        }

        for (int i = 0; i < world.Experiences.Count; i++)
        {
            if (world.Experiences.At(i).PendingXp > 0)
            {
                Gain(world, world.Experiences.Entities[i], i, settings);
            }
        }
    }

    // Tournaments (TECH-029; ReTzar's reading of the original, 0x4de900): what a kingdom's units learn (not what Leadership
    // shares or Tournaments teaches) fills the kingdom's pool; when it reaches tournamentEvery it empties, the rest lost.
    private void Tournament(World world, byte owner, int gain, CombatSettings settings)
    {
        if (settings.TournamentTechnology < 0 || settings.TournamentPupils.IsDefaultOrEmpty || !world.HasPlayer(owner)
            || !world.Players[owner].HasTechnology(settings.TournamentTechnology))
        {
            return;
        }

        ref PlayerState kingdom = ref world.Players[owner];
        long pool = (long)kingdom.KungFuPool + gain;
        if (pool < settings.TournamentEvery)
        {
            kingdom.KungFuPool = (byte)pool;
            return;
        }

        kingdom.KungFuPool = 0;
        _tournaments.Add(owner);
    }

    // Every pupil of the kingdom (Asian peasants, monks, ninjas and priests) learns tournamentGrant.
    private static void Teach(World world, byte owner, CombatSettings settings)
    {
        ReadOnlySpan<EntityId> ids = world.Experiences.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Identity identity = world.Identities.Get(ids[i]);
            if (identity.Owner == owner && settings.TournamentPupils.BinarySearch(identity.TypeId) >= 0)
            {
                world.Experiences.At(i).PendingXp += settings.TournamentGrant;
            }
        }
    }

    // Leadership: the hero's own kingdom's units within the radius learn a share of what it learned.
    private static void Share(World world, EntityId hero, int gain, CombatSettings settings)
    {
        int share = (int)((long)gain * settings.LeadershipSharePercent / 100);
        if (share <= 0)
        {
            return;
        }

        Position at = world.Positions.Get(hero);
        byte owner = world.Identities.Get(hero).Owner;
        long r2 = (long)settings.LeadershipRadiusUnits * settings.LeadershipRadiusUnits;
        ReadOnlySpan<EntityId> ids = world.Experiences.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i] == hero || world.Identities.Get(ids[i]).Owner != owner || world.IsHeld(ids[i]))
            {
                continue;
            }

            Position p = world.Positions.Get(ids[i]);
            if (IntMath.DistanceSquared(at.X, at.Y, p.X, p.Y) <= r2)
            {
                world.Experiences.At(i).PendingXp += share;
            }
        }
    }

    private static void Gain(World world, EntityId unit, int index, CombatSettings settings)
    {
        ref Experience x = ref world.Experiences.At(index);
        int cap = CombatStats.LevelCap(world, unit);
        int ceiling = CombatStats.XpForLevel(settings, Math.Min(cap, 100_000));
        x.Xp = (int)Math.Min(Math.Min((long)x.Xp + x.PendingXp, ceiling), int.MaxValue);
        x.PendingXp = 0;
        int level = x.Level;
        while (level < cap && level < 10_000 && x.Xp >= CombatStats.XpForLevel(settings, level + 1))
        {
            level++;
        }

        if (level == x.Level)
        {
            return;
        }

        x.Level = (ushort)level;
        bool promote = level >= settings.HeroLevel && (x.Flags & ExperienceFlags.Hero) == 0;
        if (promote)
        {
            x.Flags |= ExperienceFlags.Hero;
        }

        Identity identity = world.Identities.Get(unit);
        world.RefreshMaxHealth(unit); // Body Building: hit points per level (the damage taken stays taken)
        world.Events.Emit(new SimEvent(SimEventKind.LevelUp, identity.Owner, unit, level, 0, identity.TypeId));
        if (promote)
        {
            int name = NameHero(world, unit, identity.Owner);
            world.Events.Emit(new SimEvent(SimEventKind.HeroPromoted, identity.Owner, unit, name, 0, identity.TypeId));
        }
    }

    // A new hero takes the first name of its civilization's list (HEROES.INI) that no live hero of its kingdom carries;
    // when all are taken, "<first name> <n>" (UNVERIFIED). Returns the list index, or -1.
    internal static int NameHero(World world, EntityId unit, byte owner)
    {
        ImmutableArray<string> names = world.Ruleset.Combat.NamesOf(world.CivilizationOf(unit));
        Ecs.ColdComponentStore<EntityName> store = world.Names;
        for (int k = 0; k < names.Length; k++)
        {
            if (!NameTaken(world, owner, names[k]))
            {
                store.SetValue(unit, new EntityName(names[k]));
                return k;
            }
        }

        if (names.Length > 0)
        {
            for (int n = 2; n < 100_000; n++)
            {
                string candidate = names[0] + " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!NameTaken(world, owner, candidate))
                {
                    store.SetValue(unit, new EntityName(candidate));
                    return -1;
                }
            }
        }

        return -1;
    }

    private static bool NameTaken(World world, byte owner, string name)
    {
        ReadOnlySpan<EntityId> ids = world.Names.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            if (world.Identities.IndexOf(ids[i]) is >= 0 and var p && world.Identities.At(p).Owner == owner && world.IsHero(ids[i])
                && string.Equals(world.Names.Get(ids[i]).Value, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
