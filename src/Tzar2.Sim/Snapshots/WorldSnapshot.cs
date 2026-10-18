using System;
using System.IO;
using System.Runtime.InteropServices;
using Tzar2.Content.Compiled;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Ecs;

namespace Tzar2.Sim.Snapshots;

/// <summary>
/// The complete, versioned binary sim state (REQ-SIM-13, ADR 0016) for saves, reconnects, replay keyframes and desync
/// dumps. Hot stores are block copies of their dense arrays; cold stores are serialized per value.
/// </summary>
/// <remarks>
/// Layout: magic <c>T2SN</c>, format version, ruleset and map content hashes, the <see cref="SimulationConfig"/>,
/// tick, entity registry, RNG streams, player states (raw), map dynamic layers (obstacle version and non-zero
/// blockers only: static layers are referenced by the map content hash, occupancy is rebuilt), decals, the group
/// counter, the paths and the path-request queue, the stores (count, then per store: key, tier, contents), the
/// extension blocks of in-sim controllers (count, then per block in key order: key, length, bytes; ADR 0051), and finally
/// the full-state hash of the captured world, which <see cref="Restore"/> recomputes and compares. Little-endian only
/// (like <see cref="StateHasher"/>).
/// </remarks>
public static class WorldSnapshot
{
    /// <summary>"T2SN" little-endian.</summary>
    public const uint Magic = 0x4E533254;

    /// <summary>Current format version.</summary>
    public const int FormatVersion = 8;

    /// <summary>Writes a snapshot of <paramref name="world"/>.</summary>
    public static void Capture(World world, BinaryBufferWriter writer, StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(Magic);
        writer.WriteVarUInt64(FormatVersion);
        writer.WriteUInt64(world.Ruleset.ContentHash);
        writer.WriteUInt64(world.Map.Data.ContentHash);
        SimulationConfig config = world.Config;
        writer.WriteUInt64(config.MatchSeed);
        writer.WriteVarUInt64((ulong)config.PlayerCount);
        writer.WriteBoolean(config.CheatsAllowed);
        writer.WriteVarUInt64((ulong)config.FullHashIntervalTicks);
        writer.WriteVarUInt64((ulong)config.TickLengthMilliseconds);
        writer.WriteVarUInt64((ulong)config.PathBudgetPerTick);
        writer.WriteVarUInt64((ulong)(config.StartingResources + 1));
        writer.WriteVarUInt64((ulong)(config.PopulationLimit + 1));
        writer.WriteBoolean(config.NoFog);
        writer.WriteBoolean(config.Explored);
        writer.WriteByte((byte)config.Victory);
        writer.WriteByte((byte)config.GameType);
        writer.WriteVarUInt64((ulong)config.ScoreLimit);
        writer.WriteVarUInt64((ulong)config.TimeLimitMinutes);
        writer.WriteBoolean(config.DayNight);
        writer.WriteBoolean(config.Weather);
        writer.WriteUInt64(config.Teams);
        writer.WriteBoolean(config.Ranked);
        writer.WriteVarUInt64((ulong)world.Tick.Value);
        world.Entities.WriteTo(writer);
        world.Rng.WriteTo(writer);
        writer.WriteBytes(MemoryMarshal.AsBytes(world.Players));
        world.Map.WriteTo(writer);
        world.Decals.WriteTo(writer);
        writer.WriteVarUInt64((ulong)world.NextGroupId);
        world.Paths.WriteTo(writer);
        world.PathQueue.WriteTo(writer);
        world.WriteKingdomsTo(writer);
        writer.WriteVarUInt64((ulong)world.Components.All.Length);
        foreach (IComponentStore store in world.Components.All)
        {
            writer.WriteString(store.Key);
            writer.WriteByte((byte)store.Tier);
            store.WriteTo(writer);
        }

        world.Extensions.WriteTo(writer);
        writer.WriteUInt64(StateHashing.ComputeFullHash(world, hasher));
    }

    /// <summary>Captures a snapshot into a new array.</summary>
    public static byte[] Capture(World world)
    {
        var writer = new BinaryBufferWriter(64 * 1024);
        Capture(world, writer, new StateHasher());
        return writer.ToArray();
    }

    /// <summary>Reads the config stored in a snapshot (to construct the world it restores into).</summary>
    /// <exception cref="InvalidDataException">Not a snapshot, wrong version, or the ruleset/map does not match.</exception>
    public static SimulationConfig ReadConfig(ReadOnlySpan<byte> snapshot, Ruleset ruleset, MapData map)
    {
        var reader = new BinaryBufferReader(snapshot);
        return ReadHeader(ref reader, ruleset, map);
    }

    /// <summary>
    /// Restores <paramref name="world"/> (freshly constructed with the snapshot's config, see
    /// <see cref="ReadConfig"/>) from <paramref name="snapshot"/>, validates every invariant (ADR 0019), and verifies
    /// the stored full-state hash. Internal: on failure the world is left partially overwritten, so only
    /// <see cref="Simulation.Restore"/>, which discards it, may call this.
    /// </summary>
    /// <exception cref="InvalidDataException">The snapshot is malformed, inconsistent, or fails the hash check.</exception>
    internal static void Restore(World world, ReadOnlySpan<byte> snapshot)
    {
        ArgumentNullException.ThrowIfNull(world);
        var reader = new BinaryBufferReader(snapshot);
        SimulationConfig config = ReadHeader(ref reader, world.Ruleset, world.Map.Data);
        if (config != world.Config)
        {
            throw new InvalidDataException("The world was not created with the snapshot's configuration.");
        }

        ulong tick = reader.ReadVarUInt64();
        if (tick > int.MaxValue)
        {
            throw new InvalidDataException("Tick out of range.");
        }

        world.Tick = new Tick((int)tick);
        world.Entities.ReadFrom(ref reader);
        RngStreams rng = RngStreams.ReadFrom(ref reader);
        if (rng.MatchSeed != config.MatchSeed || rng.PlayerCount != config.PlayerCount)
        {
            throw new InvalidDataException("RNG streams do not match the configuration.");
        }

        world.Rng = rng;
        reader.ReadBytes(world.Players.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<PlayerState>()).CopyTo(MemoryMarshal.AsBytes(world.Players));
        world.Map.ReadFrom(ref reader);
        world.Decals.ReadFrom(ref reader);
        ulong nextGroup = reader.ReadVarUInt64();
        if (nextGroup is 0 or > int.MaxValue)
        {
            throw new InvalidDataException("Invalid group counter.");
        }

        world.NextGroupId = (int)nextGroup;
        world.Paths.ReadFrom(ref reader);
        world.PathQueue.ReadFrom(ref reader);
        world.ReadKingdomsFrom(ref reader);
        ulong storeCount = reader.ReadVarUInt64();
        if (storeCount != (ulong)world.Components.All.Length)
        {
            throw new InvalidDataException($"Snapshot has {storeCount} stores, the world {world.Components.All.Length}.");
        }

        foreach (IComponentStore store in world.Components.All)
        {
            string key = reader.ReadString(256);
            byte tier = reader.ReadByte();
            if (!string.Equals(key, store.Key, StringComparison.Ordinal) || tier != (byte)store.Tier)
            {
                throw new InvalidDataException($"Snapshot store '{key}' does not match '{store.Key}'.");
            }

            store.ReadFrom(ref reader);
            foreach (EntityId entity in store.Entities)
            {
                if (!world.Entities.IsAlive(entity))
                {
                    throw new InvalidDataException($"Store '{key}' holds dead {entity}.");
                }
            }
        }

        world.Extensions.ReadFrom(ref reader);
        ulong expected = reader.ReadUInt64();
        if (!reader.IsAtEnd)
        {
            throw new InvalidDataException("Trailing bytes after the snapshot.");
        }

        world.ValidateRestoredState();

        ulong actual = StateHashing.ComputeFullHash(world, new StateHasher());
        if (actual != expected)
        {
            throw new InvalidDataException($"Snapshot hash mismatch: stored {expected:X16}, restored {actual:X16}.");
        }
    }

    private static SimulationConfig ReadHeader(ref BinaryBufferReader reader, Ruleset ruleset, MapData map)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(map);
        if (reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Not a Tzar2 snapshot.");
        }

        ulong version = reader.ReadVarUInt64();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported snapshot format version {version}.");
        }

        if (reader.ReadUInt64() != ruleset.ContentHash)
        {
            throw new InvalidDataException("The snapshot was taken with a different ruleset.");
        }

        if (reader.ReadUInt64() != map.ContentHash)
        {
            throw new InvalidDataException("The snapshot was taken on a different map.");
        }

        ulong seed = reader.ReadUInt64();
        ulong players = reader.ReadVarUInt64();
        bool cheats = reader.ReadBoolean();
        ulong fullHashInterval = reader.ReadVarUInt64();
        ulong tickLength = reader.ReadVarUInt64();
        ulong pathBudget = reader.ReadVarUInt64();
        ulong startingResources = reader.ReadVarUInt64();
        ulong populationLimit = reader.ReadVarUInt64();
        bool noFog = reader.ReadBoolean();
        bool explored = reader.ReadBoolean();
        byte victory = reader.ReadByte();
        byte gameType = reader.ReadByte();
        ulong scoreLimit = reader.ReadVarUInt64();
        ulong timeLimit = reader.ReadVarUInt64();
        bool dayNight = reader.ReadBoolean();
        bool weather = reader.ReadBoolean();
        ulong teams = reader.ReadUInt64();
        bool ranked = reader.ReadBoolean();
        if (victory > (byte)VictoryCondition.NotSpecified || gameType > (byte)GameType.Deathmatch || scoreLimit is 0 or > 100_000_000
            || timeLimit is 0 or > 10_000 || (ranked && cheats))
        {
            throw new InvalidDataException("Invalid configuration in snapshot.");
        }

        if (players is 0 or > 16 || fullHashInterval is 0 or > int.MaxValue || tickLength is 0 or > 1000 || pathBudget is 0 or > int.MaxValue
            || startingResources > 16 || populationLimit > 100_001)
        {
            throw new InvalidDataException("Invalid configuration in snapshot.");
        }

        return new SimulationConfig
        {
            MatchSeed = seed,
            PlayerCount = (int)players,
            CheatsAllowed = cheats,
            FullHashIntervalTicks = (int)fullHashInterval,
            TickLengthMilliseconds = (int)tickLength,
            PathBudgetPerTick = (int)pathBudget,
            StartingResources = (int)startingResources - 1,
            PopulationLimit = (int)populationLimit - 1,
            NoFog = noFog,
            Explored = explored,
            Victory = (VictoryCondition)victory,
            GameType = (GameType)gameType,
            ScoreLimit = (int)scoreLimit,
            TimeLimitMinutes = (int)timeLimit,
            DayNight = dayNight,
            Weather = weather,
            Teams = (players < 16 && (teams >> ((int)players * 4)) != 0) ? throw new InvalidDataException("Invalid teams in snapshot.") : teams,
            Ranked = ranked,
        };
    }
}
